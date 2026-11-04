using Tankstat.Domain;
using Tankstat.Domain.Charts;

namespace Tankstat.Domain.UnitTests;

/// <summary>A trend figure's own period (#230): what may be stored and asked for, checked exactly as it is (nothing filled in or dropped).</summary>
public class TrendPeriodTests
{
    private static readonly DateOnly Today = new(2026, 11, 3);

    private static string Key(TrendPeriod period) => Assert.Throws<DomainException>(() => period.Validated(Today)).Key;

    [Theory]
    [InlineData(TrendUnit.Days, 1, false)]
    [InlineData(TrendUnit.Weeks, 8, true)]
    [InlineData(TrendUnit.Months, 12, true)]
    [InlineData(TrendUnit.Years, 1000, false)]
    [InlineData(TrendUnit.FillUps, 20, false)]
    public void TheLastNOfAUnit_IsValid_AndComesBackUnchanged(TrendUnit unit, int count, bool whole)
    {
        var period = new TrendPeriod(TrendWindowKind.Last, unit, count, Whole: whole, Against: TrendComparison.YearEarlier);

        Assert.Same(period, period.Validated(Today));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(TrendPeriod.MaxCount + 1)]
    public void ACountOutsideItsRange_IsRefused(int count) => Assert.Equal("trend.countRange", Key(new(TrendWindowKind.Last, TrendUnit.Days, count)));

    [Fact]
    public void TheLastN_NeedsAUnitAndACount_AndNoDate()
    {
        Assert.Equal("trend.invalid", Key(new(TrendWindowKind.Last, Count: 5)));
        Assert.Equal("trend.countRange", Key(new(TrendWindowKind.Last, TrendUnit.Weeks)));
        Assert.Equal("trend.invalid", Key(new(TrendWindowKind.Last, TrendUnit.Weeks, 5, Since: Today)));
    }

    [Theory]
    [InlineData(TrendUnit.Days)]
    [InlineData(TrendUnit.FillUps)]
    public void WholeIsOnlyForWeeksMonthsAndYears(TrendUnit unit) => Assert.Equal("trend.wholeNeedsUnit", Key(new(TrendWindowKind.Last, unit, 5, Whole: true)));

    [Fact]
    public void ASince_FromTheEarliestDayToToday_IsValid_TomorrowAndBefore2000AreNot()
    {
        Assert.NotNull(new TrendPeriod(TrendWindowKind.Since, Since: Today).Validated(Today));
        Assert.NotNull(new TrendPeriod(TrendWindowKind.Since, Since: TrendPeriod.Earliest).Validated(Today));
        Assert.Equal("trend.sinceRange", Key(new(TrendWindowKind.Since, Since: Today.AddDays(1))));
        Assert.Equal("trend.sinceRange", Key(new(TrendWindowKind.Since, Since: new DateOnly(1999, 12, 31))));
        Assert.Equal("trend.invalid", Key(new(TrendWindowKind.Since))); // no date
        Assert.Equal("trend.invalid", Key(new(TrendWindowKind.Since, TrendUnit.Days, Since: Today)));
        Assert.Equal("trend.wholeNeedsUnit", Key(new(TrendWindowKind.Since, Since: Today, Whole: true)));
    }

    [Fact]
    public void ThisYear_TakesNothingElse()
    {
        Assert.NotNull(TrendPeriod.ThisYearAgainstLast.Validated(Today));
        Assert.Equal("trend.invalid", Key(new(TrendWindowKind.ThisYear, TrendUnit.Days)));
        Assert.Equal("trend.invalid", Key(new(TrendWindowKind.ThisYear, Count: 1)));
        Assert.Equal("trend.invalid", Key(new(TrendWindowKind.ThisYear, Since: Today)));
        Assert.Equal("trend.wholeNeedsUnit", Key(new(TrendWindowKind.ThisYear, Whole: true)));
    }

    [Fact]
    public void AnUnknownKindUnitOrComparison_IsRefused()
    {
        Assert.Equal("trend.invalid", Key(new((TrendWindowKind)9)));
        Assert.Equal("trend.invalid", Key(new(TrendWindowKind.Last, (TrendUnit)9, 5)));
        Assert.Equal("trend.invalid", Key(new(TrendWindowKind.Last, TrendUnit.Days, 5, Against: (TrendComparison)9)));
    }

    [Fact]
    public void TheDefaults_AreWhatTheFiguresShowedBeforePeriodsExisted()
    {
        Assert.Equal(new TrendPeriod(TrendWindowKind.Last, TrendUnit.Months, 12, Whole: true, Against: TrendComparison.Before), TrendPeriod.TwelveWholeMonths);
        Assert.Equal(new TrendPeriod(TrendWindowKind.Last, TrendUnit.FillUps, 10, Against: TrendComparison.Before), TrendPeriod.TenFillUps);
        Assert.Equal(new TrendPeriod(TrendWindowKind.ThisYear, Against: TrendComparison.YearEarlier), TrendPeriod.ThisYearAgainstLast);
    }
}
