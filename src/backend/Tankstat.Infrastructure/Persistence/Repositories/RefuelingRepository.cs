using Microsoft.EntityFrameworkCore;
using Tankstat.Application.Access;
using Tankstat.Application.Photos;
using Tankstat.Domain.Photos;
using Tankstat.Application.Refuelings;
using Tankstat.Application.Vehicles;
using Tankstat.Domain.Vehicles;
using Tankstat.Domain;

namespace Tankstat.Infrastructure.Persistence.Repositories;

internal sealed class RefuelingRepository(IDbContextFactory<AppDbContext> dbFactory, TimeProvider clock) : IRefuelingRepository
{
    public async Task<IReadOnlyList<Refueling>> ListForVehicleAsync(Guid vehicleId, RefuelingQuery query, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Page(db, db.Refuelings.Include(r => r.OdometerReading).Include(r => r.Cost).AsNoTracking().Where(r => r.VehicleId == vehicleId), query).ToListAsync(ct);
    }

    public async Task<int> CountForVehicleAsync(Guid vehicleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Refuelings.CountAsync(r => r.VehicleId == vehicleId, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountForVehiclesAsync(IReadOnlyCollection<Guid> vehicleIds, CancellationToken ct)
    {
        if (vehicleIds.Count == 0) return new Dictionary<Guid, int>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await CountsByVehicle(db, vehicleIds).ToDictionaryAsync(x => x.VehicleId, x => x.Count, ct);
    }

    /// <summary>The vehicles' live refuellings counted in the database, one row per vehicle with any (internal for its translation test).</summary>
    internal static IQueryable<VehicleCount> CountsByVehicle(AppDbContext db, IReadOnlyCollection<Guid> vehicleIds) =>
        db.Refuelings.Where(r => vehicleIds.Contains(r.VehicleId)).GroupBy(r => r.VehicleId).Select(g => new VehicleCount(g.Key, g.Count()));

    internal sealed record VehicleCount(Guid VehicleId, int Count);

    public async Task<bool> AnyForVehicleAsync(Guid vehicleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Refuelings.IgnoreQueryFilters().AnyAsync(r => r.VehicleId == vehicleId, ct);
    }

    public async Task<IReadOnlyList<Refueling>> ListAllForVehicleAsync(Guid vehicleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Refuelings.Include(r => r.OdometerReading).Include(r => r.Cost).AsNoTracking().Where(r => r.VehicleId == vehicleId).ToListAsync(ct);
    }

    public async Task SaveConsumptionsAsync(IReadOnlyList<Refueling> refuelings, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (refuelings.Count == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var r in refuelings)
        {
            var value = r.Consumption;
            // Derived, so no new Version; but a device shows it, so the row is downloaded again (UpdatedAt).
            await db.Refuelings.Where(x => x.Id == r.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Consumption, value).SetProperty(x => x.UpdatedAt, now), ct);
        }
    }

    public async Task<IReadOnlyList<Refueling>> ListDeletedAsync(OwnerScope scope, RefuelingQuery query, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Page(db, Trashed(db, scope).AsNoTracking(), query).ToListAsync(ct);
    }

    public async Task<int> CountDeletedAsync(OwnerScope scope, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Trashed(db, scope).CountAsync(ct);
    }

    public async Task<Refueling?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Refuelings.Include(r => r.OdometerReading).Include(r => r.Cost).AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<Refueling?> FindIncludingDeletedAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Refuelings.IgnoreQueryFilters().Include(r => r.OdometerReading).Include(r => r.Cost).AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<IReadOnlyList<Refueling>> ListByIdsIncludingDeletedAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Refuelings.IgnoreQueryFilters().Include(r => r.OdometerReading).Include(r => r.Cost).AsNoTracking().Where(r => ids.Contains(r.Id)).ToListAsync(ct);
    }

    public Task<bool> AddAsync(Refueling refueling, CancellationToken ct) => dbFactory.AddOnceAsync(refueling, refueling.Id, ct);

    public async Task UpdateAsync(Refueling refueling, LinkedChanges changes, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SaveLogAsync(refueling, changes, ct);
    }

    public async Task<PurgedLogs> PurgeAsync(OwnerScope scope, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var doomed = await Trashed(db, scope).ToListAsync(ct);
        var ids = doomed.Select(r => r.Id).ToList();
        var photos = await db.LogPhotos.Where(p => p.LogType == LogType.Refueling && ids.Contains(p.LogId)).ToListAsync(ct);
        db.LogPhotos.RemoveRange(photos);
        db.Refuelings.RemoveRange(doomed); // the dependents first, then the reading and cost that went with each log
        db.OdometerReadings.RemoveRange(doomed.Where(r => r.OdometerReading is not null).Select(r => r.OdometerReading!));
        db.Costs.RemoveRange(doomed.Where(r => r.Cost is not null).Select(r => r.Cost!));
        await db.SaveChangesAsync(ct);
        var withPhotos = photos.Select(p => p.LogId).ToHashSet();
        return new PurgedLogs(doomed.Count, doomed.Where(r => withPhotos.Contains(r.Id)).Select(r => (r.VehicleId, r.Id)).ToList());
    }

    private static IQueryable<Refueling> Trashed(AppDbContext db, OwnerScope scope) =>
        db.Refuelings.IgnoreQueryFilters().Include(r => r.OdometerReading).Include(r => r.Cost).Where(r => r.DeletedAt != null).InScope(scope, r => r.VehicleId);

    /// <summary>Orders (always ending in date, odometer and id, so pages are stable) and pages inside the database.</summary>
    private static IQueryable<Refueling> Page(AppDbContext db, IQueryable<Refueling> refuelings, RefuelingQuery query)
    {
        var q = query.Normalized();
        var desc = q.Direction == SortDirection.Desc;

        var ordered = q.SortBy switch
        {
            RefuelingSortField.Volume => Order(refuelings, r => r.Volume, desc),
            RefuelingSortField.TotalCost => Order(refuelings, r => r.Cost == null ? 0 : r.Cost.Amount, desc),
            RefuelingSortField.Odometer => Order(refuelings, r => r.OdometerReading == null ? 0 : r.OdometerReading.Value, desc),
            RefuelingSortField.Consumption => Order(refuelings, r => r.Consumption, desc),
            RefuelingSortField.PricePerUnit => Order(refuelings, r => r.Cost == null || r.Volume == null ? 0 : r.Cost.Amount / r.Volume, desc),
            RefuelingSortField.CreatedBy => Order(refuelings, r => db.Users.Where(u => u.Id == r.CreatedById).Select(u => u.DisplayName.ToLower()).FirstOrDefault(), desc),
            RefuelingSortField.Vehicle => Order(refuelings, r => db.Vehicles.IgnoreQueryFilters().Where(v => v.Id == r.VehicleId).Select(v => v.Name.ToLower()).FirstOrDefault(), desc),
            RefuelingSortField.DeletedAt => Order(refuelings, r => r.DeletedAt, desc),
            _ => Order(refuelings, r => r.Date, desc),
        };
        return (desc ? ordered.ThenByDescending(r => r.OdometerReading == null ? 0 : r.OdometerReading.Value) : ordered.ThenBy(r => r.OdometerReading == null ? 0 : r.OdometerReading.Value))
            .ThenBy(r => r.Id).Skip(q.Skip).Take(q.Take);
    }

    private static IOrderedQueryable<Refueling> Order<TKey>(IQueryable<Refueling> refuelings, System.Linq.Expressions.Expression<Func<Refueling, TKey>> key, bool desc) =>
        desc ? refuelings.OrderByDescending(key) : refuelings.OrderBy(key);
}
