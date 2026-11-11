using Microsoft.EntityFrameworkCore;
using Tankstat.Domain.Photos;

namespace Tankstat.Infrastructure.Persistence;

/// <summary>
/// Marks rows for download again (<c>UpdatedAt</c>) when something a device shows with them changed without the row itself being saved:
/// a log's photos, the schedules an expense covered, the tyre log line an expense was made from. See <see cref="SyncInterceptor"/> for everything else.
/// </summary>
internal static class SyncTouch
{
    public static Task TouchLogAsync(this AppDbContext db, LogType logType, Guid logId, DateTimeOffset now, CancellationToken ct) =>
        logType == LogType.Refueling
            ? db.Refuelings.IgnoreQueryFilters().Where(r => r.Id == logId).ExecuteUpdateAsync(s => s.SetProperty(r => r.UpdatedAt, now), ct)
            : db.Expenses.IgnoreQueryFilters().Where(e => e.Id == logId).ExecuteUpdateAsync(s => s.SetProperty(e => e.UpdatedAt, now), ct);

    /// <summary>The expenses that list these schedules (<c>Expense.schedules</c>): their titles changed, or the schedules are going.</summary>
    public static Task TouchExpensesOfSchedulesAsync(this AppDbContext db, IReadOnlyCollection<Guid> scheduleIds, DateTimeOffset now, CancellationToken ct) =>
        db.Expenses.IgnoreQueryFilters()
            .Where(e => db.RecurringCompletions.Any(c => c.ExpenseId == e.Id && scheduleIds.Contains(c.RecurringExpenseId)))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.UpdatedAt, now), ct);

    /// <summary>
    /// The expenses made from these tyre log lines, or from a line of these sets (<c>Expense.tyreEntry</c>, #288): the line's kind or its
    /// place in the trash changed, its set was renamed, trashed or restored, or the line is going.
    /// </summary>
    public static Task TouchExpensesOfTyreLinesAsync(this AppDbContext db, IReadOnlyCollection<Guid> entryIds, IReadOnlyCollection<Guid> setIds, DateTimeOffset now, CancellationToken ct) =>
        entryIds.Count + setIds.Count == 0
            ? Task.CompletedTask
            : db.Expenses.IgnoreQueryFilters()
                .Where(e => db.TyreEntries.IgnoreQueryFilters().Any(l => l.ExpenseId == e.Id && (entryIds.Contains(l.Id) || (l.SetId != null && setIds.Contains(l.SetId.Value)))))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.UpdatedAt, now), ct);
}
