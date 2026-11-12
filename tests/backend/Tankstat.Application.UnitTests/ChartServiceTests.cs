using Tankstat.Application;
using Tankstat.Application.Auth;
using Tankstat.Application.Refuelings;
using Tankstat.Application.Stats;
using Tankstat.Domain;
using Tankstat.Domain.Access;
using Tankstat.Domain.Charts;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Users;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.UnitTests;

public class ChartServiceTests
{
    private static ChartConfig Config(ChartMetric metric = ChartMetric.TotalSpend) => new(metric, ChartGrouping.Month, ChartKind.Bar, ChartRange.Last6Months, false);

    private sealed record Scene(World W, User Alice, User Bob, Vehicle Car);

    private static async Task<Scene> Setup()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        var bob = w.AddUser("bob@x.co");
        w.Current.SignInAs(alice);
        return new Scene(w, alice, bob, await w.VehicleService.AddAsync("Car", null, FuelType.Petrol, MeasurementUnits.Metric, default));
    }

    // ---- statistics ----------------------------------------------------------------------------------------

    [Fact]
    public async Task ChartAndSummary_AreCalculatedFromTheVehiclesLogs()
    {
        var s = await Setup();
        await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(new DateOnly(2026, 9, 5), 40, 100, "EUR", 1000, true, null), default);
        await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(new DateOnly(2026, 9, 25), 30, 80, "EUR", 1500, true, null), default);

        var chart = await s.W.Stats.ChartAsync(s.Car.Id, Config(ChartMetric.AverageConsumption), default);
        var summary = await s.W.Stats.SummaryAsync(s.Car.Id, default);

        Assert.Contains(chart.Series.Single().Points, p => p.Key == "2026-09" && p.Value == 6m);
        Assert.Equal((new DateOnly(2026, 9, 25), 1500L, 6m, "EUR"), (summary!.LastFillUpDate, summary.LatestOdometer, summary.AverageConsumption, summary.Currency));
    }

    [Fact]
    public async Task Statistics_AreOnlyForPeopleWhoCanSeeTheLogs()
    {
        var s = await Setup();
        s.W.Current.SignInAs(s.Bob);

        Assert.Null(await s.W.Stats.SummaryAsync(s.Car.Id, default));
        await Assert.ThrowsAsync<NotFoundException>(() => s.W.Stats.ChartAsync(s.Car.Id, Config(), default));

        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.View));
        Assert.NotNull(await s.W.Stats.SummaryAsync(s.Car.Id, default));
    }

    [Fact]
    public async Task TheSummariesOfManyVehicles_AreWhatEachOneIsOnItsOwn()
    {
        var s = await Setup();
        await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(new DateOnly(2026, 9, 5), 40, 100, "EUR", 1000, true, null), default);
        await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(new DateOnly(2026, 9, 25), 30, 12000, "HUF", 1500, true, null), default);
        var empty = await s.W.VehicleService.AddAsync("Empty", null, FuelType.Petrol, MeasurementUnits.Metric, default);
        var trashed = await s.W.VehicleService.AddAsync("Old", null, FuelType.Petrol, MeasurementUnits.Metric, default);
        await s.W.RefuelingService.LogAsync(trashed.Id, new RefuelingInput(new DateOnly(2026, 9, 5), 40, 100, "EUR", 500, true, null), default);
        await s.W.VehicleService.DeleteAsync(trashed.Id, default);
        s.W.Current.SignInAs(s.Bob);
        var bobs = await s.W.VehicleService.AddAsync("Bob's", null, FuelType.Diesel, MeasurementUnits.Metric, default);
        await s.W.RefuelingService.LogAsync(bobs.Id, new RefuelingInput(new DateOnly(2026, 9, 7), 50, 90, "EUR", 300, true, null), default);
        var ids = new[] { s.Car.Id, empty.Id, trashed.Id, bobs.Id, Guid.NewGuid(), s.Car.Id };

        foreach (var who in new[] { s.Alice, s.Bob })
        {
            s.W.Current.SignInAs(who);
            var together = await s.W.Stats.SummariesAsync(ids, default);
            Assert.Equal(ids.Distinct().Count(), together.Count);
            foreach (var id in ids) Assert.Equivalent(await s.W.Stats.SummaryAsync(id, default), together[id]);
        }
        s.W.Current.SignInAs(s.Alice);
        var alices = await s.W.Stats.SummariesAsync(ids, default);
        Assert.Equal([true, true, false, false, false], new[] { s.Car.Id, empty.Id, trashed.Id, bobs.Id, ids[4] }.Select(id => alices[id] is not null));
    }

    [Fact]
    public async Task AnInvalidConfig_IsRejectedBeforeAnythingIsLoaded()
    {
        var s = await Setup();

        var error = await Assert.ThrowsAsync<DomainException>(() => s.W.Stats.ChartAsync(s.Car.Id, new ChartConfig(ChartMetric.FuelCost, ChartGrouping.Category, ChartKind.Bar, ChartRange.All, false), default));

        Assert.Equal("chart.categoryNeedsExpenses", error.Key);
    }

    // ---- saved charts --------------------------------------------------------------------------------------

    [Fact]
    public async Task ACreatorSeesOwnCharts_OthersOnlySeeSharedOnes()
    {
        var s = await Setup();
        await s.W.Sharing.SetLogAccessAsync(s.Car.Id, s.Bob.Id, AccessLevel.Edit, default);
        await s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("Mine", Config(), false), default);
        await s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("For all", Config(ChartMetric.FuelVolume), true), default);

        Assert.Equal(["Mine", "For all"], (await s.W.ChartService.ListAsync(s.Car.Id, default)).Select(c => c.Title));
        s.W.Current.SignInAs(s.Bob);
        Assert.Equal(["For all"], (await s.W.ChartService.ListAsync(s.Car.Id, default)).Select(c => c.Title));
    }

    [Fact]
    public async Task ViewersMayMakeOwnCharts_ButSharingNeedsEditAccess()
    {
        var s = await Setup();
        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.View));
        s.W.Current.SignInAs(s.Bob);

        var own = await s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("Bob's", Config(), false), default);
        var denied = await Assert.ThrowsAsync<ForbiddenException>(() => s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("Public", Config(), true), default));
        var denied2 = await Assert.ThrowsAsync<ForbiddenException>(() => s.W.ChartService.UpdateAsync(own.Id, new ChartInput("Bob's", Config(), true), default));

        Assert.Equal(("chart.needEditToShare", "chart.needEditToShare"), (denied.Key, denied2.Key));
    }

    [Fact]
    public async Task StrangersCannotSeeOrMakeCharts_AndPrivateChartsAreInvisibleToOthers()
    {
        var s = await Setup();
        var mine = await s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("Mine", Config(), false), default);
        s.W.Current.SignInAs(s.Bob);

        Assert.Empty(await s.W.ChartService.ListAsync(s.Car.Id, default));
        await Assert.ThrowsAsync<NotFoundException>(() => s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("x", Config(), false), default));
        await Assert.ThrowsAsync<NotFoundException>(() => s.W.ChartService.DeleteAsync(mine.Id, default));
    }

    [Fact]
    public async Task OnlyTheCreatorChangesAChart_ButDeleteAccessMayRemoveASharedOne()
    {
        var s = await Setup();
        await s.W.Sharing.SetLogAccessAsync(s.Car.Id, s.Bob.Id, AccessLevel.Edit, default);
        s.W.Current.SignInAs(s.Bob);
        var shared = await s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("Bob's shared", Config(), true), default);

        s.W.Current.SignInAs(s.Alice); // the owner has Delete access, and did not make it (#210: she may remove it, never change it)
        Assert.False(ChartService.MayChange(shared, s.W.Current.Principal!));
        Assert.True(ChartService.MayDelete(shared, s.W.Current.Principal!, await s.W.Access.LogLevelAsync(s.Car, default)));
        var notHers = await Assert.ThrowsAsync<ForbiddenException>(() => s.W.ChartService.UpdateAsync(shared.Id, new ChartInput("Hijacked", Config(), true), default));
        await s.W.ChartService.DeleteAsync(shared.Id, default);

        Assert.Equal("chart.notYours", notHers.Key);
        Assert.Empty(s.W.Charts.Items);
    }

    [Fact]
    public async Task EditorsWithoutDeleteAccess_CannotRemoveSomeoneElsesSharedChart()
    {
        var s = await Setup();
        await s.W.Sharing.SetLogAccessAsync(s.Car.Id, s.Bob.Id, AccessLevel.Edit, default);
        var shared = await s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("Alice's", Config(), true), default);
        s.W.Current.SignInAs(s.Bob);

        Assert.False(ChartService.MayChange(shared, s.W.Current.Principal!));
        Assert.False(ChartService.MayDelete(shared, s.W.Current.Principal!, await s.W.Access.LogLevelAsync(s.Car, default)));
        await Assert.ThrowsAsync<ForbiddenException>(() => s.W.ChartService.DeleteAsync(shared.Id, default));
    }

    public enum Role { Creator, Owner, Administrator, DeleteGrant, EditGrant, Viewer }

    // #210: the flags the UI shows the buttons by say exactly what the mutations allow, role by role, on someone else's shared chart.
    [Theory]
    [InlineData(Role.Creator, true, true)]
    [InlineData(Role.Owner, false, true)]
    [InlineData(Role.Administrator, false, true)]
    [InlineData(Role.DeleteGrant, false, true)]
    [InlineData(Role.EditGrant, false, false)]
    [InlineData(Role.Viewer, false, false)]
    public async Task TheRights_SayExactlyWhatTheMutationsAllow(Role role, bool canEdit, bool canDelete)
    {
        var s = await Setup();
        await s.W.Sharing.SetLogAccessAsync(s.Car.Id, s.Bob.Id, AccessLevel.Edit, default);
        s.W.Current.SignInAs(s.Bob);
        var shared = await s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("Bob's shared", Config(), true), default);
        var carol = s.W.AddUser("carol@x.co", admin: role == Role.Administrator);
        var (who, grant) = role switch
        {
            Role.Creator => (s.Bob, (AccessLevel?)null),
            Role.Owner => (s.Alice, null),
            Role.Administrator => (carol, null),
            Role.DeleteGrant => (carol, AccessLevel.Delete),
            Role.EditGrant => (carol, AccessLevel.Edit),
            _ => (carol, null), // a viewer: a log grant is Edit or Delete, so View comes from Alice's data as a whole
        };
        s.W.Current.SignInAs(s.Alice);
        if (grant is { } level) await s.W.Sharing.SetLogAccessAsync(s.Car.Id, carol.Id, level, default);
        if (role == Role.Viewer) s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, carol.Id, AccessLevel.View));
        s.W.Current.SignInAs(who);

        Assert.Equal(canEdit, ChartService.MayChange(shared, s.W.Current.Principal!));
        Assert.Equal(canDelete, ChartService.MayDelete(shared, s.W.Current.Principal!, await s.W.Access.LogLevelAsync(s.Car, default)));
        Task Update() => s.W.ChartService.UpdateAsync(shared.Id, new ChartInput("Changed", Config(), true), default);
        if (canEdit) await Update();
        else Assert.Equal("chart.notYours", (await Assert.ThrowsAsync<ForbiddenException>(Update)).Key);
        Task Delete() => s.W.ChartService.DeleteAsync(shared.Id, default);
        if (canDelete) await Delete();
        else Assert.Equal("chart.notYours", (await Assert.ThrowsAsync<ForbiddenException>(Delete)).Key);
        Assert.Equal(canDelete ? 0 : 1, s.W.Charts.Items.Count);
    }

    [Fact]
    public async Task Update_ChangesTheRecipe()
    {
        var s = await Setup();
        var chart = await s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("A", Config(), false), default);

        var updated = await s.W.ChartService.UpdateAsync(chart.Id, new ChartInput("B", Config(ChartMetric.FillUps), true), default);

        Assert.Equal(("B", ChartMetric.FillUps, true), (updated.Title, updated.Metric, updated.IsShared));
    }

    [Fact]
    public async Task InvalidCharts_AreRejected_AndThereIsALimitPerUser()
    {
        var s = await Setup();

        var invalid = await Assert.ThrowsAsync<DomainException>(() => s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("x", new ChartConfig(ChartMetric.FuelCost, ChartGrouping.Category, ChartKind.Bar, ChartRange.All, false), false), default));
        for (var i = 0; i < ChartService.MaxPerUserAndVehicle; i++) await s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput($"c{i}", Config(), false), default);
        var limit = await Assert.ThrowsAsync<DomainException>(() => s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("one too many", Config(), false), default));

        Assert.Equal(("chart.categoryNeedsExpenses", "chart.limit"), (invalid.Key, limit.Key));
    }

    // ---- saved key figures (#273) ---------------------------------------------------------------------------

    private static readonly FigureRecipe Spent = new(FigureMeasure.TotalSpend, new TrendPeriod(TrendWindowKind.ThisMonth));

    [Fact]
    public async Task ASavedKeyFigure_IsCreatedAndChanged_ButListedAmongTheChartsNever()
    {
        var s = await Setup();
        var chart = await s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("Bars", Config(), false), default);
        var figure = await s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("Spent", null, false, Spent), default);

        var updated = await s.W.ChartService.UpdateAsync(figure.Id, new ChartInput("Odometer", null, true, new FigureRecipe(FigureMeasure.Odometer)), default);
        var wrongKind = await Assert.ThrowsAsync<DomainException>(() => s.W.ChartService.UpdateAsync(figure.Id, new ChartInput("x", Config(), true), default));

        Assert.Equal(("Odometer", FigureMeasure.Odometer, true), (updated.Title, updated.Measure, updated.IsShared));
        Assert.Equal("chart.invalid", wrongKind.Key);
        Assert.Equal([chart.Id], (await s.W.ChartService.ListAsync(s.Car.Id, default)).Select(c => c.Id)); // an older app's list
        Assert.Equal([chart.Id, figure.Id], (await s.W.ChartService.ListWidgetsAsync(s.Car.Id, default)).Select(c => c.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheLimit_CountsSavedChartsAndKeyFiguresTogether(bool lastIsAFigure)
    {
        var s = await Setup();
        for (var i = 0; i < 20; i++) await s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput($"c{i}", Config(), false), default);
        for (var i = 0; i < 10; i++) await s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput($"f{i}", null, false, Spent), default);

        var limit = await Assert.ThrowsAsync<DomainException>(() => s.W.ChartService.CreateAsync(s.Car.Id,
            lastIsAFigure ? new ChartInput("one too many", null, false, Spent) : new ChartInput("one too many", Config(), false), default));

        Assert.Equal("chart.limit", limit.Key);
    }

    [Fact]
    public async Task ASavedKeyFigure_HasAChartsRights_SharingNeedsEdit_OnlyItsCreatorChangesIt()
    {
        var s = await Setup();
        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.View));
        var alices = await s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("Alice's", null, true, Spent), default);
        s.W.Current.SignInAs(s.Bob);

        var share = await Assert.ThrowsAsync<ForbiddenException>(() => s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("Public", null, true, Spent), default));
        var change = await Assert.ThrowsAsync<ForbiddenException>(() => s.W.ChartService.UpdateAsync(alices.Id, new ChartInput("Bob's now", null, true, Spent), default));
        var delete = await Assert.ThrowsAsync<ForbiddenException>(() => s.W.ChartService.DeleteAsync(alices.Id, default));
        var own = await s.W.ChartService.CreateAsync(s.Car.Id, new ChartInput("Bob's", null, false, Spent), default);

        Assert.Equal(("chart.needEditToShare", "chart.notYours", "chart.notYours"), (share.Key, change.Key, delete.Key));
        Assert.False(ChartService.MayChange(alices, s.W.Current.Principal!));
        Assert.True(ChartService.MayChange(own, s.W.Current.Principal!));
        s.W.Current.SignInAs(s.Alice); // the owner: Delete access removes someone else's, never changes it
        Assert.Equal("chart.notFound", (await Assert.ThrowsAsync<NotFoundException>(() => s.W.ChartService.DeleteAsync(own.Id, default))).Key); // Bob's private one is not hers to see
        await s.W.ChartService.DeleteAsync(alices.Id, default);
        Assert.Equal([own.Id], s.W.Charts.Items.Select(c => c.Id));
    }
}
