using System.Text.Unicode;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Text.Json;
using GreenDonut;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tankstat.Application.Access;
using Tankstat.Application.Expenses;
using Tankstat.Application.Images;
using Tankstat.Application.Photos;
using Tankstat.Application.Recurring;
using Tankstat.Application.Refuelings;
using Tankstat.Application.Sync;
using Tankstat.Application.Tyres;
using Tankstat.Application.Vehicles;
using Tankstat.Application;
using Tankstat.Domain.Access;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Photos;
using Tankstat.Domain.Sync;
using Tankstat.Domain.Tyres;
using Tankstat.Domain.Vehicles;
using Tankstat.Domain;

namespace Tankstat.Api.GraphQL;

/// <summary>
/// One change a device kept while the server was out of reach: its own id, the version of what it changes it was made from, and exactly
/// one operation (a log's, a vehicle's or its picture's, a schedule's, or one of the fifteen tyre changes), with the same input as the single mutation (an add carries the id the device gave it). <c>Base</c> is the values the
/// change was made from (JSON in the shape of its input, as the device knew them), kept with it if it is parked so whoever decides can
/// merge it; the server never reads it.
/// </summary>
public sealed record ChangeInput(
    Guid Id, int? ExpectedVersion = null,
    LogRefuelingInput? LogRefueling = null, UpdateRefuelingInput? UpdateRefueling = null, Guid? DeleteRefueling = null, Guid? RestoreRefueling = null,
    AddExpenseInput? AddExpense = null, UpdateExpenseInput? UpdateExpense = null, Guid? DeleteExpense = null, Guid? RestoreExpense = null,
    AddRecurringExpenseInput? AddRecurringExpense = null, UpdateRecurringExpenseInput? UpdateRecurringExpense = null, Guid? DeleteRecurringExpense = null,
    MarkRecurringExpensesDoneInput? MarkRecurringExpensesDone = null,
    AddVehicleInput? AddVehicle = null, UpdateVehicleInput? UpdateVehicle = null, Guid? DeleteVehicle = null, Guid? RestoreVehicle = null,
    AddLogPhotoInput? AddRefuelingPhoto = null, RemoveLogPhotoInput? RemoveRefuelingPhoto = null,
    AddLogPhotoInput? AddExpensePhoto = null, RemoveLogPhotoInput? RemoveExpensePhoto = null,
    SetVehiclePictureInput? SetVehiclePicture = null, Guid? RemoveVehiclePicture = null,
    AddTyreSetInput? AddTyreSet = null, UpdateTyreSetInput? UpdateTyreSet = null, SetTyreRoughDistanceInput? SetTyreRoughDistance = null,
    RetireTyreSetInput? RetireTyreSet = null, Guid? UnretireTyreSet = null, Guid? DeleteTyreSet = null, Guid? RestoreTyreSet = null,
    SwapTyresInput? SwapTyres = null, SwapTyresInput? ChangeTyrePositions = null, AddPastTyreSwapsInput? AddPastTyreSwaps = null,
    AddTyreInstallationInput? AddTyreInstallation = null, UpdateTyreInstallationInput? UpdateTyreInstallation = null, Guid? DeleteTyreInstallation = null, Guid? RestoreTyreInstallation = null,
    AddTyreEntryInput? AddTyreEntry = null, UpdateTyreEntryInput? UpdateTyreEntry = null, DeleteTyreEntryInput? DeleteTyreEntry = null, Guid? RestoreTyreEntry = null,
    BreakUpTyreSetInput? BreakUpTyreSet = null, UpdateTyreInput? UpdateTyre = null, Guid? DeleteTyre = null, Guid? RestoreTyre = null,
    AddTyrePlaceInput? AddTyrePlace = null, UpdateTyrePlaceInput? UpdateTyrePlace = null, Guid? DeleteTyrePlace = null, Guid? RestoreTyrePlace = null,
    string? Base = null);

/// <summary>A photo added to a saved log while offline: uploaded as a draft of the log's vehicle (<c>PUT /media/vehicles/{id}/photo-drafts</c>) just before.</summary>
public sealed record AddLogPhotoInput(Guid LogId, Guid DraftId);

/// <summary>A photo of a saved log removed while offline.</summary>
public sealed record RemoveLogPhotoInput(Guid LogId, Guid ImageId);

/// <summary>A vehicle's picture chosen while offline: uploaded as a draft of the vehicle (<c>PUT /media/vehicles/{id}/photo-drafts</c>) just before.</summary>
public sealed record SetVehiclePictureInput(Guid VehicleId, Guid DraftId);

/// <param name="Changes">In the order they were made (1 to 200).</param>
public sealed record SyncChangesInput(IReadOnlyList<ChangeInput> Changes);

public sealed record SyncReasonArg(string Name, string Value);

/// <summary>Why the server could not apply a change: the same error key (and arguments) as the single mutation would have given.</summary>
public sealed record SyncReasonInfo(string Key, IReadOnlyList<SyncReasonArg> Args);

/// <param name="EntityId">What it added or changed (applied changes).</param>
/// <param name="Version">Its version after the change.</param>
public sealed record SyncChangeResultInfo(Guid Id, SyncChangeStatus Status, Guid? EntityId, int? Version, SyncReasonInfo? Reason);

public sealed record SyncResultInfo(IReadOnlyList<SyncChangeResultInfo> Results, int Applied, int Parked);

/// <summary>
/// A change sent from a device that the server parked (or that someone decided about): what it is, the change itself as the device sent it
/// (`change`, JSON of a <c>ChangeInput</c>), the values it was made from (`base`, JSON, when the device knew them), why it was parked, and
/// who sent it and when.
/// </summary>
public sealed record SyncChangeInfo(
    Guid Id, SyncChangeKind Kind, SyncChangeStatus Status, Guid? VehicleId, Guid? TargetId, int? ExpectedVersion, string Change, DateTimeOffset ReceivedAt,
    SyncReasonInfo? Reason, Guid SubmittedById, DateTimeOffset? ResolvedAt, Guid? EntityId, int? Version, string? Base)
{
    public static SyncChangeInfo From(SyncChange c) => new(
        c.Id, c.Kind, c.Status, c.VehicleId, c.TargetId, c.ExpectedVersion, c.Payload, c.ReceivedAt,
        c.ReasonKey is null ? null : new SyncReasonInfo(c.ReasonKey, c.ReasonArgs.Select(a => new SyncReasonArg(a.Key, a.Value)).ToList()),
        c.SubmittedById, c.ResolvedAt, c.ResultId, c.ResultVersion, c.Base);
}

