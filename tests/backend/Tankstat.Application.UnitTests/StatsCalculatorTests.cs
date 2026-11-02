using Tankstat.Application.Stats;
using Tankstat.Domain.Charts;

namespace Tankstat.Application.UnitTests;

public class StatsCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static FuelPoint Fill(string date, decimal volume, decimal amount, long odometer, string currency = "HUF", bool full = true, decimal? consumption = null, decimal discount = 0) =>
        new(DateOnly.Parse(date), volume, amount, currency, odometer, full, consumption, discount);

    private static ExpensePoint Cost(string date, decimal amount, string? category = "Service", string currency = "HUF") => new(DateOnly.Parse(date), category, amount, currency);

    private static ChartConfig Config(ChartMetric metric = ChartMetric.TotalSpend, ChartGrouping grouping = ChartGrouping.Month, ChartRange range = ChartRange.Last3Months,
        ChartKind kind = ChartKind.Bar, bool stacked = false, DateOnly? from = null, DateOnly? to = null) => new(metric, grouping, kind, range, stacked, from, to);

    private static StatsData Data(FuelPoint[]? fuel = null, ExpensePoint[]? expenses = null, OdometerPoint[]? readings = null) =>
        new(fuel ?? [], expenses ?? [], readings ?? (fuel ?? []).Select(f => new OdometerPoint(f.Date, f.Odometer)).ToArray());

    private static decimal?[] Values(ChartSeries s) => s.Points.Select(p => p.Value).ToArray();

    // ---- periods -------------------------------------------------------------------------------------------

    [Fact]
    public void MonthlyRanges_AreWholeCalendarMonths_WithEmptyOnesIncluded()
    {
        var chart = StatsCalculator.Chart(Config(range: ChartRange.Last3Months), Data([Fill("2026-08-03", 40, 100, 1000)]), Today);

        Assert.Equal(["2026-08", "2026-09", "2026-10"], chart.Series.Single().Points.Select(p => p.Key));
        Assert.Equal([100m, 0m, 0m], Values(chart.Series.Single()));
    }

    [Theory]
    [InlineData(ChartRange.Last6Months, 6, "2026-05")]
    [InlineData(ChartRange.Last12Months, 12, "2025-11")]
    public void HalfAYear_AndAYear_AreCountedInMonths(ChartRange range, int months, string first)
    {
        var chart = StatsCalculator.Chart(Config(range: range), Data([Fill("2026-08-03", 40, 100, 1000)]), Today);

        var keys = chart.Series.Single().Points.Select(p => p.Key).ToList();
        Assert.Equal((months, first, "2026-10"), (keys.Count, keys[0], keys[^1]));
    }

    [Fact]
    public void OneMonth_IsARollingWindow_NotACalendarMonth()
    {
        var data = Data([Fill("2026-09-14", 40, 100, 1000), Fill("2026-09-15", 40, 200, 1100), Fill("2026-10-15", 40, 300, 1200)]);

        var chart = StatsCalculator.Chart(Config(range: ChartRange.Last1Month), data, Today);

        // 2026-09-16 .. 2026-10-15: the 14th and 15th of September are out, the 15th of October is in
        Assert.Equal([0m, 300m], Values(chart.Series.Single()));
    }

    [Fact]
    public void ThisYear_LastYear_AndAll()
    {
        var data = Data([Fill("2025-12-30", 40, 100, 1000), Fill("2026-02-01", 40, 200, 1100)]);

        Assert.Equal(["2026-01", "2026-02", "2026-03", "2026-04", "2026-05", "2026-06", "2026-07", "2026-08", "2026-09", "2026-10"],
            StatsCalculator.Chart(Config(range: ChartRange.ThisYear), data, Today).Series.Single().Points.Select(p => p.Key));
        Assert.Equal(["2025"], StatsCalculator.Chart(Config(grouping: ChartGrouping.Year, range: ChartRange.LastYear), data, Today).Series.Single().Points.Select(p => p.Key));
        Assert.Equal(["2025", "2026"], StatsCalculator.Chart(Config(grouping: ChartGrouping.Year, range: ChartRange.All), data, Today).Series.Single().Points.Select(p => p.Key));
    }

    [Fact]
    public void ACustomRange_UsesExactlyTheChosenDays()
    {
        var data = Data([Fill("2026-08-31", 40, 1, 1000), Fill("2026-09-01", 40, 10, 1100), Fill("2026-09-30", 40, 100, 1200), Fill("2026-10-01", 40, 1000, 1300)]);

        var chart = StatsCalculator.Chart(Config(range: ChartRange.Custom, from: new DateOnly(2026, 9, 1), to: new DateOnly(2026, 9, 30)), data, Today);

        Assert.Equal(["2026-09"], chart.Series.Single().Points.Select(p => p.Key));
        Assert.Equal([110m], Values(chart.Series.Single()));
    }

    [Fact]
    public void Quarters_AndYears_AreGrouped()
    {
        var data = Data([Fill("2026-02-10", 40, 10, 1000), Fill("2026-03-10", 40, 20, 1100), Fill("2026-04-10", 40, 100, 1200)]);

        var quarters = StatsCalculator.Chart(Config(grouping: ChartGrouping.Quarter, range: ChartRange.ThisYear), data, Today);

        Assert.Equal(["2026-Q1", "2026-Q2", "2026-Q3", "2026-Q4"], quarters.Series.Single().Points.Select(p => p.Key));
        Assert.Equal([30m, 100m, 0m, 0m], Values(quarters.Series.Single()));
    }

    [Fact]
    public void WithoutAnyData_AllRangeIsEmpty()
    {
        Assert.Empty(StatsCalculator.Chart(Config(range: ChartRange.All), Data(), Today).Series);
    }

    [Fact]
    public void AllRange_ReachesALogDatedAfterToday()
    {
        var data = Data([Fill("2026-09-20", 40, 100, 1000), Fill("2026-11-02", 40, 200, 1100)]); // a device whose clock is ahead

        var chart = StatsCalculator.Chart(Config(range: ChartRange.All), data, Today);

        Assert.Equal(["2026-09", "2026-10", "2026-11"], chart.Series.Single().Points.Select(p => p.Key));
        Assert.Equal([100m, 0m, 200m], Values(chart.Series.Single()));
    }

    // ---- metrics -------------------------------------------------------------------------------------------

    [Fact]
    public void TotalSpend_AddsFuelAndExpenses_PerCurrency_LargestFirst()
    {
        var data = Data([Fill("2026-09-03", 40, 20000, 1000), Fill("2026-09-20", 20, 50, 1100, currency: "EUR")], [Cost("2026-09-05", 35000), Cost("2026-10-02", 1500)]);

        var chart = StatsCalculator.Chart(Config(), data, Today);

        Assert.Equal(ChartUnit.Currency, chart.Unit);
        Assert.Equal(["HUF", "EUR"], chart.Series.Select(s => s.Currency));
        Assert.Equal([0m, 55000m, 1500m], Values(chart.Series[0]));
        Assert.Equal([0m, 50m, 0m], Values(chart.Series[1]));
        Assert.All(chart.Series, s => Assert.Equal("total", s.Kind));
    }

    [Fact]
    public void TotalSpend_CanBeSplitIntoFuelAndExpenses()
    {
        var data = Data([Fill("2026-09-03", 40, 20000, 1000)], [Cost("2026-09-05", 35000)]);

        var chart = StatsCalculator.Chart(Config(stacked: true), data, Today);

        Assert.Equal(["expenses", "fuel"], chart.Series.Select(s => s.Kind).Order());
        Assert.Equal([0m, 20000m, 0m], Values(chart.Series.Single(s => s.Kind == "fuel")));
        Assert.Equal([0m, 35000m, 0m], Values(chart.Series.Single(s => s.Kind == "expenses")));
    }

    [Fact]
    public void FuelCost_AndExpenseCost_AreSeparate()
    {
        var data = Data([Fill("2026-09-03", 40, 20000, 1000)], [Cost("2026-09-05", 35000)]);

        Assert.Equal([0m, 20000m, 0m], Values(StatsCalculator.Chart(Config(ChartMetric.FuelCost), data, Today).Series.Single()));
        Assert.Equal([0m, 35000m, 0m], Values(StatsCalculator.Chart(Config(ChartMetric.ExpenseCost), data, Today).Series.Single()));
    }

    [Fact]
    public void Volume_AndFillUps_AreCounted()
    {
        var data = Data([Fill("2026-09-03", 40.5m, 1, 1000), Fill("2026-09-20", 20, 1, 1100, full: false)]);

        var volume = StatsCalculator.Chart(Config(ChartMetric.FuelVolume), data, Today);
        var fills = StatsCalculator.Chart(Config(ChartMetric.FillUps), data, Today);

        Assert.Equal((ChartUnit.Volume, ChartUnit.Count), (volume.Unit, fills.Unit));
        Assert.Equal([0m, 60.5m, 0m], Values(volume.Series.Single()));
        Assert.Equal([0m, 2m, 0m], Values(fills.Series.Single()));
    }

    [Fact]
    public void AverageConsumption_IsTheMeanOfTheStoredValues_AndEmptyWhereThereAreNone()
    {
        var data = Data([Fill("2026-08-03", 40, 1, 1000), Fill("2026-09-03", 30, 1, 1500, consumption: 6m), Fill("2026-09-20", 30, 1, 2000, consumption: 7m), Fill("2026-09-25", 10, 1, 2100, full: false)]);

        var chart = StatsCalculator.Chart(Config(ChartMetric.AverageConsumption), data, Today);

        Assert.Equal(ChartUnit.Consumption, chart.Unit);
        Assert.Equal([null, 6.5m, null], Values(chart.Series.Single()));
    }

    [Fact]
    public void PricePerUnit_IsCostDividedByVolume_PerCurrency()
    {
        var data = Data([Fill("2026-09-03", 40, 24000, 1000), Fill("2026-09-20", 10, 7000, 1100)]);

        var chart = StatsCalculator.Chart(Config(ChartMetric.AveragePricePerUnit), data, Today);

        Assert.Equal([null, 620m, null], Values(chart.Series.Single())); // 31000 / 50
        Assert.Equal("HUF", chart.Series.Single().Currency);
    }

    [Fact]
    public void PricePerUnitBeforeDiscount_AddsTheDiscountsBack_WhileThePricePaidLeavesThemOff()
    {
        var data = Data([Fill("2026-09-03", 43, 34744, 1000, discount: 430), Fill("2026-09-20", 10, 8180, 1100)]);

        var paid = StatsCalculator.Chart(Config(ChartMetric.AveragePricePerUnit), data, Today);
        var pump = StatsCalculator.Chart(Config(ChartMetric.AveragePricePerUnitBeforeDiscount), data, Today);

        Assert.Equal([null, 809.887m, null], Values(paid.Series.Single())); // 42 924 / 53
        Assert.Equal([null, 818m, null], Values(pump.Series.Single())); // 43 354 / 53
        Assert.Equal(paid.Unit, pump.Unit);
    }

    [Fact]
    public void Distance_IsMeasuredBetweenReadings_AcrossPeriodBoundaries()
    {
        var readings = new[] { new OdometerPoint(DateOnly.Parse("2026-07-20"), 1000), new OdometerPoint(DateOnly.Parse("2026-08-10"), 1400), new OdometerPoint(DateOnly.Parse("2026-08-25"), 1700), new OdometerPoint(DateOnly.Parse("2026-10-05"), 2300) };

        var chart = StatsCalculator.Chart(Config(ChartMetric.Distance, range: ChartRange.Last3Months), Data(readings: readings), Today);

        Assert.Equal(ChartUnit.Distance, chart.Unit);
        Assert.Equal([700m, 0m, 600m], Values(chart.Series.Single())); // Aug: 1700-1000; Sep: no reading; Oct: 2300-1700
    }

    [Fact]
    public void Distance_OfTheFirstEverPeriod_StartsAtItsFirstReading()
    {
        var readings = new[] { new OdometerPoint(DateOnly.Parse("2026-08-10"), 1400), new OdometerPoint(DateOnly.Parse("2026-08-25"), 1700) };

        var chart = StatsCalculator.Chart(Config(ChartMetric.Distance, range: ChartRange.Last3Months), Data(readings: readings), Today);

        Assert.Equal([300m, 0m, 0m], Values(chart.Series.Single()));
    }

    [Fact]
    public void ExpensesByCategory_AreSortedByAmount_WithUncategorisedLast_PerCurrency()
    {
        var data = Data(expenses: [Cost("2026-09-01", 100, "Parking"), Cost("2026-09-02", 5000, "Service"), Cost("2026-09-03", 300, "Parking"), Cost("2026-09-04", 50, null), Cost("2026-09-05", 20, "Wash", "EUR")]);

        var chart = StatsCalculator.Chart(Config(ChartMetric.ExpenseCost, ChartGrouping.Category, ChartRange.All), data, Today);

        Assert.Equal(["HUF", "EUR"], chart.Series.Select(s => s.Currency));
        Assert.Equal([("Service", 5000m), ("Parking", 400m), ("", 50m)], chart.Series[0].Points.Select(p => (p.Key, p.Value!.Value)));
        Assert.Equal([("Wash", 20m)], chart.Series[1].Points.Select(p => (p.Key, p.Value!.Value)));
    }

    [Fact]
    public void AnInvalidChart_IsRejected()
    {
        Assert.Throws<Domain.DomainException>(() => StatsCalculator.Chart(Config(ChartMetric.FuelCost, ChartGrouping.Category), Data(), Today));
    }

    // ---- summary -------------------------------------------------------------------------------------------

    [Fact]
    public void Summary_HasTheKeyFigures_InTheMainCurrency()
    {
        var data = Data(
            [Fill("2026-08-03", 40, 20000, 1000, consumption: null), Fill("2026-09-10", 30, 18000, 1500, consumption: 6m), Fill("2026-10-05", 30, 21000, 2000, consumption: 7m), Fill("2026-10-06", 5, 10, 2050, currency: "EUR")],
            [Cost("2026-10-02", 35000), Cost("2025-09-01", 99999)]);

        var s = StatsCalculator.Summary(data, Today);

        Assert.Equal(("HUF", new DateOnly(2026, 10, 6), 2050L, 6.5m), (s.Currency, s.LastFillUpDate, s.LatestOdometer, s.AverageConsumption));
        Assert.Equal((56000m, 18000m), (s.ThisMonthSpend, s.LastMonthSpend));
        Assert.Equal((4, 2), (s.FillUpCount, s.ExpenseCount));
        Assert.Equal(["2026-05", "2026-06", "2026-07", "2026-08", "2026-09", "2026-10"], s.SpendTrend.Select(m => m.Month));
        Assert.Equal([0m, 0m, 0m, 20000m, 18000m, 56000m], s.SpendTrend.Select(m => m.Amount));
    }

    [Fact]
    public void Summary_ShowsEveryCurrencySpentThisMonthOrLast_TheMainOneFirst_NothingConverted()
    {
        var data = Data(
            [Fill("2026-09-10", 30, 18000, 1500), Fill("2026-10-05", 30, 21000, 2000), Fill("2026-10-06", 40, 61.40m, 2400, currency: "EUR"), Fill("2026-09-20", 10, 30, 1900, currency: "CHF")],
            [Cost("2026-10-02", 35000), Cost("2026-10-07", 12.50m, currency: "EUR"), Cost("2026-03-01", 500, currency: "USD")]); // March: outside both months

        var s = StatsCalculator.Summary(data, Today);

        Assert.Equal("HUF", s.Currency);
        Assert.Equal([new CurrencySpend("HUF", 56000m, 18000m), new CurrencySpend("EUR", 73.90m, 0m), new CurrencySpend("CHF", 0m, 30m)], s.Spending);
        Assert.Equal(56000m, s.ThisMonthSpend); // the main currency's figures stay as they were
    }

    [Fact]
    public void Summary_OfAVehicleWithoutData_IsEmpty()
    {
        var s = StatsCalculator.Summary(Data(), Today);

        Assert.Equal((null, null, null, null, 0m), (s.LastFillUpDate, s.LatestOdometer, s.AverageConsumption, s.Currency, s.ThisMonthSpend));
        Assert.Equal(6, s.SpendTrend.Count);
        Assert.Empty(s.Spending);
    }

    [Fact]
    public void Summary_AverageUsesTheLastTenValues_AndAnIdleVehicleKeepsItsLastCurrency()
    {
        var fills = Enumerable.Range(0, 12).Select(i => Fill($"2024-{i + 1:00}-05", 30, 100, 1000 + i * 500, currency: "EUR", consumption: i < 2 ? 100m : 5m)).ToArray();

        var s = StatsCalculator.Summary(Data(fills), Today);

        Assert.Equal(5m, s.AverageConsumption); // the two old outliers are outside the last ten
        Assert.Equal("EUR", s.Currency); // nothing in the last year, so the last used currency
        Assert.Equal(0m, s.ThisMonthSpend);
        Assert.Equal([new CurrencySpend("EUR", 0m, 0m)], s.Spending); // the main currency is always listed, at zero when idle
    }
}
