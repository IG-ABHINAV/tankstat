using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tankstat.Application.Expenses;
using Tankstat.Application.Settings;
using Tankstat.Application.Users;
using Tankstat.Application.Vehicles;
using Tankstat.Domain.Access;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Photos;
using Tankstat.Domain.Settings;
using Tankstat.Domain.Users;
using Tankstat.Domain.Vehicles;
using Tankstat.Infrastructure.Persistence;
using Tankstat.TestSupport;

namespace Tankstat.Infrastructure.UnitTests;

public class UserDataRepositoryTests
{
    private static readonly DateOnly Day = new(2026, 9, 1);
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<User> AddUser(TestDatabase db, string email)
    {
        var user = User.CreateLocal(email, null, false);
        await db.Get<IUserRepository>().AddAsync(user, default);
        return user;
    }

    private static async Task<(Vehicle Vehicle, Expense Expense)> AddVehicleWithExpense(TestDatabase db, Guid owner, Guid createdBy, bool trashed = false)
    {
        var vehicle = TestData.Vehicle(owner, "Car " + Guid.NewGuid().ToString("N")[..4]);
        if (trashed) vehicle.MarkDeleted(Now);
        await db.Get<IVehicleRepository>().AddAsync(vehicle, default);
        var cost = Cost.Create(owner, vehicle.Id, Day, 100, "EUR");
        var reading = OdometerReading.Create(owner, vehicle.Id, Day, 1000);
        var expense = Expense.Create(owner, createdBy, vehicle.Id, Day, "Tyres", null, cost, reading);
        await db.Get<IExpenseRepository>().AddAsync(expense, default);
        return (vehicle, expense);
    }

    private static async Task<AppDbContext> Context(TestDatabase db) =>
        await db.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();

    [Fact]
    public async Task OwnsData_IsTrueForVehiclesIncludingTrashedOnes()
    {
        await using var db = new TestDatabase();
        var alice = await AddUser(db, "alice@x.co");
        var bob = await AddUser(db, "bob@x.co");
        await AddVehicleWithExpense(db, alice.Id, alice.Id, trashed: true);
        var repo = db.Get<IUserDataRepository>();

        Assert.True(await repo.OwnsDataAsync(alice.Id, default));
        Assert.False(await repo.OwnsDataAsync(bob.Id, default));
    }

