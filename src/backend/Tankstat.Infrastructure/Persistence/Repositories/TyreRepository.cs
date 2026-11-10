using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Tankstat.Application.Access;
using Tankstat.Application.Tyres;
using Tankstat.Domain.Tyres;

namespace Tankstat.Infrastructure.Persistence.Repositories;

internal sealed class TyreRepository(IDbContextFactory<AppDbContext> dbFactory) : ITyreRepository
{
    public async Task<TyreBook> LoadAsync(Guid vehicleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sets = await db.TyreSets.AsNoTracking().Where(s => s.VehicleId == vehicleId).ToListAsync(ct);
        var stretches = await db.TyreStretches.AsNoTracking().Where(s => s.VehicleId == vehicleId).ToListAsync(ct);
        var days = await db.TyreLogDays.AsNoTracking().Include(d => d.OdometerReading).Where(d => d.VehicleId == vehicleId).ToListAsync(ct);
        var readings = await db.OdometerReadings.AsNoTracking().Where(r => r.VehicleId == vehicleId).Select(r => new ReadingPoint(r.Date, r.Value)).ToListAsync(ct);
        return new TyreBook(sets, stretches, days, TyreCalculator.Sorted(readings));
    }

    public async Task<TyreSet?> FindSetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TyreSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<TyreStretch?> FindStretchAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TyreStretches.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<TyreLogDay?> FindDayAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TyreLogDays.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    /// <summary>
    /// One transaction. Rows let go of (a day's reading, a day left empty) are deleted in a second save: the context never saw them linked,
    /// so in one save it could delete a reading before the day stops referring to it, which the database refuses. An insert that fails
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
        catch (DbUpdateException e) when (e is not DbUpdateConcurrencyException && save.AddedSets.Count + save.AddedStretches.Count + save.AddedDays.Count > 0)
        {
            failure = ExceptionDispatchInfo.Capture(e);
        }
        var sets = save.AddedSets.Select(s => s.Id).ToList();
        var stretches = save.AddedStretches.Select(s => s.Id).ToList();
        var days = save.AddedDays.Select(d => d.Id).ToList();
        await using var check = await dbFactory.CreateDbContextAsync(ct); // a fresh context: the failed one still holds the inserts
        if (await check.TyreSets.AnyAsync(s => sets.Contains(s.Id), ct) || await check.TyreStretches.AnyAsync(s => stretches.Contains(s.Id), ct)
            || await check.TyreLogDays.AnyAsync(d => days.Contains(d.Id), ct))
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
        foreach (var s in save.AddedStretches) db.TyreStretches.Add(s);
        foreach (var s in save.ChangedStretches) db.TyreStretches.Update(s);
        foreach (var r in save.AddedReadings) db.OdometerReadings.Add(r);
        foreach (var d in save.AddedDays) db.Entry(d).State = EntityState.Added;
        foreach (var d in save.ChangedDays.Concat(save.RemovedDays)) db.Entry(d).State = EntityState.Modified;
        foreach (var d in save.ChangedDays.Concat(save.RemovedDays))
            if (d.OdometerReading is { } kept && !save.AddedReadings.Contains(kept)) db.Entry(kept).State = EntityState.Modified;
        await db.SaveChangesAsync(ct);
        if (save.RemovedDays.Count + save.RemovedReadings.Count > 0)
        {
            foreach (var d in save.RemovedDays) db.Entry(d).State = EntityState.Deleted;
            foreach (var r in save.RemovedReadings) db.Entry(r).State = EntityState.Deleted;
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<bool> HasRoughFiguresAsync(Guid vehicleId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TyreSets.AnyAsync(s => s.VehicleId == vehicleId && s.RoughDistance > 0, ct);
    }

    public async Task<IReadOnlyList<TyreTrashRow>> ListTrashAsync(OwnerScope scope, int skip, int take, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await Trash(db, scope).OrderByDescending(r => r.DeletedAt).ThenBy(r => r.Id).Skip(skip).Take(take).ToListAsync(ct);
        return [.. rows.Select(r => new TyreTrashRow(r.IsSet ? TyreTrashKind.Set : TyreTrashKind.Stretch, r.Id, r.VehicleId, r.SetName, r.From, r.Until, r.StretchCount, r.DeletedAt, r.Version))];
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
        var stretches = await db.TyreStretches.Where(s => s.DeletedAt != null || setIds.Contains(s.SetId)).InScope(scope, s => s.VehicleId).ToListAsync(ct);
        var listed = sets.Count + stretches.Count(s => !setIds.Contains(s.SetId));
        // Through the change tracker (not the cascade alone), so devices get a tombstone for every row.
        db.TyreStretches.RemoveRange(stretches);
        db.TyreSets.RemoveRange(sets);
        await db.SaveChangesAsync(ct);
        return listed;
    }

    /// <summary>The trashed sets, and the trashed stretches of sets not in the trash, in one query (each projected to the same shape).</summary>
    internal static IQueryable<TrashRow> Trash(AppDbContext db, OwnerScope scope)
    {
        var sets = db.TyreSets.Where(s => s.DeletedAt != null).InScope(scope, s => s.VehicleId)
            .Select(s => new TrashRow
            {
                IsSet = true, Id = s.Id, VehicleId = s.VehicleId, SetName = s.Name, From = (DateOnly?)null, Until = (DateOnly?)null,
                StretchCount = db.TyreStretches.Count(x => x.SetId == s.Id && x.DeletedAt == null), DeletedAt = s.DeletedAt!.Value, Version = s.Version,
            });
        var stretches = db.TyreStretches.Where(x => x.DeletedAt != null).InScope(scope, x => x.VehicleId)
            .Join(db.TyreSets.Where(s => s.DeletedAt == null), x => x.SetId, s => s.Id, (x, s) => new TrashRow
            {
                IsSet = false, Id = x.Id, VehicleId = x.VehicleId, SetName = s.Name, From = x.From, Until = x.Until,
                StretchCount = 0, DeletedAt = x.DeletedAt!.Value, Version = x.Version,
            });
        return sets.Concat(stretches);
    }

    internal sealed class TrashRow
    {
        public bool IsSet { get; init; }
        public Guid Id { get; init; }
        public Guid VehicleId { get; init; }
        public string SetName { get; init; } = "";
        public DateOnly? From { get; init; }
        public DateOnly? Until { get; init; }
        public int StretchCount { get; init; }
        public DateTimeOffset DeletedAt { get; init; }
        public int Version { get; init; }
    }
}
