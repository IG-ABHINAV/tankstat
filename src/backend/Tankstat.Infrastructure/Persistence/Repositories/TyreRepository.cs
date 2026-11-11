using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Tankstat.Application.Access;
using Tankstat.Application.Tyres;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Tyres;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Infrastructure.Persistence.Repositories;

internal sealed class TyreRepository(IDbContextFactory<AppDbContext> dbFactory) : ITyreRepository
{
    public async Task<TyreBook> LoadAsync(Guid vehicleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sets = await db.TyreSets.AsNoTracking().Where(s => s.VehicleId == vehicleId).ToListAsync(ct);
        var installations = await db.TyreInstallations.AsNoTracking().Where(s => s.VehicleId == vehicleId).ToListAsync(ct);
        var entries = await Entries(db).Where(e => e.VehicleId == vehicleId).ToListAsync(ct);
        var readings = await db.OdometerReadings.AsNoTracking().Where(r => r.VehicleId == vehicleId).Select(r => new ReadingPoint(r.Date, r.Value)).ToListAsync(ct);
        return new TyreBook(sets, installations, entries, TyreCalculator.Sorted(readings), await LinkedExpensesAsync(db, entries, ct));
    }

    /// <summary>The expenses the lines' amounts made, the trash included, each holding its line's reading object (one row, one object).</summary>
    private static async Task<IReadOnlyList<Expense>> LinkedExpensesAsync(AppDbContext db, IReadOnlyCollection<TyreEntry> entries, CancellationToken ct)
    {
        var ids = entries.Where(e => e.ExpenseId is not null).Select(e => e.ExpenseId!.Value).ToList();
        if (ids.Count == 0) return [];
        var expenses = await db.Expenses.IgnoreQueryFilters().AsNoTracking().Include(e => e.Cost).Include(e => e.OdometerReading)
            .Where(e => ids.Contains(e.Id)).ToListAsync(ct);
        foreach (var expense in expenses)
            if (entries.FirstOrDefault(e => e.ExpenseId == expense.Id)?.OdometerReading is { } reading) expense.ShareReadingObject(reading);
        return expenses;
    }

