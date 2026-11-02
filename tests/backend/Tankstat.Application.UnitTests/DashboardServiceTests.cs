using Tankstat.Application.Auth;
using Tankstat.Application.Stats;
using Tankstat.Domain;
using Tankstat.Domain.Access;
using Tankstat.Domain.Charts;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Users;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.UnitTests;

public class DashboardServiceTests
{
    private static readonly ChartConfig Config = new(ChartMetric.FuelVolume, ChartGrouping.Month, ChartKind.Bar, ChartRange.Last6Months, false);

    private sealed record Scene(World W, User Alice, User Bob, Vehicle Car);

    private static async Task<Scene> Setup()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        var bob = w.AddUser("bob@x.co");
        w.Current.SignInAs(alice);
        return new Scene(w, alice, bob, await w.VehicleService.AddAsync("Car", null, FuelType.Petrol, MeasurementUnits.Metric, default));
    }

    private static DashboardLayoutValues Layout(string[]? charts = null, string[]? hidden = null) =>
        new([new DashboardTile("odometer", 0, 0, 1, 3), .. (charts ?? ["preset:distance"]).Select((c, i) => new DashboardTile(c, 0, 3 + i * 8, 2, 8))], hidden ?? []);

    private static IEnumerable<string> ChartsOf(DashboardLayout layout) => layout.Tiles.Select(t => t.Id).Where(id => id != "odometer");

    private static string Ref(VehicleChart chart) => $"chart:{chart.Id}";

    [Fact]
    public async Task ADashboard_IsTheStandardPreset_UntilSomeoneArrangesIt_AndAPersonalOneWinsForItsOwnerAlone()
    {
        var s = await Setup();
        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.View));

        var fresh = (await s.W.Dashboards.GetAsync(s.Car.Id, default))!;
        Assert.Equal((null, null, true), ((object?)fresh.Shared, (object?)fresh.Personal, fresh.CanChangeShared));
        await s.W.Dashboards.SaveAsync(s.Car.Id, personal: false, Layout(), default);
        s.W.Current.SignInAs(s.Bob);
        await s.W.Dashboards.SaveAsync(s.Car.Id, personal: true, Layout(["preset:consumption"]), default);

        var bobs = (await s.W.Dashboards.GetAsync(s.Car.Id, default))!;
        Assert.Equal(["preset:distance"], ChartsOf(bobs.Shared!));
        Assert.Equal(["preset:consumption"], ChartsOf(bobs.Personal!));
        Assert.False(bobs.CanChangeShared);
        s.W.Current.SignInAs(s.Alice);
        Assert.Null((await s.W.Dashboards.GetAsync(s.Car.Id, default))!.Personal);
    }

    [Fact]
    public async Task TheVehiclesDashboard_NeedsEditAccess_AnyoneWhoSeesTheVehicleMayArrangeTheirOwn_OthersFindNoVehicle()
    {
        var s = await Setup();
        s.W.Current.SignInAs(s.Bob);
        await Assert.ThrowsAsync<NotFoundException>(() => s.W.Dashboards.SaveAsync(s.Car.Id, personal: true, Layout(), default));
        Assert.Null(await s.W.Dashboards.GetAsync(s.Car.Id, default));

        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.View));
        var refused = await Assert.ThrowsAsync<ForbiddenException>(() => s.W.Dashboards.SaveAsync(s.Car.Id, personal: false, Layout(), default));
        await Assert.ThrowsAsync<ForbiddenException>(() => s.W.Dashboards.ResetAsync(s.Car.Id, personal: false, default));
        await s.W.Dashboards.SaveAsync(s.Car.Id, personal: true, Layout(), default);

        Assert.Equal("dashboard.needEditToChange", refused.Key);
        Assert.Single(s.W.DashboardLayouts.Personal);
        Assert.Empty(s.W.DashboardLayouts.Shared);
    }

    [Fact]
    public async Task ASavedChart_StaysOnlyWhereItsAudienceSeesIt_AndAnythingElseIsDropped()
    {
        var s = await Setup();
        var mine = await s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("Mine", Config, false), default);
        var shared = await s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("Shared", Config, true), default);
        var gone = Guid.NewGuid();

        await s.W.Dashboards.SaveAsync(s.Car.Id, personal: false, Layout([Ref(shared), Ref(mine), $"chart:{gone}", "preset:distance"], [Ref(mine), Ref(shared)]), default);
        await s.W.Dashboards.SaveAsync(s.Car.Id, personal: true, Layout([Ref(mine), Ref(shared), $"chart:{gone}"], [Ref(mine)]), default);

        var vehicles = s.W.DashboardLayouts.Shared.Single();
        var alices = s.W.DashboardLayouts.Personal.Single();
        Assert.Equal([Ref(shared), "preset:distance"], ChartsOf(vehicles)); // a private chart is no one else's: not on the vehicle's dashboard
        Assert.Equal([Ref(shared)], vehicles.Hidden);
        Assert.Equal([Ref(mine), Ref(shared)], ChartsOf(alices));
        Assert.Equal([Ref(mine)], alices.Hidden);
    }

    [Fact]
    public async Task AnotherVehiclesChart_IsDroppedToo()
    {
        var s = await Setup();
        var other = await s.W.VehicleService.AddAsync("Other", null, FuelType.Petrol, MeasurementUnits.Metric, default);
        var foreign = await s.W.ChartService.CreateAsync(other.Id, new ChartInput("Elsewhere", Config, true), default);

        await s.W.Dashboards.SaveAsync(s.Car.Id, personal: true, Layout([Ref(foreign)]), default);

        Assert.Empty(ChartsOf(s.W.DashboardLayouts.Personal.Single()));
    }

    [Fact]
    public async Task Resetting_TheVehiclesGoesBackToStandard_APersonalOneToTheVehicles()
    {
        var s = await Setup();
        await s.W.Dashboards.SaveAsync(s.Car.Id, personal: false, Layout(), default);
        await s.W.Dashboards.SaveAsync(s.Car.Id, personal: true, Layout(), default);

        await s.W.Dashboards.ResetAsync(s.Car.Id, personal: true, default);
        Assert.Equal((true, false), (s.W.DashboardLayouts.Shared.Count == 1, s.W.DashboardLayouts.Personal.Count == 1));
        await s.W.Dashboards.ResetAsync(s.Car.Id, personal: false, default);
        Assert.Empty(s.W.DashboardLayouts.Shared);
    }

    [Fact]
    public async Task AnEntryOutOfShape_IsRefused_BeforeAnythingIsSaved()
    {
        var s = await Setup();

        var key = (await Assert.ThrowsAsync<DomainException>(() => s.W.Dashboards.SaveAsync(s.Car.Id, personal: true, new([new DashboardTile("Odometer!", 0, 0, 1, 3)], []), default))).Key;

        Assert.Equal("dashboard.itemInvalid", key);
        Assert.Empty(s.W.DashboardLayouts.Personal);
    }

    [Fact]
    public async Task SavingAndResetting_AreLoggedWithIdsOnly()
    {
        var s = await Setup();

        await s.W.Dashboards.SaveAsync(s.Car.Id, personal: true, Layout(["preset:zzzzsecret"]), default);

        var line = Assert.Single(s.W.Log.From<DashboardService>());
        Assert.Equal((s.Alice.Id, s.Car.Id), (line.Values["UserId"], line.Values["VehicleId"]));
        Assert.False(s.W.Log.Mentions("zzzzsecret"));
    }
}
