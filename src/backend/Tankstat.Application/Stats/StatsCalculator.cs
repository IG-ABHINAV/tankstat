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
        var unit = config.Metric switch
        {
            ChartMetric.FuelVolume => ChartUnit.Volume,
            ChartMetric.Distance => ChartUnit.Distance,
            ChartMetric.AverageConsumption => ChartUnit.Consumption,
            ChartMetric.FillUps => ChartUnit.Count,
            _ => ChartUnit.Currency,
        };
        if (RangeOf(config, today, data) is not { } range) return new ChartData(unit, []);
        var (from, to) = range;

        var fuel = data.Fuel.Where(f => f.Date >= from && f.Date <= to).ToList();
        var expenses = data.Expenses.Where(e => e.Date >= from && e.Date <= to).ToList();

        if (config.Grouping == ChartGrouping.Category) return new ChartData(unit, CategorySeries(expenses));

        var periods = Periods(from, to, config.Grouping);
        var keys = periods.Select(p => Key(p, config.Grouping)).ToList();
        IReadOnlyList<ChartSeries> series = config.Metric switch
        {
            ChartMetric.TotalSpend when config.Stacked => Money(keys, config.Grouping, fuel, expenses, includeFuel: true, includeExpenses: true, split: true),
            ChartMetric.TotalSpend => Money(keys, config.Grouping, fuel, expenses, includeFuel: true, includeExpenses: true, split: false),
            ChartMetric.FuelCost => Money(keys, config.Grouping, fuel, expenses, includeFuel: true, includeExpenses: false, split: false),
            ChartMetric.ExpenseCost => Money(keys, config.Grouping, fuel, expenses, includeFuel: false, includeExpenses: true, split: false),
            ChartMetric.FuelVolume => [new ChartSeries(Fuel, null, keys.Select(k => new ChartPoint(k, fuel.Where(f => Key(f.Date, config.Grouping) == k).Sum(f => f.Volume))).ToList())],
            ChartMetric.FillUps => [new ChartSeries(Fuel, null, keys.Select(k => new ChartPoint(k, fuel.Count(f => Key(f.Date, config.Grouping) == k))).ToList())],
            ChartMetric.AverageConsumption => [new ChartSeries(Fuel, null, keys.Select(k => Mean(k, fuel.Where(f => Key(f.Date, config.Grouping) == k && f.Consumption is not null).Select(f => f.Consumption!.Value))).ToList())],
            ChartMetric.AveragePricePerUnit => PricePerUnit(keys, config.Grouping, fuel, f => f.Amount),
            ChartMetric.AveragePricePerUnitBeforeDiscount => PricePerUnit(keys, config.Grouping, fuel, f => f.Amount + f.Discount),
            ChartMetric.Distance => [new ChartSeries(Total, null, periods.Select(p => new ChartPoint(Key(p, config.Grouping), DistanceIn(data.Readings, Bounds(p, config.Grouping)))).ToList())],
            _ => [],
        };
        return new ChartData(unit, series);
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

    public static VehicleSummary Summary(StatsData data, DateOnly today)
    {
        var thisMonth = MonthStart(today);
        var windowStart = thisMonth.AddMonths(-11);
        var recent = data.Fuel.Where(f => f.Date >= windowStart).Select(f => (f.Currency, f.Amount))
            .Concat(data.Expenses.Where(e => e.Date >= windowStart).Select(e => (e.Currency, e.Amount))).ToList();

        // The currency most money was spent in over the last year, else the one used last (for a vehicle that has been idle).
        var currency = recent.Count > 0
            ? recent.GroupBy(r => r.Currency).OrderByDescending(g => g.Sum(r => r.Amount)).First().Key
            : data.Fuel.OrderByDescending(f => f.Date).Select(f => f.Currency).Concat(data.Expenses.OrderByDescending(e => e.Date).Select(e => e.Currency)).FirstOrDefault();

        decimal MonthTotal(DateOnly month, string? inCurrency = null)
        {
            var c = inCurrency ?? currency;
            return data.Fuel.Where(f => f.Currency == c && MonthStart(f.Date) == month).Sum(f => f.Amount)
                + data.Expenses.Where(e => e.Currency == c && MonthStart(e.Date) == month).Sum(e => e.Amount);
        }

        var trend = Enumerable.Range(0, 6).Select(i => thisMonth.AddMonths(i - 5)).Select(m => new MonthAmount($"{m.Year}-{m.Month:00}", currency is null ? 0 : MonthTotal(m))).ToList();
        var consumptions = data.Fuel.Where(f => f.Consumption is not null).OrderByDescending(f => f.Date).ThenByDescending(f => f.Odometer).Take(10).Select(f => f.Consumption!.Value).ToList();

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
            consumptions.Count == 0 ? null : Math.Round(consumptions.Average(), 2),
            currency, trend[^1].Amount, trend[^2].Amount, trend, data.Fuel.Count, data.Expenses.Count, spending);
    }
}
