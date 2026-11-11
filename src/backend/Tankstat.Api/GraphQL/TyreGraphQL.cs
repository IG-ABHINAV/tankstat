using Tankstat.Application.Tyres;
using Tankstat.Application.Users;
using Tankstat.Domain.Access;
using Tankstat.Domain.Tyres;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Api.GraphQL;

/// <param name="RoughDistance">What a used set ran before it is tracked, roughly, in the vehicle's distance unit (0 or left out for a new set).</param>
/// <param name="Fitted">On the car from <paramref name="Day"/> (a swap that day); else in storage.</param>
/// <param name="Day">The day tracking starts (the device's today): the rough figure stands for the time before it.</param>
/// <param name="Replace">A fitted set on a day that has a swap replaces that swap (its installation goes to the trash) instead of being refused.</param>
/// <param name="Id">The set's id chosen by the client: the same add sent again answers with what it created.</param>
/// <param name="InstallationId">The id of the installation a fitted set starts with.</param>
public sealed record AddTyreSetInput(
    Guid VehicleId, string Name, TyreSeason Season, string? Size, DateOnly Since, int? MadeWeek, int? MadeYear, bool Fitted, DateOnly Day,
    long RoughDistance = 0, long? Odometer = null, bool? PairOneFront = null, bool Replace = false, Guid? Id = null, Guid? InstallationId = null);

/// <param name="Day">The device's today.</param>
public sealed record RetireTyreSetInput(Guid Id, DateOnly Day);

public sealed record UpdateTyreSetInput(Guid Id, string Name, TyreSeason Season, string? Size, DateOnly Since, int? MadeWeek, int? MadeYear);

/// <param name="Until">The first day the rough figure does not cover, when accepting a suggestion; left out keeps it.</param>
public sealed record SetTyreRoughDistanceInput(Guid Id, long Distance, DateOnly? Until = null);

/// <param name="PairOneFront">True: pair 1 at the front; false: pair 2 at the front; null: not recorded ("Not sure").</param>
/// <param name="Replace">A swap logged on that day already is replaced instead of refused with <c>tyres.swapExists</c>.</param>
/// <param name="Odometer">The reading of that day, kept on the tyre log's line for it as one of the car's readings.</param>
/// <param name="InstallationId">The id of the installation it creates, if it creates one: the same change sent again changes nothing.</param>
public sealed record SwapTyresInput(
    Guid VehicleId, Guid SetId, DateOnly Day, bool? PairOneFront = null, bool Replace = false, long? Odometer = null, string? Note = null, Guid? InstallationId = null);

public sealed record PastTyreSwapInput(DateOnly Day, Guid SetId, long? Odometer = null, Guid? InstallationId = null);

/// <param name="Swaps">One per day (two on one day are refused), 1 to 25, applied in day order, all or nothing.</param>
public sealed record AddPastTyreSwapsInput(Guid VehicleId, IReadOnlyList<PastTyreSwapInput> Swaps, bool Replace = false);

/// <param name="OdometerFrom">A reading for the first day, kept on that day's tyre log line; left out leaves the day as it is.</param>
public sealed record AddTyreInstallationInput(
    Guid SetId, DateOnly From, DateOnly? Until = null, bool? PairOneFront = null, long? OdometerFrom = null, long? OdometerUntil = null, Guid? Id = null);

public sealed record UpdateTyreInstallationInput(
    Guid Id, DateOnly From, DateOnly? Until = null, bool? PairOneFront = null, long? OdometerFrom = null, long? OdometerUntil = null);

/// <param name="Odometer">The reading; left out clears it (for good). A day left with neither a reading nor a note is deleted.</param>
/// <param name="Id">The id of the line if it is created (the client's).</param>
public sealed record SetTyreLogDayInput(Guid VehicleId, DateOnly Day, long? Odometer = null, string? Note = null, Guid? Id = null);

/// <summary>The stored set; the installation, the reading and the version's bookkeeping stay internal.</summary>
public sealed class TyreSetType : ObjectType<TyreSet>
{
    protected override void Configure(IObjectTypeDescriptor<TyreSet> descriptor)
    {
        descriptor.Ignore(s => s.SavedVersion);
        descriptor.Ignore(s => s.IsDeleted);
    }
}

public sealed class TyreInstallationType : ObjectType<TyreInstallation>
{
    protected override void Configure(IObjectTypeDescriptor<TyreInstallation> descriptor)
    {
        descriptor.Ignore(s => s.SavedVersion);
        descriptor.Ignore(s => s.IsDeleted);
        descriptor.Ignore(s => s.Update(default, default, default, default));
    }
}

