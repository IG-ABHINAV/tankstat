using GreenDonut;
using Microsoft.Extensions.Options;
using Tankstat.Application.Recurring;
using Tankstat.Application.Vehicles;
using Tankstat.Domain.Recurring;
using Tankstat.Domain.Sync;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Api.GraphQL;

/// <param name="LastDoneDate">Omit to start counting today.</param>
/// <param name="LastDoneOdometer">Omit to start from the vehicle's current odometer (its latest reading); needed when the vehicle has none and distance counts.</param>
/// <param name="WarnDays">Omit for the instance default (<c>vehicleDefaults.recurringWarnDays</c>).</param>
/// <param name="WarnDistance">Omit for the instance default (<c>vehicleDefaults.recurringWarnDistance</c>, in the vehicle's distance unit).</param>
/// <param name="Id">An id the client chose for the new schedule; the same add sent again (a lost answer, a replay after being offline) answers with what it created. Omit to let the server choose.</param>
public sealed record AddRecurringExpenseInput(
    Guid VehicleId, string Title, string? Category, string? Note, RecurrenceKind Kind, int? IntervalMonths, long? IntervalDistance,
    DateOnly? LastDoneDate, long? LastDoneOdometer, int? WarnDays, long? WarnDistance, Guid? Id = null);

/// <param name="WarnDays">Omit to keep the current value.</param>
/// <param name="WarnDistance">Omit to keep the current value.</param>
public sealed record UpdateRecurringExpenseInput(
    Guid Id, string Title, string? Category, string? Note, RecurrenceKind Kind, int? IntervalMonths, long? IntervalDistance,
    DateOnly LastDoneDate, long? LastDoneOdometer, int? WarnDays, long? WarnDistance);

/// <param name="Ids">Schedules of one vehicle, all done on the same day at the same odometer (one service visit).</param>
/// <param name="Amount">What the visit cost for all of them together (never split). With an amount, or with photos (one still being read may
/// fill it in), one expense is logged and linked to every schedule; without, only the schedules move on.</param>
/// <param name="Currency">ISO 4217 code; omit to use the instance default.</param>
/// <param name="Title">The expense's title; omit for the schedules' titles joined.</param>
/// <param name="Category">The expense's category; omit for the schedules' common (or first) category.</param>
/// <param name="PhotoIds">Photos uploaded beforehand (<c>PUT /media/vehicles/{id}/photo-drafts</c>); they become the logged expense's photos.</param>
/// <param name="ExpenseId">An id the client chose for the logged expense: the same visit sent again (a lost answer, a replay after being offline) changes nothing.</param>
/// <param name="Discount">What was taken off the amount (what was paid), kept with the logged expense; needs the amount.</param>
public sealed record MarkRecurringExpensesDoneInput(
    IReadOnlyList<Guid> Ids, DateOnly Date, long? Odometer, decimal? Amount, string? Currency, string? Title, string? Category, IReadOnlyList<Guid>? PhotoIds = null,
    Guid? ExpenseId = null, decimal? Discount = null);

/// <summary>The schedules as they stand now, and the expense logged for them (null when none was).</summary>
public sealed record MarkRecurringExpensesDonePayload(IReadOnlyList<RecurringExpenseInfo> Schedules, Expense? Expense);

/// <summary>A recurring expense an expense covered when it was marked done.</summary>
public sealed record RecurringRef(Guid Id, string Title);

/// <summary>Where a recurring expense stands today. The days and distance left are negative once it is overdue; null when that side does not apply.</summary>
public sealed record RecurrenceStatusInfo(RecurrenceState State, RecurrenceLimit? Limit, DateOnly? DueDate, long? DueOdometer, int? DaysLeft, long? DistanceLeft);

/// <summary>A schedule such as insurance or an oil change, with its status and when it was added. Intervals and odometers are in the vehicle's distance unit.</summary>
/// <param name="Version">Counts its saves (edits, being marked done), from 1.</param>
/// <param name="UpdatedAt">When it was last saved; a device that keeps a copy downloads it again.</param>
public sealed record RecurringExpenseInfo(
    Guid Id, Guid VehicleId, string Title, string? Category, string? Note, RecurrenceKind Kind, int? IntervalMonths, long? IntervalDistance,
    DateOnly LastDoneDate, long? LastDoneOdometer, int WarnDays, long WarnDistance, DateTimeOffset CreatedAt, int Version, DateTimeOffset UpdatedAt, RecurrenceStatusInfo Status,
    DateTimeOffset? ChangedAt, Guid? ChangedById, EntityChange? LastChange)
{
    public static RecurringExpenseInfo From(RecurringItem r) => new(
        r.Item.Id, r.Item.VehicleId, r.Item.Title, r.Item.Category, r.Item.Note, r.Item.Kind, r.Item.IntervalMonths, r.Item.IntervalDistance,
        r.Item.LastDoneDate, r.Item.LastDoneOdometer, r.Item.WarnDays, r.Item.WarnDistance, r.Item.CreatedAt, r.Item.Version, r.Item.UpdatedAt,
        new RecurrenceStatusInfo(r.Status.State, r.Status.Limit, r.Status.DueDate, r.Status.DueOdometer, r.Status.DaysLeft, r.Status.DistanceLeft),
        r.Item.ChangedAt, r.Item.ChangedById, r.Item.LastChange);
}

