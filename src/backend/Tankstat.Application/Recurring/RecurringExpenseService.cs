using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tankstat.Application.Access;
using Tankstat.Application.Auth;
using Tankstat.Application.Expenses;
using Tankstat.Application.Odometers;
using Tankstat.Application.Sync;
using Tankstat.Application.Vehicles;
using Tankstat.Domain;
using Tankstat.Domain.Access;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Recurring;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.Recurring;

/// <param name="LastDoneDate">When adding, omit to start counting today; when updating, omit to keep it.</param>
/// <param name="LastDoneOdometer">When adding a schedule that counts distance, omit to start from the vehicle's current odometer (its latest reading).</param>
/// <param name="WarnDays">Omit for the instance default (<see cref="VehicleDefaultsOptions.RecurringWarnDays"/>) when adding, or to keep the current value when updating.</param>
/// <param name="WarnDistance">Omit for the instance default (<see cref="VehicleDefaultsOptions.RecurringWarnDistance"/>, in the vehicle's distance unit) when adding, or to keep the current value when updating.</param>
public sealed record RecurringExpenseInput(
    string Title, string? Category, string? Note, RecurrenceKind Kind, int? IntervalMonths, long? IntervalDistance,
    DateOnly? LastDoneDate, long? LastDoneOdometer, int? WarnDays, long? WarnDistance);

/// <param name="Amount">
/// What the visit cost, for all the schedules together (never split). One expense is logged exactly when it is given or photos are sent
/// (a photo still being read may fill it in, under the expense rules); otherwise only the schedules move on.
/// </param>
/// <param name="Title">The logged expense's title; omit for the schedules' titles joined (<see cref="RecurringDoneDefaults"/>).</param>
/// <param name="Category">The logged expense's category; omit for the schedules' common (or first) one.</param>
/// <param name="PhotoIds">Photos uploaded beforehand as drafts (an invoice, the dashboard): they become the logged expense's photos.</param>
/// <param name="ExpenseId">
/// The id the client chose for the logged expense. Marking done with it again (an answer that never arrived, a replay after being offline)
/// finds the visit already recorded and changes nothing.
/// </param>
/// <param name="Discount">What was taken off <c>Amount</c> (what was paid), kept with the logged expense; needs the amount.</param>
public sealed record MarkDoneInput(
    DateOnly Date, long? Odometer, decimal? Amount, string? Currency, string? Title = null, string? Category = null, IReadOnlyCollection<Guid>? PhotoIds = null,
    Guid? ExpenseId = null, decimal? Discount = null);

/// <summary>The schedules as they stand after being marked done (in the order asked for), and the expense logged for them, if any.</summary>
public sealed record MarkedDone(IReadOnlyList<RecurringItem> Schedules, Expense? Expense);

/// <summary>A recurring expense with where it stands today.</summary>
public sealed record RecurringItem(RecurringExpense Item, RecurrenceStatus Status);

