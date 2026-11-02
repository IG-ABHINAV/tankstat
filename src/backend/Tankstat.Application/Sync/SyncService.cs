using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tankstat.Application.Access;
using Tankstat.Application.Auth;
using Tankstat.Application.Telemetry;
using Tankstat.Application.Vehicles;
using Tankstat.Domain;
using Tankstat.Domain.Access;
using Tankstat.Domain.Sync;

namespace Tankstat.Application.Sync;

/// <summary>What applying a change produced: the entity (an add's, an update's) with its new version, and the vehicle it belongs to.</summary>
public sealed record AppliedChange(Guid? EntityId, int? Version, Guid? VehicleId);

/// <summary>
/// One change of a batch: what it is, as the device sent it (`Payload`, JSON), and how to apply it through the services (`Apply`, built by
/// the API from the typed input; every rule and access check of the single mutations applies).
/// </summary>
/// <param name="VehicleOf">Finds the vehicle of what it changes, whoever may see it (a parked change is filed with its vehicle, also when it was
/// parked because the access is gone); needed when <paramref name="VehicleIdHint"/> is not known from the change itself.</param>
/// <param name="Base">The values the change was made from (JSON, as the device sent them), kept with it when it is parked.</param>
public sealed record SyncChangeRequest(
    Guid Id, SyncChangeKind Kind, Guid? TargetId, Guid? VehicleIdHint, int? ExpectedVersion, string Payload, Func<CancellationToken, Task<AppliedChange>> Apply,
    Func<CancellationToken, Task<Guid?>>? VehicleOf = null, string? Base = null);

public sealed record SyncChangeResult(Guid Id, SyncChangeStatus Status, Guid? EntityId, int? Version, string? ReasonKey, IReadOnlyDictionary<string, string> ReasonArgs)
{
    public static SyncChangeResult From(SyncChange c) => new(c.Id, c.Status, c.ResultId, c.ResultVersion, c.ReasonKey, c.ReasonArgs);
}

public sealed record SyncOutcome(IReadOnlyList<SyncChangeResult> Results, int Applied, int Parked);

public interface ISyncChangeRepository
{
    Task<IReadOnlyList<SyncChange>> FindManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    Task<SyncChange?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// The parked changes of the vehicles in the scope (live vehicles only), and those the submitter sent, newest first: at most
    /// <see cref="SyncChange.MaxParkedListed"/> (a device that keeps sending what cannot be applied must not grow every list and inbox).
    /// </summary>
    Task<IReadOnlyList<SyncChange>> ListParkedAsync(OwnerScope scope, Guid submitterId, CancellationToken ct);

    /// <summary>How many parked changes each of the vehicles has.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountParkedAsync(IReadOnlyCollection<Guid> vehicleIds, CancellationToken ct);

    /// <summary>Records a change; false when a change with its id is recorded already (the same batch on another server at the same moment).</summary>
    Task<bool> AddAsync(SyncChange change, CancellationToken ct);

    /// <summary>
    /// Takes a parked change for one person's decision: false when it is not parked, or someone else's claim is newer than
    /// <paramref name="staleBefore"/>. While parked, the claim is <c>ResolvedById</c>/<c>ResolvedAt</c>.
    /// </summary>
    Task<bool> ClaimAsync(Guid id, Guid by, DateTimeOffset now, DateTimeOffset staleBefore, CancellationToken ct);

    /// <summary>Gives a claim back without a decision (the apply failed for want of the person's own access, or unexpectedly).</summary>
    Task ReleaseAsync(Guid id, Guid by, CancellationToken ct);

    /// <summary>
    /// Saves what was decided about a parked change only while it is still parked and claimed by <paramref name="claimedBy"/>; false when
    /// someone else took it meanwhile. Parked again, it is saved unclaimed.
    /// </summary>
    Task<bool> SettleParkedAsync(SyncChange change, Guid claimedBy, CancellationToken ct);

    /// <summary>Removes the applied (and discarded) rows received before the moment; parked ones stay.</summary>
    Task<int> PurgeResolvedAsync(DateTimeOffset before, CancellationToken ct);
}