/// <summary>A day of the tyre log: its reading (one of the car's readings) and note.</summary>
public sealed class TyreLogDayType : ObjectType<TyreLogDay>
{
    protected override void Configure(IObjectTypeDescriptor<TyreLogDay> descriptor)
    {
        descriptor.Ignore(d => d.SavedVersion);
        descriptor.Ignore(d => d.OdometerReading);
        descriptor.Ignore(d => d.OdometerReadingId);
        descriptor.Ignore(d => d.IsEmpty);
        descriptor.Ignore(d => d.Update(default, default, default));
    }
}

/// <summary>A vehicle's tyres as the Tyres tab shows them, with every distance worked out on read.</summary>
public sealed class VehicleTyresType : ObjectType<TyreOverview>
{
    protected override void Configure(IObjectTypeDescriptor<TyreOverview> descriptor)
    {
        descriptor.Name("VehicleTyres");
        descriptor.Field(o => o.Readings).Description("The vehicle's odometer readings by date (the highest of a day decides), to work out the odometer of a day as the server does.");
    }
}

public sealed class ReadingPointType : ObjectType<ReadingPoint>
{
    protected override void Configure(IObjectTypeDescriptor<ReadingPoint> descriptor) => descriptor.Name("OdometerPoint");
}

public sealed class TyreSetViewType : ObjectType<TyreSetView>
{
    protected override void Configure(IObjectTypeDescriptor<TyreSetView> descriptor) => descriptor.Name("TyreSetInfo");
}

public sealed class TyreInstallationViewType : ObjectType<TyreInstallationView>
{
    protected override void Configure(IObjectTypeDescriptor<TyreInstallationView> descriptor) => descriptor.Name("TyreInstallationInfo");
}

[ExtendObjectType<TyreSet>]
public sealed class TyreSetExtensions
{
    public Task<UserRef?> GetCreatedBy([Parent] TyreSet set, UserRefLoader users, CancellationToken ct) => users.LoadAsync(set.CreatedById, ct);

    public async Task<UserRef?> GetChangedBy([Parent] TyreSet set, UserRefLoader users, CancellationToken ct) =>
        set.ChangedById is { } id ? await users.LoadAsync(id, ct) : null;
}

[ExtendObjectType<TyreTrashRow>]
public sealed class TyreTrashRowExtensions
{
    public Task<Vehicle?> GetVehicle([Parent] TyreTrashRow row, VehicleIncludingDeletedLoader vehicles, CancellationToken ct) => vehicles.LoadAsync(row.VehicleId, ct);

    public async Task<bool> GetCanDelete([Parent] TyreTrashRow row, LogLevelByVehicleLoader levels, CancellationToken ct) =>
        await levels.LoadAsync(row.VehicleId, ct) >= AccessLevel.Delete;
}

[ExtendObjectType(OperationTypeNames.Query)]
public sealed class TyreQueries
{
    /// <summary>A vehicle's tyre sets, their installations and the tyre log's days; null when the vehicle cannot be seen.</summary>
    public Task<TyreOverview?> GetVehicleTyres(Guid vehicleId, [Service] TyreService tyres, CancellationToken ct) => tyres.OverviewAsync(vehicleId, ct);

    /// <summary>The trashed tyre sets and the trashed installations of sets not in the trash, by when they were trashed (newest first by default).</summary>
    public Task<IReadOnlyList<TyreTrashRow>> GetTyreTrash(
        [Service] TyreService tyres, CancellationToken ct,
        TyreTrashSortField orderBy = TyreTrashSortField.DeletedAt, Tankstat.Domain.SortDirection direction = Tankstat.Domain.SortDirection.Desc,
        int skip = 0, int take = 50) =>
        tyres.ListTrashAsync(skip, take, ct, direction == Tankstat.Domain.SortDirection.Desc);

    public Task<int> GetTyreTrashCount([Service] TyreService tyres, CancellationToken ct) => tyres.CountTrashAsync(ct);

    public Task<int> GetTyreTrashDeletableCount([Service] TyreService tyres, CancellationToken ct) => tyres.CountDeletableTrashAsync(ct);
}

/// <summary>Every change answers with the vehicle's tyres as they are after it.</summary>
[ExtendObjectType(OperationTypeNames.Mutation)]
public sealed class TyreMutations
{
    public Task<TyreOverview> AddTyreSet(AddTyreSetInput input, [Service] TyreService tyres, CancellationToken ct) => TyreChanges.Add(input, tyres, ct);

    public Task<TyreOverview> UpdateTyreSet(UpdateTyreSetInput input, [Service] TyreService tyres, CancellationToken ct) => TyreChanges.Update(input, tyres, ct);

    public Task<TyreOverview> SetTyreRoughDistance(SetTyreRoughDistanceInput input, [Service] TyreService tyres, CancellationToken ct) =>
        tyres.SetRoughAsync(input.Id, input.Distance, input.Until, ct);

    public Task<TyreOverview> SwapTyres(SwapTyresInput input, [Service] TyreService tyres, CancellationToken ct) => tyres.SwapAsync(TyreChanges.Swap(input), ct, input.InstallationId);

