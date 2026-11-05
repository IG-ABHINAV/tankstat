using System.Text.Json.Serialization;

namespace Tankstat.Domain.Charts;

/// <summary>
/// What a key figure measures (#272): any chart metric over a period (<see cref="TrendPeriod"/>), compared or not, plus two that are a point
/// in time and take no period: the odometer's reading and the last fill-up. Stored and sent by name: a later order of the members never
/// remaps a saved figure.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FigureMeasure>))]
public enum FigureMeasure
{
    TotalSpend,
    FuelCost,
    ExpenseCost,
    FuelVolume,
    Distance,
    AverageConsumption,
    AveragePricePerUnit,
    FillUps,
    AveragePricePerUnitBeforeDiscount,
    CostPerDistance,
    FuelCostPerDistance,
    /// <summary>The highest odometer reading (the summary's): a point in time, no period.</summary>
    Odometer,
    /// <summary>The latest fill-up's date and the counts of fill-ups and expenses (the summary's): a point in time, no period.</summary>
    LastFillUp,
}

public static class FigureMeasures
{
    /// <summary>Whether the measure is a point in time (the odometer, the last fill-up), which takes no period and no comparison.</summary>
    public static bool IsPointInTime(this FigureMeasure measure) => measure is FigureMeasure.Odometer or FigureMeasure.LastFillUp;

    /// <summary>The chart metric a measure over a period is (the same name); null for a point in time.</summary>
    public static ChartMetric? ToMetric(this FigureMeasure measure) => measure switch
    {
        FigureMeasure.TotalSpend => ChartMetric.TotalSpend,
        FigureMeasure.FuelCost => ChartMetric.FuelCost,
        FigureMeasure.ExpenseCost => ChartMetric.ExpenseCost,
        FigureMeasure.FuelVolume => ChartMetric.FuelVolume,
        FigureMeasure.Distance => ChartMetric.Distance,
        FigureMeasure.AverageConsumption => ChartMetric.AverageConsumption,
        FigureMeasure.AveragePricePerUnit => ChartMetric.AveragePricePerUnit,
        FigureMeasure.FillUps => ChartMetric.FillUps,
        FigureMeasure.AveragePricePerUnitBeforeDiscount => ChartMetric.AveragePricePerUnitBeforeDiscount,
        FigureMeasure.CostPerDistance => ChartMetric.CostPerDistance,
        FigureMeasure.FuelCostPerDistance => ChartMetric.FuelCostPerDistance,
        _ => null,
    };
}