    [Fact]
    public async Task Move_HandsEverythingToTheTarget_AndDeletesTheUser()
    {
        await using var db = new TestDatabase();
        var alice = await AddUser(db, "alice@x.co");
        var bob = await AddUser(db, "bob@x.co");
        var carol = await AddUser(db, "carol@x.co");
        var (live, liveExpense) = await AddVehicleWithExpense(db, alice.Id, alice.Id);
        var (trashed, _) = await AddVehicleWithExpense(db, alice.Id, alice.Id, trashed: true);
        var (carols, carolsExpense) = await AddVehicleWithExpense(db, carol.Id, alice.Id); // Alice logged on Carol's vehicle

        await using (var seed = await Context(db))
        {
            seed.AccessGrants.Add(AccessGrant.Create(alice.Id, carol.Id, AccessLevel.View));     // moves
            seed.AccessGrants.Add(AccessGrant.Create(alice.Id, bob.Id, AccessLevel.Edit));       // would be Bob's own: dropped
            seed.AccessGrants.Add(AccessGrant.Create(carol.Id, alice.Id, AccessLevel.Edit));     // received: moves
            seed.ResourceGrants.Add(ResourceGrant.Create(ResourceType.Vehicle, carols.Id, alice.Id, GrantedFeature.Logs, AccessLevel.Edit)); // moves
            seed.ResourceGrants.Add(ResourceGrant.Create(ResourceType.Vehicle, live.Id, bob.Id, GrantedFeature.Logs, AccessLevel.Edit));     // Bob owns it now: dropped
            seed.LogPhotos.Add(LogPhoto.Create(alice.Id, live.Id, LogType.Expense, liveExpense.Id, Guid.NewGuid(), alice.Id, Now));          // her photo on her expense
            seed.LogPhotos.Add(LogPhoto.Create(carol.Id, carols.Id, LogType.Expense, carolsExpense.Id, Guid.NewGuid(), alice.Id, Now));      // her photo on Carol's expense
            seed.PhotoDrafts.Add(PhotoDraft.Create(Guid.NewGuid(), alice.Id, live.Id, alice.Id, Now));                                      // her draft on her vehicle
            seed.PhotoDrafts.Add(PhotoDraft.Create(Guid.NewGuid(), carol.Id, carols.Id, alice.Id, Now));                                    // her draft on Carol's vehicle
            await seed.SaveChangesAsync();
        }

        var purged = await db.Get<IUserDataRepository>().DeleteUserAsync(alice.Id, bob.Id, default);

        Assert.Empty(purged.VehicleIds);
        Assert.Empty(purged.PictureIds);
        await using var ctx = await Context(db);
        Assert.Null(await ctx.Users.FindAsync(alice.Id));
        Assert.All(await ctx.Vehicles.IgnoreQueryFilters().Where(v => v.Id == live.Id || v.Id == trashed.Id).ToListAsync(), v => Assert.Equal(bob.Id, v.OwnerId));
        var expense = await ctx.Expenses.IgnoreQueryFilters().SingleAsync(e => e.Id == liveExpense.Id);
        Assert.Equal(bob.Id, expense.OwnerId);
        Assert.Equal(bob.Id, expense.CreatedById);
        Assert.Equal(bob.Id, (await ctx.Costs.IgnoreQueryFilters().SingleAsync(c => c.Id == expense.CostId)).OwnerId);
        Assert.Equal(bob.Id, (await ctx.OdometerReadings.IgnoreQueryFilters().SingleAsync(r => r.Id == expense.OdometerReadingId)).OwnerId);
        var photos = await ctx.LogPhotos.ToListAsync();
        var onHers = photos.Single(p => p.LogId == liveExpense.Id);
        var onCarolsLog = photos.Single(p => p.LogId == carolsExpense.Id);
        Assert.Equal((bob.Id, bob.Id), (onHers.OwnerId, onHers.CreatedById));            // the photo of a moved expense is Bob's now
        Assert.Equal((carol.Id, bob.Id), (onCarolsLog.OwnerId, onCarolsLog.CreatedById)); // someone else's expense stays theirs, the authorship follows
        var drafts = await ctx.PhotoDrafts.ToListAsync();
        Assert.Equal((bob.Id, bob.Id), drafts.Where(d => d.VehicleId == live.Id).Select(d => (d.OwnerId, d.CreatedById)).Single());
        Assert.Equal((carol.Id, bob.Id), drafts.Where(d => d.VehicleId == carols.Id).Select(d => (d.OwnerId, d.CreatedById)).Single()); // drafts move like photos
        var onCarols = await ctx.Expenses.SingleAsync(e => e.Id == carolsExpense.Id);
        Assert.Equal(carol.Id, onCarols.OwnerId);   // someone else's vehicle stays theirs ...
        Assert.Equal(bob.Id, onCarols.CreatedById); // ... but the authorship follows

        var grants = await ctx.AccessGrants.ToListAsync();
        Assert.Contains(grants, g => g.OwnerId == bob.Id && g.GranteeId == carol.Id);
        Assert.Contains(grants, g => g.OwnerId == carol.Id && g.GranteeId == bob.Id);
        Assert.Equal(2, grants.Count);
        var resourceGrants = await ctx.ResourceGrants.ToListAsync();
        Assert.Equal(bob.Id, Assert.Single(resourceGrants).GranteeId);
    }

