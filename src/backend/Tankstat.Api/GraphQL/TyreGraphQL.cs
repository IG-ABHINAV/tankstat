using GreenDonut;
using Tankstat.Application.Expenses;
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
/// <param name="Price">What the set cost (#288): a <c>BOUGHT</c> line of the tyre log with its expense, on <paramref name="BoughtOn"/> (left out: <paramref name="Day"/>).</param>
/// <param name="BoughtEntryId">The client's id of that line.</param>
public sealed record AddTyreSetInput(
    Guid VehicleId, string Name, TyreSeason Season, string? Size, DateOnly Since, int? MadeWeek, int? MadeYear, bool Fitted, DateOnly Day,
    long RoughDistance = 0, long? Odometer = null, bool? PairOneFront = null, bool Replace = false, Guid? Id = null, Guid? InstallationId = null,
    TyreFeeInput? Price = null, DateOnly? BoughtOn = null, Guid? BoughtEntryId = null);

/// <summary>
/// The amount of a tyre log line (#288): an ordinary expense on the Expenses tab, with the line's day and reading. Left out, the line's stays
/// as it is; with no <c>amount</c> its expense goes to the trash; typed again, it comes back.
/// </summary>
/// <param name="Title">The expense's title when it is made, worded by the client ("Puncture repaired: Michelin Primacy 4").</param>
/// <param name="ExpenseId">The client's id for the expense it makes; a device sends one, so the change sent again makes one expense.</param>
public sealed record TyreFeeInput(decimal? Amount, string? Currency = null, string? Category = null, string? Title = null, Guid? ExpenseId = null)
{
    internal TyreFee ToFee() => new(Amount, Currency, Category, Title, ExpenseId);
}

/// <param name="WithExpense">Its expense goes to the trash with it (the remove dialog's box, ticked at first), and comes back with it.</param>
public sealed record DeleteTyreEntryInput(Guid Id, bool WithExpense = true);

/// <param name="Day">The device's today.</param>
public sealed record RetireTyreSetInput(Guid Id, DateOnly Day);

public sealed record UpdateTyreSetInput(Guid Id, string Name, TyreSeason Season, string? Size, DateOnly Since, int? MadeWeek, int? MadeYear);

/// <param name="Until">The first day the rough figure does not cover, when accepting a suggestion; left out keeps it.</param>
public sealed record SetTyreRoughDistanceInput(Guid Id, long Distance, DateOnly? Until = null);

/// <param name="PairOneFront">True: pair 1 at the front; false: pair 2 at the front; null: not recorded ("Not sure").</param>
/// <param name="Replace">A swap logged on that day already is replaced instead of refused with <c>tyres.swapExists</c>.</param>
/// <param name="Odometer">The reading of that day, kept on the swap's line of the tyre log as one of the car's readings.</param>
/// <param name="InstallationId">The id of the installation it creates, if it creates one: the same change sent again changes nothing.</param>
/// <param name="Fee">What it cost (#288), on the line of the installation that starts that day.</param>
public sealed record SwapTyresInput(
    Guid VehicleId, Guid SetId, DateOnly Day, bool? PairOneFront = null, bool Replace = false, long? Odometer = null, string? Note = null, Guid? InstallationId = null,
    TyreFeeInput? Fee = null);

public sealed record PastTyreSwapInput(DateOnly Day, Guid SetId, long? Odometer = null, Guid? InstallationId = null);

/// <param name="Swaps">One per day (two on one day are refused), 1 to 25, applied in day order, all or nothing.</param>
public sealed record AddPastTyreSwapsInput(Guid VehicleId, IReadOnlyList<PastTyreSwapInput> Swaps, bool Replace = false);

/// <param name="OdometerFrom">A reading for the first day, kept on the installation's line of the tyre log; left out leaves it as it is.</param>
public sealed record AddTyreInstallationInput(
    Guid SetId, DateOnly From, DateOnly? Until = null, bool? PairOneFront = null, long? OdometerFrom = null, long? OdometerUntil = null, Guid? Id = null);

