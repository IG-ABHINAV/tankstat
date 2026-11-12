using Microsoft.EntityFrameworkCore;
using Tankstat.Application.Users;
using Tankstat.Domain;
using Tankstat.Domain.Access;

namespace Tankstat.Infrastructure.Persistence.Repositories;

/// <summary>
/// Hands a user's data to someone else or purges it, then deletes the user, all in one transaction. The owner and creator columns have
/// no foreign key to the users, so everything is updated here, and trashed rows are included (<c>IgnoreQueryFilters</c>).
/// Same-table collision checks load ids first instead of using a subquery, because MySQL does not allow those in updates.
/// </summary>
internal sealed class UserDataRepository(IDbContextFactory<AppDbContext> dbFactory, TimeProvider clock) : IUserDataRepository
{
    public async Task<bool> OwnsDataAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Vehicles.IgnoreQueryFilters().AnyAsync(v => v.OwnerId == userId, ct)
               || await db.Refuelings.IgnoreQueryFilters().AnyAsync(r => r.OwnerId == userId, ct)
               || await db.Expenses.IgnoreQueryFilters().AnyAsync(e => e.OwnerId == userId, ct);
    }

    public async Task<PurgedUserData> DeleteUserAsync(Guid userId, Guid? moveDataTo, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);

        // Checked inside the transaction so two administrators deleting each other cannot both pass the check made before.
        var doomed = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (doomed is { IsAdmin: true, IsDisabled: false } && !await db.Users.AnyAsync(u => u.Id != userId && u.IsAdmin && !u.IsDisabled, ct))
            throw new DomainException("user.lastAdmin", "The last active administrator cannot be deleted.");

        var purged = PurgedUserData.None;
        if (moveDataTo is { } to) await MoveAsync(db, userId, to, clock.GetUtcNow(), ct);
        else purged = await PurgeAsync(db, userId, clock.GetUtcNow(), ct);

        // Whatever grants are left (purge, or none to move) must go before the user: grantee-side access grants are Restrict.
        await db.AccessGrants.Where(g => g.OwnerId == userId || g.GranteeId == userId).ExecuteDeleteAsync(ct);
        await db.ResourceGrants.Where(g => g.GranteeId == userId).ExecuteDeleteAsync(ct);
        await db.PasswordResetTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
        await db.UserSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct); // their devices are signed out with them
        await db.Notifications.Where(n => n.RecipientId == userId).ExecuteDeleteAsync(ct); // a user's inbox is theirs alone, never moved
        // What the UI remembered for them is theirs alone too (other users' positions on purged vehicles went through the cascade).
        await db.UiSettings.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
        await db.GridSettings.Where(g => g.UserId == userId).ExecuteDeleteAsync(ct);
        await db.VehicleOrders.Where(o => o.UserId == userId).ExecuteDeleteAsync(ct);
        await db.UserDashboardLayouts.Where(l => l.UserId == userId).ExecuteDeleteAsync(ct);
        // Changes they sent from their devices: kept with the data when it moves (below), else they go (the vehicle's own go with it).
        await db.SyncChanges.Where(c => c.SubmittedById == userId).ExecuteDeleteAsync(ct);
        await db.OfflineSettings.Where(o => o.UserId == userId).ExecuteDeleteAsync(ct);
        await db.OfflineVehicleSettings.Where(o => o.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        return purged;
    }

    /// <param name="now">The moved vehicles, logs and schedules show their new owner or creator: devices download them again.</param>
    private static async Task MoveAsync(AppDbContext db, Guid from, Guid to, DateTimeOffset now, CancellationToken ct)
    {
        await db.Vehicles.IgnoreQueryFilters().Where(x => x.OwnerId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerId, to).SetProperty(x => x.UpdatedAt, now), ct);
        await db.Refuelings.IgnoreQueryFilters().Where(x => x.OwnerId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerId, to).SetProperty(x => x.UpdatedAt, now), ct);
        await db.Expenses.IgnoreQueryFilters().Where(x => x.OwnerId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerId, to).SetProperty(x => x.UpdatedAt, now), ct);
        await db.OdometerReadings.IgnoreQueryFilters().Where(x => x.OwnerId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerId, to), ct);
        await db.Costs.IgnoreQueryFilters().Where(x => x.OwnerId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerId, to), ct);

        await db.Refuelings.IgnoreQueryFilters().Where(x => x.CreatedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedById, to).SetProperty(x => x.UpdatedAt, now), ct);
        await db.Expenses.IgnoreQueryFilters().Where(x => x.CreatedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedById, to).SetProperty(x => x.UpdatedAt, now), ct);
        await db.RecurringExpenses.Where(x => x.OwnerId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerId, to).SetProperty(x => x.UpdatedAt, now), ct);
        await db.RecurringExpenses.Where(x => x.CreatedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedById, to).SetProperty(x => x.UpdatedAt, now), ct);
        // Who changed them last (not downloaded: no UpdatedAt).
        await db.Vehicles.IgnoreQueryFilters().Where(x => x.ChangedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ChangedById, to), ct);
        await db.Refuelings.IgnoreQueryFilters().Where(x => x.ChangedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ChangedById, to), ct);
        await db.Expenses.IgnoreQueryFilters().Where(x => x.ChangedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ChangedById, to), ct);
        await db.RecurringExpenses.Where(x => x.ChangedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ChangedById, to), ct);
        // Tyres (#287): sets, their installations and the tyre log's lines, like the logs (trashed ones too: there is no query filter on them).
        await db.TyreSets.Where(x => x.OwnerId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerId, to).SetProperty(x => x.UpdatedAt, now), ct);
        await db.TyreSets.Where(x => x.CreatedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedById, to).SetProperty(x => x.UpdatedAt, now), ct);
        await db.TyreSets.Where(x => x.ChangedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ChangedById, to), ct);
        await db.TyreInstallations.Where(x => x.OwnerId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerId, to).SetProperty(x => x.UpdatedAt, now), ct);
        await db.TyreInstallations.Where(x => x.CreatedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedById, to).SetProperty(x => x.UpdatedAt, now), ct);
        await db.TyreInstallations.Where(x => x.ChangedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ChangedById, to), ct);
        await db.TyreEntries.Where(x => x.OwnerId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerId, to).SetProperty(x => x.UpdatedAt, now), ct);
        await db.TyreEntries.Where(x => x.CreatedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedById, to).SetProperty(x => x.UpdatedAt, now), ct);
        await db.TyreEntries.Where(x => x.ChangedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ChangedById, to), ct);
        await db.VehicleCharts.Where(x => x.CreatedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedById, to), ct);
        await db.LogPhotos.Where(x => x.OwnerId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerId, to), ct);
        await db.LogPhotos.Where(x => x.CreatedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedById, to), ct);
        // Drafts move too rather than vanish: their files are only removed when the drafts expire (or the vehicle goes).
        await db.PhotoDrafts.Where(x => x.OwnerId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerId, to), ct);
        await db.PhotoDrafts.Where(x => x.CreatedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedById, to), ct);
        await db.SyncChanges.Where(x => x.OwnerId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerId, to), ct);
        await db.SyncChanges.Where(x => x.SubmittedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.SubmittedById, to), ct);
        await db.SyncChanges.Where(x => x.ResolvedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResolvedById, to), ct);

        await MoveAccessGrantsAsync(db, from, to, ct);
        await MoveResourceGrantsAsync(db, from, to, ct);
    }

    /// <summary>Grants given by the user become the target's, grants the user received too; ones the target already has, or would give itself, are dropped.</summary>
    private static async Task MoveAccessGrantsAsync(AppDbContext db, Guid from, Guid to, CancellationToken ct)
    {
        var all = await db.AccessGrants.AsNoTracking().Where(g => g.OwnerId == from || g.GranteeId == from || g.OwnerId == to || g.GranteeId == to).ToListAsync(ct);

        var existing = all.Where(g => g.OwnerId == to && g.GranteeId != from).Select(g => g.GranteeId).ToHashSet();
        var given = all.Where(g => g.OwnerId == from).ToList();
        var dropGiven = given.Where(g => g.GranteeId == to || existing.Contains(g.GranteeId)).Select(g => g.Id).ToList();
        await db.AccessGrants.Where(g => dropGiven.Contains(g.Id)).ExecuteDeleteAsync(ct);
        await db.AccessGrants.Where(g => g.OwnerId == from).ExecuteUpdateAsync(s => s.SetProperty(g => g.OwnerId, to), ct);

        var held = all.Where(g => g.GranteeId == to && g.OwnerId != from).Select(g => g.OwnerId).ToHashSet();
        var received = all.Where(g => g.GranteeId == from).ToList();
        var dropReceived = received.Where(g => g.OwnerId == to || held.Contains(g.OwnerId)).Select(g => g.Id).ToList();
        await db.AccessGrants.Where(g => dropReceived.Contains(g.Id)).ExecuteDeleteAsync(ct);
        await db.AccessGrants.Where(g => g.GranteeId == from).ExecuteUpdateAsync(s => s.SetProperty(g => g.GranteeId, to), ct);
    }

    private static async Task MoveResourceGrantsAsync(AppDbContext db, Guid from, Guid to, CancellationToken ct)
    {
        var mine = await db.ResourceGrants.AsNoTracking().Where(g => g.GranteeId == from).ToListAsync(ct);
        var theirs = await db.ResourceGrants.AsNoTracking().Where(g => g.GranteeId == to).ToListAsync(ct);
        var taken = theirs.Select(g => (g.ResourceType, g.ResourceId, g.Feature)).ToHashSet();
        var duplicate = mine.Where(g => taken.Contains((g.ResourceType, g.ResourceId, g.Feature))).Select(g => g.Id).ToList();
        await db.ResourceGrants.Where(g => duplicate.Contains(g.Id)).ExecuteDeleteAsync(ct);
        await db.ResourceGrants.Where(g => g.GranteeId == from).ExecuteUpdateAsync(s => s.SetProperty(g => g.GranteeId, to), ct);

        // The target now owns the user's vehicles (and may have been granted some of them): owners need no grant for their own vehicle.
        var owned = await db.Vehicles.IgnoreQueryFilters().AsNoTracking().Where(v => v.OwnerId == to).Select(v => v.Id).ToListAsync(ct);
        await db.ResourceGrants
            .Where(g => g.ResourceType == ResourceType.Vehicle && g.GranteeId == to && owned.Contains(g.ResourceId))
            .ExecuteDeleteAsync(ct);
    }

    private static async Task<PurgedUserData> PurgeAsync(AppDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var vehicles = await db.Vehicles.IgnoreQueryFilters().Where(v => v.OwnerId == userId).ToListAsync(ct);
        var ids = vehicles.Select(v => v.Id).ToList();
        await db.ResourceGrants.Where(g => g.ResourceType == ResourceType.Vehicle && ids.Contains(g.ResourceId)).ExecuteDeleteAsync(ct);

        // Charts they made on other people's vehicles are theirs too (a move re-points them instead).
        await db.VehicleCharts.Where(c => c.CreatedById == userId).ExecuteDeleteAsync(ct);
        // Likewise their schedules on other people's vehicles; removed through the change tracker, so devices get their tombstones.
        var schedules = await db.RecurringExpenses.Where(r => r.CreatedById == userId && !ids.Contains(r.VehicleId)).ToListAsync(ct);
        await db.TouchExpensesOfSchedulesAsync([.. schedules.Select(r => r.Id)], now, ct); // the expenses that list them, before the links cascade away
        db.RecurringExpenses.RemoveRange(schedules);

        db.Vehicles.RemoveRange(vehicles); // logs, readings, costs, charts and photo rows go with them through the database's cascade
        await db.SaveChangesAsync(ct);
        return new PurgedUserData(ids, vehicles.Where(v => v.PictureImageId is not null).Select(v => v.PictureImageId!.Value).ToList());
    }
}
