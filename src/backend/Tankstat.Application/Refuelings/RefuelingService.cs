using Microsoft.Extensions.Logging;
using Tankstat.Application.Access;
using Tankstat.Application.Auth;
using Tankstat.Application.Odometers;
using Tankstat.Application.Photos;
using Tankstat.Application.Recognition;
using Tankstat.Application.Sync;
using Tankstat.Application.Vehicles;
using Tankstat.Domain;
using Tankstat.Domain.Access;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Photos;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.Refuelings;

/// <summary>What a new log can start from: the vehicle's latest odometer reading and the currency of its latest log.</summary>
public sealed record RefuelingDefaults(long? LastOdometer, DateOnly? LastDate, string? LastCurrency);

/// <param name="Currency">Required with a total when logging; when updating, omit it to keep the log's currency.</param>
/// <param name="Volume">Volume, total and odometer may be null only while a photo of the log is still being read: it fills them in later.</param>
/// <param name="MissedPreviousFillUp">A fill-up before this one was not logged (see <see cref="Refueling.MissedPreviousFillUp"/>); when updating, omit it to keep what the log says.</param>
/// <param name="Discount">
/// What was taken off <paramref name="TotalCost"/> at the till (see <see cref="Cost.Discount"/>); refused without a total. On an update null keeps
/// the log's (a build from before discounts sends none) and 0 is none.
/// </param>
public sealed record RefuelingInput(
    DateOnly Date, decimal? Volume, decimal? TotalCost, string? Currency, long? Odometer, bool IsFullTank, string? Note, bool? MissedPreviousFillUp = null,
    decimal? Discount = null);

