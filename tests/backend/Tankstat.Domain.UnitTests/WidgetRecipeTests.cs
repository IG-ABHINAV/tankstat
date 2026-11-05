using System.Text.Json;
using Tankstat.Domain;
using Tankstat.Domain.Charts;

namespace Tankstat.Domain.UnitTests;

/// <summary>A tile carrying its own recipe, and a key figure's recipe (#273): their shapes, their checks, and how the tiles' JSON keeps them.</summary>
public class WidgetRecipeTests
{
    private static readonly DateOnly Today = new(2026, 11, 3);
    private static string Key(Action action) => Assert.Throws<DomainException>(action).Key;
    private static string TileId => $"tile:{Guid.NewGuid()}";

    private static readonly ChartConfig Bars = new(ChartMetric.TotalSpend, ChartGrouping.Month, ChartKind.Bar, ChartRange.Last12Months, false);
    private static readonly FigureRecipe Spent = new(FigureMeasure.TotalSpend, new TrendPeriod(TrendWindowKind.ThisMonth), Sparkline: true, Template: "thisMonth");

    private static DashboardLayoutValues Checked(params DashboardTile[] tiles) => new DashboardLayoutValues(tiles, []).Checked(Today);
    private static DashboardTile Tile(string id, TileRecipe? recipe = null, TrendPeriod? period = null) => new(id, 0, 0, 1, 4, Period: period, Recipe: recipe);

    [Fact]
    public void ATileWithItsOwnRecipe_IsATileIdWithAChartOrAFigure_KeptAsChecked()
    {
        var chartTile = TileId;
        var figureTile = TileId;
        var saved = $"chart:{Guid.NewGuid()}";

        var tiles = Checked(
            Tile(chartTile, new TileRecipe(WidgetKind.Chart, "  Spending by month ", Chart: Bars with { From = Today }, Figure: null)),
            Tile(figureTile, new TileRecipe(WidgetKind.Figure, "", Figure: Spent)),
            Tile(saved),
            Tile("spendingTrend", period: TrendPeriod.TwelveWholeMonths),
            Tile("preset:monthlyCosts")).Tiles;

        Assert.Equal([chartTile, figureTile, saved, "spendingTrend", "preset:monthlyCosts"], tiles.Select(t => t.Id));
        Assert.Equal(new TileRecipe(WidgetKind.Chart, "Spending by month", Bars), tiles[0].Recipe); // trimmed; the dates of a non-custom range dropped
        Assert.Equal(new TileRecipe(WidgetKind.Figure, null, null, Spent), tiles[1].Recipe); // a figure's empty title: the web app's default
        Assert.Equal(Guid.Parse(figureTile["tile:".Length..]), DashboardLayoutValues.TileIdOf(figureTile));
        Assert.Null(DashboardLayoutValues.TileIdOf(saved));
    }

    [Fact]
    public void ARecipe_IsOnlyForATileId_AndATileIdNeedsOne_WithItsPeriodInside()
    {
        var recipe = new TileRecipe(WidgetKind.Chart, "Bars", Bars);

        Assert.Equal("dashboard.itemInvalid", Key(() => Checked(Tile(TileId))));
        Assert.Equal("dashboard.itemInvalid", Key(() => Checked(Tile($"chart:{Guid.NewGuid()}", recipe))));
        Assert.Equal("dashboard.itemInvalid", Key(() => Checked(Tile("preset:monthlyCosts", recipe))));
        Assert.Equal("dashboard.itemInvalid", Key(() => Checked(Tile("spendingTrend", recipe))));
        Assert.Equal("dashboard.itemInvalid", Key(() => Checked(Tile(TileId, new TileRecipe(WidgetKind.Figure, null, Figure: Spent), period: TrendPeriod.TwelveWholeMonths))));
        Assert.Equal("dashboard.itemInvalid", Key(() => Checked(Tile("tile:not-a-guid", recipe))));
    }