/// <summary>LIVE: there to be changed. TRASHED: in the trash (it can be restored).</summary>
public enum SyncTargetState
{
    Live,
    Trashed,
}

/// <summary>
/// What a parked change concerns, as it is on the server now (exactly one of the entities is set), with its version and what changed it
/// last: who (<c>changedBy</c>), when and how. A change made from an older version can be merged with it and applied against this version.
/// </summary>
public sealed record SyncCurrent(
    SyncTargetState State, int Version, DateTimeOffset? ChangedAt, Guid? ChangedById, EntityChange? LastChange,
    Vehicle? Vehicle, Refueling? Refueling, Expense? Expense, RecurringExpenseInfo? Schedule, TyreSet? TyreSet = null, TyreInstallation? TyreInstallation = null,
    TyreEntry? TyreEntry = null, Tyre? Tyre = null, TyrePlace? TyrePlace = null)
{
    public static SyncCurrent From(SyncTarget t) => new(
        t.Trashed ? SyncTargetState.Trashed : SyncTargetState.Live, t.Entity.Version, t.Entity.ChangedAt, t.Entity.ChangedById, t.Entity.LastChange,
        t.Entity as Vehicle, t.Entity as Refueling, t.Entity as Expense, t.Schedule is { } s ? RecurringExpenseInfo.From(s) : null,
        t.Entity as TyreSet, t.Entity as TyreInstallation, t.Entity as TyreEntry, t.Entity as Tyre, t.Entity as TyrePlace);
}

[ExtendObjectType<SyncCurrent>]
public sealed class SyncCurrentExtensions
{
    public async Task<UserRef?> GetChangedBy([Parent] SyncCurrent current, UserRefLoader users, CancellationToken ct) =>
        current.ChangedById is { } id ? await users.LoadAsync(id, ct) : null;
}

/// <summary>What the parked changes of a response concern, as it is now: one query per kind of target, however many changes are listed.</summary>
public sealed class SyncCurrentLoader(SyncTargetService targets, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<SyncTargetKey, SyncCurrent?>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<SyncTargetKey, SyncCurrent?>> LoadBatchAsync(IReadOnlyList<SyncTargetKey> keys, CancellationToken ct)
    {
        var found = await targets.CurrentAsync(keys, ct);
        return keys.ToDictionary(k => k, k => found.TryGetValue(k, out var t) ? SyncCurrent.From(t) : null);
    }
}

[ExtendObjectType<SyncChangeInfo>]
public sealed class SyncChangeInfoExtensions
{
    public Task<UserRef?> GetSubmittedBy([Parent] SyncChangeInfo change, UserRefLoader users, CancellationToken ct) => users.LoadAsync(change.SubmittedById, ct);

    /// <summary>
    /// The vehicle's name and units, to show the change by (even a trashed vehicle, or one the sender no longer has access to: they saw it
    /// when they made the change). Null for a vehicle the server never took.
    /// </summary>
    public async Task<SyncVehicleRef?> GetVehicle([Parent] SyncChangeInfo change, VehicleIncludingDeletedLoader vehicles, CancellationToken ct) =>
        change.VehicleId is { } id && await vehicles.LoadAsync(id, ct) is { } v ? new SyncVehicleRef(v.Id, v.Name, v.Units.Distance, v.Units.Volume) : null;

    /// <summary>
    /// What it concerns as it is now, for a change of one thing someone else could have changed meanwhile (an edit, a trash, a restore).
    /// Null when that is gone (purged, a schedule deleted), or the current user may not see it (whoever sent the change included), and for
    /// adds, visits and photos.
    /// </summary>
    public async Task<SyncCurrent?> GetCurrent([Parent] SyncChangeInfo change, SyncCurrentLoader loader, CancellationToken ct) =>
        SyncTargetService.TargetOf(change.Kind) is { } type && change.TargetId is { } id ? await loader.LoadAsync(new SyncTargetKey(type, id), ct) : null;

    /// <summary>
    /// Whether the current user may apply or discard it: whoever sent it, or may make the same change (Edit on the vehicle's logs; on the
    /// vehicle itself for a change of the vehicle).
    /// </summary>
    public async Task<bool> GetCanResolve(
        [Parent] SyncChangeInfo change, [Service] AccessService access, [Service] SyncService sync, LogLevelByVehicleLoader levels, CancellationToken ct)
    {
        if (change.SubmittedById == (await access.RequirePrincipalAsync(ct)).Id) return true;
        if (SyncService.IsVehicleChange(change.Kind)) return await sync.CanResolveAsync(change.SubmittedById, change.VehicleId, change.Kind, ct);
        return change.VehicleId is { } id && await levels.LoadAsync(id, ct) >= AccessLevel.Edit;
    }
}

/// <summary>Just enough of a vehicle to name a change of it; nothing else of the vehicle is shown through a change.</summary>
public sealed record SyncVehicleRef(Guid Id, string Name, DistanceUnit DistanceUnit, VolumeUnit VolumeUnit);

/// <summary>APPLY: apply it anyway (the version it was made from no longer counts). DISCARD: it is never applied.</summary>
public enum SyncResolveAction
{
    Apply,
    Discard,
}

/// <param name="Change">
/// For APPLY: the change as edited before applying (same kind); omit to apply it as it was sent. Its <c>expectedVersion</c> counts (the
/// version the person merged with: changed again meanwhile, it stays parked); without one it is applied over whatever is there.
/// </param>
/// <param name="RestoreFirst">
/// For APPLY of an edit of a log or a vehicle that is in the trash now: restore it (from the change's <c>expectedVersion</c>, when given),
/// then apply the edit; an edit refused after the restore puts it back in the trash.
/// </param>
public sealed record ResolveSyncChangeInput(Guid Id, SyncResolveAction Action, ChangeInput? Change = null, bool RestoreFirst = false);

