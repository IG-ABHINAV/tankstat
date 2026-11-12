using Tankstat.Application.Vehicles;
using Tankstat.Application;
using Tankstat.Application.Access;
using Tankstat.Application.Auth;
using Tankstat.Application.Refuelings;
using Tankstat.Domain.Access;
using Tankstat.Domain.Users;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.UnitTests;

public class AccessServiceTests
{
    [Fact]
    public async Task NoAuth_ActsAsAnonymousWithFullAccess()
    {
        var w = new World(AuthMode.None);

        Assert.True((await w.Access.RequirePrincipalAsync(default)).IsAnonymous);
        Assert.True((await w.Access.ScopeAsync(AccessLevel.Edit, default)).IsAll);
    }

    [Fact]
    public void AScope_HoldsItsOwners_AndTheResourcesGrantedOneByOne()
    {
        var (alice, bob, car) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var scope = OwnerScope.Of([alice], [car]);

        Assert.True(OwnerScope.All.Contains(bob) && OwnerScope.All.Contains(bob, Guid.NewGuid()));
        Assert.Equal((true, false), (scope.Contains(alice), scope.Contains(bob)));
        Assert.Equal((true, true, false), (scope.Contains(alice, Guid.NewGuid()), scope.Contains(bob, car), scope.Contains(bob, Guid.NewGuid())));
    }

    [Fact]
    public async Task NoAuth_HasNoAdministratorFeatures()
    {
        var w = new World(AuthMode.None);

        await Assert.ThrowsAsync<ForbiddenException>(() => w.Access.RequireAdminAsync(default));
    }

    [Fact]
    public async Task Auth_WithoutSignIn_IsUnauthenticated()
    {
        var w = new World();

        await Assert.ThrowsAsync<UnauthenticatedException>(() => w.Access.RequirePrincipalAsync(default));
    }

    [Fact]
    public async Task Scope_IsOnlyOwnData_ByDefault()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        w.Current.SignInAs(alice);

        var scope = await w.Access.ScopeAsync(AccessLevel.View, default);

        Assert.False(scope.IsAll);
        Assert.Equal([alice.Id], scope.Owners);
    }

    [Fact]
    public async Task Scope_IncludesGrantedOwners_ByLevel()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        var bob = w.AddUser("bob@x.co");
        var carol = w.AddUser("carol@x.co");
        w.Grants.Items.Add(AccessGrant.Create(bob.Id, alice.Id, AccessLevel.View));
        w.Grants.Items.Add(AccessGrant.Create(carol.Id, alice.Id, AccessLevel.Edit));
        w.Current.SignInAs(alice);

        var view = await w.Access.ScopeAsync(AccessLevel.View, default);
        var edit = await w.Access.ScopeAsync(AccessLevel.Edit, default);

        Assert.Equivalent(new[] { alice.Id, bob.Id, carol.Id }, view.Owners);
        Assert.Equivalent(new[] { alice.Id, carol.Id }, edit.Owners);
    }

    [Fact]
    public async Task Scope_IsEverything_WhenInstanceDefaultAllows()
    {
        var w = new World();
        w.Settings.Value.SetDefaultLevelForOthers(AccessLevel.View);
        w.Current.SignInAs(w.AddUser("alice@x.co"));

        Assert.True((await w.Access.ScopeAsync(AccessLevel.View, default)).IsAll);
        Assert.False((await w.Access.ScopeAsync(AccessLevel.Edit, default)).IsAll);
    }

    [Fact]
    public async Task Admin_SeesEverything()
    {
        var w = new World();
        w.Current.SignInAs(w.AddUser("root@x.co", admin: true));

        Assert.True((await w.Access.ScopeAsync(AccessLevel.Edit, default)).IsAll);
    }
}

