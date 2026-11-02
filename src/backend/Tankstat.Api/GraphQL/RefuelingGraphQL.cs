using Microsoft.Extensions.Options;
using Tankstat.Application.Access;
using Tankstat.Application.Refuelings;
using Tankstat.Application.Sharing;
using Tankstat.Application.Users;
using Tankstat.Application.Vehicles;
using Tankstat.Domain.Access;
using Tankstat.Domain.Vehicles;
using Tankstat.Domain;

namespace Tankstat.Api.GraphQL;

/// <param name="Volume">Volume, total cost and odometer may be omitted only while one of <paramref name="PhotoIds"/> is still being read: the reading fills them in later (<c>reviewState</c>).</param>
/// <param name="Currency">ISO 4217 code of the currency paid in; omit to use the instance default.</param>
/// <param name="PhotoIds">Photos uploaded for this log beforehand (<c>PUT /media/vehicles/{id}/photo-drafts</c>); they become its photos.</param>
/// <param name="MissedPreviousFillUp">A fill-up before this one was not logged: no consumption is worked out across the gap. Omit for false.</param>
/// <param name="Id">An id the client chose for the new log; the same add sent again (a lost answer, a replay after being offline) answers with what it created. Omit to let the server choose.</param>
/// <param name="Discount">What was taken off the total at the till (a loyalty or fuel-card discount); <c>totalCost</c> is what was paid after it. Omit for none; refused without a total.</param>
public sealed record LogRefuelingInput(
    Guid VehicleId, DateOnly Date, decimal? Volume, decimal? TotalCost, string? Currency, long? Odometer, bool IsFullTank, string? Note, IReadOnlyList<Guid>? PhotoIds = null,
    bool? MissedPreviousFillUp = null, Guid? Id = null, decimal? Discount = null);

/// <param name="Volume">Volume, total cost and odometer may be omitted only while a photo of the log is still being read.</param>
/// <param name="Currency">Omit to keep the log's currency.</param>
/// <param name="MissedPreviousFillUp">Omit to keep what the log says.</param>
/// <param name="Discount">What was taken off the total at the till; omit (or null) to keep the log's, like <c>missedPreviousFillUp</c>, and send 0 for none.</param>
public sealed record UpdateRefuelingInput(
    Guid Id, DateOnly Date, decimal? Volume, decimal? TotalCost, string? Currency, long? Odometer, bool IsFullTank, string? Note, bool? MissedPreviousFillUp = null,
    decimal? Discount = null);
public sealed record SetLogAccessInput(Guid VehicleId, Guid UserId, AccessLevel Level);

/// <summary>What the add-log form starts from.</summary>
public sealed record LogDefaults(long? LastOdometer, DateOnly? LastDate, string Currency);

public sealed record LogAccessGrantInfo(UserRef User, AccessLevel Level);

/// <summary>A value of a log that a photo can provide.</summary>
public enum LogValue
{
    Odometer,
    Volume,
    Total,
    Discount,
}

public static class LogValueList
{
    /// <summary>The flags as a list, for clients.</summary>
    public static IReadOnlyList<LogValue> Of(LogValues values) =>
        [.. new[] { (LogValues.Odometer, LogValue.Odometer), (LogValues.Volume, LogValue.Volume), (LogValues.Total, LogValue.Total), (LogValues.Discount, LogValue.Discount) }
            .Where(v => values.HasFlag(v.Item1)).Select(v => v.Item2)];
}

/// <summary>The refuelling type exposes its odometer as a plain number; the linked reading stays an internal detail.</summary>
public sealed class RefuelingType : ObjectType<Refueling>
{
    protected override void Configure(IObjectTypeDescriptor<Refueling> descriptor)
    {
        descriptor.Ignore(r => r.OdometerReading);
        descriptor.Ignore(r => r.SavedVersion); // the persistence layer's, not a client's
        descriptor.Ignore(r => r.OdometerReadingId);
        descriptor.Ignore(r => r.Cost);
        descriptor.Ignore(r => r.CostId);
        descriptor.Ignore(r => r.IsDeleted);
        descriptor.Ignore(r => r.Missing);
        descriptor.Ignore(r => r.Update(default, default, default, default, default, default, default, default, default));
        descriptor.Ignore(r => r.FillFromPhoto(default!));
        descriptor.Ignore(r => r.FinishReading(default));
        descriptor.Field(r => r.FilledFromPhoto)
            .Description("The values its photos filled in that nobody has checked yet.")
            .Type<NonNullType<ListType<NonNullType<EnumType<LogValue>>>>>()
            .Resolve(ctx => LogValueList.Of(ctx.Parent<Refueling>().FilledFromPhoto));
    }
}

[ExtendObjectType<Refueling>]
public sealed class RefuelingExtensions
{
    public async Task<bool> GetCanEdit([Parent] Refueling refueling, LogLevelByVehicleLoader levels, CancellationToken ct) =>
        await levels.LoadAsync(refueling.VehicleId, ct) >= AccessLevel.Edit;

    public async Task<bool> GetCanDelete([Parent] Refueling refueling, LogLevelByVehicleLoader levels, CancellationToken ct) =>
        await levels.LoadAsync(refueling.VehicleId, ct) >= AccessLevel.Delete;

    public Task<UserRef?> GetCreatedBy([Parent] Refueling refueling, UserRefLoader users, CancellationToken ct) => users.LoadAsync(refueling.CreatedById, ct);

    /// <summary>Who made its last change that counted (see <c>changedAt</c>, <c>lastChange</c>); null when nobody did (a photo) or no longer exists.</summary>
    public async Task<UserRef?> GetChangedBy([Parent] Refueling refueling, UserRefLoader users, CancellationToken ct) =>
        refueling.ChangedById is { } id ? await users.LoadAsync(id, ct) : null;