    public Task<TyreOverview> ChangeTyrePositions(SwapTyresInput input, [Service] TyreService tyres, CancellationToken ct) =>
        tyres.ChangePositionsAsync(TyreChanges.Swap(input), ct, input.InstallationId);

    public Task<TyreOverview> AddPastTyreSwaps(AddPastTyreSwapsInput input, [Service] TyreService tyres, CancellationToken ct) => TyreChanges.Past(input, tyres, ct);

    public Task<TyreOverview> AddTyreInstallation(AddTyreInstallationInput input, [Service] TyreService tyres, CancellationToken ct) => TyreChanges.AddInstallation(input, tyres, ct);

    public Task<TyreOverview> UpdateTyreInstallation(UpdateTyreInstallationInput input, [Service] TyreService tyres, CancellationToken ct) => TyreChanges.UpdateInstallation(input, tyres, ct);

    public Task<TyreOverview> DeleteTyreInstallation(Guid id, [Service] TyreService tyres, CancellationToken ct) => tyres.TrashInstallationAsync(id, ct);

    public Task<TyreOverview> RestoreTyreInstallation(Guid id, [Service] TyreService tyres, CancellationToken ct) => tyres.RestoreInstallationAsync(id, ct);

    public Task<TyreOverview> SetTyreLogDay(SetTyreLogDayInput input, [Service] TyreService tyres, CancellationToken ct) =>
        tyres.SetLogDayAsync(input.VehicleId, input.Day, input.Odometer, input.Note, ct, input.Id);

    /// <param name="day">The device's today.</param>
    public Task<TyreOverview> RetireTyreSet(Guid id, DateOnly day, [Service] TyreService tyres, CancellationToken ct) => tyres.RetireAsync(id, day, ct);

    public Task<TyreOverview> UnretireTyreSet(Guid id, [Service] TyreService tyres, CancellationToken ct) => tyres.UnretireAsync(id, ct);

    public Task<TyreOverview> DeleteTyreSet(Guid id, [Service] TyreService tyres, CancellationToken ct) => tyres.TrashSetAsync(id, ct);

    public Task<TyreOverview> RestoreTyreSet(Guid id, [Service] TyreService tyres, CancellationToken ct) => tyres.RestoreSetAsync(id, ct);

    /// <summary>Permanently removes the trashed tyre sets and installations the user has Delete access to; returns how many rows of the list went.</summary>
    public Task<int> EmptyTyreTrash([Service] TyreService tyres, CancellationToken ct) => tyres.EmptyTrashAsync(ct);
}

/// <summary>The mutations' inputs turned into the service's, shared with the changes a device sends (<c>SyncGraphQL</c>).</summary>
internal static class TyreChanges
{
    public static TyreSwapInput Swap(SwapTyresInput i) => new(i.VehicleId, i.SetId, i.Day, i.PairOneFront, i.Replace, i.Odometer, i.Note);

    public static Task<TyreOverview> Add(AddTyreSetInput i, TyreService tyres, CancellationToken ct) =>
        tyres.AddSetAsync(new NewTyreSet(i.VehicleId, new TyreSetInput(i.Name, i.Season, i.Size, i.Since, i.MadeWeek, i.MadeYear, i.RoughDistance), i.Fitted, i.Day, i.Odometer, i.PairOneFront, i.Replace),
            ct, i.Id, i.InstallationId);

    public static Task<TyreOverview> Update(UpdateTyreSetInput i, TyreService tyres, CancellationToken ct, int? version = null) =>
        tyres.UpdateSetAsync(i.Id, new TyreSetInput(i.Name, i.Season, i.Size, i.Since, i.MadeWeek, i.MadeYear), ct, version);

    public static Task<TyreOverview> Past(AddPastTyreSwapsInput i, TyreService tyres, CancellationToken ct, bool repeat = false) =>
        tyres.AddPastSwapsAsync(i.VehicleId, [.. i.Swaps.Select(s => new TyrePastSwap(s.Day, s.SetId, s.Odometer, s.InstallationId))], i.Replace, ct, repeat);

    public static Task<TyreOverview> AddInstallation(AddTyreInstallationInput i, TyreService tyres, CancellationToken ct) =>
        tyres.AddInstallationAsync(i.SetId, new TyreInstallationInput(i.From, i.Until, i.PairOneFront, i.OdometerFrom, i.OdometerUntil), ct, i.Id);

    public static Task<TyreOverview> UpdateInstallation(UpdateTyreInstallationInput i, TyreService tyres, CancellationToken ct, int? version = null) =>
        tyres.UpdateInstallationAsync(i.Id, new TyreInstallationInput(i.From, i.Until, i.PairOneFront, i.OdometerFrom, i.OdometerUntil), ct, version);
}