[ExtendObjectType<RecurringExpenseInfo>]
public sealed class RecurringExpenseInfoExtensions
{
    /// <summary>Who made its last change that counted (see <c>changedAt</c>, <c>lastChange</c>); null when nobody did (a photo) or no longer exists.</summary>
    public async Task<UserRef?> GetChangedBy([Parent] RecurringExpenseInfo item, UserRefLoader users, CancellationToken ct) =>
        item.ChangedById is { } id ? await users.LoadAsync(id, ct) : null;
}

/// <summary>The recurring expenses of the vehicles of a response, loaded together (one access check, one query for the schedules, one for the odometers).</summary>
public sealed class RecurringByVehicleLoader(RecurringExpenseService recurring, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Vehicle, IReadOnlyList<RecurringItem>>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<Vehicle, IReadOnlyList<RecurringItem>>> LoadBatchAsync(IReadOnlyList<Vehicle> keys, CancellationToken ct)
    {
        var loaded = await recurring.ListForVehiclesAsync(keys, ct);
        return keys.ToDictionary(v => v, v => loaded[v.Id]);
    }
}

/// <summary>The schedules each expense of a response covered, one query for all. Only used for expenses that were already authorised.</summary>
public sealed class ExpenseSchedulesLoader(RecurringExpenseService recurring, IBatchScheduler scheduler, DataLoaderOptions options)
    : GroupedDataLoader<Guid, CompletedSchedule>(scheduler, options)
{
    protected override async Task<ILookup<Guid, CompletedSchedule>> LoadGroupedBatchAsync(IReadOnlyList<Guid> keys, CancellationToken ct) =>
        await recurring.ListCompletionsForExpensesAsync(keys, ct);
}

[ExtendObjectType<Expense>]
public sealed class ExpenseRecurringExtensions
{
    /// <summary>The recurring expenses this expense covered when they were marked done (one visit can do several), by title.</summary>
    public async Task<IReadOnlyList<RecurringRef>> GetSchedules([Parent] Expense expense, ExpenseSchedulesLoader loader, CancellationToken ct) =>
        expense.IsDeleted ? [] : (await loader.LoadAsync(expense.Id, ct) ?? []).Select(c => new RecurringRef(c.RecurringExpenseId, c.Title)).ToList();
}

[ExtendObjectType<Vehicle>]
public sealed class VehicleRecurringExtensions
{
    /// <summary>The vehicle's recurring expenses, most urgent first.</summary>
    public async Task<IReadOnlyList<RecurringExpenseInfo>> GetRecurring([Parent] Vehicle vehicle, RecurringByVehicleLoader loader, CancellationToken ct) =>
        (await loader.LoadAsync(vehicle, ct) ?? []).Select(RecurringExpenseInfo.From).ToList();
}

[ExtendObjectType(OperationTypeNames.Mutation)]
public sealed class RecurringMutations
{
    public async Task<RecurringExpenseInfo> AddRecurringExpense(AddRecurringExpenseInput input, [Service] RecurringExpenseService recurring, CancellationToken ct) =>
        RecurringExpenseInfo.From(await recurring.AddAsync(input.VehicleId,
            new RecurringExpenseInput(input.Title, input.Category, input.Note, input.Kind, input.IntervalMonths, input.IntervalDistance, input.LastDoneDate, input.LastDoneOdometer, input.WarnDays, input.WarnDistance), ct, input.Id));

    public async Task<RecurringExpenseInfo> UpdateRecurringExpense(UpdateRecurringExpenseInput input, [Service] RecurringExpenseService recurring, CancellationToken ct) =>
        RecurringExpenseInfo.From(await recurring.UpdateAsync(input.Id,
            new RecurringExpenseInput(input.Title, input.Category, input.Note, input.Kind, input.IntervalMonths, input.IntervalDistance, input.LastDoneDate, input.LastDoneOdometer, input.WarnDays, input.WarnDistance), ct));

    /// <summary>Removes the schedule for good (expenses already logged from it stay).</summary>
    public async Task<bool> DeleteRecurringExpense(Guid id, [Service] RecurringExpenseService recurring, CancellationToken ct)
    {
        await recurring.DeleteAsync(id, ct);
        return true;
    }

    /// <summary>
    /// Marks schedules of one vehicle done on the same day and odometer (one service visit) and, with an amount or photos, logs one expense
    /// for all of them. All or nothing: a schedule that refuses (its error names it) leaves everything as it was.
    /// </summary>
    public async Task<MarkRecurringExpensesDonePayload> MarkRecurringExpensesDone(
        MarkRecurringExpensesDoneInput input, [Service] RecurringExpenseService recurring, [Service] IOptionsSnapshot<VehicleDefaultsOptions> defaults, CancellationToken ct)
    {
        var done = await recurring.MarkDoneAsync(input.Ids,
            new MarkDoneInput(input.Date, input.Odometer, input.Amount, input.Currency ?? defaults.Value.Currency, input.Title, input.Category, input.PhotoIds, input.ExpenseId, input.Discount), ct);
        return new MarkRecurringExpensesDonePayload(done.Schedules.Select(RecurringExpenseInfo.From).ToList(), done.Expense);
    }
}