public class VehicleAccessTests
{
    private static async Task<(World W, User Alice, User Bob, Vehicle AlicesCar)> Setup()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        var bob = w.AddUser("bob@x.co");
        w.Current.SignInAs(alice);
        var car = await w.VehicleService.AddAsync("Alice car", null, FuelType.Petrol, default);
        w.Current.SignInAs(bob);
        return (w, alice, bob, car);
    }

    [Fact]
    public async Task Add_AssignsTheCurrentUserAsOwner()
    {
        var (w, alice, _, car) = await Setup();

        Assert.Equal(alice.Id, car.OwnerId);
        Assert.Same(car, Assert.Single(w.Vehicles.Items));
    }

    [Fact]
    public async Task OthersCannotSeeAVehicle_ByDefault()
    {
        var (w, _, _, car) = await Setup();

        Assert.Empty(await w.VehicleService.ListMineAsync(default));
        Assert.Null(await w.VehicleService.FindAsync(car.Id, default));
    }

    [Fact]
    public async Task ViewGrant_AllowsReading_ButNotLogging()
    {
        var (w, alice, bob, car) = await Setup();
        w.Grants.Items.Add(AccessGrant.Create(alice.Id, bob.Id, AccessLevel.View));

        Assert.Single(await w.VehicleService.ListMineAsync(default));
        Assert.NotNull(await w.VehicleService.FindAsync(car.Id, default));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            w.RefuelingService.LogAsync(car.Id, new(2026, 10, 1), 10, 10, 10, true, default));
    }

    [Fact]
    public async Task EditGrant_AllowsLogging_AndRefuelingBelongsToTheVehicleOwner()
    {
        var (w, alice, bob, car) = await Setup();
        w.Grants.Items.Add(AccessGrant.Create(alice.Id, bob.Id, AccessLevel.Edit));

        var refueling = await w.RefuelingService.LogAsync(car.Id, new(2026, 10, 1), 10, 10, 10, true, default);

        Assert.Equal(alice.Id, refueling.OwnerId);
    }

    [Fact]
    public async Task LoggingOnAnInvisibleVehicle_LooksLikeItDoesNotExist()
    {
        var (w, _, _, car) = await Setup();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            w.RefuelingService.LogAsync(car.Id, new(2026, 10, 1), 10, 10, 10, true, default));
        Assert.Empty(await w.RefuelingService.ListAsync(car.Id, new RefuelingQuery(), default));
    }

    [Fact]
    public async Task LoggingOnAMissingVehicle_IsNotFound()
    {
        var (w, _, _, _) = await Setup();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            w.RefuelingService.LogAsync(Guid.NewGuid(), new(2026, 10, 1), 10, 10, 10, true, default));
    }

    [Fact]
    public async Task Unauthenticated_CannotListOrAdd()
    {
        var (w, _, _, _) = await Setup();
        w.Current.Principal = null;

        await Assert.ThrowsAsync<UnauthenticatedException>(() => w.VehicleService.ListMineAsync(default));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => w.VehicleService.AddAsync("x", null, FuelType.Diesel, default));
    }

    [Fact]
    public async Task NoAuthMode_EveryoneSeesEverything()
    {
        var w = new World(AuthMode.None);
        await w.VehicleService.AddAsync("Shared", null, FuelType.Lpg, default);

        Assert.Single(await w.VehicleService.ListMineAsync(default));
    }
}

public class AccessAdminTests
{
    [Fact]
    public async Task OnlyAdministrators_CanChangeAccessRules()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        var bob = w.AddUser("bob@x.co");
        w.Current.SignInAs(alice);

