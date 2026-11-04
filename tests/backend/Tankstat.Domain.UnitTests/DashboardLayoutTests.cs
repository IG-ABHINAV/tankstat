using Tankstat.Domain;
using Tankstat.Domain.Charts;

namespace Tankstat.Domain.UnitTests;

public class DashboardLayoutTests
{
    private static string Key(Action action) => Assert.Throws<DomainException>(action).Key;

    private static readonly DateOnly Today = new(2026, 11, 3);

    private static DashboardTile Tile(string id, int x = 0, int y = 0, int w = 1, int h = 3, bool pinned = false) => new(id, x, y, w, h, pinned);

    [Fact]
    public void ALayout_KeepsItsTilesWithTheirPlaces_WithoutDuplicates()
    {
        var chart = Guid.NewGuid();
        var layout = VehicleDashboardLayout.Create(Guid.NewGuid(), new([Tile("thisMonth"), Tile("preset:monthlyCosts", 1, 0, 2, 8, pinned: true), Tile("thisMonth", 3, 0), Tile($"chart:{chart}", 0, 8, 4, 6)], [$"chart:{chart}"]), DateTimeOffset.UnixEpoch);

        Assert.Equal(["thisMonth", "preset:monthlyCosts", $"chart:{chart}"], layout.Tiles.Select(t => t.Id));
        Assert.Equal((1, 0, 2, 8, true), (layout.Tiles[1].X, layout.Tiles[1].Y, layout.Tiles[1].W, layout.Tiles[1].H, layout.Tiles[1].Pinned));
        Assert.Equal(chart, DashboardLayoutValues.ChartIdOf(layout.Hidden.Single()));
    }

    [Theory]
    [InlineData("Odometer")] // a figure starts with a lower-case letter
    [InlineData("this-month")]
    [InlineData("")]
    [InlineData("preset:")]
    [InlineData("chart:not-a-guid")]
    [InlineData("odometer\n")] // a newline at the end is no id either
    [InlineData("preset:distance\n")]
    public void ATile_NamesAFigure_AReadyMadeChart_OrASavedChart(string id) =>
        Assert.Equal("dashboard.itemInvalid", Key(() => new DashboardLayoutValues([Tile(id)], []).Checked(Today)));

    [Theory]
    [InlineData(-1, 0, 1, 3)] // left of the grid
    [InlineData(3, 0, 2, 3)] // past its right edge
    [InlineData(0, 0, 0, 3)] // no width
    [InlineData(0, 0, 5, 3)]
    [InlineData(0, -1, 1, 3)]
    [InlineData(0, 0, 1, 0)] // no height
    [InlineData(0, 0, 1, 25)]
    public void ATile_StaysWithinTheGrid(int x, int y, int w, int h) =>
        Assert.Equal("dashboard.itemInvalid", Key(() => new DashboardLayoutValues([Tile("odometer", x, y, w, h)], []).Checked(Today)));

    [Fact]
    public void OnlySavedCharts_CanBeHidden_AndEachListHasALimit()
    {
        Assert.Equal("dashboard.itemInvalid", Key(() => new DashboardLayoutValues([], ["preset:distance"]).Checked(Today)));
        Assert.Equal("dashboard.itemInvalid", Key(() => new DashboardLayoutValues([Tile(new string('a', DashboardLayoutValues.MaxIdLength + 1))], []).Checked(Today)));
        var many = Enumerable.Range(0, DashboardLayoutValues.MaxItems + 1).Select(i => Tile($"chart:{Guid.NewGuid()}", 0, i * 3)).ToList();
        Assert.Equal("dashboard.tooMany", Key(() => new DashboardLayoutValues(many, []).Checked(Today)));
        Assert.Equal(DashboardLayoutValues.MaxItems, new DashboardLayoutValues(many.Take(DashboardLayoutValues.MaxItems).ToList(), []).Checked(Today).Tiles.Count);
        Assert.Equal("dashboard.tooMany", Key(() => new DashboardLayoutValues([], many.Select(t => t.Id).ToList()).Checked(Today)));
    }

    [Fact]
    public void OverlappingTiles_AreNotRefused_TheScreenPacksThem()
    {
        var layout = new DashboardLayoutValues([Tile("odometer", 0, 0, 2, 3), Tile("thisMonth", 1, 0, 2, 3)], []).Checked(Today);

        Assert.Equal(2, layout.Tiles.Count);
    }

    [Fact]
    public void APersonalLayout_IsItsUsers()
    {
        var user = Guid.NewGuid();
        var vehicle = Guid.NewGuid();

        var layout = UserDashboardLayout.Create(user, vehicle, new([Tile("odometer")], []), new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal((user, vehicle, 2026), (layout.UserId, layout.VehicleId, layout.UpdatedAt.Year));
    }

    [Fact]
    public void AFiguresPeriod_IsKeptWithItsTile_AndChecked()
    {
        var period = new TrendPeriod(TrendWindowKind.Last, TrendUnit.Weeks, 8, Against: TrendComparison.YearEarlier);
        var layout = VehicleDashboardLayout.Create(Guid.NewGuid(), new([Tile("spendingTrend") with { Period = period }, Tile("odometer", 1)], []), new DateTimeOffset(2026, 11, 3, 23, 30, 0, TimeSpan.Zero));

        Assert.Equal(period, layout.Tiles[0].Period);
        Assert.Null(layout.Tiles[1].Period); // none: the figure's default
        Assert.Equal("trend.countRange", Key(() => VehicleDashboardLayout.Create(Guid.NewGuid(), new([Tile("spendingTrend") with { Period = period with { Count = 0 } }], []), DateTimeOffset.UnixEpoch)));
    }

    [Theory]
    [InlineData("preset:monthlyCosts")]
    [InlineData("chart:5d2c8a1e-0f4b-4b6e-9a4c-2a8d7f1e3b90")]
    public void APeriod_IsOnlyForAKeyFigure(string id) =>
        Assert.Equal("dashboard.itemInvalid", Key(() => new DashboardLayoutValues([Tile(id, w: 2, h: 8) with { Period = TrendPeriod.TenFillUps }], []).Checked(Today)));

    [Fact]
    public void ASince_IsCheckedAgainstTheSavesUtcDay()
    {
        var since = new TrendPeriod(TrendWindowKind.Since, Since: new DateOnly(2026, 11, 4));
        var values = new DashboardLayoutValues([Tile("distanceTrend") with { Period = since }], []);

        // 23:30 UTC on the 3rd is the 4th in Budapest, but the statistics' day is UTC's: the 4th is still tomorrow.
        Assert.Equal("trend.sinceRange", Key(() => VehicleDashboardLayout.Create(Guid.NewGuid(), values, new DateTimeOffset(2026, 11, 3, 23, 30, 0, TimeSpan.Zero))));
        Assert.Equal(since, VehicleDashboardLayout.Create(Guid.NewGuid(), values, new DateTimeOffset(2026, 11, 4, 0, 30, 0, TimeSpan.Zero)).Tiles[0].Period);
    }
}