/// <summary>
/// Applies the changes a device kept while the server was out of reach (<c>syncChanges</c>), in the order they were made, each through the
/// same service as its single mutation, with the version it was made from: so every rule applies, and a change made from an old version is
/// noticed. What the server cannot apply is parked (with the reason and the change), never lost; what it applied is recorded, so a batch
/// sent again (an answer that never arrived) is answered with what was recorded and applied once. One batch of a user at a time.
/// </summary>
public sealed class SyncService(
    ISyncChangeRepository ledger, IVehicleRepository vehicles, AccessService access, IOptionsSnapshot<SyncOptions> options, TimeProvider clock, AppMetrics metrics,
    ILogger<SyncService> logger)
{
    private static readonly StripedLocks Locks = new();

    public async Task<SyncOutcome> SyncAsync(IReadOnlyList<SyncChangeRequest> changes, CancellationToken ct)
    {
        var me = await access.RequirePrincipalAsync(ct);
        if (changes.Count is 0 or > SyncChange.MaxBatch)
            throw new DomainException("sync.tooManyChanges", $"A batch holds 1 to {SyncChange.MaxBatch} changes.", new { Max = SyncChange.MaxBatch });
        if (changes.GroupBy(c => c.Id).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new DomainException("sync.duplicateChange", "A change is in the batch twice.", new { Id = twice.Key });
        if (changes.FirstOrDefault(c => c.Payload.Length > SyncChange.MaxPayloadLength || c.Base?.Length > SyncChange.MaxPayloadLength) is { } large)
            throw new DomainException("sync.payloadTooLarge", $"A change may hold at most {SyncChange.MaxPayloadLength} characters.", new { large.Id, Max = SyncChange.MaxPayloadLength });

        // A span of the request's (#219), and one per change applied below it: ids, kinds and results, never what a change holds.
        using var batch = AppTraces.Source.StartActivity("tankstat.sync.batch");
        batch?.SetTag("tankstat.sync.changes", changes.Count);
        var gate = Locks.For(me.Id);
        await gate.WaitAsync(ct);
        try
        {
            var known = (await ledger.FindManyAsync([.. changes.Select(c => c.Id)], ct)).ToDictionary(c => c.Id);
            if (known.Values.FirstOrDefault(c => c.SubmittedById != me.Id) is { } taken)
                throw new DomainException("sync.idTaken", $"The id {taken.Id} is already in use.", new { taken.Id });

            var results = new List<SyncChangeResult>(changes.Count);
            var replayed = 0;
            foreach (var change in changes)
            {
                if (known.TryGetValue(change.Id, out var recorded))
                {
                    results.Add(SyncChangeResult.From(recorded)); // sent again: what happened the first time
                    replayed++;
                    metrics.SyncChanges.Add(1, new("tankstat.sync.change.kind", recorded.Kind.ToString()), new("tankstat.sync.change.result", "repeated"));
                    continue;
                }
                using var step = AppTraces.Source.StartActivity("tankstat.sync.change");
                step?.SetTag("tankstat.sync.change.id", change.Id);
                SyncChange row;
                try
                {
                    row = await ApplyAsync(change, me.Id, ct);
                    // Recorded whatever the request does now: what was applied must be in the ledger, or the same change sent again (the device
                    // never heard back) would be applied a second time, or parked as a version mismatch with itself.
                    if (!await ledger.AddAsync(row, CancellationToken.None))
                        row = (await ledger.FindManyAsync([row.Id], CancellationToken.None)).Single(); // another server took the same batch first
                }
                catch (Exception e) when (step.FailedBy(e, ct))
                {
                    throw; // never reached: the filter only marks the span (an exception stops the batch, a refusal parks the change)
                }
                var result = row.Status.ToString().ToLowerInvariant();
                step?.SetTag("tankstat.sync.change.kind", row.Kind.ToString());
                step?.SetTag("tankstat.vehicle.id", row.VehicleId);
                step?.SetTag("tankstat.sync.change.result", result);
                if (row.Status == SyncChangeStatus.Parked) step.Failed(row.ReasonKey ?? "sync.parked"); // the reason's key, never its arguments
                metrics.SyncChanges.Add(1, new("tankstat.sync.change.kind", row.Kind.ToString()), new("tankstat.sync.change.result", result));
                results.Add(SyncChangeResult.From(row));
                logger.LogDebug("Change {ChangeId} ({Kind}) of vehicle {VehicleId}: {Status} {Key}", row.Id, row.Kind, row.VehicleId, row.Status, row.ReasonKey);
            }

            var purged = await ledger.PurgeResolvedAsync(clock.GetUtcNow() - TimeSpan.FromDays(options.Value.RetentionDays), ct);
            var applied = results.Count(r => r.Status == SyncChangeStatus.Applied);
            var parked = results.Count(r => r.Status == SyncChangeStatus.Parked);
            batch?.SetTag("tankstat.sync.applied", applied);
            batch?.SetTag("tankstat.sync.parked", parked);
            batch?.SetTag("tankstat.sync.repeated", replayed);
            logger.LogInformation("User {UserId} synced {Count} changes: {Applied} applied, {Parked} parked, {Replayed} sent again, {Purged} old records removed",
                me.Id, changes.Count, applied, parked, replayed, purged);
            return new SyncOutcome(results, applied, parked);
        }
        catch (Exception e) when (batch.FailedBy(e, ct))
        {
            throw; // never reached: the filter only marks the span (whatever stopped the batch: an id taken, the ledger, a change)
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The parked changes the user may see: those of every vehicle whose logs they may view (everyone who works with the vehicle decides
    /// about them), and those they sent themselves (a vehicle they added, which the server never took, has none). An administrator sees
    /// every vehicle's here, as in every list; the notifications only go to those who work with the vehicle themselves
    /// (<see cref="Notifications.SyncNotificationSync"/>), deliberately.
    /// </summary>
    public async Task<IReadOnlyList<SyncChange>> ListParkedAsync(CancellationToken ct)
    {
        var me = await access.RequirePrincipalAsync(ct);
        return await ledger.ListParkedAsync(await access.LogScopeAsync(AccessLevel.View, ct), me.Id, ct);
    }

    /// <summary>For vehicles the caller already got through an access check (a resolver of the vehicle).</summary>
    public Task<IReadOnlyDictionary<Guid, int>> CountParkedAsync(IReadOnlyCollection<Guid> vehicleIds, CancellationToken ct) => ledger.CountParkedAsync(vehicleIds, ct);

    /// <summary>
    /// Whoever sent it decides about a parked change, and whoever may make the same change: Edit on the vehicle's logs for a change of a log
    /// or a schedule, Edit on the vehicle itself for a change of the vehicle (a log grant does not let anyone rename someone's car).
    /// </summary>
    public Task<bool> CanResolveAsync(SyncChange change, CancellationToken ct) => CanResolveAsync(change.SubmittedById, change.VehicleId, change.Kind, ct);

    public async Task<bool> CanResolveAsync(Guid submitterId, Guid? vehicleId, SyncChangeKind kind, CancellationToken ct)
    {
        var me = await access.RequirePrincipalAsync(ct);
        if (submitterId == me.Id) return true;
        if (vehicleId is not { } id || await vehicles.FindAsync(id, ct) is not { } vehicle) return false;
        var level = IsVehicleChange(kind) ? await access.VehicleLevelAsync(vehicle, ct) : await access.LogLevelAsync(vehicle, ct);
        return level >= AccessLevel.Edit;
    }

    public static bool IsVehicleChange(SyncChangeKind kind) => kind is SyncChangeKind.AddVehicle or SyncChangeKind.UpdateVehicle or SyncChangeKind.DeleteVehicle or SyncChangeKind.RestoreVehicle
        or SyncChangeKind.SetVehiclePicture or SyncChangeKind.RemoveVehiclePicture;

    /// <summary>The parked change, when the caller may see it; anything else looks non-existent, whatever it is.</summary>
    public async Task<SyncChange> FindVisibleAsync(Guid id, CancellationToken ct)
    {
        var me = await access.RequirePrincipalAsync(ct);
        var change = await ledger.FindAsync(id, ct);
        return change is not null && await VisibleAsync(change, me.Id, ct) ? change : throw new NotFoundException("sync.notFound", "Change not found.", new { Id = id });
    }

    /// <summary>A decision taken and not finished within this long (a server that stopped meanwhile) no longer holds the change.</summary>
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A person decides about a parked change: discard it (it is never applied), or apply it anyway, as they would online (every rule and
    /// access check; the version it was made from no longer counts: they saw what is there now). <paramref name="apply"/> applies the
    /// stored change, or the edited one in <paramref name="replacement"/> (stored first, so it is kept if it is refused again). A refusal
    /// keeps it parked with the new reason, and is thrown so the person sees it.
    /// </summary>
    public async Task<SyncChange> ResolveAsync(Guid id, bool discard, Func<CancellationToken, Task<AppliedChange>>? apply, string? replacement, CancellationToken ct)
    {
        var me = await access.RequirePrincipalAsync(ct);
        var change = await FindVisibleAsync(id, ct);
        if (change.Status != SyncChangeStatus.Parked) throw NotParked();
        if (!await CanResolveAsync(change, ct)) throw new ForbiddenException("sync.cannotResolve", "Only whoever sent it, or may change the vehicle's logs, decides about it.");

        // One decision at a time: the change is claimed before anything is applied, so a discard or a second apply at the same moment is
        // refused, never the data changed while the ledger says the change was discarded.
        var now = clock.GetUtcNow();
        if (!await ledger.ClaimAsync(change.Id, me.Id, now, now - ClaimTimeout, ct)) throw NotParked();
        var settled = false;
        async Task SettleAsync()
        {
            settled = true;
            if (!await ledger.SettleParkedAsync(change, me.Id, CancellationToken.None)) throw NotParked(); // the claim ran out and someone took it
        }
        try
        {
            if (discard)
            {
                change.MarkDiscarded(me.Id, now);
                await SettleAsync();
                logger.LogInformation("User {UserId} discarded parked change {ChangeId} of vehicle {VehicleId}", me.Id, change.Id, change.VehicleId);
                return change;
            }
            if (replacement is not null) change.Replace(replacement);
            try
            {
                var applied = await apply!(ct);
                change.MarkApplied(me.Id, now, applied.EntityId, applied.Version);
                await SettleAsync();
                logger.LogInformation("User {UserId} applied parked change {ChangeId} of vehicle {VehicleId}", me.Id, change.Id, change.VehicleId);
                return change;
            }
            catch (KeyedException refused) when (change.SubmittedById != me.Id && refused is NotFoundException or ForbiddenException)
            {
                // Refused for what this person may not do: why the change was parked for the others stays as it was.
                throw;
            }
            catch (KeyedException refused)
            {
                change.ParkAgain(refused.Key, refused.Args.ToDictionary(a => a.Key, a => Convert.ToString(a.Value, CultureInfo.InvariantCulture) ?? ""));
                await SettleAsync(); // parked again, and the claim given back with it
                logger.LogDebug("Parked change {ChangeId} was refused again: {Key}", change.Id, refused.Key);
                throw;
            }
        }
        finally
        {
            if (!settled) await ledger.ReleaseAsync(change.Id, me.Id, CancellationToken.None);
        }
    }

    private static DomainException NotParked() => new("sync.notParked", "This change is not parked: someone has decided about it already.");

    private async Task<bool> VisibleAsync(SyncChange change, Guid me, CancellationToken ct) =>
        change.SubmittedById == me
        || (change.VehicleId is { } id && await vehicles.FindAsync(id, ct) is { } vehicle && await access.LogLevelAsync(vehicle, ct) >= AccessLevel.View);

    /// <summary>Applies one change; a refusal of the server's (a keyed error) parks it with the reason, anything else is unexpected and stops the batch.</summary>
    private async Task<SyncChange> ApplyAsync(SyncChangeRequest change, Guid submitter, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        try
        {
            var applied = await change.Apply(ct);
            var (vehicleId, ownerId) = await FiledUnderAsync(applied.VehicleId ?? change.VehicleIdHint, submitter, ct);
            return SyncChange.Applied(change.Id, ownerId, submitter, vehicleId, applied.EntityId ?? change.TargetId, change.Kind,
                change.ExpectedVersion, change.Payload, now, applied.EntityId, applied.Version);
        }
        catch (KeyedException refused)
        {
            var args = refused.Args.ToDictionary(a => a.Key, a => Convert.ToString(a.Value, CultureInfo.InvariantCulture) ?? "");
            var (vehicleId, ownerId) = await FiledUnderAsync(change.VehicleIdHint ?? (change.VehicleOf is { } find ? await find(ct) : null), submitter, ct);
            return SyncChange.Parked(change.Id, ownerId, submitter, vehicleId, change.TargetId, change.Kind,
                change.ExpectedVersion, change.Payload, now, refused.Key, args, change.Base);
        }
    }

    /// <summary>
    /// The vehicle a change is filed under and its owner (whoever may see the vehicle sees the change). A vehicle that does not exist (purged
    /// meanwhile, or the one an add the server refused would have added) files it with the sender alone: the ledger's vehicle has a foreign
    /// key, and a batch must never fail on a change it parks.
    /// </summary>
    private async Task<(Guid? VehicleId, Guid OwnerId)> FiledUnderAsync(Guid? vehicleId, Guid submitter, CancellationToken ct) =>
        vehicleId is { } id && await vehicles.FindIncludingDeletedAsync(id, ct) is { } vehicle ? (vehicle.Id, vehicle.OwnerId) : (null, submitter);
}
