namespace Tankstat.Domain.Charts;

/// <summary>What a chart measures. Costs are kept per currency (amounts in different currencies are never added together).</summary>
public enum ChartMetric
{
    /// <summary>Fuel and expenses together.</summary>
    TotalSpend,
    FuelCost,
    ExpenseCost,
    FuelVolume,
    /// <summary>Distance driven, from the vehicle's odometer readings.</summary>
    Distance,
    /// <summary>Mean of the stored consumption of the full fill-ups in the period.</summary>
    AverageConsumption,
    /// <summary>Fuel cost divided by fuel volume: the price paid per unit.</summary>
    AveragePricePerUnit,
    FillUps,
    /// <summary>Fuel cost before the discounts taken off at the till, divided by fuel volume: the pump's price per unit.</summary>
    AveragePricePerUnitBeforeDiscount,
}

/// <summary>How values are grouped on the horizontal axis.</summary>
public enum ChartGrouping
{
    Month,
    Quarter,
    Year,
    /// <summary>By expense category (only for <see cref="ChartMetric.ExpenseCost"/>).</summary>
    Category,
}

public enum ChartKind
{
    Bar,
    Line,
    Area,
    Donut,
}

/// <summary>The period shown, relative to today so a saved chart stays current.</summary>
public enum ChartRange
{
    /// <summary>The last month up to today (a rolling window, not a calendar month).</summary>
    Last1Month,
    /// <summary>The last three whole calendar months, plus the current one so far.</summary>
    Last3Months,
    /// <summary>Half a year, counted like the other month ranges.</summary>
    Last6Months,
    /// <summary>One year, counted like the other month ranges.</summary>
    Last12Months,
    ThisYear,
    LastYear,
    All,
    /// <summary>Between <see cref="ChartConfig.From"/> and <see cref="ChartConfig.To"/>.</summary>
    Custom,
}

/// <summary>The choices that define a chart. Only combinations that make sense are valid.</summary>
/// <param name="From">First day of a <see cref="ChartRange.Custom"/> range; null for every other range.</param>
/// <param name="To">Last day of a custom range.</param>
public sealed record ChartConfig(ChartMetric Metric, ChartGrouping Grouping, ChartKind Kind, ChartRange Range, bool Stacked, DateOnly? From = null, DateOnly? To = null)
{
    public const int MaxCustomYears = 30;

    /// <summary>Metrics that can be added up (the parts of a donut must add up to something).</summary>
    public static bool IsAdditive(ChartMetric metric) =>
        metric is ChartMetric.TotalSpend or ChartMetric.FuelCost or ChartMetric.ExpenseCost or ChartMetric.FuelVolume or ChartMetric.Distance or ChartMetric.FillUps;

    public ChartConfig Validated()
    {
        if (!Enum.IsDefined(Metric) || !Enum.IsDefined(Grouping) || !Enum.IsDefined(Kind) || !Enum.IsDefined(Range))
            throw new DomainException("chart.invalid", "This chart is not valid.");
        if (Grouping == ChartGrouping.Category && Metric != ChartMetric.ExpenseCost)
            throw new DomainException("chart.categoryNeedsExpenses", "Grouping by category is only available for expense costs.");
        if (Kind == ChartKind.Donut && !IsAdditive(Metric))
            throw new DomainException("chart.donutNeedsTotals", "A donut chart needs a metric that adds up (costs, volume, distance or fill-ups).");
        if (Stacked && Metric != ChartMetric.TotalSpend)
            throw new DomainException("chart.stackNeedsTotalSpend", "Splitting into fuel and expenses is only available for total spend.");
        if (Stacked && Grouping == ChartGrouping.Category)
            throw new DomainException("chart.invalid", "This chart is not valid.");
        if (Range == ChartRange.Custom)
        {
            if (From is null || To is null) throw new DomainException("chart.datesRequired", "Choose the first and the last day of the period.");
            if (From > To) throw new DomainException("chart.datesReversed", "The first day cannot be after the last day.");
            if (To.Value > From.Value.AddYears(MaxCustomYears)) throw new DomainException("chart.rangeTooLong", $"The period can be at most {MaxCustomYears} years.", new { Max = MaxCustomYears });
            return this;
        }
        // The dates only mean something for a custom range; other ranges are normalised so equal charts compare equal.
        return this with { From = null, To = null };
    }
}