    [Fact]
    public async Task MoveAndPurge_DeleteTheUsersSettings_AndLeaveEveryoneElses()
    {
        await using var db = new TestDatabase();
        var alice = await AddUser(db, "alice@x.co");
        var bob = await AddUser(db, "bob@x.co");
        var carol = await AddUser(db, "carol@x.co");
        var (car, _) = await AddVehicleWithExpense(db, alice.Id, alice.Id);
        var (bobs, _) = await AddVehicleWithExpense(db, bob.Id, bob.Id);
        var (carols, _) = await AddVehicleWithExpense(db, carol.Id, carol.Id);
        var settings = db.Get<IUiSettingsRepository>();
        var orders = db.Get<IVehicleOrderRepository>();
        async Task<Guid[]> Positions(Guid user)
        {
            await using var ctx = await Context(db);
            return await ctx.VehicleOrders.Where(o => o.UserId == user).OrderBy(o => o.Position).Select(o => o.VehicleId).ToArrayAsync();
        }
        foreach (var who in new[] { alice.Id, bob.Id, carol.Id })
        {
            var row = UiSettings.Create(who, Now);
            row.SetNavOpen(false, Now);
            await settings.SaveAsync(row, default);
            await settings.SaveGridAsync(GridSettings.Create(who, "vehicles", new(["name"], [], 25, "name", Tankstat.Domain.SortDirection.Asc), Now), default);
        }
        await orders.ReplaceAsync(alice.Id, [VehicleOrder.Create(alice.Id, car.Id, 0), VehicleOrder.Create(alice.Id, bobs.Id, 1)], default);
        await orders.ReplaceAsync(bob.Id, [VehicleOrder.Create(bob.Id, carols.Id, 0), VehicleOrder.Create(bob.Id, car.Id, 1)], default); // Bob placed Carol's car first
        await orders.ReplaceAsync(carol.Id, [VehicleOrder.Create(carol.Id, car.Id, 0)], default);

        await db.Get<IUserDataRepository>().DeleteUserAsync(alice.Id, bob.Id, default); // moved to Bob

        Assert.Null(await settings.FindAsync(alice.Id, default));
        Assert.Empty(await settings.ListGridsAsync(alice.Id, default));
        Assert.Empty(await Positions(alice.Id));
        Assert.NotNull(await settings.FindAsync(bob.Id, default)); // his own, never merged with hers
        Assert.Single(await settings.ListGridsAsync(bob.Id, default));
        Assert.Equal([carols.Id, car.Id], await Positions(bob.Id));

        await db.Get<IUserDataRepository>().DeleteUserAsync(carol.Id, null, default); // purged, with her car

        Assert.Null(await settings.FindAsync(carol.Id, default));
        Assert.Empty(await Positions(carol.Id));
        Assert.Equal([car.Id], await Positions(bob.Id)); // Bob's position of her car went with it
    }

    [Fact]
    public async Task Move_SkipsGrantsTheTargetAlreadyHas()
    {
        await using var db = new TestDatabase();
        var alice = await AddUser(db, "alice@x.co");
        var bob = await AddUser(db, "bob@x.co");
        var carol = await AddUser(db, "carol@x.co");
        var (shared, _) = await AddVehicleWithExpense(db, carol.Id, carol.Id);
        await using (var seed = await Context(db))
        {
            seed.AccessGrants.Add(AccessGrant.Create(alice.Id, carol.Id, AccessLevel.View));
            seed.AccessGrants.Add(AccessGrant.Create(bob.Id, carol.Id, AccessLevel.Delete));
            seed.AccessGrants.Add(AccessGrant.Create(carol.Id, alice.Id, AccessLevel.View));
            seed.AccessGrants.Add(AccessGrant.Create(carol.Id, bob.Id, AccessLevel.Edit));
            seed.ResourceGrants.Add(ResourceGrant.Create(ResourceType.Vehicle, shared.Id, alice.Id, GrantedFeature.Logs, AccessLevel.Edit));
            seed.ResourceGrants.Add(ResourceGrant.Create(ResourceType.Vehicle, shared.Id, bob.Id, GrantedFeature.Logs, AccessLevel.Delete));
            await seed.SaveChangesAsync();
        }

        await db.Get<IUserDataRepository>().DeleteUserAsync(alice.Id, bob.Id, default);

        await using var ctx = await Context(db);
        Assert.Equal(AccessLevel.Delete, (await ctx.AccessGrants.SingleAsync(g => g.OwnerId == bob.Id)).Level);   // Bob's own grants win
        Assert.Equal(AccessLevel.Edit, (await ctx.AccessGrants.SingleAsync(g => g.OwnerId == carol.Id)).Level);
        Assert.Equal(AccessLevel.Delete, (await ctx.ResourceGrants.SingleAsync()).Level);
    }