[ExtendObjectType(OperationTypeNames.Query)]
public sealed class SyncQueries
{
    /// <summary>The changes the server parked that the current user may see: of every vehicle whose logs they may view, and those they sent; newest first.</summary>
    public async Task<IReadOnlyList<SyncChangeInfo>> GetParkedChanges([Service] SyncService sync, CancellationToken ct) =>
        (await sync.ListParkedAsync(ct)).Select(SyncChangeInfo.From).ToList();
}

/// <summary>How many parked changes the vehicles of a response have, one query for all (for a vehicle the resolver already authorised).</summary>
public sealed class ParkedChangeCountLoader(SyncService sync, IBatchScheduler scheduler, DataLoaderOptions options) : BatchDataLoader<Guid, int>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<Guid, int>> LoadBatchAsync(IReadOnlyList<Guid> keys, CancellationToken ct)
    {
        var counts = await sync.CountParkedAsync(keys, ct);
        return keys.ToDictionary(k => k, k => counts.GetValueOrDefault(k));
    }
}

[ExtendObjectType<Vehicle>]
public sealed class VehicleSyncExtensions
{
    /// <summary>How many changes sent from devices the server parked for this vehicle, waiting for someone to decide.</summary>
    public async Task<int> GetParkedChangeCount([Parent] Vehicle vehicle, ParkedChangeCountLoader loader, CancellationToken ct) => await loader.LoadAsync(vehicle.Id, ct);
}