    public async Task<TyreSet?> FindSetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TyreSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<TyreInstallation?> FindInstallationAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TyreInstallations.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<IReadOnlyList<TyreSet>> ListSetsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TyreSets.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TyreInstallation>> ListInstallationsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TyreInstallations.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(ct);
    }

    public async Task<TyreEntry?> FindEntryAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Entries(db).FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<IReadOnlyList<TyreEntry>> ListEntriesByExpenseAsync(IReadOnlyCollection<Guid> expenseIds, CancellationToken ct)
    {
        if (expenseIds.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Entries(db).Where(e => e.ExpenseId != null && expenseIds.Contains(e.ExpenseId.Value)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TyreEntry>> ListEntriesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Entries(db).Where(e => ids.Contains(e.Id)).ToListAsync(ct);
    }

    /// <summary>The lines with their readings: a trashed line's reading is in the trash with it, so the readings' filter is ignored.</summary>
    private static IQueryable<TyreEntry> Entries(AppDbContext db) => db.TyreEntries.AsNoTracking().IgnoreQueryFilters().Include(e => e.OdometerReading);

    /// <summary>
    /// One transaction. Readings let go of (a line's reading cleared) are deleted in a second save: the context never saw them linked,
    /// so in one save it could delete a reading before the line stops referring to it, which the database refuses. An insert that fails
    /// because a row with one of the added ids is there by now is the same add saved at once by another request (see <c>AddOnceAsync</c>):
    /// rolled back and answered false; any other failure is rethrown as it was.
    /// </summary>
    public async Task<bool> SaveAsync(TyreSave save, CancellationToken ct)
    {
        ExceptionDispatchInfo failure;
        try
        {
            await SaveInOneAsync(save, ct);
            return true;
        }
        catch (DbUpdateException e) when (e is not DbUpdateConcurrencyException && save.AddedSets.Count + save.AddedInstallations.Count + save.AddedEntries.Count + save.AddedExpenses.Count > 0)
        {
            failure = ExceptionDispatchInfo.Capture(e);
        }
        var sets = save.AddedSets.Select(s => s.Id).ToList();
        var installations = save.AddedInstallations.Select(s => s.Id).ToList();
        var entries = save.AddedEntries.Select(e => e.Id).ToList();
        var expenses = save.AddedExpenses.Select(e => e.Id).ToList();
        await using var check = await dbFactory.CreateDbContextAsync(ct); // a fresh context: the failed one still holds the inserts
        if (await check.TyreSets.AnyAsync(s => sets.Contains(s.Id), ct) || await check.TyreInstallations.AnyAsync(s => installations.Contains(s.Id), ct)
            || await check.TyreEntries.AnyAsync(e => entries.Contains(e.Id), ct) || await check.Expenses.IgnoreQueryFilters().AnyAsync(e => expenses.Contains(e.Id), ct))
            return false;
        failure.Throw();
        return false; // not reached
    }

    private async Task SaveInOneAsync(TyreSave save, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var s in save.AddedSets) db.TyreSets.Add(s);
        foreach (var s in save.ChangedSets) db.TyreSets.Update(s);
        foreach (var s in save.AddedInstallations) db.TyreInstallations.Add(s);
        foreach (var s in save.ChangedInstallations) db.TyreInstallations.Update(s);
        foreach (var r in save.AddedReadings) db.OdometerReadings.Add(r);
        // A line's reading that is not new may have moved, gone to the trash or come back with it, or passed to another line of its day.
        foreach (var e in save.AddedEntries.Concat(save.ChangedEntries))
            if (e.OdometerReading is { } kept && !save.AddedReadings.Contains(kept) && db.Entry(kept).State == EntityState.Detached)
                db.Entry(kept).State = EntityState.Modified;
        foreach (var e in save.AddedEntries) db.Entry(e).State = EntityState.Added;
        foreach (var e in save.ChangedEntries) db.Entry(e).State = EntityState.Modified;
        // A line's expense (#288): its reading is the line's object (attached above), else its own; its cost new with it or changed.
        var createdCosts = save.ExpenseChanges.Select(c => c.CreatedCost).Concat(save.AddedExpenses.Select(e => e.Cost)).OfType<Cost>().ToHashSet();
        foreach (var r in save.ExpenseChanges.Select(c => c.CreatedReading).OfType<OdometerReading>()) db.Entry(r).State = EntityState.Added;
        foreach (var e in save.AddedExpenses.Concat(save.ChangedExpenses))
        {
            if (e.OdometerReading is { } reading && db.Entry(reading).State == EntityState.Detached) db.Entry(reading).State = EntityState.Modified;
            if (e.Cost is { } cost && db.Entry(cost).State == EntityState.Detached) db.Entry(cost).State = createdCosts.Contains(cost) ? EntityState.Added : EntityState.Modified;
        }
        foreach (var e in save.AddedExpenses) db.Entry(e).State = EntityState.Added;
        foreach (var e in save.ChangedExpenses) db.Entry(e).State = EntityState.Modified;
        await db.SaveChangesAsync(ct);
        var removedCosts = save.ExpenseChanges.Select(c => c.RemovedCost).OfType<Cost>().ToList();
        var removedReadings = save.RemovedReadings.Concat(save.ExpenseChanges.Select(c => c.RemovedReading).OfType<OdometerReading>()).Distinct().ToList();
        if (removedReadings.Count + removedCosts.Count > 0)
        {
            foreach (var c in removedCosts) db.Entry(c).State = EntityState.Deleted;
            foreach (var r in removedReadings) db.Entry(r).State = EntityState.Deleted;
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<bool> HasRoughFiguresAsync(Guid vehicleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TyreSets.AnyAsync(s => s.VehicleId == vehicleId && s.RoughDistance > 0, ct);
    }

    public async Task<IReadOnlyList<TyreTrashRow>> ListTrashAsync(OwnerScope scope, int skip, int take, CancellationToken ct, bool descending = true)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var trash = Trash(db, scope);
        var ordered = descending ? trash.OrderByDescending(r => r.DeletedAt) : trash.OrderBy(r => r.DeletedAt);
        var rows = await ordered.ThenBy(r => r.Id).Skip(skip).Take(take).ToListAsync(ct);
        return [.. rows.Select(r => new TyreTrashRow(r.IsSet ? TyreTrashKind.Set : TyreTrashKind.Entry, r.Id, r.VehicleId, r.SetName,
            r.EntryKind is null ? null : Enum.Parse<TyreEntryKind>(r.EntryKind), r.Day, r.Until, r.InstallationCount, r.DeletedAt, r.Version, r.WithExpense))];
    }

    public async Task<int> CountTrashAsync(OwnerScope scope, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Trash(db, scope).CountAsync(ct);
    }

    public async Task<int> PurgeAsync(OwnerScope scope, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sets = await db.TyreSets.Where(s => s.DeletedAt != null).InScope(scope, s => s.VehicleId).ToListAsync(ct);
        var setIds = sets.Select(s => s.Id).ToList();
        var installations = await db.TyreInstallations.Where(s => s.DeletedAt != null || setIds.Contains(s.SetId)).InScope(scope, s => s.VehicleId).ToListAsync(ct);
        // A set's lines go with it, trashed or not; a trashed line of a live set (or of none) is a row of the list.
        var entries = await db.TyreEntries.IgnoreQueryFilters().Include(e => e.OdometerReading)
            .Where(e => e.DeletedAt != null || (e.SetId != null && setIds.Contains(e.SetId.Value))).InScope(scope, e => e.VehicleId).ToListAsync(ct);
        var listed = sets.Count + entries.Count(e => e.SetId is not { } setId || !setIds.Contains(setId));
        // Through the change tracker (not the cascade alone), so devices get a tombstone for every row. A reading an expense still holds (the
        // line's amount made it, #288; in the trash or not) stays with it, and so does the expense.
        var readingIds = entries.Where(e => e.OdometerReadingId is not null).Select(e => e.OdometerReadingId!.Value).ToList();
        var held = (await db.Expenses.IgnoreQueryFilters().Where(x => x.OdometerReadingId != null && readingIds.Contains(x.OdometerReadingId.Value))
            .Select(x => x.OdometerReadingId!.Value).ToListAsync(ct)).ToHashSet();
        db.TyreEntries.RemoveRange(entries);
        db.OdometerReadings.RemoveRange(entries.Where(e => e.OdometerReading is not null && !held.Contains(e.OdometerReading.Id)).Select(e => e.OdometerReading!));
        db.TyreInstallations.RemoveRange(installations);
        db.TyreSets.RemoveRange(sets);
        await db.SaveChangesAsync(ct);
        return listed;
    }

    /// <summary>The trashed sets, and the trashed lines of no set or of a set not in the trash, in one query (each projected to the same shape).</summary>
    internal static IQueryable<TrashRow> Trash(AppDbContext db, OwnerScope scope)
    {
        var sets = db.TyreSets.Where(s => s.DeletedAt != null).InScope(scope, s => s.VehicleId)
            .Select(s => new TrashRow
            {
                IsSet = true, Id = s.Id, VehicleId = s.VehicleId, SetName = s.Name, EntryKind = null, Day = (DateOnly?)null, Until = (DateOnly?)null,
                InstallationCount = db.TyreInstallations.Count(x => x.SetId == s.Id && x.DeletedAt == null), DeletedAt = s.DeletedAt!.Value, Version = s.Version,
                WithExpense = false,
            });
        var entries = db.TyreEntries.IgnoreQueryFilters().Where(e => e.DeletedAt != null).InScope(scope, e => e.VehicleId)
            .Where(e => e.SetId == null || db.TyreSets.Any(s => s.Id == e.SetId && s.DeletedAt == null))
            .Select(e => new TrashRow
            {
                IsSet = false, Id = e.Id, VehicleId = e.VehicleId, SetName = db.TyreSets.Where(s => s.Id == e.SetId).Select(s => s.Name).FirstOrDefault(),
                EntryKind = e.Kind.ToString(), Day = e.Day, Until = db.TyreInstallations.Where(x => x.Id == e.InstallationId).Select(x => x.Until).FirstOrDefault(),
                InstallationCount = 0, DeletedAt = e.DeletedAt!.Value, Version = e.Version, WithExpense = e.ExpenseTrashedWithEntry,
            });
        return sets.Concat(entries);
    }

    internal sealed class TrashRow
    {
        public bool IsSet { get; init; }
        public Guid Id { get; init; }
        public Guid VehicleId { get; init; }
        public string? SetName { get; init; }

        /// <summary>The line's kind by name (a string: the two halves of the union must agree on the column's type on every provider).</summary>
        public string? EntryKind { get; init; }
        public DateOnly? Day { get; init; }
        public DateOnly? Until { get; init; }
        public int InstallationCount { get; init; }
        public DateTimeOffset DeletedAt { get; init; }
        public int Version { get; init; }
        public bool WithExpense { get; init; }
    }
}