    /// <summary>The vehicle (even a trashed one), for its name and units next to a log in the trash.</summary>
    public Task<Vehicle?> GetVehicle([Parent] Refueling refueling, VehicleIncludingDeletedLoader vehicles, CancellationToken ct) => vehicles.LoadAsync(refueling.VehicleId, ct);
}

[ExtendObjectType(OperationTypeNames.Query)]
public sealed class RefuelingQueries
{
    /// <summary>One page of a vehicle's logs, sorted by the server (newest first by default).</summary>
    public Task<IReadOnlyList<Refueling>> GetRefuelings(
        Guid vehicleId, [Service] RefuelingService refuelings, CancellationToken ct,
        RefuelingSortField orderBy = RefuelingSortField.Date, SortDirection direction = SortDirection.Desc,
        int skip = 0, int take = RefuelingQuery.DefaultTake) =>
        refuelings.ListAsync(vehicleId, new RefuelingQuery(orderBy, direction, skip, take), ct);

    public Task<int> GetRefuelingCount(Guid vehicleId, [Service] RefuelingService refuelings, CancellationToken ct) =>
        refuelings.CountAsync(vehicleId, ct);

    public Task<Refueling?> GetRefueling(Guid id, [Service] RefuelingService refuelings, CancellationToken ct) =>
        refuelings.FindAsync(id, ct);

    /// <summary>Starting values for a new log: the vehicle's latest odometer reading and the currency of its latest log.</summary>
    public async Task<LogDefaults?> GetLogDefaults(
        Guid vehicleId, [Service] RefuelingService refuelings, [Service] IOptionsSnapshot<VehicleDefaultsOptions> defaults, CancellationToken ct) =>
        await refuelings.DefaultsAsync(vehicleId, ct) is { } d ? new LogDefaults(d.LastOdometer, d.LastDate, d.LastCurrency ?? defaults.Value.Currency) : null;

    /// <summary>One page of trashed logs (across vehicles) the user may restore.</summary>
    public Task<IReadOnlyList<Refueling>> GetRefuelingTrash(
        [Service] RefuelingService refuelings, CancellationToken ct,
        RefuelingSortField orderBy = RefuelingSortField.DeletedAt, SortDirection direction = SortDirection.Desc,
        int skip = 0, int take = RefuelingQuery.DefaultTake) =>
        refuelings.ListTrashAsync(new RefuelingQuery(orderBy, direction, skip, take), ct);

    public Task<int> GetRefuelingTrashCount([Service] RefuelingService refuelings, CancellationToken ct) => refuelings.CountTrashAsync(ct);

    public Task<int> GetRefuelingTrashDeletableCount([Service] RefuelingService refuelings, CancellationToken ct) =>
        refuelings.CountDeletableTrashAsync(ct);

    /// <summary>Who has access to a vehicle's logs through a grant on that vehicle (needs edit access to the vehicle).</summary>
    public async Task<IReadOnlyList<LogAccessGrantInfo>> GetVehicleLogAccess(Guid vehicleId, [Service] ResourceSharingService sharing, CancellationToken ct) =>
        (await sharing.ListLogAccessAsync(vehicleId, ct)).Select(e => new LogAccessGrantInfo(UserRef.From(e.User), e.Level)).ToList();

    /// <summary>Users a vehicle's logs could still be shared with.</summary>
    public async Task<IReadOnlyList<UserRef>> GetShareCandidates(Guid vehicleId, [Service] ResourceSharingService sharing, CancellationToken ct) =>
        (await sharing.CandidatesAsync(vehicleId, ct)).Select(UserRef.From).ToList();
}

[ExtendObjectType(OperationTypeNames.Mutation)]
public sealed class RefuelingMutations
{
    public Task<Refueling> LogRefueling(
        LogRefuelingInput input, [Service] RefuelingService refuelings, [Service] IOptionsSnapshot<VehicleDefaultsOptions> defaults, CancellationToken ct) =>
        refuelings.LogAsync(input.VehicleId,
            new RefuelingInput(input.Date, input.Volume, input.TotalCost, input.Currency ?? defaults.Value.Currency, input.Odometer, input.IsFullTank, input.Note, input.MissedPreviousFillUp, input.Discount), ct,
            photoDraftIds: input.PhotoIds, id: input.Id);

    public Task<Refueling> UpdateRefueling(UpdateRefuelingInput input, [Service] RefuelingService refuelings, CancellationToken ct) =>
        refuelings.UpdateAsync(
            input.Id, new RefuelingInput(input.Date, input.Volume, input.TotalCost, input.Currency, input.Odometer, input.IsFullTank, input.Note, input.MissedPreviousFillUp, input.Discount), ct);

    /// <summary>Moves the log to the trash (needs Edit access to the vehicle's logs).</summary>
    public Task<Refueling> DeleteRefueling(Guid id, [Service] RefuelingService refuelings, CancellationToken ct) => refuelings.DeleteAsync(id, ct);

    public Task<Refueling> RestoreRefueling(Guid id, [Service] RefuelingService refuelings, CancellationToken ct) => refuelings.RestoreAsync(id, ct);

    /// <summary>Permanently removes the trashed logs the user has Delete access to; returns how many.</summary>
    public Task<int> EmptyRefuelingTrash([Service] RefuelingService refuelings, CancellationToken ct) => refuelings.EmptyTrashAsync(ct);

    /// <summary>Gives a user access to one vehicle's logs (Edit or Delete), or removes it with level NONE.</summary>
    public async Task<bool> SetVehicleLogAccess(SetLogAccessInput input, [Service] ResourceSharingService sharing, CancellationToken ct)
    {
        await sharing.SetLogAccessAsync(input.VehicleId, input.UserId, input.Level, ct);
        return true;
    }
}
