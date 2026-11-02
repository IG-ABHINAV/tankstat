using Tankstat.Domain;
using Tankstat.Domain.Charts;

namespace Tankstat.Domain.UnitTests;

public class DashboardLayoutTests
{
    private static string Key(Action action) => Assert.Throws<DomainException>(action).Key;

    [Fact]
    public void ALayout_KeepsItsEntriesInOrder_WithoutDuplicates()
    {
        var chart = Guid.NewGuid();
        var layout = VehicleDashboardLayout.Create(Guid.NewGuid(), new(["thisMonth", "odometer", "thisMonth"], ["preset:monthlyCosts", $"chart:{chart}", "preset:monthlyCosts"], [$"chart:{chart}"]), DateTimeOffset.UnixEpoch);

        Assert.Equal(["thisMonth", "odometer"], layout.Figures);
        Assert.Equal(["preset:monthlyCosts", $"chart:{chart}"], layout.Charts);
        Assert.Equal(chart, DashboardLayoutValues.ChartIdOf(layout.Hidden.Single()));
    }

    [Theory]
    [InlineData("Odometer")] // a figure starts with a lower-case letter
    [InlineData("this-month")]
    [InlineData("")]
    public void AFigure_IsAShortName(string figure) =>
        Assert.Equal("dashboard.itemInvalid", Key(() => new DashboardLayoutValues([figure], [], []).Checked()));

    [Theory]
    [InlineData("monthlyCosts")] // a chart entry says which kind it is
    [InlineData("preset:")]
    [InlineData("chart:not-a-guid")]
    public void AChartEntry_IsAPresetOrASavedChart(string entry) =>
        Assert.Equal("dashboard.itemInvalid", Key(() => new DashboardLayoutValues([], [entry], []).Checked()));

    [Fact]
    public void OnlySavedCharts_CanBeHidden_AndEachListHasALimit()
    {
        Assert.Equal("dashboard.itemInvalid", Key(() => new DashboardLayoutValues([], [], ["preset:distance"]).Checked()));
        Assert.Equal("dashboard.itemInvalid", Key(() => new DashboardLayoutValues([new string('a', DashboardLayoutValues.MaxIdLength + 1)], [], []).Checked()));
        var many = Enumerable.Range(0, DashboardLayoutValues.MaxItems + 1).Select(i => $"chart:{Guid.NewGuid()}").ToList();
        Assert.Equal("dashboard.tooMany", Key(() => new DashboardLayoutValues([], many, []).Checked()));
        Assert.Equal(DashboardLayoutValues.MaxItems, new DashboardLayoutValues([], many.Take(DashboardLayoutValues.MaxItems).ToList(), []).Checked().Charts.Count);
    }

    [Fact]
    public void APersonalLayout_IsItsUsers()
    {
        var user = Guid.NewGuid();
        var vehicle = Guid.NewGuid();

        var layout = UserDashboardLayout.Create(user, vehicle, new(["odometer"], [], []), new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal((user, vehicle, 2026), (layout.UserId, layout.VehicleId, layout.UpdatedAt.Year));
    }
}
