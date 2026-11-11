using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Tankstat.Application.Access;
using Tankstat.Application.Photos;
using Tankstat.Domain.Photos;
using Tankstat.Application.Expenses;
using Tankstat.Application.Vehicles;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Vehicles;
using Tankstat.Domain;

namespace Tankstat.Infrastructure.Persistence.Repositories;

internal sealed class ExpenseRepository(IDbContextFactory<AppDbContext> dbFactory) : IExpenseRepository
{
    private static IQueryable<Expense> WithLinks(IQueryable<Expense> q) => q.Include(e => e.OdometerReading).Include(e => e.Cost);

    public async Task<IReadOnlyList<Expense>> ListForVehicleAsync(Guid vehicleId, ExpenseQuery query, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Page(db, WithLinks(db.Expenses).AsNoTracking().Where(e => e.VehicleId == vehicleId), query).ToListAsync(ct);
    }

    public async Task<int> CountForVehicleAsync(Guid vehicleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Expenses.CountAsync(e => e.VehicleId == vehicleId, ct);
    }

    public async Task<bool> AnyForVehicleAsync(Guid vehicleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Expenses.IgnoreQueryFilters().AnyAsync(e => e.VehicleId == vehicleId, ct);
    }

    public async Task<IReadOnlyList<Expense>> ListAllForVehicleAsync(Guid vehicleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await WithLinks(db.Expenses).AsNoTracking().Where(e => e.VehicleId == vehicleId).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Expense>> ListDeletedAsync(OwnerScope scope, ExpenseQuery query, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Page(db, Trashed(db, scope).AsNoTracking(), query).ToListAsync(ct);
    }

    public async Task<int> CountDeletedAsync(OwnerScope scope, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Trashed(db, scope).CountAsync(ct);
    }

    public async Task<IReadOnlyList<string>> CategoriesAsync(Guid vehicleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Expenses.Where(e => e.VehicleId == vehicleId && e.Category != null).Select(e => e.Category!).Distinct().OrderBy(c => c).ToListAsync(ct);
    }

    public async Task<Expense?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await WithLinks(db.Expenses).AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<Expense?> FindIncludingDeletedAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await WithLinks(db.Expenses.IgnoreQueryFilters()).AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<IReadOnlyList<Expense>> ListByIdsIncludingDeletedAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await WithLinks(db.Expenses.IgnoreQueryFilters()).AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync(ct);
    }

    public Task<bool> AddAsync(Expense expense, CancellationToken ct) => dbFactory.AddOnceAsync(expense, expense.Id, ct);

    public async Task UpdateAsync(Expense expense, LinkedChanges changes, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SaveLogAsync(expense, changes, ct);
    }

    public async Task<PurgedLogs> PurgeAsync(OwnerScope scope, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var doomed = await Trashed(db, scope).ToListAsync(ct);
        var ids = doomed.Select(e => e.Id).ToList();
        var photos = await db.LogPhotos.Where(p => p.LogType == LogType.Expense && ids.Contains(p.LogId)).ToListAsync(ct);
        db.LogPhotos.RemoveRange(photos);
        // The schedules an expense covered are linked without a foreign key on the expense side (see RecurringCompletionConfiguration).
        db.RecurringCompletions.RemoveRange(await db.RecurringCompletions.Where(c => ids.Contains(c.ExpenseId)).ToListAsync(ct));
        db.Expenses.RemoveRange(doomed);
        // A tyre log line that made one (#288) keeps everything but the link, its reading included: the two held it together.
        var lines = await db.TyreEntries.IgnoreQueryFilters().Where(e => e.ExpenseId != null && ids.Contains(e.ExpenseId.Value)).ToListAsync(ct);
        foreach (var line in lines) line.UnlinkExpense();
        var held = lines.Where(l => l.OdometerReadingId is not null).Select(l => l.OdometerReadingId!.Value).ToHashSet();
        db.OdometerReadings.RemoveRange(doomed.Where(e => e.OdometerReading is not null && !held.Contains(e.OdometerReading.Id)).Select(e => e.OdometerReading!));
        db.Costs.RemoveRange(doomed.Where(e => e.Cost is not null).Select(e => e.Cost!));
        await db.SaveChangesAsync(ct);
        var withPhotos = photos.Select(p => p.LogId).ToHashSet();
        return new PurgedLogs(doomed.Count, doomed.Where(e => withPhotos.Contains(e.Id)).Select(e => (e.VehicleId, e.Id)).ToList());
    }

    private static IQueryable<Expense> Trashed(AppDbContext db, OwnerScope scope) =>
        WithLinks(db.Expenses.IgnoreQueryFilters()).Where(e => e.DeletedAt != null).InScope(scope, e => e.VehicleId);

    private static IQueryable<Expense> Page(AppDbContext db, IQueryable<Expense> expenses, ExpenseQuery query)
    {
        var q = query.Normalized();
        var desc = q.Direction == SortDirection.Desc;

        var ordered = q.SortBy switch
        {
            ExpenseSortField.Title => Order(expenses, e => e.Title.ToLower(), desc),
            ExpenseSortField.Category => Order(expenses, e => e.Category == null ? "" : e.Category.ToLower(), desc),
            ExpenseSortField.Amount => Order(expenses, e => e.Cost == null ? 0 : e.Cost.Amount, desc),
            ExpenseSortField.Odometer => Order(expenses, e => e.OdometerReading == null ? 0 : e.OdometerReading.Value, desc),
            ExpenseSortField.CreatedBy => Order(expenses, e => db.Users.Where(u => u.Id == e.CreatedById).Select(u => u.DisplayName.ToLower()).FirstOrDefault(), desc),
            ExpenseSortField.Vehicle => Order(expenses, e => db.Vehicles.IgnoreQueryFilters().Where(v => v.Id == e.VehicleId).Select(v => v.Name.ToLower()).FirstOrDefault(), desc),
            ExpenseSortField.DeletedAt => Order(expenses, e => e.DeletedAt, desc),
            _ => Order(expenses, e => e.Date, desc),
        };
        return ordered.ThenBy(e => e.Id).Skip(q.Skip).Take(q.Take);
    }

    private static IOrderedQueryable<Expense> Order<TKey>(IQueryable<Expense> expenses, Expression<Func<Expense, TKey>> key, bool desc) =>
        desc ? expenses.OrderByDescending(key) : expenses.OrderBy(key);
}