    [Fact]
    public async Task Purge_DeletesTheUsersVehiclesAndGrants_AndLeavesOthersAlone()
    {
        await using var db = new TestDatabase();
        var alice = await AddUser(db, "alice@x.co");
        var bob = await AddUser(db, "bob@x.co");
        var (doomed, doomedExpense) = await AddVehicleWithExpense(db, alice.Id, alice.Id);
        var (trashed, _) = await AddVehicleWithExpense(db, alice.Id, alice.Id, trashed: true);
        var (kept, keptExpense) = await AddVehicleWithExpense(db, bob.Id, bob.Id);
        var picture = Guid.NewGuid();
        doomed.SetPicture(picture);
        await db.Get<IVehicleRepository>().UpdateAsync(doomed, default);
        await using (var seed = await Context(db))
        {
            seed.AccessGrants.Add(AccessGrant.Create(alice.Id, bob.Id, AccessLevel.View));
            seed.AccessGrants.Add(AccessGrant.Create(bob.Id, alice.Id, AccessLevel.View));
            seed.LogPhotos.Add(LogPhoto.Create(alice.Id, doomed.Id, LogType.Expense, doomedExpense.Id, Guid.NewGuid(), alice.Id, Now));
            seed.PhotoDrafts.Add(PhotoDraft.Create(Guid.NewGuid(), alice.Id, doomed.Id, alice.Id, Now));
            seed.PhotoDrafts.Add(PhotoDraft.Create(Guid.NewGuid(), bob.Id, kept.Id, alice.Id, Now)); // hers, on Bob's vehicle
            seed.ResourceGrants.Add(ResourceGrant.Create(ResourceType.Vehicle, doomed.Id, bob.Id, GrantedFeature.Logs, AccessLevel.Edit));
            seed.ResourceGrants.Add(ResourceGrant.Create(ResourceType.Vehicle, kept.Id, alice.Id, GrantedFeature.Logs, AccessLevel.Edit));
            await seed.SaveChangesAsync();
        }

        var purged = await db.Get<IUserDataRepository>().DeleteUserAsync(alice.Id, null, default);

        Assert.Equal([picture], purged.PictureIds);
        Assert.Equivalent(new[] { doomed.Id, trashed.Id }, purged.VehicleIds);
        await using var ctx = await Context(db);
        Assert.Null(await ctx.Users.FindAsync(alice.Id));
        Assert.Equal([kept.Id], await ctx.Vehicles.IgnoreQueryFilters().Select(v => v.Id).ToListAsync());
        Assert.Equal([keptExpense.Id], await ctx.Expenses.IgnoreQueryFilters().Select(e => e.Id).ToListAsync());
        Assert.Equal(1, await ctx.Costs.IgnoreQueryFilters().CountAsync());
        Assert.Equal(1, await ctx.OdometerReadings.IgnoreQueryFilters().CountAsync());
        Assert.Empty(await ctx.AccessGrants.ToListAsync());
        Assert.Empty(await ctx.ResourceGrants.ToListAsync());
        Assert.Empty(await ctx.LogPhotos.ToListAsync()); // the photo rows of the purged vehicles went with them
        // Drafts on purged vehicles went with them; one on someone else's vehicle stays until it expires, which also removes its file.
        Assert.Equal([kept.Id], await ctx.PhotoDrafts.Select(d => d.VehicleId).ToListAsync());
        Assert.NotNull(await ctx.Users.FindAsync(bob.Id));
        Assert.DoesNotContain(trashed.Id, await ctx.Vehicles.IgnoreQueryFilters().Select(v => v.Id).ToListAsync());
    }

