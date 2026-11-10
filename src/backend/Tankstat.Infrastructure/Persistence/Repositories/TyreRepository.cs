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

    public async Task<IReadOnlyList<TyreSet>> ListSetsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TyreSets.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TyreStretch>> ListStretchesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TyreStretches.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(ct);
    }

    /// <summary>
    /// One transaction. Rows let go of (a day's reading, a day left empty) are deleted in a second save: the context never saw them linked,
    /// so in one save it could delete a reading before the day stops referring to it, which the database refuses.
    /// </summary>
    public async Task SaveAsync(TyreSave save, CancellationToken ct)
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

    public async Task<IReadOnlyList<TyreTrashRow>> ListTrashAsync(OwnerScope scope, int skip, int take, CancellationToken ct, bool descending = true)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var trash = Trash(db, scope);
        var ordered = descending ? trash.OrderByDescending(r => r.DeletedAt) : trash.OrderBy(r => r.DeletedAt);
        var rows = await ordered.ThenBy(r => r.Id).Skip(skip).Take(take).ToListAsync(ct);
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