public sealed record UpdateTyreInstallationInput(
    Guid Id, DateOnly From, DateOnly? Until = null, bool? PairOneFront = null, long? OdometerFrom = null, long? OdometerUntil = null);

/// <param name="Kind">What was done: any kind but <c>SWAP</c> and <c>POSITIONS_CHANGED</c>, which come with the swap (<c>tyres.entryKindNotAllowed</c>).</param>
/// <param name="SetId">The set it concerns; left out for none.</param>
/// <param name="Odometer">The reading of that day, one of the car's readings.</param>
/// <param name="Id">The line's id chosen by the client: the same add sent again answers with what it created.</param>
public sealed record AddTyreEntryInput(
    Guid VehicleId, TyreEntryKind Kind, DateOnly Day, Guid? SetId = null, long? Odometer = null, string? Note = null, Guid? Id = null, TyreFeeInput? Fee = null);

/// <param name="Kind">A swap's line keeps its kind and set (<c>tyres.swapLineLocked</c>); its new day moves the swap.</param>
/// <param name="Odometer">The reading; left out clears it (for good).</param>
public sealed record UpdateTyreEntryInput(Guid Id, TyreEntryKind Kind, DateOnly Day, Guid? SetId = null, long? Odometer = null, string? Note = null, TyreFeeInput? Fee = null);

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

