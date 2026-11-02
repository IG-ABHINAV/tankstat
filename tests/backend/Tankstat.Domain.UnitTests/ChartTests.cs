using Tankstat.Domain;
using Tankstat.Domain.Charts;

namespace Tankstat.Domain.UnitTests;

public class ChartTests
{
    private static ChartConfig Config(ChartMetric metric = ChartMetric.TotalSpend, ChartGrouping grouping = ChartGrouping.Month, ChartKind kind = ChartKind.Bar,
        ChartRange range = ChartRange.Last12Months, bool stacked = false, DateOnly? from = null, DateOnly? to = null) => new(metric, grouping, kind, range, stacked, from, to);

    private static string Key(Action action) => Assert.Throws<DomainException>(action).Key;

    [Fact]
    public void SensibleCombinations_AreValid()
    {
        Config().Validated();
        Config(ChartMetric.ExpenseCost, ChartGrouping.Category, ChartKind.Donut).Validated();
        Config(ChartMetric.TotalSpend, stacked: true).Validated();
        Config(ChartMetric.AverageConsumption, kind: ChartKind.Line).Validated();
    }

    [Theory]
    [InlineData(ChartMetric.FuelCost, "chart.categoryNeedsExpenses")]
    [InlineData(ChartMetric.TotalSpend, "chart.categoryNeedsExpenses")]
    public void CategoryGrouping_NeedsExpenseCosts(ChartMetric metric, string key) =>
        Assert.Equal(key, Key(() => Config(metric, ChartGrouping.Category).Validated()));

    [Theory]
    [InlineData(ChartMetric.AverageConsumption)]
    [InlineData(ChartMetric.AveragePricePerUnit)]
    [InlineData(ChartMetric.AveragePricePerUnitBeforeDiscount)]
    public void ADonut_NeedsAMetricThatAddsUp(ChartMetric metric) =>
        Assert.Equal("chart.donutNeedsTotals", Key(() => Config(metric, kind: ChartKind.Donut).Validated()));

    [Fact]
    public void SplittingIntoFuelAndExpenses_IsOnlyForTotalSpend()
    {
        Assert.Equal("chart.stackNeedsTotalSpend", Key(() => Config(ChartMetric.FuelCost, stacked: true).Validated()));
        Assert.Equal("chart.invalid", Key(() => Config((ChartMetric)99).Validated()));
    }

    [Fact]
    public void ACustomRange_NeedsBothDates_InOrder_AndALimit()
    {
        var day = new DateOnly(2026, 5, 1);

        Assert.Equal("chart.datesRequired", Key(() => Config(range: ChartRange.Custom).Validated()));
        Assert.Equal("chart.datesRequired", Key(() => Config(range: ChartRange.Custom, from: day).Validated()));
        Assert.Equal("chart.datesReversed", Key(() => Config(range: ChartRange.Custom, from: day, to: day.AddDays(-1)).Validated()));
        Assert.Equal("chart.rangeTooLong", Key(() => Config(range: ChartRange.Custom, from: day, to: day.AddYears(31)).Validated()));
        Config(range: ChartRange.Custom, from: day, to: day).Validated(); // a single day is fine
    }

    [Fact]
    public void Dates_AreDroppedForRangesThatDoNotUseThem()
    {
        var config = Config(range: ChartRange.Last3Months, from: new DateOnly(2026, 1, 1), to: new DateOnly(2026, 2, 1)).Validated();

        Assert.Equal((null, null), (config.From, config.To));
    }

    [Fact]
    public void ASavedChart_KeepsItsRecipe_AndTrimsTheTitle()
    {
        var config = Config(range: ChartRange.Custom, from: new DateOnly(2026, 1, 1), to: new DateOnly(2026, 3, 31), stacked: true);

        var chart = VehicleChart.Create(Guid.NewGuid(), Guid.NewGuid(), "  Spending  ", config, shared: true, DateTimeOffset.UtcNow);

        Assert.Equal(("Spending", true), (chart.Title, chart.IsShared));
        Assert.Equal(config, chart.Config);
    }

    [Fact]
    public void ASavedChart_NeedsATitle_NotTooLong_AndAValidRecipe()
    {
        var vehicle = Guid.NewGuid();
        VehicleChart Make(string title, ChartConfig? config = null) => VehicleChart.Create(vehicle, vehicle, title, config ?? Config(), false, DateTimeOffset.UtcNow);

        Assert.Equal("chart.titleRequired", Key(() => Make(" ")));
        Assert.Equal("chart.titleRequired", Key(() => Make(null!)));
        Assert.Equal("chart.titleTooLong", Key(() => Make(new string('x', VehicleChart.MaxTitleLength + 1))));
        Assert.Equal("chart.categoryNeedsExpenses", Key(() => Make("x", Config(ChartMetric.FuelCost, ChartGrouping.Category))));
    }

    [Fact]
    public void Updating_ChangesTheRecipe()
    {
        var chart = VehicleChart.Create(Guid.NewGuid(), Guid.NewGuid(), "A", Config(), false, DateTimeOffset.UtcNow);

        chart.Update("B", Config(ChartMetric.FuelVolume, kind: ChartKind.Line), shared: true);

        Assert.Equal(("B", ChartMetric.FuelVolume, ChartKind.Line, true), (chart.Title, chart.Metric, chart.Kind, chart.IsShared));
    }
}