        await Assert.ThrowsAsync<ForbiddenException>(() => w.AccessAdmin.SetDefaultLevelAsync(AccessLevel.Edit, default));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.AccessAdmin.SetGrantAsync(bob.Id, alice.Id, AccessLevel.View, default));
    }

    [Fact]
    public async Task Administrator_SetsDefault_AndManagesGrants()
    {
        var w = new World();
        var admin = w.AddUser("root@x.co", admin: true);
        var alice = w.AddUser("alice@x.co");
        var bob = w.AddUser("bob@x.co");
        w.Current.SignInAs(admin);

        await w.AccessAdmin.SetDefaultLevelAsync(AccessLevel.View, default);
        await w.AccessAdmin.SetGrantAsync(alice.Id, bob.Id, AccessLevel.View, default);
        await w.AccessAdmin.SetGrantAsync(alice.Id, bob.Id, AccessLevel.Edit, default); // upgrade, not duplicate

        Assert.Equal(AccessLevel.View, w.Settings.Value.DefaultLevelForOthers);
        Assert.Equal(AccessLevel.Edit, Assert.Single(await w.AccessAdmin.ListGrantsAsync(default)).Level);

        await w.AccessAdmin.SetGrantAsync(alice.Id, bob.Id, AccessLevel.None, default); // removes
        Assert.Empty(await w.AccessAdmin.ListGrantsAsync(default));
    }

    [Fact]
    public async Task Grant_ForUnknownUser_IsNotFound()
    {
        var w = new World();
        var admin = w.AddUser("root@x.co", admin: true);
        w.Current.SignInAs(admin);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            w.AccessAdmin.SetGrantAsync(admin.Id, Guid.NewGuid(), AccessLevel.View, default));
    }
}

public class LogLevelsTests
{
    [Fact]
    public async Task ManyVehiclesAtOnce_GetWhatEachGetsOnItsOwn()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        var bob = w.AddUser("bob@x.co");
        var carol = w.AddUser("carol@x.co");
        w.Current.SignInAs(alice);
        var alices = await w.VehicleService.AddAsync("Alice car", null, FuelType.Petrol, default);
        var trashed = await w.VehicleService.AddAsync("Alice old car", null, FuelType.Petrol, default);
        await w.VehicleService.DeleteAsync(trashed.Id, default);
        w.Current.SignInAs(carol);
        var carols = await w.VehicleService.AddAsync("Carol car", null, FuelType.Diesel, default);
        var carolsOther = await w.VehicleService.AddAsync("Carol van", null, FuelType.Diesel, default);
        w.Current.SignInAs(bob);
        var bobs = await w.VehicleService.AddAsync("Bob car", null, FuelType.Lpg, default);
        w.Grants.Items.Add(AccessGrant.Create(alice.Id, bob.Id, AccessLevel.View));
        w.ResourceGrants.Items.Add(ResourceGrant.Create(ResourceType.Vehicle, alices.Id, bob.Id, GrantedFeature.Logs, AccessLevel.Edit));
        w.ResourceGrants.Items.Add(ResourceGrant.Create(ResourceType.Vehicle, carols.Id, bob.Id, GrantedFeature.Logs, AccessLevel.Delete));
        var ids = new[] { alices.Id, trashed.Id, carols.Id, carolsOther.Id, bobs.Id, Guid.NewGuid() };