/// <summary>A line of the tyre log (#288): what was done, to which set (or none), its reading (one of the car's readings) and note.</summary>
public sealed class TyreEntryType : ObjectType<TyreEntry>
{
    protected override void Configure(IObjectTypeDescriptor<TyreEntry> descriptor)
    {
        descriptor.Ignore(e => e.SavedVersion);
        descriptor.Ignore(e => e.OdometerReading);
        descriptor.Ignore(e => e.OdometerReadingId);
        descriptor.Ignore(e => e.IsDeleted);
        descriptor.Ignore(e => e.DeletedAt);
        descriptor.Ignore(e => e.Update(default, default, default, default, default, default));
        descriptor.Ignore(e => e.MoveTo(default, default));
        descriptor.Ignore(e => e.TakeReading(default!, default));
        descriptor.Ignore(e => e.ExpenseTrashedWithEntry);
        descriptor.Ignore(e => e.TakesAmount);
        descriptor.Field(e => e.ExpenseId).Description("The expense its amount made (#288), an ordinary one on the Expenses tab; in the trash or not.");
        descriptor.Field(e => e.InstallationId).Description("The installation whose line it is (the same id): set on a swap's and a change of positions' line.");
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

/// <summary>A line's amount (#288): its expense's, while that is not in the trash (a line whose expense is there shows none).</summary>
[ExtendObjectType<TyreEntry>]
public sealed class TyreEntryExtensions
{
    public async Task<decimal?> GetAmount([Parent] TyreEntry line, TyreEntryExpenseLoader expenses, CancellationToken ct) =>
        await Live(line, expenses, ct) is { } e ? e.Amount : null;

    public async Task<string?> GetCurrency([Parent] TyreEntry line, TyreEntryExpenseLoader expenses, CancellationToken ct) =>
        await Live(line, expenses, ct) is { } e ? e.Currency : null;

    public async Task<string?> GetCategory([Parent] TyreEntry line, TyreEntryExpenseLoader expenses, CancellationToken ct) =>
        line.ExpenseId is { } id ? (await expenses.LoadAsync(id, ct))?.Category : null;

    /// <summary>Its expense is in the trash (on its own, or with the line): the line shows no amount until it comes back.</summary>
    public async Task<bool> GetExpenseTrashed([Parent] TyreEntry line, TyreEntryExpenseLoader expenses, CancellationToken ct) =>
        line.ExpenseId is { } id && (await expenses.LoadAsync(id, ct))?.IsDeleted == true;

    private static async Task<Expense?> Live(TyreEntry line, TyreEntryExpenseLoader expenses, CancellationToken ct) =>
        line.ExpenseId is { } id && await expenses.LoadAsync(id, ct) is { IsDeleted: false } e ? e : null;
}

/// <summary>The tyre log line an expense was made from (#288), for its chip and banner on the Expenses tab.</summary>
/// <param name="SetName">The line's set; none for a line of no set.</param>
/// <param name="Trashed">The line is in the trash (on its own, or with its set).</param>
public sealed record TyreEntryRef(Guid Id, TyreEntryKind Kind, string? SetName, bool Trashed);

[ExtendObjectType<Expense>]
public sealed class ExpenseTyreExtensions
{
    /// <summary>The tyre log line it was made from (#288); none for an ordinary expense.</summary>
    public Task<TyreEntryRef?> GetTyreEntry([Parent] Expense expense, ExpenseTyreEntryLoader lines, CancellationToken ct) => lines.LoadAsync(expense.Id, ct);
}

/// <summary>Each line's expense, the trash included, for a whole response at once (the lines are already authorised).</summary>
public sealed class TyreEntryExpenseLoader(IExpenseRepository expenses, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Guid, Expense?>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<Guid, Expense?>> LoadBatchAsync(IReadOnlyList<Guid> keys, CancellationToken ct)
    {
        var found = (await expenses.ListByIdsIncludingDeletedAsync(keys, ct)).ToDictionary(e => e.Id);
        return keys.ToDictionary(k => k, k => found.GetValueOrDefault(k));
    }
}

/// <summary>Each expense's tyre log line, with its set's name, for a whole page of expenses at once (the expenses are already authorised).</summary>
public sealed class ExpenseTyreEntryLoader(ITyreRepository tyres, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Guid, TyreEntryRef?>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<Guid, TyreEntryRef?>> LoadBatchAsync(IReadOnlyList<Guid> keys, CancellationToken ct)
    {
        var lines = await tyres.ListEntriesByExpenseAsync(keys, ct);
        var sets = (await tyres.ListSetsAsync([.. lines.Where(l => l.SetId is not null).Select(l => l.SetId!.Value).Distinct()], ct)).ToDictionary(s => s.Id);
        return keys.ToDictionary(k => k, k => lines.FirstOrDefault(l => l.ExpenseId == k) is { } line
            ? new TyreEntryRef(line.Id, line.Kind, line.SetId is { } setId ? sets.GetValueOrDefault(setId)?.Name : null,
                line.IsDeleted || (line.SetId is { } s && sets.GetValueOrDefault(s)?.IsDeleted == true))
            : null);
    }
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
    /// <summary>A vehicle's tyre sets, their installations and the tyre log's lines; null when the vehicle cannot be seen.</summary>
    public Task<TyreOverview?> GetVehicleTyres(Guid vehicleId, [Service] TyreService tyres, CancellationToken ct) => tyres.OverviewAsync(vehicleId, ct);

    /// <summary>The trashed tyre sets and the trashed tyre log lines of no set or of a set not in the trash, by when they were trashed (newest first by default).</summary>
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

    public Task<TyreOverview> AddTyreEntry(AddTyreEntryInput input, [Service] TyreService tyres, CancellationToken ct) => TyreChanges.AddEntry(input, tyres, ct);

    public Task<TyreOverview> UpdateTyreEntry(UpdateTyreEntryInput input, [Service] TyreService tyres, CancellationToken ct) => TyreChanges.UpdateEntry(input, tyres, ct);

    /// <summary>Moves a tyre log line to the trash; a swap's line takes its installation along, and its expense goes too when asked.</summary>
    public Task<TyreOverview> DeleteTyreEntry(DeleteTyreEntryInput input, [Service] TyreService tyres, CancellationToken ct) =>
        tyres.TrashEntryAsync(input.Id, ct, withExpense: input.WithExpense);

    /// <summary>Brings a tyre log line back; a swap's line brings its installation back, or is refused with <c>tyres.restoreConflict</c>.</summary>
    public Task<TyreOverview> RestoreTyreEntry(Guid id, [Service] TyreService tyres, CancellationToken ct) => tyres.RestoreEntryAsync(id, ct);

    /// <param name="day">The device's today.</param>
    public Task<TyreOverview> RetireTyreSet(Guid id, DateOnly day, [Service] TyreService tyres, CancellationToken ct) => tyres.RetireAsync(id, day, ct);

    public Task<TyreOverview> UnretireTyreSet(Guid id, [Service] TyreService tyres, CancellationToken ct) => tyres.UnretireAsync(id, ct);

    public Task<TyreOverview> DeleteTyreSet(Guid id, [Service] TyreService tyres, CancellationToken ct) => tyres.TrashSetAsync(id, ct);

    public Task<TyreOverview> RestoreTyreSet(Guid id, [Service] TyreService tyres, CancellationToken ct) => tyres.RestoreSetAsync(id, ct);

    /// <summary>Permanently removes the trashed tyre sets and tyre log lines the user has Delete access to; returns how many rows of the list went.</summary>
    public Task<int> EmptyTyreTrash([Service] TyreService tyres, CancellationToken ct) => tyres.EmptyTrashAsync(ct);
}

/// <summary>The mutations' inputs turned into the service's, shared with the changes a device sends (<c>SyncGraphQL</c>).</summary>
internal static class TyreChanges
{
    public static TyreSwapInput Swap(SwapTyresInput i) => new(i.VehicleId, i.SetId, i.Day, i.PairOneFront, i.Replace, i.Odometer, i.Note, i.Fee?.ToFee());

    public static Task<TyreOverview> Add(AddTyreSetInput i, TyreService tyres, CancellationToken ct) =>
        tyres.AddSetAsync(new NewTyreSet(i.VehicleId, new TyreSetInput(i.Name, i.Season, i.Size, i.Since, i.MadeWeek, i.MadeYear, i.RoughDistance), i.Fitted, i.Day, i.Odometer, i.PairOneFront, i.Replace,
            i.Price?.ToFee(), i.BoughtOn, i.BoughtEntryId), ct, i.Id, i.InstallationId);

    public static Task<TyreOverview> Update(UpdateTyreSetInput i, TyreService tyres, CancellationToken ct, int? version = null) =>
        tyres.UpdateSetAsync(i.Id, new TyreSetInput(i.Name, i.Season, i.Size, i.Since, i.MadeWeek, i.MadeYear), ct, version);

    public static Task<TyreOverview> Past(AddPastTyreSwapsInput i, TyreService tyres, CancellationToken ct, bool repeat = false) =>
        tyres.AddPastSwapsAsync(i.VehicleId, [.. i.Swaps.Select(s => new TyrePastSwap(s.Day, s.SetId, s.Odometer, s.InstallationId))], i.Replace, ct, repeat);

    public static Task<TyreOverview> AddInstallation(AddTyreInstallationInput i, TyreService tyres, CancellationToken ct) =>
        tyres.AddInstallationAsync(i.SetId, new TyreInstallationInput(i.From, i.Until, i.PairOneFront, i.OdometerFrom, i.OdometerUntil), ct, i.Id);

    public static Task<TyreOverview> UpdateInstallation(UpdateTyreInstallationInput i, TyreService tyres, CancellationToken ct, int? version = null) =>
        tyres.UpdateInstallationAsync(i.Id, new TyreInstallationInput(i.From, i.Until, i.PairOneFront, i.OdometerFrom, i.OdometerUntil), ct, version);

    public static Task<TyreOverview> AddEntry(AddTyreEntryInput i, TyreService tyres, CancellationToken ct) =>
        tyres.AddEntryAsync(i.VehicleId, new TyreEntryInput(i.Kind, i.SetId, i.Day, i.Odometer, i.Note, i.Fee?.ToFee()), ct, i.Id);

    public static Task<TyreOverview> UpdateEntry(UpdateTyreEntryInput i, TyreService tyres, CancellationToken ct, int? version = null) =>
        tyres.UpdateEntryAsync(i.Id, new TyreEntryInput(i.Kind, i.SetId, i.Day, i.Odometer, i.Note, i.Fee?.ToFee()), ct, version);
}