[ExtendObjectType(OperationTypeNames.Mutation)]
public sealed class SyncMutations
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Enums as the API spells them (PETROL, US_GALLONS), so a stored change is a ChangeInput a client can read back as it is.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Letters as they are, not \uXXXX: an accented note of 500 characters would take 3,000 of the ledger's 4,000 otherwise (#288). Still
        // valid JSON, read back the same; quotes, control characters and markup are escaped as before.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>
    /// Applies the changes a device kept while the server was out of reach, in order, each as its single mutation would (every rule and
    /// access check), from the version it was made from. What cannot be applied is parked with the reason, never lost. A change sent again
    /// is answered with what happened the first time.
    /// </summary>
    public async Task<SyncResultInfo> SyncChanges(SyncChangesInput input, [Service] SyncService sync, [Service] SyncChangeServices services, CancellationToken ct)
    {
        var requests = input.Changes.Select(change => Request(change, services)).ToList();
        var outcome = await sync.SyncAsync(requests, ct);
        return new SyncResultInfo(
            outcome.Results.Select(r => new SyncChangeResultInfo(r.Id, r.Status, r.EntityId, r.Version,
                r.ReasonKey is null ? null : new SyncReasonInfo(r.ReasonKey, r.ReasonArgs.Select(a => new SyncReasonArg(a.Key, a.Value)).ToList()))).ToList(),
            outcome.Applied, outcome.Parked);
    }

    /// <summary>
    /// Applies a parked change anyway (as edited, when <c>change</c> is given) or discards it. Applying is the person's own change: every
    /// rule and access check applies, the version it was made from does not (they saw what is there now), and photos waiting since are
    /// not attached (they may be gone). A change that is a photo (of a saved log, or a vehicle's picture) takes the sender's draft, whoever
    /// applies it, while that lasts. A refusal keeps it parked with the new reason and is the error of this mutation.
    /// </summary>
    public async Task<SyncChangeInfo> ResolveSyncChange(
        ResolveSyncChangeInput input, [Service] SyncService sync, [Service] SyncChangeServices services, [Service] ILogger<SyncMutations> logger, CancellationToken ct)
    {
        if (input.Action == SyncResolveAction.Discard) return SyncChangeInfo.From(await sync.ResolveAsync(input.Id, discard: true, null, null, ct));

        // Who may not see it learns nothing of it (not even its kind) from what follows.
        var stored = await sync.FindVisibleAsync(input.Id, ct);
        var change = input.Change ?? JsonSerializer.Deserialize<ChangeInput>(stored.Payload, Json)!;
        // A visit whose expense would only have come from its photos logs none without them: say so instead of moving the schedules alone.
        if (change.MarkRecurringExpensesDone is { ExpenseId: not null, Amount: null, PhotoIds.Count: > 0 })
            throw new DomainException("log.valuesRequired", "Fill in every value. Only a photo that is still being read may leave them empty.");
        if (input.RestoreFirst && stored.Kind is not (SyncChangeKind.UpdateRefueling or SyncChangeKind.UpdateExpense or SyncChangeKind.UpdateVehicle))
            throw new DomainException("sync.cannotRestoreFirst", "Only an edit of a log or a vehicle can restore it first.");
        // The same change (its id, so the ledger row is the same), from now on: the version the person merged with, if they did (else none
        // to keep to), no photos that may be gone. Restored first, the edit follows the restore's version.
        var restoreFrom = input.RestoreFirst ? input.Change?.ExpectedVersion : null;
        change = change with
        {
            Id = stored.Id, ExpectedVersion = input.RestoreFirst ? restoreFrom + 1 : input.Change?.ExpectedVersion, Base = null,
            LogRefueling = change.LogRefueling is { } lr ? lr with { PhotoIds = null } : null,
            AddExpense = change.AddExpense is { } ae ? ae with { PhotoIds = null } : null,
            MarkRecurringExpensesDone = change.MarkRecurringExpensesDone is { } md ? md with { PhotoIds = null } : null,
        };
        var request = Request(change, services, stored.SubmittedById, resolving: true);
        // The same change of the same thing: the ledger row stays filed under what it concerns (found through what it names when the change
        // does not carry its vehicle, as an installation added to a set does).
        var vehicleOf = request.VehicleIdHint ?? (input.Change is not null && request.VehicleOf is { } find ? await find(ct) : null);
        if (request.Kind != stored.Kind || request.TargetId != stored.TargetId || (vehicleOf is { } vehicle && stored.VehicleId is { } filed && vehicle != filed))
            throw new DomainException("sync.kindMismatch", "An edited change does the same as the one parked.");
        var replacement = input.Change is null ? null : JsonSerializer.Serialize(change, Json);
        var apply = input.RestoreFirst ? RestoredFirst(stored.Kind, stored.TargetId!.Value, restoreFrom, request.Apply, services, logger) : request.Apply;
        return SyncChangeInfo.From(await sync.ResolveAsync(input.Id, discard: false, apply, replacement, ct));
    }

    /// <summary>An edit of something in the trash, applied after restoring it; refused after the restore, it goes back to the trash.</summary>
    private static Func<CancellationToken, Task<AppliedChange>> RestoredFirst(
        SyncChangeKind kind, Guid id, int? version, Func<CancellationToken, Task<AppliedChange>> edit, SyncChangeServices services, ILogger logger) => async ct =>
    {
        Func<CancellationToken, Task> trash;
        switch (kind)
        {
            case SyncChangeKind.UpdateRefueling:
                await services.Refuelings.RestoreAsync(id, ct, version);
                trash = c => services.Refuelings.DeleteAsync(id, c);
                break;
            case SyncChangeKind.UpdateExpense:
                await services.Expenses.RestoreAsync(id, ct, version);
                trash = c => services.Expenses.DeleteAsync(id, c);
                break;
            default:
                await services.Vehicles.RestoreAsync(id, ct, version);
                trash = c => services.Vehicles.DeleteAsync(id, c);
                break;
        }
        try
        {
            return await edit(ct);
        }
        catch (KeyedException)
        {
            try
            {
                await trash(CancellationToken.None);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Change {Kind} of {EntityId} was refused after restoring it, and it could not be put back in the trash", kind, id);
            }
            throw;
        }
    };

    /// <summary>A trash of something already in the trash has nothing left to do: applied, as long as the sender may restore it.</summary>
    private static async Task<AppliedChange> TrashOnce(Func<CancellationToken, Task<AppliedChange>> trash, Func<CancellationToken, Task<AppliedChange?>> inTrash, CancellationToken ct)
    {
        try
        {
            return await trash(ct);
        }
        catch (NotFoundException)
        {
            if (await inTrash(ct) is { } already) return already;
            throw;
        }
    }

    /// <summary>
    /// A tyre change (#287), applied through <see cref="TyreService"/> like the mutation. An add names the id of what it creates: the set (and
    /// the installation a fitted set starts with), the installation a swap or a past swap creates (its line takes the same id), the line an
    /// added line is, so a change sent again changes nothing. Its answer is the set, installation or line it concerns, with its version after
    /// the change. Sent from a device's queue, a swap (or a past swap, a change of positions) whose set is on the car already that day just as
    /// asked is a retry's answer; not when someone applies it anyway (<paramref name="resolving"/>), which is never a retry and decides on purpose.
    /// </summary>
    private static SyncChangeRequest? Tyres(
        ChangeInput c, SyncChangeServices services, int? version, bool resolving,
        Func<SyncChangeKind, Guid?, Guid?, Func<CancellationToken, Task<AppliedChange>>, Func<CancellationToken, Task<Guid?>>?, SyncChangeRequest> make)
    {
        var tyres = services.Tyres;
        static Guid Required(Guid? id, Guid change) =>
            id is { } given && given != Guid.Empty ? given : throw new DomainException("sync.entityIdRequired", "An add sent by a device names the id it gave it.", new { Id = change });
        async Task<AppliedChange> SetAnswer(Guid id, CancellationToken ct) =>
            await services.TyreSetRow(id, ct) is { } set ? new AppliedChange(set.Id, set.Version, set.VehicleId) : new AppliedChange(id, null, null);
        async Task<AppliedChange> InstallationAnswer(Guid id, CancellationToken ct) =>
            await services.TyreInstallationRow(id, ct) is { } st ? new AppliedChange(st.Id, st.Version, st.VehicleId) : new AppliedChange(id, null, null);
        async Task<AppliedChange> EntryAnswer(Guid id, CancellationToken ct) =>
            await services.TyreEntryRow(id, ct) is { } e ? new AppliedChange(e.Id, e.Version, e.VehicleId) : new AppliedChange(id, null, null);
        SyncChangeRequest OnEntry(SyncChangeKind kind, Guid id, Func<CancellationToken, Task> apply) =>
            make(kind, id, null, async ct => { await apply(ct); return await EntryAnswer(id, ct); }, services.TyreEntry(id));
        SyncChangeRequest OnSet(SyncChangeKind kind, Guid id, Func<CancellationToken, Task> apply) =>
            make(kind, id, null, async ct => { await apply(ct); return await SetAnswer(id, ct); }, services.TyreSet(id));
        SyncChangeRequest OnInstallation(SyncChangeKind kind, Guid id, Func<CancellationToken, Task> apply) =>
            make(kind, id, null, async ct => { await apply(ct); return await InstallationAnswer(id, ct); }, services.TyreInstallation(id));

        // An amount sent from a device makes an expense: it names that expense's id, so the change sent again makes one (#288).
        TyreFeeInput? Fee(TyreFeeInput? fee) => fee is { Amount: not null } ? fee with { ExpenseId = Required(fee.ExpenseId, c.Id) } : fee;
        if (c.AddTyreSet is { } a)
        {
            var id = Required(a.Id, c.Id);
            var input = a with
            {
                Id = id, InstallationId = a.Fitted ? Required(a.InstallationId, c.Id) : a.InstallationId, Price = Fee(a.Price),
                BoughtEntryId = a.Price is { Amount: not null } ? Required(a.BoughtEntryId, c.Id) : a.BoughtEntryId,
            };
            return make(SyncChangeKind.AddTyreSet, id, a.VehicleId, async ct => { await TyreChanges.Add(input, tyres, ct); return await SetAnswer(id, ct); }, null);
        }
        if (c.UpdateTyreSet is { } u) return OnSet(SyncChangeKind.UpdateTyreSet, u.Id, ct => TyreChanges.Update(u, tyres, ct, version));
        if (c.SetTyreRoughDistance is { } r) return OnSet(SyncChangeKind.SetTyreRoughDistance, r.Id, ct => tyres.SetRoughAsync(r.Id, r.Distance, r.Until, ct, version));
        if (c.RetireTyreSet is { } rt) return OnSet(SyncChangeKind.RetireTyreSet, rt.Id, ct => tyres.RetireAsync(rt.Id, rt.Day, ct, version));
        if (c.UnretireTyreSet is { } ur) return OnSet(SyncChangeKind.UnretireTyreSet, ur, ct => tyres.UnretireAsync(ur, ct, version));
        if (c.DeleteTyreSet is { } ds)
            return make(SyncChangeKind.DeleteTyreSet, ds, null, async ct =>
            {
                try
                {
                    await tyres.TrashSetAsync(ds, ct, version);
                }
                catch (NotFoundException)
                {
                    if (await tyres.SetInTrashAsync(ds, ct) is null) throw; // in the trash already: nothing left to do
                }
                return await SetAnswer(ds, ct);
            }, services.TyreSet(ds));
        if (c.RestoreTyreSet is { } rs) return OnSet(SyncChangeKind.RestoreTyreSet, rs, ct => tyres.RestoreSetAsync(rs, ct, version));

        if (c.SwapTyres is { } sw)
        {
            var id = Required(sw.InstallationId, c.Id);
            return make(SyncChangeKind.SwapTyres, id, sw.VehicleId, async ct =>
            {
                await tyres.SwapAsync(TyreChanges.Swap(sw with { Fee = Fee(sw.Fee) }), ct, id, repeat: !resolving);
                return new AppliedChange(id, null, sw.VehicleId);
            }, null);
        }
        if (c.ChangeTyrePositions is { } cp)
        {
            var id = Required(cp.InstallationId, c.Id);
            return make(SyncChangeKind.ChangeTyrePositions, id, cp.VehicleId, async ct =>
            {
                await tyres.ChangePositionsAsync(TyreChanges.Swap(cp with { Fee = Fee(cp.Fee) }), ct, id, repeat: !resolving);
                return new AppliedChange(id, null, cp.VehicleId);
            }, null);
        }
        if (c.AddPastTyreSwaps is { } ps)
        {
            var rows = ps with { Swaps = [.. ps.Swaps.Select(w => w with { InstallationId = Required(w.InstallationId, c.Id) })] };
            return make(SyncChangeKind.AddPastTyreSwaps, rows.Swaps.FirstOrDefault()?.InstallationId, ps.VehicleId, async ct =>
            {
                await TyreChanges.Past(rows, tyres, ct, repeat: !resolving);
                return new AppliedChange(rows.Swaps.FirstOrDefault()?.InstallationId, null, ps.VehicleId);
            }, null);
        }
        if (c.AddTyreInstallation is { } ast)
        {
            var id = Required(ast.Id, c.Id);
            return make(SyncChangeKind.AddTyreInstallation, id, null, async ct => { await TyreChanges.AddInstallation(ast with { Id = id }, tyres, ct); return await InstallationAnswer(id, ct); },
                services.TyreSet(ast.SetId));
        }
        if (c.UpdateTyreInstallation is { } ust) return OnInstallation(SyncChangeKind.UpdateTyreInstallation, ust.Id, ct => TyreChanges.UpdateInstallation(ust, tyres, ct, version));
        if (c.DeleteTyreInstallation is { } dst)
            return make(SyncChangeKind.DeleteTyreInstallation, dst, null, async ct =>
            {
                try
                {
                    await tyres.TrashInstallationAsync(dst, ct, version);
                }
                catch (NotFoundException)
                {
                    if (await tyres.InstallationInTrashAsync(dst, ct) is null) throw;
                }
                return await InstallationAnswer(dst, ct);
            }, services.TyreInstallation(dst));
        if (c.RestoreTyreInstallation is { } rst) return OnInstallation(SyncChangeKind.RestoreTyreInstallation, rst, ct => tyres.RestoreInstallationAsync(rst, ct, version));
        if (c.AddTyreEntry is { } ae)
        {
            // For single tyres (#289) each line names its id, and a break-up its tyres': the change sent again changes nothing.
            var lines = ae.Tyres?.Select(t => t with { Id = Required(t.Id, c.Id) }).ToList();
            var id = lines is [{ Id: { } first }, ..] ? first : Required(ae.Id, c.Id);
            var input = ae with
            {
                Id = id, Fee = Fee(ae.Fee), Tyres = lines,
                BreakUp = ae.BreakUp is { } b ? b with { Tyres = [.. b.Tyres.Select(t => t with { Id = Required(t.Id, c.Id) })] } : null,
            };
            return make(SyncChangeKind.AddTyreEntry, id, ae.VehicleId, async ct => { await TyreChanges.AddEntry(input, tyres, ct); return await EntryAnswer(id, ct); }, null);
        }
        if (c.UpdateTyreEntry is { } ue) return OnEntry(SyncChangeKind.UpdateTyreEntry, ue.Id, ct => TyreChanges.UpdateEntry(ue with { Fee = Fee(ue.Fee) }, tyres, ct, version));
        if (c.DeleteTyreEntry is { } de)
            return make(SyncChangeKind.DeleteTyreEntry, de.Id, null, async ct =>
            {
                try
                {
                    await tyres.TrashEntryAsync(de.Id, ct, version, de.WithExpense);
                }
                catch (NotFoundException)
                {
                    if (await tyres.EntryInTrashAsync(de.Id, ct) is null) throw; // in the trash already: nothing left to do
                }
                return await EntryAnswer(de.Id, ct);
            }, services.TyreEntry(de.Id));
        if (c.RestoreTyreEntry is { } re) return OnEntry(SyncChangeKind.RestoreTyreEntry, re, ct => tyres.RestoreEntryAsync(re, ct, version));

        // Single tyres (#289): a break-up names its four tyres' ids, an added time on the car its own, so a change sent again changes nothing.
        async Task<AppliedChange> TyreAnswer(Guid id, CancellationToken ct) =>
            await services.TyreRow(id, ct) is { } t ? new AppliedChange(t.Id, t.Version, t.VehicleId) : new AppliedChange(id, null, null);
        async Task<AppliedChange> PlaceAnswer(Guid id, CancellationToken ct) =>
            await services.TyrePlaceRow(id, ct) is { } p ? new AppliedChange(p.Id, p.Version, p.VehicleId) : new AppliedChange(id, null, null);
        SyncChangeRequest OnTyre(SyncChangeKind kind, Guid id, Func<CancellationToken, Task> apply) =>
            make(kind, id, null, async ct => { await apply(ct); return await TyreAnswer(id, ct); }, services.Tyre(id));
        SyncChangeRequest OnPlace(SyncChangeKind kind, Guid id, Func<CancellationToken, Task> apply) =>
            make(kind, id, null, async ct => { await apply(ct); return await PlaceAnswer(id, ct); }, services.TyrePlace(id));
        if (c.BreakUpTyreSet is { } bu)
        {
            var input = bu with { Tyres = [.. bu.Tyres.Select(t => t with { Id = Required(t.Id, c.Id) })] };
            return OnSet(SyncChangeKind.BreakUpTyreSet, bu.SetId, ct => TyreChanges.BreakUp(input, tyres, ct, version));
        }
        if (c.UpdateTyre is { } ut) return OnTyre(SyncChangeKind.UpdateTyre, ut.Id, ct => tyres.UpdateTyreAsync(ut.Id, ut.Label, ct, version));
        if (c.DeleteTyre is { } dt)
            return make(SyncChangeKind.DeleteTyre, dt, null, async ct =>
            {
                try
                {
                    await tyres.TrashTyreAsync(dt, ct, version);
                }
                catch (NotFoundException)
                {
                    if (await tyres.TyreInTrashAsync(dt, ct) is null) throw; // in the trash already: nothing left to do
                }
                return await TyreAnswer(dt, ct);
            }, services.Tyre(dt));
        if (c.RestoreTyre is { } rty) return OnTyre(SyncChangeKind.RestoreTyre, rty, ct => tyres.RestoreTyreAsync(rty, ct, version));
        if (c.AddTyrePlace is { } ap)
        {
            var id = Required(ap.Id, c.Id);
            return make(SyncChangeKind.AddTyrePlace, id, null, async ct => { await TyreChanges.AddPlace(ap with { Id = id }, tyres, ct); return await PlaceAnswer(id, ct); },
                services.Tyre(ap.TyreId));
        }
        if (c.UpdateTyrePlace is { } up) return OnPlace(SyncChangeKind.UpdateTyrePlace, up.Id, ct => TyreChanges.UpdatePlace(up, tyres, ct, version));
        if (c.DeleteTyrePlace is { } dp)
            return make(SyncChangeKind.DeleteTyrePlace, dp, null, async ct =>
            {
                try
                {
                    await tyres.TrashPlaceAsync(dp, ct, version);
                }
                catch (NotFoundException)
                {
                    if (await tyres.PlaceInTrashAsync(dp, ct) is null) throw;
                }
                return await PlaceAnswer(dp, ct);
            }, services.TyrePlace(dp));
        if (c.RestoreTyrePlace is { } rp) return OnPlace(SyncChangeKind.RestoreTyrePlace, rp, ct => tyres.RestorePlaceAsync(rp, ct, version));
        return null;
    }

    /// <param name="uploadedBy">Whose drafts a photo change takes: its sender's (null: the current user, who sends it).</param>
    /// <param name="resolving">Applied anyway from <c>resolveSyncChange</c>: on purpose, never a retry.</param>
    private static SyncChangeRequest Request(ChangeInput c, SyncChangeServices services, Guid? uploadedBy = null, bool resolving = false)
    {
        var (vehicles, refuelings, expenses, recurring, photos, defaults) = (services.Vehicles, services.Refuelings, services.Expenses, services.Recurring, services.Photos, services.Defaults);
        var find = services;
        var set = new object?[]
        {
            c.LogRefueling, c.UpdateRefueling, c.DeleteRefueling, c.RestoreRefueling, c.AddExpense, c.UpdateExpense, c.DeleteExpense, c.RestoreExpense,
            c.AddRecurringExpense, c.UpdateRecurringExpense, c.DeleteRecurringExpense, c.MarkRecurringExpensesDone, c.AddVehicle, c.UpdateVehicle,
            c.DeleteVehicle, c.RestoreVehicle, c.AddRefuelingPhoto, c.RemoveRefuelingPhoto, c.AddExpensePhoto, c.RemoveExpensePhoto,
            c.SetVehiclePicture, c.RemoveVehiclePicture,
            c.AddTyreSet, c.UpdateTyreSet, c.SetTyreRoughDistance, c.RetireTyreSet, c.UnretireTyreSet, c.DeleteTyreSet, c.RestoreTyreSet,
            c.SwapTyres, c.ChangeTyrePositions, c.AddPastTyreSwaps, c.AddTyreInstallation, c.UpdateTyreInstallation, c.DeleteTyreInstallation, c.RestoreTyreInstallation,
            c.AddTyreEntry, c.UpdateTyreEntry, c.DeleteTyreEntry, c.RestoreTyreEntry,
            c.BreakUpTyreSet, c.UpdateTyre, c.DeleteTyre, c.RestoreTyre, c.AddTyrePlace, c.UpdateTyrePlace, c.DeleteTyrePlace, c.RestoreTyrePlace,
        }.Count(o => o is not null);
        if (set != 1) throw new DomainException("sync.oneOperationRequired", "A change carries exactly one operation.", new { c.Id });
        var payload = JsonSerializer.Serialize(c with { Base = null }, Json);
        var version = c.ExpectedVersion;

        static Guid Required(Guid? id, Guid change) =>
            id is { } given && given != Guid.Empty ? given : throw new DomainException("sync.entityIdRequired", "An add sent by a device names the id it gave it.", new { Id = change });
        static AppliedChange Of<T>(T entity, Func<T, Guid> id, Func<T, int> v, Func<T, Guid> vehicle) => new(id(entity), v(entity), vehicle(entity));

        SyncChangeRequest Make(
            SyncChangeKind kind, Guid? target, Guid? vehicleHint, Func<CancellationToken, Task<AppliedChange>> apply, Func<CancellationToken, Task<Guid?>>? vehicleOf = null) =>
            new(c.Id, kind, target, vehicleHint, version, payload, apply, vehicleOf, c.Base);

        if (c.LogRefueling is { } lr)
        {
            var id = Required(lr.Id, c.Id);
            return Make(SyncChangeKind.LogRefueling, id, lr.VehicleId, async ct => Of(await refuelings.LogAsync(lr.VehicleId,
                new RefuelingInput(lr.Date, lr.Volume, lr.TotalCost, lr.Currency ?? defaults.Currency, lr.Odometer, lr.IsFullTank, lr.Note, lr.MissedPreviousFillUp, lr.Discount), ct,
                photoDraftIds: lr.PhotoIds, id: id), r => r.Id, r => r.Version, r => r.VehicleId));
        }
        if (c.UpdateRefueling is { } ur)
            return Make(SyncChangeKind.UpdateRefueling, ur.Id, null, async ct => Of(await refuelings.UpdateAsync(ur.Id,
                new RefuelingInput(ur.Date, ur.Volume, ur.TotalCost, ur.Currency, ur.Odometer, ur.IsFullTank, ur.Note, ur.MissedPreviousFillUp, ur.Discount), ct, version),
                r => r.Id, r => r.Version, r => r.VehicleId), find.Refueling(ur.Id));
        if (c.DeleteRefueling is { } dr)
            return Make(SyncChangeKind.DeleteRefueling, dr, null, ct => TrashOnce(
                async ct => Of(await refuelings.DeleteAsync(dr, ct, version), r => r.Id, r => r.Version, r => r.VehicleId),
                async ct => await refuelings.InTrashAsync(dr, ct) is { } r ? Of(r, r => r.Id, r => r.Version, r => r.VehicleId) : null, ct), find.Refueling(dr));
        if (c.RestoreRefueling is { } rr)
            return Make(SyncChangeKind.RestoreRefueling, rr, null, async ct => Of(await refuelings.RestoreAsync(rr, ct, version), r => r.Id, r => r.Version, r => r.VehicleId), find.Refueling(rr));

        if (c.AddExpense is { } ae)
        {
            var id = Required(ae.Id, c.Id);
            return Make(SyncChangeKind.AddExpense, id, ae.VehicleId, async ct => Of(await expenses.AddAsync(ae.VehicleId,
                new ExpenseInput(ae.Date, ae.Title, ae.Category, ae.Amount, ae.Currency ?? defaults.Currency, ae.Odometer, ae.Note, ae.Discount), ct, ae.PhotoIds, id),
                e => e.Id, e => e.Version, e => e.VehicleId));
        }
        if (c.UpdateExpense is { } ue)
            return Make(SyncChangeKind.UpdateExpense, ue.Id, null, async ct => Of(await expenses.UpdateAsync(ue.Id,
                new ExpenseInput(ue.Date, ue.Title, ue.Category, ue.Amount, ue.Currency, ue.Odometer, ue.Note, ue.Discount), ct, version), e => e.Id, e => e.Version, e => e.VehicleId), find.Expense(ue.Id));
        if (c.DeleteExpense is { } de)
            return Make(SyncChangeKind.DeleteExpense, de, null, ct => TrashOnce(
                async ct => Of(await expenses.DeleteAsync(de, ct, version), e => e.Id, e => e.Version, e => e.VehicleId),
                async ct => await expenses.InTrashAsync(de, ct) is { } e ? Of(e, e => e.Id, e => e.Version, e => e.VehicleId) : null, ct), find.Expense(de));
        if (c.RestoreExpense is { } re)
            return Make(SyncChangeKind.RestoreExpense, re, null, async ct => Of(await expenses.RestoreAsync(re, ct, version), e => e.Id, e => e.Version, e => e.VehicleId), find.Expense(re));

        if (c.AddRecurringExpense is { } ar)
        {
            var id = Required(ar.Id, c.Id);
            return Make(SyncChangeKind.AddRecurringExpense, id, ar.VehicleId, async ct => Of(await recurring.AddAsync(ar.VehicleId,
                new RecurringExpenseInput(ar.Title, ar.Category, ar.Note, ar.Kind, ar.IntervalMonths, ar.IntervalDistance, ar.LastDoneDate, ar.LastDoneOdometer, ar.WarnDays, ar.WarnDistance),
                ct, id), i => i.Item.Id, i => i.Item.Version, i => i.Item.VehicleId));
        }
        if (c.UpdateRecurringExpense is { } urc)
            return Make(SyncChangeKind.UpdateRecurringExpense, urc.Id, null, async ct => Of(await recurring.UpdateAsync(urc.Id,
                new RecurringExpenseInput(urc.Title, urc.Category, urc.Note, urc.Kind, urc.IntervalMonths, urc.IntervalDistance, urc.LastDoneDate, urc.LastDoneOdometer, urc.WarnDays, urc.WarnDistance),
                ct, version), i => i.Item.Id, i => i.Item.Version, i => i.Item.VehicleId), find.Schedule(urc.Id));
        if (c.DeleteRecurringExpense is { } drc)
            return Make(SyncChangeKind.DeleteRecurringExpense, drc, null, async ct =>
            {
                await recurring.DeleteAsync(drc, ct, version);
                return new AppliedChange(drc, null, null);
            }, find.Schedule(drc));
        if (c.MarkRecurringExpensesDone is { } md)
            return Make(SyncChangeKind.MarkRecurringExpensesDone, md.ExpenseId, null, async ct =>
            {
                var done = await recurring.MarkDoneAsync(md.Ids,
                    new MarkDoneInput(md.Date, md.Odometer, md.Amount, md.Currency ?? defaults.Currency, md.Title, md.Category, md.PhotoIds, md.ExpenseId, md.Discount), ct);
                return new AppliedChange(done.Expense?.Id, done.Expense?.Version, done.Schedules.FirstOrDefault()?.Item.VehicleId);
            }, md.Ids.Count > 0 ? find.Schedule(md.Ids[0]) : null);

        if (c.AddVehicle is { } av)
        {
            var id = Required(av.Id, c.Id);
            return Make(SyncChangeKind.AddVehicle, id, id, async ct => Of(await vehicles.AddAsync(av.Name, av.LicensePlate, av.FuelType,
                av.Units?.ToDomain() ?? MeasurementUnits.Create(defaults.DistanceUnit, defaults.VolumeUnit), ct, id), v => v.Id, v => v.Version, v => v.Id));
        }
        if (c.UpdateVehicle is { } uv)
            return Make(SyncChangeKind.UpdateVehicle, uv.Id, uv.Id, async ct => Of(await vehicles.UpdateAsync(uv.Id, uv.Name, uv.LicensePlate, uv.FuelType, uv.Units?.ToDomain(), ct, version),
                v => v.Id, v => v.Version, v => v.Id));
        if (c.DeleteVehicle is { } dv)
            return Make(SyncChangeKind.DeleteVehicle, dv, dv, ct => TrashOnce(
                async ct => Of(await vehicles.DeleteAsync(dv, ct, version), v => v.Id, v => v.Version, v => v.Id),
                async ct => await vehicles.InTrashAsync(dv, ct) is { } v ? Of(v, v => v.Id, v => v.Version, v => v.Id) : null, ct));
        // Photos of saved logs: the log's version does not move (photo rows are not part of it), so nothing is checked against one.
        SyncChangeRequest AddPhoto(SyncChangeKind kind, LogType type, AddLogPhotoInput a, Func<Guid, Func<CancellationToken, Task<Guid?>>> vehicleOf) =>
            Make(kind, a.LogId, null, async ct => new AppliedChange(await photos.AttachDraftAsync(type, a.LogId, a.DraftId, ct, uploadedBy), null, await vehicleOf(a.LogId)(ct)), vehicleOf(a.LogId));
        SyncChangeRequest RemovePhoto(SyncChangeKind kind, LogType type, RemoveLogPhotoInput r, Func<Guid, Func<CancellationToken, Task<Guid?>>> vehicleOf) =>
            Make(kind, r.LogId, null, async ct =>
            {
                await photos.RemoveAsync(type, r.LogId, r.ImageId, ct);
                return new AppliedChange(r.ImageId, null, await vehicleOf(r.LogId)(ct));
            }, vehicleOf(r.LogId));
        if (c.AddRefuelingPhoto is { } arp) return AddPhoto(SyncChangeKind.AddRefuelingPhoto, LogType.Refueling, arp, find.Refueling);
        if (c.RemoveRefuelingPhoto is { } rrp) return RemovePhoto(SyncChangeKind.RemoveRefuelingPhoto, LogType.Refueling, rrp, find.Refueling);
        if (c.AddExpensePhoto is { } aep) return AddPhoto(SyncChangeKind.AddExpensePhoto, LogType.Expense, aep, find.Expense);
        if (c.RemoveExpensePhoto is { } rep) return RemovePhoto(SyncChangeKind.RemoveExpensePhoto, LogType.Expense, rep, find.Expense);

        // A vehicle's picture is not versioned (the last one wins, as online): nothing is checked against a version.
        if (c.SetVehiclePicture is { } svp)
            return Make(SyncChangeKind.SetVehiclePicture, svp.VehicleId, svp.VehicleId, async ct =>
                new AppliedChange(await services.Images.SetVehiclePictureFromDraftAsync(svp.VehicleId, svp.DraftId, ct, uploadedBy), null, svp.VehicleId));
        if (c.RemoveVehiclePicture is { } rvp)
            return Make(SyncChangeKind.RemoveVehiclePicture, rvp, rvp, async ct =>
            {
                await services.Images.RemoveVehiclePictureAsync(rvp, ct);
                return new AppliedChange(rvp, null, rvp);
            });

        if (Tyres(c, services, version, resolving, Make) is { } tyreChange) return tyreChange;

        var rv = c.RestoreVehicle!.Value;
        return Make(SyncChangeKind.RestoreVehicle, rv, rv, async ct => Of(await vehicles.RestoreAsync(rv, ct, version), v => v.Id, v => v.Version, v => v.Id));
    }
}

