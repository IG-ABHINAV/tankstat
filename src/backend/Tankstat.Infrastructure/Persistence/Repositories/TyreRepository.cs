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
        var installations = await db.TyreInstallations.AsNoTracking().Where(s => s.VehicleId == vehicleId).ToListAsync(ct);
        var days = await db.TyreLogDays.AsNoTracking().Include(d => d.OdometerReading).Where(d => d.VehicleId == vehicleId).ToListAsync(ct);
        var readings = await db.OdometerReadings.AsNoTracking().Where(r => r.VehicleId == vehicleId).Select(r => new ReadingPoint(r.Date, r.Value)).ToListAsync(ct);
        return new TyreBook(sets, installations, days, TyreCalculator.Sorted(readings));
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
        catch (DbUpdateException e) when (e is not DbUpdateConcurrencyException && save.AddedSets.Count + save.AddedInstallations.Count + save.AddedDays.Count > 0)
        {
            failure = ExceptionDispatchInfo.Capture(e);
        }
        var sets = save.AddedSets.Select(s => s.Id).ToList();
        var installations = save.AddedInstallations.Select(s => s.Id).ToList();
        var days = save.AddedDays.Select(d => d.Id).ToList();
        await using var check = await dbFactory.CreateDbContextAsync(ct); // a fresh context: the failed one still holds the inserts
        if (await check.TyreSets.AnyAsync(s => sets.Contains(s.Id), ct) || await check.TyreInstallations.AnyAsync(s => installations.Contains(s.Id), ct)
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
        foreach (var s in save.AddedInstallations) db.TyreInstallations.Add(s);
        foreach (var s in save.ChangedInstallations) db.TyreInstallations.Update(s);
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
        return [.. rows.Select(r => new TyreTrashRow(r.IsSet ? TyreTrashKind.Set : TyreTrashKind.Installation, r.Id, r.VehicleId, r.SetName, r.From, r.Until, r.InstallationCount, r.DeletedAt, r.Version))];
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
        var listed = sets.Count + installations.Count(s => !setIds.Contains(s.SetId));
        // Through the change tracker (not the cascade alone), so devices get a tombstone for every row.
        db.TyreInstallations.RemoveRange(installations);
        db.TyreSets.RemoveRange(sets);
        await db.SaveChangesAsync(ct);
        return listed;
    }

    /// <summary>The trashed sets, and the trashed installations of sets not in the trash, in one query (each projected to the same shape).</summary>
    internal static IQueryable<TrashRow> Trash(AppDbContext db, OwnerScope scope)
    {
        var sets = db.TyreSets.Where(s => s.DeletedAt != null).InScope(scope, s => s.VehicleId)
            .Select(s => new TrashRow
            {
                IsSet = true, Id = s.Id, VehicleId = s.VehicleId, SetName = s.Name, From = (DateOnly?)null, Until = (DateOnly?)null,
                InstallationCount = db.TyreInstallations.Count(x => x.SetId == s.Id && x.DeletedAt == null), DeletedAt = s.DeletedAt!.Value, Version = s.Version,
            });
        var installations = db.TyreInstallations.Where(x => x.DeletedAt != null).InScope(scope, x => x.VehicleId)
            .Join(db.TyreSets.Where(s => s.DeletedAt == null), x => x.SetId, s => s.Id, (x, s) => new TrashRow
            {
                IsSet = false, Id = x.Id, VehicleId = x.VehicleId, SetName = s.Name, From = x.From, Until = x.Until,
                InstallationCount = 0, DeletedAt = x.DeletedAt!.Value, Version = x.Version,
            });
        return sets.Concat(installations);
    }

    internal sealed class TrashRow
    {
        public bool IsSet { get; init; }
        public Guid Id { get; init; }
        public Guid VehicleId { get; init; }
        public string SetName { get; init; } = "";
        public DateOnly? From { get; init; }
        public DateOnly? Until { get; init; }
        public int InstallationCount { get; init; }
        public DateTimeOffset DeletedAt { get; init; }
        public int Version { get; init; }
    }
}
