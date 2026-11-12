using Microsoft.EntityFrameworkCore;
using Tankstat.Application.Sync;
using Tankstat.Domain.Sync;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Infrastructure.Persistence.Repositories;

/// <summary>
/// The rows of the offline feed. Paging is by (UpdatedAt, Id) in the database's own order of ids (each provider orders uuids its own way;
/// the order and the comparison come from the same database, so pages never skip or repeat a row).
/// </summary>
internal sealed class OfflineFeedRepository(IDbContextFactory<AppDbContext> dbFactory) : IOfflineFeedRepository
{
    public async Task<IReadOnlyList<Refueling>> RefuelingsAsync(OfflineRows rows, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await RefuelingRows(db, rows).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Expense>> ExpensesAsync(OfflineRows rows, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await ExpenseRows(db, rows).ToListAsync(ct);
    }

    /// <summary>The query of a page of refuelings (also read by a test, which prints each provider's SQL).</summary>
    internal static IQueryable<Refueling> RefuelingRows(AppDbContext db, OfflineRows rows)
    {
        var query = db.Refuelings.IgnoreQueryFilters().AsNoTracking().Include(r => r.OdometerReading).Include(r => r.Cost)
            .Where(r => r.VehicleId == rows.VehicleId);
        if (rows.Since is { } since) query = query.Where(r => r.UpdatedAt >= since);
        else if (rows.From is { } from) query = query.Where(r => r.Date >= from);
        if (rows.After is { } after) query = query.Where(r => r.UpdatedAt > after.UpdatedAt || (r.UpdatedAt == after.UpdatedAt && r.Id.CompareTo(after.Id) > 0));
        return query.OrderBy(r => r.UpdatedAt).ThenBy(r => r.Id).Take(rows.Take);
    }

    internal static IQueryable<Expense> ExpenseRows(AppDbContext db, OfflineRows rows)
    {
        var query = db.Expenses.IgnoreQueryFilters().AsNoTracking().Include(e => e.OdometerReading).Include(e => e.Cost)
            .Where(e => e.VehicleId == rows.VehicleId);
        if (rows.Since is { } since) query = query.Where(e => e.UpdatedAt >= since);
        else if (rows.From is { } from) query = query.Where(e => e.Date >= from);
        if (rows.After is { } after) query = query.Where(e => e.UpdatedAt > after.UpdatedAt || (e.UpdatedAt == after.UpdatedAt && e.Id.CompareTo(after.Id) > 0));
        return query.OrderBy(e => e.UpdatedAt).ThenBy(e => e.Id).Take(rows.Take);
    }

    public async Task<IReadOnlyList<RemovedEntity>> RemovedSinceAsync(Guid vehicleId, DateTimeOffset since, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tombstones = (await db.Tombstones.AsNoTracking().Where(t => t.VehicleId == vehicleId && t.PurgedAt >= since)
            .Select(t => new { t.EntityType, t.EntityId }).Distinct().ToListAsync(ct))
            // Tyre rows come whole with every first page (OfflinePage.Tyres), so their tombstones are not sent.
            .Where(t => t.EntityType is not (OfflineEntityType.TyreSet or OfflineEntityType.TyreInstallation or OfflineEntityType.TyreEntry
                or OfflineEntityType.Tyre or OfflineEntityType.TyrePlace)).ToList();
        if (tombstones.Count == 0) return [];
        // A device may have added something with the same id again (a replayed add): what exists is not removed.
        var ids = tombstones.Select(t => t.EntityId).ToList();
        var alive = new HashSet<(OfflineEntityType, Guid)>();
        foreach (var id in await db.Refuelings.IgnoreQueryFilters().Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct)) alive.Add((OfflineEntityType.Refueling, id));
        foreach (var id in await db.Expenses.IgnoreQueryFilters().Where(e => ids.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct)) alive.Add((OfflineEntityType.Expense, id));
        foreach (var id in await db.RecurringExpenses.Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct)) alive.Add((OfflineEntityType.RecurringExpense, id));
        foreach (var id in await db.Vehicles.IgnoreQueryFilters().Where(v => ids.Contains(v.Id)).Select(v => v.Id).ToListAsync(ct)) alive.Add((OfflineEntityType.Vehicle, id));
        return tombstones.Where(t => !alive.Contains((t.EntityType, t.EntityId))).Select(t => new RemovedEntity(t.EntityType, t.EntityId)).ToList();
    }

    public async Task<int> SweepTombstonesAsync(DateTimeOffset before, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Tombstones.Where(t => t.PurgedAt < before).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, LogCounts>> CountSinceAsync(IReadOnlyCollection<Guid> vehicleIds, DateOnly? from, CancellationToken ct)
    {
        if (vehicleIds.Count == 0) return new Dictionary<Guid, LogCounts>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var refuelings = db.Refuelings.IgnoreQueryFilters().Where(r => vehicleIds.Contains(r.VehicleId));
        var expenses = db.Expenses.IgnoreQueryFilters().Where(e => vehicleIds.Contains(e.VehicleId));
        if (from is { } date)
        {
            refuelings = refuelings.Where(r => r.Date >= date);
            expenses = expenses.Where(e => e.Date >= date);
        }
        var r = await refuelings.GroupBy(x => x.VehicleId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var e = await expenses.GroupBy(x => x.VehicleId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return vehicleIds.Distinct().ToDictionary(id => id, id => new LogCounts(r.GetValueOrDefault(id), e.GetValueOrDefault(id)));
    }
}