/// <summary>
/// The recurring expenses of a vehicle (schedules such as insurance or an oil change). They follow the access rules of the vehicle's
/// logs like expenses do: View sees them, Edit changes them. Marking some done (one visit) starts their next intervals and, with an
/// amount, logs one normal expense for them, so every expense and odometer rule applies.
/// </summary>
public sealed class RecurringExpenseService(
    LogAccessGuard guard, IRecurringExpenseRepository items, AccessService access, OdometerService odometer, ExpenseService expenses, IOptionsSnapshot<VehicleDefaultsOptions> defaults, TimeProvider clock,
    ILogger<RecurringExpenseService> logger)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
    private DateOnly LatestAllowedDate => Today.AddDays(1); // a day ahead covers every time zone, like the expenses

    /// <summary>The vehicle's recurring expenses, most urgent first; empty when the vehicle does not exist or may not be seen.</summary>
    public async Task<IReadOnlyList<RecurringItem>> ListAsync(Guid vehicleId, CancellationToken ct)
    {
        if (await VisibleVehicleAsync(vehicleId, ct) is null) return [];
        return Evaluate(await items.ListForVehicleAsync(vehicleId, ct), (await odometer.LatestAsync(vehicleId, ct))?.Value);
    }

    /// <summary>
    /// The recurring expenses of many (already loaded) vehicles at once, for lists of vehicles: the logs access is decided from one scope
    /// and the schedules and latest odometers come from one query each, instead of several queries per vehicle. Vehicles whose logs may
    /// not be seen get an empty list.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<RecurringItem>>> ListForVehiclesAsync(IReadOnlyCollection<Vehicle> vehicles, CancellationToken ct)
    {
        var result = vehicles.ToDictionary(v => v.Id, _ => (IReadOnlyList<RecurringItem>)[]);
        if (vehicles.Count == 0) return result;

        var scope = await access.LogScopeAsync(AccessLevel.View, ct);
        var visible = vehicles.Where(v => scope.Contains(v.OwnerId, v.Id)).Select(v => v.Id).ToList();
        if (visible.Count == 0) return result;

        var schedules = (await items.ListForVehiclesAsync(visible, ct)).ToLookup(i => i.VehicleId);
        var readings = schedules.Count == 0 ? new Dictionary<Guid, Domain.Odometers.OdometerReading>() : await odometer.LatestForVehiclesAsync([.. schedules.Select(g => g.Key)], ct);
        foreach (var group in schedules)
            result[group.Key] = Evaluate(group, readings.TryGetValue(group.Key, out var reading) ? reading.Value : null);
        return result;
    }

    private IReadOnlyList<RecurringItem> Evaluate(IEnumerable<RecurringExpense> schedules, long? currentOdometer) => schedules
        .Select(i => new RecurringItem(i, RecurrenceCalculator.Evaluate(i, Today, currentOdometer)))
        .OrderByDescending(r => r.Status.State)
        .ThenBy(r => r.Status.DueDate ?? DateOnly.MaxValue)
        .ThenBy(r => r.Item.Title, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <param name="id">The id the client chose; a schedule it already added with this id is answered as it is (see <see cref="ClientIds"/>).</param>
    public async Task<RecurringItem> AddAsync(Guid vehicleId, RecurringExpenseInput input, CancellationToken ct, Guid? id = null)
    {
        var vehicle = await EditableVehicleAsync(vehicleId, ct);
        var creator = await access.RequirePrincipalAsync(ct);
        if (id is { } given && await items.FindAsync(given, ct) is { } existing) return await WithStatusAsync(Repeated(existing, vehicle.Id, creator.Id), ct);
        var start = input.LastDoneDate ?? Today;
        CheckDate(start);
        // Counting starts now, so the odometer is the one the vehicle has now, unless the person says otherwise.
        var startOdometer = input.LastDoneOdometer;
        if (startOdometer is null && input.Kind is RecurrenceKind.Odometer or RecurrenceKind.Combined) startOdometer = (await odometer.LatestAsync(vehicle.Id, ct))?.Value;

        var item = RecurringExpense.Create(
            vehicle.OwnerId, creator.Id, vehicle.Id, input.Title, input.Category, input.Note, input.Kind, input.IntervalMonths, input.IntervalDistance,
            start, startOdometer, input.WarnDays ?? defaults.Value.RecurringWarnDays, input.WarnDistance ?? defaults.Value.RecurringWarnDistance, clock.GetUtcNow(), id);
        if (!await items.AddAsync(item, ct)) // the same add, sent twice at once: the other one saved it
            return await WithStatusAsync(Repeated(await items.FindAsync(item.Id, ct) ?? item, vehicle.Id, creator.Id), ct);
        logger.LogDebug("User {UserId} added recurring expense {RecurringId} for vehicle {VehicleId}", creator.Id, item.Id, vehicle.Id);
        return await WithStatusAsync(item, ct);
    }

    /// <param name="expectedVersion">The version the change was made from; refused when the schedule was saved since (see <see cref="VersionCheck"/>).</param>
    public async Task<RecurringItem> UpdateAsync(Guid id, RecurringExpenseInput input, CancellationToken ct, int? expectedVersion = null)
    {
        var item = await EditableAsync(id, ct);
        VersionCheck.Require(expectedVersion, item.Version);
        var lastDone = input.LastDoneDate ?? item.LastDoneDate;
        CheckDate(lastDone);

        item.Update(
            input.Title, input.Category, input.Note, input.Kind, input.IntervalMonths, input.IntervalDistance, lastDone, input.LastDoneOdometer,
            input.WarnDays ?? item.WarnDays, input.WarnDistance ?? item.WarnDistance, (await access.RequirePrincipalAsync(ct)).Id);
        await items.UpdateAsync(item, ct);
        logger.LogDebug("Recurring expense {RecurringId} updated", item.Id);
        return await WithStatusAsync(item, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var item = await EditableAsync(id, ct);
        VersionCheck.Require(expectedVersion, item.Version);
        await items.RemoveAsync(item, ct);
        logger.LogDebug("Recurring expense {RecurringId} deleted", id);
    }

    /// <summary>
    /// Starts the next interval of every given schedule (of one vehicle) from the same day and odometer: a service visit that did several
    /// things at once. With an amount (or photos) it logs one expense for all of them, not split, and links it to each. All or nothing:
    /// every schedule's own rules are checked before anything is logged, and the baselines and links are saved together.
    /// </summary>
    public async Task<MarkedDone> MarkDoneAsync(IReadOnlyCollection<Guid> ids, MarkDoneInput input, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) throw new DomainException("recurring.noneSelected", "Pick at least one recurring expense.");
        var byId = (await items.FindManyAsync(wanted, ct)).ToDictionary(i => i.Id);
        if (wanted.Where(id => !byId.ContainsKey(id)).Select(id => (Guid?)id).FirstOrDefault() is { } missing) throw NotFound(missing);
        var found = wanted.Select(id => byId[id]).ToList();

        var vehicleId = found[0].VehicleId;
        var creator = await access.RequirePrincipalAsync(ct);
        LogAccessGuard.RequireEdit(await guard.ForVehicleAsync(vehicleId, ct), () => NotFound(found[0].Id));
        // Schedules of another vehicle look non-existent: one visit is one vehicle, and access was only checked for this one.
        if (found.FirstOrDefault(i => i.VehicleId != vehicleId) is { } stray) throw NotFound(stray.Id);
        // The visit was already recorded (its expense is linked to every schedule asked for): the same request again changes nothing, before
        // any rule could refuse it. An expense linked to other schedules is another visit: its id cannot be used for this one.
        if (input.ExpenseId is { } expenseId && await items.ListCompletionsForExpensesAsync([expenseId], ct) is { Count: > 0 } recorded)
        {
            if (!wanted.All(id => recorded.Any(l => l.RecurringExpenseId == id)))
                throw new DomainException("sync.idTaken", $"The id {expenseId} is already in use.", new { Id = expenseId });
            logger.LogDebug("Recurring expenses {RecurringIds} of vehicle {VehicleId} were already marked done with expense {ExpenseId}; the request came again", wanted, vehicleId, expenseId);
            return await CurrentAsync(found, vehicleId, await expenses.FindAsync(expenseId, ct), ct);
        }

        CheckDate(input.Date);
        Cost.RequireAmountForDiscount(input.Amount, input.Discount); // before anything moves: a discount without the amount it was taken off
        foreach (var item in found) item.CheckDone(input.Date, input.Odometer); // every schedule's own rules first: nothing is logged for a day one would refuse

        var logged = input.Amount is not null || input.PhotoIds is { Count: > 0 }
            ? await expenses.AddAsync(vehicleId, new ExpenseInput(
                input.Date, input.Title ?? RecurringDoneDefaults.Title(found), input.Category ?? RecurringDoneDefaults.Category(found),
                input.Amount, input.Currency, input.Odometer, RecurringDoneDefaults.Note(found), input.Discount), ct, photoDraftIds: input.PhotoIds, id: input.ExpenseId)
            : null;
        // A first try that failed moved its expense to the trash (below); this try takes it back. Only that case gets here: once a visit
        // completed, its expense is linked and the request returned above, so an expense a person trashed after the visit is never restored.
        // (The expense with this id is the same creator's on this vehicle: ExpenseService refuses anyone else's with sync.idTaken.)
        if (logged is { IsDeleted: true }) logged = await expenses.RestoreAsync(logged.Id, ct);
        try
        {
            foreach (var item in found) item.MarkDone(input.Date, input.Odometer, creator.Id);
            var links = logged is null ? [] : found.Select(i => RecurringCompletion.Create(logged.Id, i.Id)).ToList();
            await items.CompleteAsync(found, links, ct);
        }
        catch
        {
            // A twin of this request (the same visit sent again while the first was still on its way) recorded it meanwhile: this save only
            // failed on what the twin saved, so the visit is done and its expense stays. Answered like any visit sent again.
            if (logged is not null && await RecordedMeanwhileAsync(logged.Id, wanted, ct))
            {
                logger.LogDebug("Recurring expenses {RecurringIds} of vehicle {VehicleId} were marked done with expense {ExpenseId} by a twin of this request", wanted, vehicleId, logged.Id);
                var current = (await items.FindManyAsync(wanted, ct)).ToDictionary(i => i.Id);
                return await CurrentAsync([.. wanted.Select(id => current[id])], vehicleId, await expenses.FindAsync(logged.Id, ct), ct);
            }
            // The expense and the schedules are saved separately, so undo the expense (to the trash) when the schedules could not move on:
            // they are then still due, and a second try does not log the cost twice.
            if (logged is not null) await TryTrashAsync(logged.Id, wanted);
            throw;
        }
        logger.LogDebug("User {UserId} marked recurring expenses {RecurringIds} of vehicle {VehicleId} done (expense logged: {ExpenseId})", creator.Id, wanted, vehicleId, logged?.Id);
        return await CurrentAsync(found, vehicleId, logged, ct);
    }

    private async Task<MarkedDone> CurrentAsync(IReadOnlyList<RecurringExpense> schedules, Guid vehicleId, Expense? expense, CancellationToken ct)
    {
        var current = (await odometer.LatestAsync(vehicleId, ct))?.Value;
        return new MarkedDone(schedules.Select(i => new RecurringItem(i, RecurrenceCalculator.Evaluate(i, Today, current))).ToList(), expense);
    }

    /// <summary>A schedule this add already created (same vehicle, same creator) is the answer; another one's id is refused.</summary>
    private RecurringExpense Repeated(RecurringExpense existing, Guid vehicleId, Guid creatorId)
    {
        var same = existing.VehicleId == vehicleId && existing.CreatedById == creatorId;
        if (same) logger.LogDebug("Recurring expense {RecurringId} of vehicle {VehicleId} was already added; the add came again", existing.Id, vehicleId);
        return ClientIds.Repeat(existing, same, existing.Id);
    }

    /// <summary>The schedules the given (already authorised) expenses covered, for the expense's <c>schedules</c> field.</summary>
    public async Task<ILookup<Guid, CompletedSchedule>> ListCompletionsForExpensesAsync(IReadOnlyCollection<Guid> expenseIds, CancellationToken ct) =>
        (await items.ListCompletionsForExpensesAsync(expenseIds, ct)).ToLookup(c => c.ExpenseId);

    /// <summary>Whether the expense is linked to every one of the schedules by now; false when that cannot be told (the original failure stands).</summary>
    private async Task<bool> RecordedMeanwhileAsync(Guid expenseId, IReadOnlyCollection<Guid> recurringIds, CancellationToken ct)
    {
        try
        {
            var recorded = await items.ListCompletionsForExpensesAsync([expenseId], ct);
            return recurringIds.All(id => recorded.Any(l => l.RecurringExpenseId == id));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not tell whether the expense {ExpenseId} was recorded for recurring expenses {RecurringIds} meanwhile", expenseId, recurringIds);
            return false;
        }
    }

    private async Task TryTrashAsync(Guid expenseId, IReadOnlyCollection<Guid> recurringIds)
    {
        try
        {
            await expenses.DeleteAsync(expenseId, CancellationToken.None);
        }
        catch (Exception e)
        {
            // best effort: the original failure is the one to report, but nobody else will ever hear that this cost stays logged
            logger.LogWarning(e, "The expense {ExpenseId} logged for recurring expenses {RecurringIds} could not be moved to the trash after they failed to move on", expenseId, recurringIds);
            return;
        }
        logger.LogWarning("Recurring expenses {RecurringIds} could not move on; the expense {ExpenseId} logged for them was moved to the trash", recurringIds, expenseId);
    }

    private static NotFoundException NotFound(Guid id) => new("recurring.notFound", $"Recurring expense {id} does not exist.", new { Id = id });

    private void CheckDate(DateOnly date)
    {
        if (date > LatestAllowedDate) throw new DomainException("refueling.dateInFuture", "The date cannot be in the future.");
    }

    private async Task<RecurringItem> WithStatusAsync(RecurringExpense item, CancellationToken ct) =>
        new(item, RecurrenceCalculator.Evaluate(item, Today, (await odometer.LatestAsync(item.VehicleId, ct))?.Value));

    private async Task<Vehicle?> VisibleVehicleAsync(Guid vehicleId, CancellationToken ct) => (await guard.ForVehicleAsync(vehicleId, ct))?.Vehicle;

    private async Task<Vehicle> EditableVehicleAsync(Guid vehicleId, CancellationToken ct) =>
        LogAccessGuard.RequireEdit(await guard.ForVehicleAsync(vehicleId, ct),
            () => new NotFoundException("vehicle.notFound", $"Vehicle {vehicleId} does not exist.", new { Id = vehicleId })).Vehicle;

    private async Task<RecurringExpense> EditableAsync(Guid id, CancellationToken ct)
    {
        var item = await items.FindAsync(id, ct);
        LogAccessGuard.RequireEdit(await guard.ForVehicleAsync(item?.VehicleId, ct), () => NotFound(id));
        return item!;
    }
}
