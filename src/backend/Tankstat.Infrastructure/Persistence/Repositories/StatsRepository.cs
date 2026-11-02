using Microsoft.EntityFrameworkCore;
using Tankstat.Application.Stats;
using Tankstat.Domain.Charts;

namespace Tankstat.Infrastructure.Persistence.Repositories;

internal sealed class StatsRepository(IDbContextFactory<AppDbContext> dbFactory) : IStatsRepository
{
    public async Task<StatsData> LoadAsync(Guid vehicleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct); // the global filters already leave out the trash

        // Logs still missing values (their photos are being read, or left them out) count once they are complete.
        var fuel = await db.Refuelings.AsNoTracking()
            .Where(r => r.VehicleId == vehicleId && r.Volume != null && r.Cost != null && r.OdometerReading != null)
            .Select(r => new FuelPoint(r.Date, r.Volume!.Value, r.Cost!.Amount, r.Cost.Currency, r.OdometerReading!.Value, r.IsFullTank, r.Consumption, r.Cost.Discount ?? 0)).ToListAsync(ct);
        var expenses = await db.Expenses.AsNoTracking().Where(e => e.VehicleId == vehicleId && e.Cost != null)
            .Select(e => new ExpensePoint(e.Date, e.Category, e.Cost!.Amount, e.Cost.Currency)).ToListAsync(ct);
        var readings = await db.OdometerReadings.AsNoTracking().Where(r => r.VehicleId == vehicleId)
            .Select(r => new OdometerPoint(r.Date, r.Value)).ToListAsync(ct);
        return new StatsData(fuel, expenses, readings);
    }
}

internal sealed class VehicleChartRepository(IDbContextFactory<AppDbContext> dbFactory) : IVehicleChartRepository
{
    public async Task<IReadOnlyList<VehicleChart>> ListVisibleAsync(Guid vehicleId, Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.VehicleCharts.AsNoTracking().Where(c => c.VehicleId == vehicleId && (c.IsShared || c.CreatedById == userId))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
    }

    public async Task<int> CountByUserAsync(Guid vehicleId, Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.VehicleCharts.CountAsync(c => c.VehicleId == vehicleId && c.CreatedById == userId, ct);
    }

    public async Task<VehicleChart?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.VehicleCharts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task AddAsync(VehicleChart chart, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.VehicleCharts.Add(chart);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(VehicleChart chart, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.VehicleCharts.Update(chart);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(VehicleChart chart, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.VehicleCharts.Remove(chart);
        await db.SaveChangesAsync(ct);
    }
}

internal sealed class DashboardLayoutRepository(IDbContextFactory<AppDbContext> dbFactory) : IDashboardLayoutRepository
{
    public async Task<VehicleDashboardLayout?> FindSharedAsync(Guid vehicleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.VehicleDashboardLayouts.AsNoTracking().FirstOrDefaultAsync(l => l.VehicleId == vehicleId, ct);
    }

    public async Task<UserDashboardLayout?> FindPersonalAsync(Guid userId, Guid vehicleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserDashboardLayouts.AsNoTracking().FirstOrDefaultAsync(l => l.UserId == userId && l.VehicleId == vehicleId, ct);
    }

    public Task SaveSharedAsync(VehicleDashboardLayout layout, CancellationToken ct) =>
        dbFactory.UpsertAsync(layout, db => db.VehicleDashboardLayouts.AnyAsync(l => l.VehicleId == layout.VehicleId, ct), ct);

    public Task SavePersonalAsync(UserDashboardLayout layout, CancellationToken ct) =>
        dbFactory.UpsertAsync(layout, db => db.UserDashboardLayouts.AnyAsync(l => l.UserId == layout.UserId && l.VehicleId == layout.VehicleId, ct), ct);

    public async Task RemoveSharedAsync(Guid vehicleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.VehicleDashboardLayouts.Where(l => l.VehicleId == vehicleId).ExecuteDeleteAsync(ct);
    }

    public async Task RemovePersonalAsync(Guid userId, Guid vehicleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.UserDashboardLayouts.Where(l => l.UserId == userId && l.VehicleId == vehicleId).ExecuteDeleteAsync(ct);
    }

}
