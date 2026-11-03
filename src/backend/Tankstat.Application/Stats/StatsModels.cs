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
    IReadOnlyList<MonthAmount> SpendTrend, int FillUpCount, int ExpenseCount, IReadOnlyList<CurrencySpend> Spending, DashboardTrends Trends);

/// <summary>A figure now against the same figure a step back; null where it cannot be worked out (nothing driven, too few fill-ups).</summary>
public sealed record ValueChange(decimal? Recent, decimal? Earlier);

/// <summary>A money figure in one currency now against a step back (never converted).</summary>
public sealed record CurrencyChange(string Currency, decimal? Recent, decimal? Earlier);

/// <summary>
/// Where a vehicle's figures are heading, for the dashboard's trend figures (#211). This month is left out: it is still running.
/// </summary>
/// <param name="Spending">The last 12 whole months against the 12 before them, per currency used in either, the main currency first.</param>
/// <param name="CostPerDistance">The same windows, money per 100 distance units driven in each (null where nothing was driven).</param>
/// <param name="Consumption">The mean of the last 10 stored consumptions against the mean of the 10 before them; null with fewer than 3 in either.</param>
/// <param name="Distance">This year to today against last year up to the same day.</param>
public sealed record DashboardTrends(IReadOnlyList<CurrencyChange> Spending, IReadOnlyList<CurrencyChange> CostPerDistance, ConsumptionChange? Consumption, ValueChange? Distance);

/// <summary>The mean consumption of the latest fill-ups against the ones before, and how many each half holds (null where too few).</summary>
public sealed record ConsumptionChange(decimal? Recent, decimal? Earlier, int RecentFillUps, int EarlierFillUps);

/// <summary>The trend figures that take a period of their own (#230).</summary>
public enum TrendFigure { Spending, CostPerDistance, Consumption, Distance }

/// <summary>One window of a trend figure: its first and last day, and how many fill-ups fall in it (with a stored consumption, for Consumption).</summary>
public sealed record TrendWindow(DateOnly Start, DateOnly End, int FillUps);

/// <summary>
/// A trend figure over its period (#230): the two windows compared, and the figure's values in them. Only the figure's own part is filled
/// in: <see cref="Currencies"/> for Spending (what was spent) and Cost per distance (per 100 distance units), <see cref="Consumption"/>
/// for Consumption, <see cref="Distance"/> for Distance (null for a vehicle without a reading).
/// </summary>
public sealed record TrendFigureData(TrendWindow Recent, TrendWindow Earlier, IReadOnlyList<CurrencyChange> Currencies, ConsumptionChange? Consumption, ValueChange? Distance);