    [Fact]
    public void ATilesRecipe_IsExactlyTheOneItsKindNames_TitledAsASavedChart_AndCheckedWithItsKeys()
    {
        string Refused(TileRecipe recipe) => Key(() => Checked(Tile(TileId, recipe)));
        string Refused0(DashboardTile tile) => Key(() => Checked(tile));

        Assert.Equal("dashboard.itemInvalid", Refused(new TileRecipe(WidgetKind.Chart, "Bars", Figure: Spent))); // a chart without its config
        Assert.Equal("dashboard.itemInvalid", Refused(new TileRecipe(WidgetKind.Figure, null, Bars, Spent))); // both
        Assert.Equal("dashboard.itemInvalid", Refused(new TileRecipe((WidgetKind)9, "Bars", Bars)));
        Assert.Equal("dashboard.itemInvalid", Refused(new TileRecipe(WidgetKind.Figure, null, Figure: Spent with { Template = "This-Month" }))); // a template out of shape
        Assert.Equal("chart.titleRequired", Refused(new TileRecipe(WidgetKind.Chart, " ", Bars)));
        Assert.Equal("chart.titleTooLong", Refused(new TileRecipe(WidgetKind.Figure, new string('x', VehicleChart.MaxTitleLength + 1), Figure: Spent)));
        Assert.Equal("chart.donutNeedsTotals", Refused(new TileRecipe(WidgetKind.Chart, "Donut", Bars with { Kind = ChartKind.Donut, Metric = ChartMetric.AverageConsumption })));
        Assert.Equal("trend.countRange", Refused(new TileRecipe(WidgetKind.Figure, null, Figure: Spent with { Period = new TrendPeriod(TrendWindowKind.Last, TrendUnit.Days, 0) })));
        Assert.Equal("trend.invalid", Refused(new TileRecipe(WidgetKind.Figure, null, Figure: new FigureRecipe(FigureMeasure.Odometer, TrendPeriod.TwelveWholeMonths)))); // a point in time takes no period
        Assert.Equal("trend.invalid", Refused(new TileRecipe(WidgetKind.Figure, null, Figure: new FigureRecipe(FigureMeasure.TotalSpend)))); // a measure over a period needs one
        Assert.NotNull(Checked(Tile(TileId, new TileRecipe(WidgetKind.Figure, null, Figure: new FigureRecipe(FigureMeasure.Odometer)))));
        // A figure's recipe may compare with nothing (vehicleFigure answers it); an old trend id's period may not (it would never load).
        Assert.NotNull(Checked(Tile(TileId, new TileRecipe(WidgetKind.Figure, null, Figure: Spent with { Period = new TrendPeriod(TrendWindowKind.ThisMonth, Against: TrendComparison.None) }))));
        Assert.Equal("dashboard.itemInvalid", Refused0(Tile("spendingTrend", period: new TrendPeriod(TrendWindowKind.ThisMonth, Against: TrendComparison.None))));
    }

    [Fact]
    public void AFigureRecipe_IsCheckedOnItsOwnToo_ATemplateOutOfShapeIsChartInvalid()
    {
        Assert.Equal(Spent, Spent.Validated(Today));
        Assert.Equal("chart.invalid", Key(() => (Spent with { Template = new string('a', DashboardLayoutValues.MaxIdLength + 1) }).Validated(Today)));
        Assert.Equal("chart.invalid", Key(() => (Spent with { Template = "preset:monthlyCosts" }).Validated(Today)));
        Assert.Equal("trend.invalid", Key(() => new FigureRecipe((FigureMeasure)99, Spent.Period).Validated(Today)));
    }

    [Fact]
    public void TheTilesJson_KeepsARecipesEnumsByName_ANullableOneAndACustomRangesDatesToo()
    {
        var custom = new ChartConfig(ChartMetric.FuelCost, ChartGrouping.Quarter, ChartKind.Line, ChartRange.Custom, false, new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31),
            ChartTrend.Linear, SecondMetric: ChartMetric.Distance, SecondKind: ChartKind.Bar);
        IReadOnlyList<DashboardTile> tiles =
        [
            Tile(TileId, new TileRecipe(WidgetKind.Chart, "Custom", custom)),
            Tile(TileId, new TileRecipe(WidgetKind.Chart, "Plain", Bars)), // the second measure and its kind null
            Tile(TileId, new TileRecipe(WidgetKind.Figure, null, Figure: Spent)),
            Tile("spendingTrend", period: TrendPeriod.TwelveWholeMonths),
        ];

        // The tiles' column is written with no options, as AsJsonList writes it.
        var json = JsonSerializer.Serialize(tiles, (JsonSerializerOptions?)null);

        Assert.Contains("\"Metric\":\"FuelCost\"", json);
        Assert.Contains("\"SecondMetric\":\"Distance\"", json);
        Assert.Contains("\"SecondMetric\":null", json);
        Assert.Contains("\"Widget\":\"Figure\"", json);
        Assert.Contains("\"Measure\":\"TotalSpend\"", json);
        Assert.Equal(tiles, JsonSerializer.Deserialize<List<DashboardTile>>(json, (JsonSerializerOptions?)null));
        // A tile stored before recipes existed reads with none.
        Assert.Null(JsonSerializer.Deserialize<List<DashboardTile>>("[{\"Id\":\"odometer\",\"X\":0,\"Y\":0,\"W\":1,\"H\":3,\"Pinned\":false,\"Period\":null}]", (JsonSerializerOptions?)null)![0].Recipe);
    }
}