/// <summary>
/// Refuelling logs of a vehicle. Access is the one defined for a vehicle's logs: the owner-based access (owner,
/// administrators, owner-wide grants, instance default) or a grant on that vehicle's logs, whichever is more. Edit may
/// add, change, trash and restore; only Delete may delete permanently.
/// </summary>
public sealed class RefuelingService(
    IVehicleRepository vehicles, LogAccessGuard guard, IRefuelingRepository refuelings, AccessService access, OdometerService odometer, LogPhotoService photos,
    LogPhotoFiller filler, TimeProvider clock, ILogger<RefuelingService> logger)
{
    /// <summary>Logs may be dated today in any time zone, but not further ahead.</summary>
    private DateOnly LatestAllowedDate => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(1);

    public async Task<IReadOnlyList<Refueling>> ListAsync(Guid vehicleId, RefuelingQuery query, CancellationToken ct) =>
        await VisibleVehicleAsync(vehicleId, ct) is null ? [] : await refuelings.ListForVehicleAsync(vehicleId, query.Normalized(), ct);

    public async Task<int> CountAsync(Guid vehicleId, CancellationToken ct) =>
        await VisibleVehicleAsync(vehicleId, ct) is null ? 0 : await refuelings.CountForVehicleAsync(vehicleId, ct);

    /// <summary>
    /// <see cref="CountAsync"/> for many vehicles in a fixed number of queries (#311: a list of vehicles shows every row's count): 0 for a
    /// vehicle that is gone, in the trash or not visible, as one by one.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, int>> CountsAsync(IReadOnlyCollection<Guid> vehicleIds, CancellationToken ct)
    {
        var visible = await guard.ForVehiclesAsync(vehicleIds, ct);
        var counts = await refuelings.CountForVehiclesAsync([.. visible.Keys], ct);
        return vehicleIds.Distinct().ToDictionary(id => id, id => counts.GetValueOrDefault(id));
    }

    /// <summary>Suggestions for the add-log form; null when the vehicle is not visible.</summary>
    public async Task<RefuelingDefaults?> DefaultsAsync(Guid vehicleId, CancellationToken ct)
    {
        if (await VisibleVehicleAsync(vehicleId, ct) is null) return null;
        var latest = await odometer.LatestAsync(vehicleId, ct);
        var last = (await refuelings.ListForVehicleAsync(vehicleId, new RefuelingQuery(RefuelingSortField.Date, SortDirection.Desc, 0, 1), ct)).FirstOrDefault();
        return new RefuelingDefaults(latest?.Value, latest?.Date, last?.Currency);
    }

    /// <summary>Null when the log does not exist or the user may not see it.</summary>
    public async Task<Refueling?> FindAsync(Guid id, CancellationToken ct)
    {
        var refueling = await refuelings.FindAsync(id, ct);
        return refueling is not null && await VisibleVehicleAsync(refueling.VehicleId, ct) is not null ? refueling : null;
    }

    /// <param name="recalculateConsumption">Set to false when adding many logs in a row (an import) and call <see cref="RecalculateConsumptionAsync"/> once at the end.</param>
    /// <param name="photoDraftIds">Photos the user uploaded for this log before saving it (see <see cref="PhotoDraftService"/>).</param>
    /// <param name="id">The id the client chose; a log it already created with this id is answered as it is (see <see cref="ClientIds"/>).</param>
    public async Task<Refueling> LogAsync(
        Guid vehicleId, RefuelingInput input, CancellationToken ct, bool recalculateConsumption = true, IReadOnlyCollection<Guid>? photoDraftIds = null,
        Guid? id = null)
    {
        var (vehicle, _) = await EditableVehicleAsync(vehicleId, ct);
        var creator = await access.RequirePrincipalAsync(ct);
        // Before any rule: what the first try saved may itself be a neighbour of this log now.
        if (id is { } given && await refuelings.FindIncludingDeletedAsync(given, ct) is { } existing)
            return await FinishRepeatedAsync(Repeated(existing, vehicle.Id, creator.Id), photoDraftIds, recalculateConsumption, ct);
        await ValidateAsync(vehicle.Id, input, exceptReadingId: null, ct);
        var drafts = await photos.RequireDraftsAsync(vehicle.Id, photoDraftIds, ct);
        var readingPhotos = IsIncomplete(input) && await filler.MayWaitForDraftsAsync([.. drafts.Select(d => d.Id)], ct);

        var reading = input.Odometer is { } value ? OdometerReading.Create(vehicle.OwnerId, vehicle.Id, input.Date, value) : null;
        Cost.RequireAmountForDiscount(input.TotalCost, input.Discount);
        var cost = input.TotalCost is { } amount ? Cost.Create(vehicle.OwnerId, vehicle.Id, input.Date, amount, input.Currency, input.Discount) : null;
        var refueling = Refueling.Create(
            vehicle.OwnerId, creator.Id, vehicle.Id, input.Date, input.Volume, cost, reading, input.IsFullTank, input.MissedPreviousFillUp ?? false, input.Note, readingPhotos, id);
        if (!await refuelings.AddAsync(refueling, ct)) // the same add, sent twice at once: the other one saved it
            return Repeated(await refuelings.FindIncludingDeletedAsync(refueling.Id, ct) ?? refueling, vehicle.Id, creator.Id);
        await photos.AttachDraftsAsync(LogType.Refueling, refueling.Id, drafts, ct);
        // Readings that finished before the save are taken now; a draft that could not be attached leaves nothing to wait for.
        if (readingPhotos) await filler.FillAsync(LogType.Refueling, refueling.Id, ct);
        if (recalculateConsumption) await RecalculateConsumptionAsync(vehicle.Id, ct);
        logger.LogDebug("User {UserId} logged refueling {RefuelingId} for vehicle {VehicleId} with {Photos} photos", creator.Id, refueling.Id, vehicle.Id, drafts.Count);
        return await refuelings.FindAsync(refueling.Id, ct) ?? refueling;
    }

    /// <param name="expectedVersion">The version the change was made from; refused when the log was saved since (see <see cref="VersionCheck"/>).</param>
    public async Task<Refueling> UpdateAsync(Guid id, RefuelingInput input, CancellationToken ct, int? expectedVersion = null)
    {
        var refueling = await EditableLogAsync(id, includeDeleted: false, ct);
        VersionCheck.Require(expectedVersion, refueling.Version);
        await ValidateAsync(refueling.VehicleId, input, exceptReadingId: refueling.OdometerReadingId, ct);
        var readingPhotos = IsIncomplete(input) && await filler.MayWaitForLogPhotosAsync(LogType.Refueling, id, ct);
        var waited = refueling.ReviewState;

        var changes = refueling.Update(
            input.Date, input.Volume, input.TotalCost, input.Currency ?? refueling.Currency, input.Odometer, input.IsFullTank,
            input.MissedPreviousFillUp ?? refueling.MissedPreviousFillUp, input.Note, readingPhotos, (await access.RequirePrincipalAsync(ct)).Id,
            input.Discount ?? refueling.Discount); // left out is kept (a build from before discounts knows none), 0 is none
        await refuelings.UpdateAsync(refueling, changes, ct);
        // Readings that finished before the save are taken now (the worker's round may have passed while the log did not wait yet).
        if (refueling.ReviewState == ReviewState.AwaitingPhotos) await filler.FillAsync(LogType.Refueling, id, ct);
        await RecalculateConsumptionAsync(refueling.VehicleId, ct);
        logger.LogDebug("Refueling {RefuelingId} of vehicle {VehicleId} updated", id, refueling.VehicleId);
        if (waited != ReviewState.None && refueling.ReviewState == ReviewState.None)
            await filler.ReviewedAsync(LogType.Refueling, id, refueling.CreatedById, ct);
        return await refuelings.FindAsync(id, ct) ?? refueling;
    }

    /// <summary>Moves the log to the trash; it can be restored until it is deleted permanently.</summary>
    public async Task<Refueling> DeleteAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var refueling = await EditableLogAsync(id, includeDeleted: false, ct);
        VersionCheck.Require(expectedVersion, refueling.Version);
        refueling.MarkDeleted(clock.GetUtcNow(), (await access.RequirePrincipalAsync(ct)).Id);
        await refuelings.UpdateAsync(refueling, LinkedChanges.None, ct);
        await RecalculateConsumptionAsync(refueling.VehicleId, ct); // the neighbours' fill-up intervals change
        logger.LogDebug("Refueling {RefuelingId} of vehicle {VehicleId} moved to the trash", id, refueling.VehicleId);
        return refueling;
    }

    /// <summary>
    /// The log when it is in the trash and the user may edit it (restore it), else null: a trash a device sends for what is already in the
    /// trash has nothing left to do.
    /// </summary>
    public async Task<Refueling?> InTrashAsync(Guid id, CancellationToken ct) =>
        await refuelings.FindIncludingDeletedAsync(id, ct) is { IsDeleted: true } refueling
        && await guard.ForVehicleAsync(refueling.VehicleId, ct) is { Level: >= AccessLevel.Edit } ? refueling : null;

    public async Task<Refueling> RestoreAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var refueling = await EditableLogAsync(id, includeDeleted: true, ct);
        VersionCheck.Require(expectedVersion, refueling.Version);
        await ValidateAsync(refueling.VehicleId, new RefuelingInput(refueling.Date, refueling.Volume, refueling.TotalCost, refueling.Currency, refueling.Odometer, refueling.IsFullTank, refueling.Note), exceptReadingId: null, ct);
        refueling.Restore((await access.RequirePrincipalAsync(ct)).Id);
        await refuelings.UpdateAsync(refueling, LinkedChanges.None, ct);
        // Its photos may have been read while it was in the trash.
        if (refueling.ReviewState == ReviewState.AwaitingPhotos) await filler.FillAsync(LogType.Refueling, id, ct);
        await RecalculateConsumptionAsync(refueling.VehicleId, ct);
        logger.LogDebug("Refueling {RefuelingId} of vehicle {VehicleId} restored from the trash", id, refueling.VehicleId);
        return await refuelings.FindAsync(id, ct) ?? refueling;
    }

    /// <summary>
    /// Refreshes the stored consumption of every log of the vehicle (see <see cref="ConsumptionCalculator"/>) and saves only the ones that
    /// changed. Runs whenever a log is added, changed, trashed or restored, because a change moves the boundaries between full fill-ups.
    /// </summary>
    public async Task RecalculateConsumptionAsync(Guid vehicleId, CancellationToken ct)
    {
        var logs = await refuelings.ListAllForVehicleAsync(vehicleId, ct);
        var changed = ConsumptionCalculator.Apply(logs);
        await refuelings.SaveConsumptionsAsync(changed, ct);
        logger.LogDebug("Consumption of vehicle {VehicleId} recalculated: {Changed} of {Total} refuelings changed", vehicleId, changed.Count, logs.Count);
    }

    /// <summary>Trashed logs the user could restore.</summary>
    public async Task<IReadOnlyList<Refueling>> ListTrashAsync(RefuelingQuery query, CancellationToken ct) =>
        await refuelings.ListDeletedAsync(await access.LogScopeAsync(AccessLevel.Edit, ct), query.Normalized(), ct);

    public async Task<int> CountTrashAsync(CancellationToken ct) =>
        await refuelings.CountDeletedAsync(await access.LogScopeAsync(AccessLevel.Edit, ct), ct);

    /// <summary>How many trashed logs the user may delete permanently.</summary>
    public async Task<int> CountDeletableTrashAsync(CancellationToken ct) =>
        await refuelings.CountDeletedAsync(await access.LogScopeAsync(AccessLevel.Delete, ct), ct);

    /// <summary>Permanently removes the trashed logs the user has Delete access to. Returns how many were removed.</summary>
    public async Task<int> EmptyTrashAsync(CancellationToken ct)
    {
        var user = await access.RequirePrincipalAsync(ct);
        var purged = await refuelings.PurgeAsync(await access.LogScopeAsync(AccessLevel.Delete, ct), ct);
        await photos.DeleteFilesAsync(LogType.Refueling, purged, ct); // their photos go with them
        if (purged.Count > 0) logger.LogInformation("User {UserId} emptied the refueling trash: {Count} refuelings deleted for good ({WithPhotos} with photos)", user.Id, purged.Count, purged.WithPhotos.Count);
        return purged.Count;
    }

    /// <summary>What the current user may do with a vehicle's logs (the UI shows the matching buttons).</summary>
    public async Task<AccessLevel> LevelForVehicleAsync(Guid vehicleId, CancellationToken ct)
    {
        var vehicle = await vehicles.FindAsync(vehicleId, ct) ?? await vehicles.FindIncludingDeletedAsync(vehicleId, ct);
        return vehicle is null ? AccessLevel.None : await access.LogLevelAsync(vehicle, ct);
    }

    /// <summary><see cref="LevelForVehicleAsync"/> for many vehicles (trashed ones too) in a fixed number of queries; unknown ones get None.</summary>
    public async Task<IReadOnlyDictionary<Guid, AccessLevel>> LevelsForVehiclesAsync(IReadOnlyCollection<Guid> vehicleIds, CancellationToken ct)
    {
        var found = await access.LogLevelsAsync(await vehicles.ListByIdsIncludingDeletedAsync(vehicleIds, ct), ct);
        return vehicleIds.Distinct().ToDictionary(id => id, id => found.GetValueOrDefault(id, AccessLevel.None));
    }

    /// <summary>
    /// The add came again: the first try saved the log but may have failed after that (a lost connection to the database while attaching
    /// the photos or recalculating), and the caller cannot tell. The steps after the save are safe to repeat, so they run again: the drafts
    /// still waiting are attached, a reading that finished is taken, and the consumption is recalculated. A log in the trash stays as it is.
    /// The concurrent twin (<c>AddAsync</c> lost the race) does not do this: the request that won is doing it.
    /// </summary>
    private async Task<Refueling> FinishRepeatedAsync(Refueling existing, IReadOnlyCollection<Guid>? photoDraftIds, bool recalculateConsumption, CancellationToken ct)
    {
        if (existing.IsDeleted) return existing;
        await photos.AttachDraftsAsync(LogType.Refueling, existing.Id, await photos.DraftsStillWaitingAsync(existing.VehicleId, photoDraftIds, ct), ct);
        if (existing.ReviewState == ReviewState.AwaitingPhotos) await filler.FillAsync(LogType.Refueling, existing.Id, ct);
        if (recalculateConsumption) await RecalculateConsumptionAsync(existing.VehicleId, ct);
        return await refuelings.FindAsync(existing.Id, ct) ?? existing;
    }

    /// <summary>A log this add already created (same vehicle, same creator) is the answer; another one's id is refused.</summary>
    private Refueling Repeated(Refueling existing, Guid vehicleId, Guid creatorId)
    {
        var same = existing.VehicleId == vehicleId && existing.CreatedById == creatorId;
        if (same) logger.LogDebug("Refueling {RefuelingId} of vehicle {VehicleId} was already logged; the add came again", existing.Id, vehicleId);
        return ClientIds.Repeat(existing, same, existing.Id);
    }

    private async Task<Vehicle?> VisibleVehicleAsync(Guid vehicleId, CancellationToken ct) => (await guard.ForVehicleAsync(vehicleId, ct))?.Vehicle;

    /// <summary>A visible vehicle the user may add logs to; others look missing, view-only users are forbidden.</summary>
    private async Task<(Vehicle Vehicle, AccessLevel Level)> EditableVehicleAsync(Guid vehicleId, CancellationToken ct)
    {
        var context = LogAccessGuard.RequireEdit(await guard.ForVehicleAsync(vehicleId, ct),
            () => new NotFoundException("vehicle.notFound", $"Vehicle {vehicleId} does not exist.", new { Id = vehicleId }));
        return (context.Vehicle, context.Level);
    }

    private async Task<Refueling> EditableLogAsync(Guid id, bool includeDeleted, CancellationToken ct)
    {
        var refueling = includeDeleted ? await refuelings.FindIncludingDeletedAsync(id, ct) : await refuelings.FindAsync(id, ct);
        LogAccessGuard.RequireEdit(await guard.ForVehicleAsync(refueling?.VehicleId, ct),
            () => new NotFoundException("refueling.notFound", $"Refuelling {id} does not exist.", new { Id = id }));
        return refueling!;
    }

    private async Task ValidateAsync(Guid vehicleId, RefuelingInput input, Guid? exceptReadingId, CancellationToken ct)
    {
        if (input.Date > LatestAllowedDate) throw new DomainException("refueling.dateInFuture", "The date cannot be in the future.");
        if (input.Odometer is { } value) await odometer.ValidateAsync(vehicleId, input.Date, value, exceptReadingId, ct);
    }

    private static bool IsIncomplete(RefuelingInput input) => input.Odometer is null || input.Volume is null || input.TotalCost is null;
}
