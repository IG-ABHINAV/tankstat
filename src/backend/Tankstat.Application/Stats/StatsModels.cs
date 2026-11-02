using Tankstat.Domain.Charts;

namespace Tankstat.Application.Stats;

/// <summary>One fill-up, reduced to what statistics need.</summary>
/// <param name="Amount">What was paid, after <paramref name="Discount"/>.</param>
/// <param name="Discount">What was taken off at the till, 0 when none: the pump's price is (<paramref name="Amount"/> + <paramref name="Discount"/>) ÷ <paramref name="Volume"/>.</param>
public sealed record FuelPoint(DateOnly Date, decimal Volume, decimal Amount, string Currency, long Odometer, bool IsFull, decimal? Consumption, decimal Discount = 0);

public sealed record ExpensePoint(DateOnly Date, string? Category, decimal Amount, string Currency);

public sealed record OdometerPoint(DateOnly Date, long Value);

/// <summary>Everything a vehicle's statistics are calculated from (live records only).</summary>
public sealed record StatsData(IReadOnlyList<FuelPoint> Fuel, IReadOnlyList<ExpensePoint> Expenses, IReadOnlyList<OdometerPoint> Readings);

public enum ChartUnit
{
    Currency,
    Volume,
    Distance,
    Consumption,
    Count,
    /// <summary>Money per 100 distance units, in the series' currency.</summary>
    CostPerDistance,
    /// <summary>Money per volume unit (a fuel price), in the series' currency.</summary>
    PricePerVolume,
}

/// <summary>What a series shows: the values themselves, or a line drawn from them.</summary>
public enum ChartSeriesRole
{
    Value,
    /// <summary>The mean of the last three periods of a value series.</summary>
    MovingAverage,
    /// <summary>A straight line fitted to a value series.</summary>
    Linear,
    /// <summary>The same periods a year earlier, keyed by the periods they line up with.</summary>
    YearEarlier,
}

/// <param name="Key">The period ("2026-09", "2026-Q3", "2026") or the expense category (empty for uncategorised).</param>
/// <param name="Value">Null where an average cannot be calculated (no data in that period).</param>
public sealed record ChartPoint(string Key, decimal? Value);

/// <param name="Kind">"total", "fuel" or "expenses" (translated by the client).</param>
/// <param name="Currency">The currency of a cost series; null for the others.</param>
/// <param name="Role">What the series shows; a trend or a year earlier belongs to the value series of the same kind and currency.</param>
/// <param name="Slope">For a <see cref="ChartSeriesRole.Linear"/> trend: how much it changes per period, in the series' unit.</param>
/// <param name="Metric">The measure the series belongs to (a chart can have two), stamped on every series by <see cref="StatsCalculator.Chart"/>.</param>
/// <param name="Unit">The unit of that measure, which the series is written in.</param>
public sealed record ChartSeries(string Kind, string? Currency, IReadOnlyList<ChartPoint> Points, ChartSeriesRole Role = ChartSeriesRole.Value, decimal? Slope = null,
    ChartMetric Metric = default, ChartUnit Unit = default);

/// <param name="Unit">The first measure's unit; with a second measure, each series says its own.</param>
public sealed record ChartData(ChartUnit Unit, IReadOnlyList<ChartSeries> Series);

public sealed record MonthAmount(string Month, decimal Amount);

/// <summary>What was spent in one currency this month and last month (fuel and other expenses together).</summary>
public sealed record CurrencySpend(string Currency, decimal ThisMonth, decimal LastMonth);

/// <summary>
/// The key figures of a vehicle, for its card and the top of its dashboard. Money is never converted: <see cref="Currency"/> is the main
/// currency (the one most was spent in over the last year) and the month figures and the trend are in it; <see cref="Spending"/> has this
/// month and last month in every currency used in them, the main one first, so nothing spent in another currency goes unshown.
/// </summary>
public sealed record VehicleSummary(
    DateOnly? LastFillUpDate, long? LatestOdometer, decimal? AverageConsumption, string? Currency, decimal ThisMonthSpend, decimal LastMonthSpend,
    IReadOnlyList<MonthAmount> SpendTrend, int FillUpCount, int ExpenseCount, IReadOnlyList<CurrencySpend> Spending);
