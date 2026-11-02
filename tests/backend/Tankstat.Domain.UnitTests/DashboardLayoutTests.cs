using Tankstat.Domain;
using Tankstat.Domain.Charts;

namespace Tankstat.Domain.UnitTests;

public class DashboardLayoutTests
{
    private static string Key(Action action) => Assert.Throws<DomainException>(action).Key;

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
        Assert.Equal("dashboard.itemInvalid", Key(() => new DashboardLayoutValues([Tile(id)], []).Checked()));

    [Theory]
    [InlineData(-1, 0, 1, 3)] // left of the grid
    [InlineData(3, 0, 2, 3)] // past its right edge
    [InlineData(0, 0, 0, 3)] // no width
    [InlineData(0, 0, 5, 3)]
    [InlineData(0, -1, 1, 3)]
    [InlineData(0, 0, 1, 0)] // no height
    [InlineData(0, 0, 1, 25)]
    public void ATile_StaysWithinTheGrid(int x, int y, int w, int h) =>
        Assert.Equal("dashboard.itemInvalid", Key(() => new DashboardLayoutValues([Tile("odometer", x, y, w, h)], []).Checked()));

    [Fact]
    public void OnlySavedCharts_CanBeHidden_AndEachListHasALimit()
    {
        Assert.Equal("dashboard.itemInvalid", Key(() => new DashboardLayoutValues([], ["preset:distance"]).Checked()));
        Assert.Equal("dashboard.itemInvalid", Key(() => new DashboardLayoutValues([Tile(new string('a', DashboardLayoutValues.MaxIdLength + 1))], []).Checked()));
        var many = Enumerable.Range(0, DashboardLayoutValues.MaxItems + 1).Select(i => Tile($"chart:{Guid.NewGuid()}", 0, i * 3)).ToList();
        Assert.Equal("dashboard.tooMany", Key(() => new DashboardLayoutValues(many, []).Checked()));
        Assert.Equal(DashboardLayoutValues.MaxItems, new DashboardLayoutValues(many.Take(DashboardLayoutValues.MaxItems).ToList(), []).Checked().Tiles.Count);
        Assert.Equal("dashboard.tooMany", Key(() => new DashboardLayoutValues([], many.Select(t => t.Id).ToList()).Checked()));
    }

    [Fact]
    public void OverlappingTiles_AreNotRefused_TheScreenPacksThem()
    {
        var layout = new DashboardLayoutValues([Tile("odometer", 0, 0, 2, 3), Tile("thisMonth", 1, 0, 2, 3)], []).Checked();

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
}
