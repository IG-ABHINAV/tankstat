using Tankstat.Domain;
using Tankstat.Domain.Charts;

namespace Tankstat.Application.Stats;

/// <summary>
/// Turns a vehicle's records into chart data and key figures. Pure and provider-independent: the database only supplies the records.
/// Costs are summed per currency (a trip abroad does not mix euros into forints); the other metrics have a single series.
/// </summary>
public static class StatsCalculator
{
    public const string Total = "total", Fuel = "fuel", Expenses = "expenses";

    // ---- periods ------------------------------------------------------------------------------------------------

    private static DateOnly MonthStart(DateOnly d) => new(d.Year, d.Month, 1);

    private static (DateOnly From, DateOnly To)? RangeOf(ChartConfig config, DateOnly today, StatsData data)
    {
        switch (config.Range)
        {
            case ChartRange.Last1Month: return (today.AddMonths(-1).AddDays(1), today);
            case ChartRange.Custom: return (config.From!.Value, config.To!.Value);
            case ChartRange.Last3Months: return (MonthStart(today).AddMonths(-2), today);
            case ChartRange.Last6Months: return (MonthStart(today).AddMonths(-5), today);
            case ChartRange.Last12Months: return (MonthStart(today).AddMonths(-11), today);
            case ChartRange.ThisYear: return (new DateOnly(today.Year, 1, 1), today);
            case ChartRange.LastYear: return (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31));
            default:
                var dates = data.Fuel.Select(f => f.Date).Concat(data.Expenses.Select(e => e.Date)).Concat(data.Readings.Select(r => r.Date)).ToList();
                return dates.Count == 0 ? null : (dates.Min(), today > dates.Max() ? today : dates.Max());
        }
    }

    private static string Key(DateOnly d, ChartGrouping g) => g switch
    {
        ChartGrouping.Year => $"{d.Year}",
        ChartGrouping.Quarter => $"{d.Year}-Q{(d.Month - 1) / 3 + 1}",
        _ => $"{d.Year}-{d.Month:00}",
    };

    private static (DateOnly Start, DateOnly End) Bounds(DateOnly d, ChartGrouping g) => g switch
    {
        ChartGrouping.Year => (new DateOnly(d.Year, 1, 1), new DateOnly(d.Year, 12, 31)),
        ChartGrouping.Quarter => (new DateOnly(d.Year, ((d.Month - 1) / 3) * 3 + 1, 1), new DateOnly(d.Year, ((d.Month - 1) / 3) * 3 + 1, 1).AddMonths(3).AddDays(-1)),
        _ => (MonthStart(d), MonthStart(d).AddMonths(1).AddDays(-1)),
    };

    /// <summary>The periods from <paramref name="from"/> to <paramref name="to"/>, oldest first (empty ones included, so the axis is continuous).</summary>
    private static List<DateOnly> Periods(DateOnly from, DateOnly to, ChartGrouping g)
    {
        var list = new List<DateOnly>();
        var cursor = Bounds(from, g).Start;
        while (cursor <= to)
        {
            list.Add(cursor);
            cursor = g switch { ChartGrouping.Year => cursor.AddYears(1), ChartGrouping.Quarter => cursor.AddMonths(3), _ => cursor.AddMonths(1) };
        }
        return list;
    }

    // ---- charts -------------------------------------------------------------------------------------------------

    public static ChartData Chart(ChartConfig config, StatsData data, DateOnly today)
    {
        config = config.Validated();
        return new ChartData(UnitOf(config.Metric), Series(config, data, today));
    }

    private static ChartUnit UnitOf(ChartMetric metric) => metric switch
    {
        ChartMetric.FuelVolume => ChartUnit.Volume,
        ChartMetric.Distance => ChartUnit.Distance,
        ChartMetric.AverageConsumption => ChartUnit.Consumption,
        ChartMetric.FillUps => ChartUnit.Count,
        ChartMetric.CostPerDistance or ChartMetric.FuelCostPerDistance => ChartUnit.CostPerDistance,
        ChartMetric.AveragePricePerUnit or ChartMetric.AveragePricePerUnitBeforeDiscount => ChartUnit.PricePerVolume,
        _ => ChartUnit.Currency,
    };

    /// <summary>
    /// The first measure's values, then the lines drawn from them (a year earlier, a trend), then the second measure's values: the options
    /// are the first measure's only. Every series leaves here stamped with its measure and unit.
    /// </summary>
    private static List<ChartSeries> Series(ChartConfig config, StatsData data, DateOnly today)
    {
        if (RangeOf(config, today, data) is not { } range) return [];
        var (from, to) = range;

        if (config.Grouping == ChartGrouping.Category)
            return Stamp(CategorySeries(data.Expenses.Where(e => e.Date >= from && e.Date <= to).ToList()), config.Metric);

        var values = Measure(config.Metric, config.Stacked, config.Grouping, data, from, to);
        if (config.RunningTotal) values = values.Select(Running).ToList();
        var series = new List<ChartSeries>(values);
        if (config.CompareYearEarlier) series.AddRange(YearEarlier(config, data, from, to));
        if (config.Trend != ChartTrend.None) series.AddRange(Trends(config, values, Periods(from, to, config.Grouping), from, to, today));
        var stamped = Stamp(series, config.Metric);
        if (config.SecondMetric is { } second) stamped.AddRange(Stamp(Measure(second, stacked: false, config.Grouping, data, from, to), second));
        return stamped;
    }

    private static List<ChartSeries> Stamp(IEnumerable<ChartSeries> series, ChartMetric metric) =>
        series.Select(s => s with { Metric = metric, Unit = UnitOf(metric) }).ToList();

    /// <summary>
    /// The value series of a metric over the periods from <paramref name="from"/> to <paramref name="to"/>. Only what happened inside the
    /// range counts: costs by their date, distance from the readings of the part of each period inside it.
    /// </summary>
    private static List<ChartSeries> Measure(ChartMetric metric, bool stacked, ChartGrouping g, StatsData data, DateOnly from, DateOnly to)
    {
        var fuel = data.Fuel.Where(f => f.Date >= from && f.Date <= to).ToList();
        var expenses = data.Expenses.Where(e => e.Date >= from && e.Date <= to).ToList();
        var periods = Periods(from, to, g);
        var keys = periods.Select(p => Key(p, g)).ToList();
        (DateOnly Start, DateOnly End) Inside(DateOnly period)
        {
            var (start, end) = Bounds(period, g);
            return (start < from ? from : start, end > to ? to : end);
        }
        // Only the metrics over distance walk the readings (once per period): the others never ask.
        var distances = new Lazy<List<decimal>>(() => periods.Select(p => DistanceIn(data.Readings, Inside(p))).ToList());

        return metric switch
        {
            ChartMetric.TotalSpend when stacked => Money(keys, g, fuel, expenses, includeFuel: true, includeExpenses: true, split: true),
            ChartMetric.TotalSpend => Money(keys, g, fuel, expenses, includeFuel: true, includeExpenses: true, split: false),
            ChartMetric.FuelCost => Money(keys, g, fuel, expenses, includeFuel: true, includeExpenses: false, split: false),
            ChartMetric.ExpenseCost => Money(keys, g, fuel, expenses, includeFuel: false, includeExpenses: true, split: false),
            ChartMetric.FuelVolume => [new ChartSeries(Fuel, null, keys.Select(k => new ChartPoint(k, fuel.Where(f => Key(f.Date, g) == k).Sum(f => f.Volume))).ToList())],
            ChartMetric.FillUps => [new ChartSeries(Fuel, null, keys.Select(k => new ChartPoint(k, fuel.Count(f => Key(f.Date, g) == k))).ToList())],
            ChartMetric.AverageConsumption => [new ChartSeries(Fuel, null, keys.Select(k => Mean(k, fuel.Where(f => Key(f.Date, g) == k && f.Consumption is not null).Select(f => f.Consumption!.Value))).ToList())],
            ChartMetric.AveragePricePerUnit => PricePerUnit(keys, g, fuel, f => f.Amount),
            ChartMetric.AveragePricePerUnitBeforeDiscount => PricePerUnit(keys, g, fuel, f => f.Amount + f.Discount),
            ChartMetric.Distance => [new ChartSeries(Total, null, keys.Select((k, i) => new ChartPoint(k, distances.Value[i])).ToList())],
            ChartMetric.CostPerDistance => PerDistance(Money(keys, g, fuel, expenses, includeFuel: true, includeExpenses: true, split: false), distances.Value),
            ChartMetric.FuelCostPerDistance => PerDistance(Money(keys, g, fuel, expenses, includeFuel: true, includeExpenses: false, split: false), distances.Value),
            _ => [],
        };
    }

    /// <summary>Each period's cost per 100 distance units driven in it (per currency, never converted); none where nothing was driven.</summary>
    private static List<ChartSeries> PerDistance(List<ChartSeries> costs, List<decimal> distances) =>
        costs.Select(c => c with { Points = c.Points.Select((p, i) => p with { Value = distances[i] > 0 ? Math.Round(p.Value!.Value / distances[i] * 100, 3) : null }).ToList() }).ToList();

    /// <summary>The sum so far in every period (only for metrics that add up, whose values are never missing).</summary>
    private static ChartSeries Running(ChartSeries s)
    {
        var sum = 0m;
        return s with { Points = s.Points.Select(p => p with { Value = sum += p.Value ?? 0 }).ToList() };
    }

    /// <summary>
    /// The same periods a year earlier, keyed by the periods they line up with ("2025-09" becomes "2026-09"), so the table and the chart put
    /// them side by side. A stacked chart compares each currency's total.
    /// </summary>
    private static IEnumerable<ChartSeries> YearEarlier(ChartConfig config, StatsData data, DateOnly from, DateOnly to)
    {
        var earlier = Measure(config.Metric, stacked: false, config.Grouping, data, from.AddYears(-1), to.AddYears(-1));
        if (config.RunningTotal) earlier = earlier.Select(Running).ToList();
        return earlier.Select(s => s with
        {
            Points = s.Points.Select(p => p with { Key = $"{int.Parse(p.Key[..4], System.Globalization.CultureInfo.InvariantCulture) + 1}{p.Key[4..]}" }).ToList(),
            Role = ChartSeriesRole.YearEarlier,
        });
    }

    public const int MovingAverageWindow = 3;

    /// <summary>The fewest points a linear trend is fitted to.</summary>
    public const int MinTrendPoints = 3;

    /// <summary>
    /// A trend line per value series (on a stacked chart, per currency's total). For a metric that adds up, a period that is not whole is
    /// left out of it: one not over yet, or one the range cuts at its edge (a custom range's first and last month, the first period of
    /// "all time"), since half a month's costs would pull the line down. An average (consumption, price) is as good in a part of a period.
    /// A fitted line never goes below zero: nothing here is negative.
    /// </summary>
    private static IEnumerable<ChartSeries> Trends(ChartConfig config, List<ChartSeries> values, List<DateOnly> periods, DateOnly from, DateOnly to, DateOnly today)
    {
        var bases = config.Stacked
            ? values.GroupBy(s => s.Currency).Select(c => new ChartSeries(Total, c.Key,
                c.First().Points.Select((p, i) => new ChartPoint(p.Key, c.Sum(s => s.Points[i].Value ?? 0))).ToList())).ToList()
            : values;
        var additive = ChartConfig.IsAdditive(config.Metric);
        bool Whole(int i)
        {
            var (start, end) = Bounds(periods[i], config.Grouping);
            return start >= from && end <= to && end < today;
        }
        bool Usable(int i, decimal? value) => value is not null && (!additive || Whole(i));

        foreach (var s in bases)
        {
            if (config.Trend == ChartTrend.MovingAverage)
            {
                var points = s.Points.Select((p, i) =>
                {
                    if (i < MovingAverageWindow - 1 || !Usable(i, p.Value)) return p with { Value = null };
                    var window = Enumerable.Range(i - MovingAverageWindow + 1, MovingAverageWindow).Where(j => Usable(j, s.Points[j].Value)).Select(j => s.Points[j].Value!.Value).ToList();
                    return p with { Value = Math.Round(window.Average(), 3) };
                }).ToList();
                yield return new ChartSeries(s.Kind, s.Currency, points, ChartSeriesRole.MovingAverage);
            }
            else if (Fit(s.Points.Select((p, i) => (i, p.Value)).Where(x => Usable(x.i, x.Value)).Select(x => ((decimal)x.i, x.Value!.Value)).ToList()) is { } line)
            {
                var points = s.Points.Select((p, i) => p with { Value = Math.Max(0, Math.Round(line.Intercept + line.Slope * i, 3)) }).ToList();
                yield return new ChartSeries(s.Kind, s.Currency, points, ChartSeriesRole.Linear, Math.Round(line.Slope, 3));
            }
        }
    }

    /// <summary>The least-squares line through the points (x = the period's index), or null with fewer than <see cref="MinTrendPoints"/>.</summary>
    private static (decimal Intercept, decimal Slope)? Fit(List<(decimal X, decimal Y)> points)
    {
        if (points.Count < MinTrendPoints) return null;
        var meanX = points.Average(p => p.X);
        var meanY = points.Average(p => p.Y);
        var spread = points.Sum(p => (p.X - meanX) * (p.X - meanX));
        var slope = points.Sum(p => (p.X - meanX) * (p.Y - meanY)) / spread;
        return (meanY - slope * meanX, slope);
    }

    private static ChartPoint Mean(string key, IEnumerable<decimal> values)
    {
        var list = values.ToList();
        return new ChartPoint(key, list.Count == 0 ? null : Math.Round(list.Average(), 3));
    }

    /// <summary>Costs per currency (largest total first); with <paramref name="split"/>, fuel and expenses are separate series.</summary>
    private static List<ChartSeries> Money(List<string> keys, ChartGrouping g, List<FuelPoint> fuel, List<ExpensePoint> expenses, bool includeFuel, bool includeExpenses, bool split)
    {
        var parts = new List<(string Kind, string Currency, DateOnly Date, decimal Amount)>();
        if (includeFuel) parts.AddRange(fuel.Select(f => (split || !includeExpenses ? Fuel : Total, f.Currency, f.Date, f.Amount)));
        if (includeExpenses) parts.AddRange(expenses.Select(e => (split || !includeFuel ? Expenses : Total, e.Currency, e.Date, e.Amount)));

        return parts.GroupBy(p => (p.Kind, p.Currency))
            .OrderByDescending(g2 => g2.Sum(p => p.Amount)).ThenBy(g2 => g2.Key.Kind == Fuel ? 0 : 1)
            .Select(g2 => new ChartSeries(g2.Key.Kind, g2.Key.Currency, keys.Select(k => new ChartPoint(k, g2.Where(p => Key(p.Date, g) == k).Sum(p => p.Amount))).ToList()))
            .ToList();
    }

    /// <summary>The money <paramref name="amount"/> names (what was paid, or what it was before the discounts) over the volume, per currency and period.</summary>
    private static List<ChartSeries> PricePerUnit(List<string> keys, ChartGrouping g, List<FuelPoint> fuel, Func<FuelPoint, decimal> amount) =>
        fuel.GroupBy(f => f.Currency).OrderByDescending(c => c.Sum(f => f.Amount))
            .Select(c => new ChartSeries(Fuel, c.Key, keys.Select(k =>
            {
                var rows = c.Where(f => Key(f.Date, g) == k).ToList();
                var volume = rows.Sum(f => f.Volume);
                return new ChartPoint(k, volume > 0 ? Math.Round(rows.Sum(amount) / volume, 3) : null);
            }).ToList())).ToList();

    private static List<ChartSeries> CategorySeries(List<ExpensePoint> expenses) =>
        expenses.GroupBy(e => e.Currency).OrderByDescending(c => c.Sum(e => e.Amount))
            .Select(c => new ChartSeries(Expenses, c.Key, c.GroupBy(e => e.Category ?? "").OrderByDescending(x => x.Sum(e => e.Amount)).ThenBy(x => x.Key)
                .Select(x => new ChartPoint(x.Key, x.Sum(e => e.Amount))).ToList())).ToList();

    /// <summary>
    /// Distance driven in a period: from the last reading before it (or, for the first period, the first reading in it) to the last reading
    /// in it. A period without readings has no distance.
    /// </summary>
    private static decimal DistanceIn(IReadOnlyList<OdometerPoint> readings, (DateOnly Start, DateOnly End) period)
    {
        var inside = readings.Where(r => r.Date >= period.Start && r.Date <= period.End).Select(r => r.Value).ToList();
        if (inside.Count == 0) return 0;
        var before = readings.Where(r => r.Date < period.Start).Select(r => (long?)r.Value).Max();
        return Math.Max(0, inside.Max() - (before ?? inside.Min()));
    }

    // ---- summary --------------------------------------------------------------------------------------------

    /// <summary>The currency most money was spent in over the last year, else the one used last (for a vehicle that has been idle); null with none.</summary>
    public static string? MainCurrency(StatsData data, DateOnly today)
    {
        var windowStart = MonthStart(today).AddMonths(-11);
        var recent = data.Fuel.Where(f => f.Date >= windowStart).Select(f => (f.Currency, f.Amount))
            .Concat(data.Expenses.Where(e => e.Date >= windowStart).Select(e => (e.Currency, e.Amount))).ToList();
        return recent.Count > 0
            ? recent.GroupBy(r => r.Currency).OrderByDescending(g => g.Sum(r => r.Amount)).First().Key
            : data.Fuel.OrderByDescending(f => f.Date).Select(f => f.Currency).Concat(data.Expenses.OrderByDescending(e => e.Date).Select(e => e.Currency)).FirstOrDefault();
    }

    public static VehicleSummary Summary(StatsData data, DateOnly today)
    {
        var thisMonth = MonthStart(today);
        var currency = MainCurrency(data, today);

        decimal MonthTotal(DateOnly month, string? inCurrency = null)
        {
            var c = inCurrency ?? currency;
            return data.Fuel.Where(f => f.Currency == c && MonthStart(f.Date) == month).Sum(f => f.Amount)
                + data.Expenses.Where(e => e.Currency == c && MonthStart(e.Date) == month).Sum(e => e.Amount);
        }

        var trend = Enumerable.Range(0, 6).Select(i => thisMonth.AddMonths(i - 5)).Select(m => new MonthAmount($"{m.Year}-{m.Month:00}", currency is null ? 0 : MonthTotal(m))).ToList();
        // Every stored consumption, latest first (by date, then odometer): the average of the last ten.
        var consumptions = data.Fuel.Where(f => f.Consumption is not null).OrderByDescending(f => f.Date).ThenByDescending(f => f.Odometer).Select(f => f.Consumption!.Value).ToList();
        var lastTen = consumptions.Take(TrendFillUps).ToList();

        // Every currency spent in this month or last month, the main one first (also when nothing was spent in it), then by this month.
        var lastMonth = thisMonth.AddMonths(-1);
        var used = data.Fuel.Where(f => MonthStart(f.Date) >= lastMonth && MonthStart(f.Date) <= thisMonth).Select(f => f.Currency)
            .Concat(data.Expenses.Where(e => MonthStart(e.Date) >= lastMonth && MonthStart(e.Date) <= thisMonth).Select(e => e.Currency));
        var spending = (currency is null ? used : used.Prepend(currency)).Distinct()
            .Select(c => new CurrencySpend(c, MonthTotal(thisMonth, c), MonthTotal(lastMonth, c)))
            .OrderByDescending(s => s.Currency == currency).ThenByDescending(s => s.ThisMonth).ThenByDescending(s => s.LastMonth).ThenBy(s => s.Currency, StringComparer.Ordinal)
            .ToList();

        return new VehicleSummary(
            data.Fuel.Count == 0 ? null : data.Fuel.Max(f => f.Date),
            data.Readings.Count == 0 ? null : data.Readings.Max(r => r.Value),
            lastTen.Count == 0 ? null : Math.Round(lastTen.Average(), 2),
            currency, trend[^1].Amount, trend[^2].Amount, trend, data.Fuel.Count, data.Expenses.Count, spending, Trends(data, today, currency));
    }

    public const int TrendFillUps = 10;
    public const int MinTrendFillUps = 3;

    /// <summary>
    /// The trend figures at their defaults (<see cref="TrendPeriod.TwelveWholeMonths"/>, <see cref="TrendPeriod.TenFillUps"/>,
    /// <see cref="TrendPeriod.ThisYearAgainstLast"/>), through <see cref="Trend"/>: what the summary answered before periods existed, for an
    /// older client; the web app asks each figure over its own period (<c>vehicleTrend</c>).
    /// </summary>
    private static DashboardTrends Trends(StatsData data, DateOnly today, string? mainCurrency) =>
        new(Trend(data, TrendFigure.Spending, TrendPeriod.TwelveWholeMonths, today, mainCurrency).Currencies,
            Trend(data, TrendFigure.CostPerDistance, TrendPeriod.TwelveWholeMonths, today, mainCurrency).Currencies,
            Trend(data, TrendFigure.Consumption, TrendPeriod.TenFillUps, today, mainCurrency).Consumption,
            Trend(data, TrendFigure.Distance, TrendPeriod.ThisYearAgainstLast, today, mainCurrency).Distance);

    // ---- a trend figure over its own period (#230) -----------------------------------------------------------

    /// <summary>
    /// A trend figure over <paramref name="period"/> (checked by the caller): the recent window and the one it is compared with, and the
    /// figure's values in both, through <see cref="Figure"/> (one code path, #272): spending is <see cref="FigureMeasure.TotalSpend"/>, cost
    /// per distance <see cref="FigureMeasure.CostPerDistance"/>, consumption <see cref="FigureMeasure.AverageConsumption"/> (its fill-ups
    /// those of the windows), distance <see cref="FigureMeasure.Distance"/>. A trend figure always compares: a period without a comparison
    /// is refused.
    /// </summary>
    public static TrendFigureData Trend(StatsData data, TrendFigure figure, TrendPeriod period, DateOnly today, string? mainCurrency)
    {
        if (period.Against == TrendComparison.None) throw new DomainException("trend.invalid", "This period is not valid.");
        var measure = figure switch
        {
            TrendFigure.Spending => FigureMeasure.TotalSpend,
            TrendFigure.CostPerDistance => FigureMeasure.CostPerDistance,
            TrendFigure.Consumption => FigureMeasure.AverageConsumption,
            _ => FigureMeasure.Distance,
        };
        var f = Figure(data, measure, period, today, mainCurrency);
        var (recent, earlier) = (f.Recent!, f.Earlier!);
        return figure switch
        {
            TrendFigure.Spending or TrendFigure.CostPerDistance => new(recent, earlier, f.Currencies, null, null),
            TrendFigure.Consumption => new(recent, earlier, [], f.Value is { } v ? new ConsumptionChange(v.Recent, v.Earlier, recent.FillUps, earlier.FillUps) : null, null),
            _ => new(recent, earlier, [], null, f.Value),
        };
    }

    // ---- a key figure: any measure over a period, compared or not (#272) ----------------------------------------

    /// <summary>
    /// A key figure: <paramref name="measure"/> over <paramref name="period"/> (checked by the caller) and over the window it is compared
    /// with, if any; a point in time (<see cref="FigureMeasure.Odometer"/>, <see cref="FigureMeasure.LastFillUp"/>) takes no period.
    /// <list type="bullet">
    /// <item>The windows are <see cref="Windows"/>'s, over the fill-ups latest first (by date, then odometer): for the consumption only those
    /// with a stored consumption, every one otherwise.</item>
    /// <item>Totals add up over each window: money (per currency, never converted; the main currency first, always, then the others used in
    /// either window by what was spent in the recent one, then by code), volume, distance (null for a vehicle without a reading) and the
    /// fill-ups.</item>
    /// <item>Per distance is each window's money over its <see cref="DistanceIn"/>, per 100 units, rounded to 3 places, null where nothing was
    /// driven.</item>
    /// <item>Averages: the consumption is the mean of the stored consumptions (2 places, as the summary), a price per unit each window's
    /// money over its volume per currency (3 places, as the charts); the recent one from one fill-up, the earlier only from
    /// <see cref="MinTrendFillUps"/>.</item>
    /// <item>Money is counted by its dates; what fill-ups measure (volume, count, consumption, price) over a window of the last N fill-ups
    /// is those fill-ups themselves, since two on one day may fall either side of it.</item>
    /// </list>
    /// A point in time with a period, or a measure over a period without one, is refused (<c>trend.invalid</c>), as a period is: nothing
    /// is filled in or dropped.
    /// </summary>
    public static FigureData Figure(StatsData data, FigureMeasure measure, TrendPeriod? period, DateOnly today, string? mainCurrency)
    {
        if (!Enum.IsDefined(measure) || measure.IsPointInTime() != period is null) throw new DomainException("trend.invalid", "This period is not valid.");
        switch (measure)
        {
            case FigureMeasure.Odometer:
                return new(null, null, ChartUnit.Distance, [], data.Readings.Count == 0 ? null : new ValueChange(data.Readings.Max(r => r.Value), null), null);
            case FigureMeasure.LastFillUp:
                return new(null, null, null, [], null, new LastFillUpFigure(data.Fuel.Count == 0 ? null : data.Fuel.Max(f => f.Date), data.Fuel.Count, data.Expenses.Count));
        }

        var fillUps = data.Fuel.Where(f => measure != FigureMeasure.AverageConsumption || f.Consumption is not null)
            .OrderByDescending(f => f.Date).ThenByDescending(f => f.Odometer).ToList();
        var (recent, earlierRange, recentItems, earlierItems) = Windows(period!, today, fillUps);
        // No comparison, or an earlier window of fill-ups that holds none: nothing to compare with, no sums of its dates.
        var nothingEarlier = earlierRange is null || earlierItems is { Count: 0 };
        var earlier = earlierRange ?? default;
        // The fill-ups of a window: those counted by number keep their own, windows of dates take those inside.
        List<FuelPoint> FuelIn((DateOnly Start, DateOnly End) w, List<FuelPoint>? items) => items ?? fillUps.Where(f => f.Date >= w.Start && f.Date <= w.End).ToList();
        var recentFuel = FuelIn(recent, recentItems);
        var earlierFuel = nothingEarlier ? [] : FuelIn(earlier, earlierItems);
        var recentWindow = new TrendWindow(recent.Start, recent.End, recentFuel.Count);
        TrendWindow? earlierWindow = earlierRange is { } e ? new TrendWindow(e.Start, e.End, earlierFuel.Count) : null;
        var unit = UnitOf(measure.ToMetric()!.Value);

        // The currencies used, the main one first (always), then by what the recent window spent in them, then by code: the summary's order.
        IEnumerable<string> Ordered(IEnumerable<string> used, Func<string, decimal> spentRecently) =>
            (mainCurrency is null ? used : used.Prepend(mainCurrency)).Distinct()
                .OrderByDescending(c => c == mainCurrency).ThenByDescending(spentRecently).ThenBy(c => c, StringComparer.Ordinal);

        // Money per currency, in the order above.
        List<CurrencyChange> Money(bool fuel, bool expenses)
        {
            var costs = (fuel ? data.Fuel.Select(f => (f.Currency, f.Date, f.Amount)) : [])
                .Concat(expenses ? data.Expenses.Select(x => (x.Currency, x.Date, x.Amount)) : []).ToList();
            decimal Spent(string c, (DateOnly Start, DateOnly End) w) => costs.Where(x => x.Currency == c && x.Date >= w.Start && x.Date <= w.End).Sum(x => x.Amount);
            bool In(DateOnly d) => d >= recent.Start && d <= recent.End || !nothingEarlier && d >= earlier.Start && d <= earlier.End;
            var used = costs.Where(x => In(x.Date)).Select(x => x.Currency);
            return Ordered(used, c => Spent(c, recent))
                .Select(c => new CurrencyChange(c, Spent(c, recent), nothingEarlier ? null : Spent(c, earlier))).ToList();
        }
        List<CurrencyChange> PerDistance(List<CurrencyChange> money)
        {
            var recentDistance = DistanceIn(data.Readings, recent);
            var earlierDistance = nothingEarlier ? 0 : DistanceIn(data.Readings, earlier);
            decimal? Over(decimal? amount, decimal driven) => amount is { } a && driven > 0 ? Math.Round(a / driven * 100, 3) : null;
            return money.Select(m => new CurrencyChange(m.Currency, Over(m.Recent, recentDistance), Over(m.Earlier, earlierDistance))).ToList();
        }
        // A fuel price per currency: the money over the volume of the window's fill-ups in it; the earlier from MinTrendFillUps of them.
        List<CurrencyChange> PricePerUnit(Func<FuelPoint, decimal> amount)
        {
            decimal? Price(List<FuelPoint> rows, int least)
            {
                var volume = rows.Sum(f => f.Volume);
                return rows.Count >= least && volume > 0 ? Math.Round(rows.Sum(amount) / volume, 3) : null;
            }
            return Ordered(recentFuel.Concat(earlierFuel).Select(f => f.Currency), c => recentFuel.Where(f => f.Currency == c).Sum(f => f.Amount))
                .Select(c => new CurrencyChange(c, Price(recentFuel.Where(f => f.Currency == c).ToList(), 1),
                    nothingEarlier ? null : Price(earlierFuel.Where(f => f.Currency == c).ToList(), MinTrendFillUps))).ToList();
        }
        ValueChange Total(Func<List<FuelPoint>, decimal> of) => new(of(recentFuel), nothingEarlier ? null : of(earlierFuel));

        return measure switch
        {
            FigureMeasure.TotalSpend => new(recentWindow, earlierWindow, unit, Money(fuel: true, expenses: true), null, null),
            FigureMeasure.FuelCost => new(recentWindow, earlierWindow, unit, Money(fuel: true, expenses: false), null, null),
            FigureMeasure.ExpenseCost => new(recentWindow, earlierWindow, unit, Money(fuel: false, expenses: true), null, null),
            FigureMeasure.CostPerDistance => new(recentWindow, earlierWindow, unit, PerDistance(Money(fuel: true, expenses: true)), null, null),
            FigureMeasure.FuelCostPerDistance => new(recentWindow, earlierWindow, unit, PerDistance(Money(fuel: true, expenses: false)), null, null),
            FigureMeasure.AveragePricePerUnit => new(recentWindow, earlierWindow, unit, PricePerUnit(f => f.Amount), null, null),
            FigureMeasure.AveragePricePerUnitBeforeDiscount => new(recentWindow, earlierWindow, unit, PricePerUnit(f => f.Amount + f.Discount), null, null),
            FigureMeasure.FuelVolume => new(recentWindow, earlierWindow, unit, [], Total(rows => rows.Sum(f => f.Volume)), null),
            FigureMeasure.FillUps => new(recentWindow, earlierWindow, unit, [], Total(rows => rows.Count), null),
            // The mean of the stored consumptions: the recent as soon as there is one, the earlier from MinTrendFillUps; none without a recent one.
            FigureMeasure.AverageConsumption => new(recentWindow, earlierWindow, unit, [], recentFuel.Count == 0 ? null
                : new ValueChange(Math.Round(recentFuel.Average(f => f.Consumption!.Value), 2), earlierFuel.Count >= MinTrendFillUps ? Math.Round(earlierFuel.Average(f => f.Consumption!.Value), 2) : null), null),
            // Nothing driven is a 0 like the distance chart's; a vehicle without a reading has no distance at all.
            _ => new(recentWindow, earlierWindow, unit, [], data.Readings.Count == 0 ? null : new ValueChange(DistanceIn(data.Readings, recent), nothingEarlier ? null : DistanceIn(data.Readings, earlier)), null),
        };
    }

    /// <summary>
    /// The two windows of a period. A time period ends today, or, whole, with the last completed week (Monday to Sunday), month or year.
    /// Fill-ups: the latest N (<paramref name="fillUps"/>, latest first) from the oldest of them to today, and the N before them up to the
    /// day before; they are handed back too, since two on one day may fall either side of that day. A window before is as long (in
    /// calendar months or years for those, else in days) and ends the day before; this month's is the whole month before (#272); a year
    /// earlier is the same dates a year back (29 February to 28 February), and then fill-ups are those inside its dates. No comparison
    /// (<see cref="TrendComparison.None"/>) has no earlier window.
    /// </summary>
    private static ((DateOnly Start, DateOnly End) Recent, (DateOnly Start, DateOnly End)? Earlier, List<FuelPoint>? RecentItems, List<FuelPoint>? EarlierItems)
        Windows(TrendPeriod period, DateOnly today, List<FuelPoint> fillUps)
    {
        (DateOnly Start, DateOnly End) recent;
        List<FuelPoint>? recentItems = null, earlierItems = null;
        Func<(DateOnly Start, DateOnly End), (DateOnly Start, DateOnly End)> before = w => (w.Start.AddDays(-(w.End.DayNumber - w.Start.DayNumber + 1)), w.Start.AddDays(-1));
        switch (period.Kind)
        {
            case TrendWindowKind.ThisYear:
                recent = (new DateOnly(today.Year, 1, 1), today);
                break;
            case TrendWindowKind.ThisMonth:
                // The month so far, against the whole month before (the owner's choice: "the month before" says it), not its first days.
                recent = (MonthStart(today), today);
                before = w => (w.Start.AddMonths(-1), w.Start.AddDays(-1));
                break;
            case TrendWindowKind.Since:
                recent = (period.Since!.Value, today);
                break;
            default:
                var n = period.Count!.Value;
                switch (period.Unit!.Value)
                {
                    case TrendUnit.FillUps:
                        recentItems = fillUps.Take(n).ToList();
                        var start = recentItems.Count > 0 ? recentItems[^1].Date : today;
                        recent = (start, today);
                        if (period.Against == TrendComparison.None) return (recent, null, recentItems, null);
                        if (period.Against == TrendComparison.Before)
                        {
                            earlierItems = fillUps.Skip(n).Take(n).ToList();
                            var earlierStart = earlierItems.Count > 0 ? earlierItems[^1].Date : start;
                            return (recent, (earlierStart, start.AddDays(-1)), recentItems, earlierItems);
                        }
                        break;
                    case TrendUnit.Days:
                        recent = (today.AddDays(-n + 1), today);
                        break;
                    case TrendUnit.Weeks:
                        // A whole week is Monday to Sunday: the last one completed ends on the Sunday before today (a week ago on a Sunday).
                        var weekEnd = period.Whole ? today.AddDays(-(today.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)today.DayOfWeek)) : today;
                        recent = (weekEnd.AddDays(-7 * n + 1), weekEnd);
                        break;
                    case TrendUnit.Months:
                        if (period.Whole)
                        {
                            var monthEnd = MonthStart(today).AddDays(-1);
                            recent = (MonthStart(monthEnd).AddMonths(-(n - 1)), monthEnd);
                        }
                        else recent = (today.AddMonths(-n).AddDays(1), today);
                        before = w => (w.Start.AddMonths(-n), w.Start.AddDays(-1));
                        break;
                    default: // years
                        recent = period.Whole ? (new DateOnly(today.Year - n, 1, 1), new DateOnly(today.Year - 1, 12, 31)) : (today.AddYears(-n).AddDays(1), today);
                        before = w => (w.Start.AddYears(-n), w.Start.AddDays(-1));
                        break;
                }
                break;
        }
        (DateOnly Start, DateOnly End)? earlier = period.Against switch
        {
            TrendComparison.None => null,
            TrendComparison.YearEarlier => (recent.Start.AddYears(-1), recent.End.AddYears(-1)),
            _ => before(recent),
        };
        // A year earlier counts the fill-ups inside its dates, as a window of dates does.
        return (recent, earlier, period.Against == TrendComparison.YearEarlier ? null : recentItems, null);
    }
}