/// <summary>
/// What a change from a device is applied through: the services of the single mutations, and the repositories that find the vehicle of
/// what a change concerns, whoever may see it (a parked change is filed with it).
/// </summary>
public sealed class SyncChangeServices(
    VehicleService vehicles, RefuelingService refuelings, ExpenseService expenses, RecurringExpenseService recurring, LogPhotoService photos, ImageService images,
    IOptionsSnapshot<VehicleDefaultsOptions> defaults, IRefuelingRepository refuelingRows, IExpenseRepository expenseRows, IRecurringExpenseRepository scheduleRows,
    TyreService tyres, ITyreRepository tyreRows)
{
    public TyreService Tyres => tyres;
    public Func<CancellationToken, Task<Guid?>> TyreSet(Guid id) => async ct => (await tyreRows.FindSetAsync(id, ct))?.VehicleId;
    public Func<CancellationToken, Task<Guid?>> TyreInstallation(Guid id) => async ct => (await tyreRows.FindInstallationAsync(id, ct))?.VehicleId;
    public Func<CancellationToken, Task<Guid?>> TyreEntry(Guid id) => async ct => (await tyreRows.FindEntryAsync(id, ct))?.VehicleId;
    public Func<CancellationToken, Task<Guid?>> Tyre(Guid id) => async ct => (await TyreRow(id, ct))?.VehicleId;
    public Func<CancellationToken, Task<Guid?>> TyrePlace(Guid id) => async ct => (await TyrePlaceRow(id, ct))?.VehicleId;

    public async Task<Tyre?> TyreRow(Guid id, CancellationToken ct) => (await tyreRows.ListTyresAsync([id], ct)).FirstOrDefault();

    public async Task<TyrePlace?> TyrePlaceRow(Guid id, CancellationToken ct) => (await tyreRows.ListPlacesAsync([id], ct)).FirstOrDefault();

    /// <summary>A set's or installation's version after a change was applied (the change itself checked the access).</summary>
    public Task<TyreSet?> TyreSetRow(Guid id, CancellationToken ct) => tyreRows.FindSetAsync(id, ct);

    public Task<TyreInstallation?> TyreInstallationRow(Guid id, CancellationToken ct) => tyreRows.FindInstallationAsync(id, ct);

    public Task<TyreEntry?> TyreEntryRow(Guid id, CancellationToken ct) => tyreRows.FindEntryAsync(id, ct);

    public ImageService Images => images;
    public VehicleService Vehicles => vehicles;
    public RefuelingService Refuelings => refuelings;
    public ExpenseService Expenses => expenses;
    public RecurringExpenseService Recurring => recurring;
    public LogPhotoService Photos => photos;
    public VehicleDefaultsOptions Defaults => defaults.Value;

    public Func<CancellationToken, Task<Guid?>> Refueling(Guid id) => async ct => (await refuelingRows.FindIncludingDeletedAsync(id, ct))?.VehicleId;
    public Func<CancellationToken, Task<Guid?>> Expense(Guid id) => async ct => (await expenseRows.FindIncludingDeletedAsync(id, ct))?.VehicleId;
    public Func<CancellationToken, Task<Guid?>> Schedule(Guid id) => async ct => (await scheduleRows.FindAsync(id, ct))?.VehicleId;
}