    [Fact]
    public async Task Purge_AlsoDeletesChartsTheUserMadeOnOtherPeoplesVehicles()
    {
        await using var db = new TestDatabase();
        var alice = await AddUser(db, "alice@x.co");
        var bob = await AddUser(db, "bob@x.co");
        var (bobs, _) = await AddVehicleWithExpense(db, bob.Id, bob.Id);
        await using (var seed = await Context(db))
        {
            seed.VehicleCharts.Add(Tankstat.Domain.Charts.VehicleChart.Create(bobs.Id, alice.Id, "Mine", new Tankstat.Domain.Charts.ChartConfig(Tankstat.Domain.Charts.ChartMetric.TotalSpend, Tankstat.Domain.Charts.ChartGrouping.Month, Tankstat.Domain.Charts.ChartKind.Bar, Tankstat.Domain.Charts.ChartRange.Last1Month, false), false, Now));
            seed.VehicleCharts.Add(Tankstat.Domain.Charts.VehicleChart.Create(bobs.Id, bob.Id, "Bob's", new Tankstat.Domain.Charts.ChartConfig(Tankstat.Domain.Charts.ChartMetric.TotalSpend, Tankstat.Domain.Charts.ChartGrouping.Month, Tankstat.Domain.Charts.ChartKind.Bar, Tankstat.Domain.Charts.ChartRange.Last1Month, false), false, Now));
            await seed.SaveChangesAsync();
        }

        await db.Get<IUserDataRepository>().DeleteUserAsync(alice.Id, null, default);

        await using var ctx = await Context(db);
        Assert.Equal(["Bob's"], await ctx.VehicleCharts.Select(c => c.Title).ToListAsync());
    }

    [Fact]
    public async Task Delete_TakesTheUsersOwnDashboardLayoutsAlong_NeverTheVehiclesOrAnotherUsers()
    {
        await using var db = new TestDatabase();
        var alice = await AddUser(db, "alice@x.co");
        var bob = await AddUser(db, "bob@x.co");
        var (bobs, _) = await AddVehicleWithExpense(db, bob.Id, bob.Id);
        var layout = new Tankstat.Domain.Charts.DashboardLayoutValues([new Tankstat.Domain.Charts.DashboardTile("odometer", 0, 0, 1, 3)], []);
        await using (var seed = await Context(db))
        {
            seed.UserDashboardLayouts.Add(Tankstat.Domain.Charts.UserDashboardLayout.Create(alice.Id, bobs.Id, layout, Now));
            seed.UserDashboardLayouts.Add(Tankstat.Domain.Charts.UserDashboardLayout.Create(bob.Id, bobs.Id, layout, Now));
            seed.VehicleDashboardLayouts.Add(Tankstat.Domain.Charts.VehicleDashboardLayout.Create(bobs.Id, layout, Now));
            await seed.SaveChangesAsync();
        }

        await db.Get<IUserDataRepository>().DeleteUserAsync(alice.Id, bob.Id, default); // even when her data moves to Bob: a layout is a setting

        await using var ctx = await Context(db);
        Assert.Equal([bob.Id], await ctx.UserDashboardLayouts.Select(l => l.UserId).ToListAsync());
        Assert.Equal(1, await ctx.VehicleDashboardLayouts.CountAsync());
    }

    [Fact]
    public async Task Delete_RefusesTheLastActiveAdministrator_InsideTheTransaction()
    {
        await using var db = new TestDatabase();
        var only = User.CreateLocal("root@x.co", null, true);
        await db.Get<IUserRepository>().AddAsync(only, default);

        var ex = await Assert.ThrowsAsync<Tankstat.Domain.DomainException>(() => db.Get<IUserDataRepository>().DeleteUserAsync(only.Id, null, default));

        Assert.Equal("user.lastAdmin", ex.Key);
        Assert.NotNull(await db.Get<IUserRepository>().FindByIdAsync(only.Id, default));
    }
}