        foreach (var principal in new Principal?[] { null, new(Guid.NewGuid(), "Admin", "admin@x.co", IsAdmin: true) })
        {
            if (principal is not null) w.Current.Principal = principal;
            foreach (var defaults in new[] { AccessLevel.None, AccessLevel.View })
            {
                w.Settings.Value.SetDefaultLevelForOthers(defaults);
                var together = await w.RefuelingService.LevelsForVehiclesAsync(ids, default);

                Assert.Equal(ids.Length, together.Count);
                foreach (var id in ids) Assert.Equal(await w.RefuelingService.LevelForVehicleAsync(id, default), together[id]);
            }
        }
        w.Current.SignInAs(bob);
        w.Settings.Value.SetDefaultLevelForOthers(AccessLevel.None);
        var bobSees = await w.RefuelingService.LevelsForVehiclesAsync(ids, default);
        Assert.Equal([AccessLevel.Edit, AccessLevel.View, AccessLevel.Delete, AccessLevel.None, AccessLevel.Delete, AccessLevel.None], ids.Select(id => bobSees[id]));
    }

    [Fact]
    public async Task ManyOwnersAtOnce_GetWhatEachGetsOnItsOwn()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        var bob = w.AddUser("bob@x.co");
        var carol = w.AddUser("carol@x.co");
        var dave = w.AddUser("dave@x.co");
        w.Grants.Items.Add(AccessGrant.Create(alice.Id, bob.Id, AccessLevel.View));
        w.Grants.Items.Add(AccessGrant.Create(carol.Id, bob.Id, AccessLevel.Delete));
        var owners = new[] { alice.Id, bob.Id, carol.Id, dave.Id, Guid.NewGuid(), alice.Id };

        foreach (var principal in new[] { bob, alice })
        {
            w.Current.SignInAs(principal);
            foreach (var defaults in new[] { AccessLevel.None, AccessLevel.View, AccessLevel.Edit })
            {
                w.Settings.Value.SetDefaultLevelForOthers(defaults);
                var together = await w.Access.LevelsAsync(owners, default);

                Assert.Equal(owners.Distinct().Count(), together.Count);
                foreach (var owner in owners) Assert.Equal(await w.Access.LevelAsync(owner, default), together[owner]);
            }
        }
        w.Current.Principal = new(Guid.NewGuid(), "Admin", "admin@x.co", IsAdmin: true);
        Assert.All(await w.Access.LevelsAsync(owners, default), level => Assert.Equal(AccessLevel.Delete, level.Value));
        w.Current.SignInAs(bob);
        w.Settings.Value.SetDefaultLevelForOthers(AccessLevel.None);
        var bobGets = await w.Access.LevelsAsync(owners, default);
        Assert.Equal([AccessLevel.View, AccessLevel.Delete, AccessLevel.Delete, AccessLevel.None], new[] { alice.Id, bob.Id, carol.Id, dave.Id }.Select(o => bobGets[o]));
    }

    [Fact]
    public async Task RefuelingCountsOfManyVehicles_AreWhatEachCountsOnItsOwn()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        var bob = w.AddUser("bob@x.co");
        w.Current.SignInAs(alice);
        var twice = await w.VehicleService.AddAsync("Alice car", null, FuelType.Petrol, default);
        var none = await w.VehicleService.AddAsync("Alice van", null, FuelType.Petrol, default);
        var trashed = await w.VehicleService.AddAsync("Alice old car", null, FuelType.Petrol, default);
        foreach (var (car, odometer) in new[] { (twice, 1000L), (twice, 1500L), (trashed, 900L) })
            await w.RefuelingService.LogAsync(car.Id, new RefuelingInput(new DateOnly(2026, 9, 1), 40, 60, "EUR", odometer, true, null), default);
        var log = await w.RefuelingService.LogAsync(twice.Id, new RefuelingInput(new DateOnly(2026, 9, 2), 40, 60, "EUR", 2000, true, null), default);
        await w.RefuelingService.DeleteAsync(log.Id, default);
        await w.VehicleService.DeleteAsync(trashed.Id, default);
        w.Current.SignInAs(bob);
        var bobs = await w.VehicleService.AddAsync("Bob car", null, FuelType.Lpg, default);
        await w.RefuelingService.LogAsync(bobs.Id, new RefuelingInput(new DateOnly(2026, 9, 1), 40, 60, "EUR", 500, true, null), default);
        var ids = new[] { twice.Id, none.Id, trashed.Id, bobs.Id, Guid.NewGuid() };

        foreach (var who in new[] { alice, bob })
        {
            w.Current.SignInAs(who);
            var together = await w.RefuelingService.CountsAsync(ids, default);
            foreach (var id in ids) Assert.Equal(await w.RefuelingService.CountAsync(id, default), together[id]);
        }
        w.Current.SignInAs(alice);
        var alicesCounts = await w.RefuelingService.CountsAsync(ids, default);
        Assert.Equal([2, 0, 0, 0, 0], ids.Select(id => alicesCounts[id]));
    }
}
