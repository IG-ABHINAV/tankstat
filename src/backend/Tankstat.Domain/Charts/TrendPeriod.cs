using System.Text.Json.Serialization;

namespace Tankstat.Domain.Charts;

/// <summary>How a trend figure's recent window is set (#230).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrendWindowKind>))] // stored by name: a later order of the members never remaps a saved period
public enum TrendWindowKind
{
    /// <summary>The last <see cref="TrendPeriod.Count"/> <see cref="TrendPeriod.Unit"/>s, ending today (or, whole, with the last one completed).</summary>
    Last,
    /// <summary>From <see cref="TrendPeriod.Since"/> to today.</summary>
    Since,
    /// <summary>From 1 January of this year to today: the year so far, which no stored date can stay (a date would freeze at the year it was saved).</summary>
    ThisYear,
}

[JsonConverter(typeof(JsonStringEnumConverter<TrendUnit>))]
public enum TrendUnit
{
    Days,
    Weeks,
    Months,
    Years,
    /// <summary>Fill-ups rather than time: the window runs from the oldest of the last N to today.</summary>
    FillUps,
}

/// <summary>What the recent window is compared with.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrendComparison>))]
public enum TrendComparison
{
    /// <summary>A window of the same length (or the same number of fill-ups) just before it.</summary>
    Before,
    /// <summary>The same dates one year back.</summary>
    YearEarlier,
}

/// <summary>
/// The period a trend figure looks at and what it compares it with (#230): the last N days, weeks, months, years or fill-ups (weeks,
/// months and years whole if asked: Monday to Sunday, calendar months and years, the running one left out), since a date, or this year so
/// far; against the period before or the same dates a year earlier. A tile without one shows its figure's default (<see cref="TwelveWholeMonths"/>,
/// <see cref="TenFillUps"/>, <see cref="ThisYearAgainstLast"/>), exactly what the figure showed before periods existed.
/// </summary>
public sealed record TrendPeriod(TrendWindowKind Kind, TrendUnit? Unit = null, int? Count = null, DateOnly? Since = null, bool Whole = false, TrendComparison Against = TrendComparison.Before)
{
    public const int MaxCount = 1000;

    /// <summary>The earliest date a period may start from.</summary>
    public static readonly DateOnly Earliest = new(2000, 1, 1);

    /// <summary>The last 12 whole months against the 12 before: <c>spendingTrend</c> and <c>costPerDistance</c> without a period of their own.</summary>
    public static readonly TrendPeriod TwelveWholeMonths = new(TrendWindowKind.Last, TrendUnit.Months, 12, Whole: true);

    /// <summary>The last 10 fill-ups with a stored consumption against the 10 before: <c>consumptionTrend</c>'s default.</summary>
    public static readonly TrendPeriod TenFillUps = new(TrendWindowKind.Last, TrendUnit.FillUps, 10);

    /// <summary>This year so far against last year to the same day: <c>distanceTrend</c>'s default.</summary>
    public static readonly TrendPeriod ThisYearAgainstLast = new(TrendWindowKind.ThisYear, Against: TrendComparison.YearEarlier);

    /// <summary>
    /// The period as it may be stored and asked for, unchanged (nothing is filled in or dropped, so what is saved is what was checked):
    /// <c>trend.countRange</c> for a count outside 1..<see cref="MaxCount"/>, <c>trend.sinceRange</c> for a date before
    /// <see cref="Earliest"/> or after <paramref name="today"/>, <c>trend.wholeNeedsUnit</c> for <see cref="Whole"/> without weeks, months
    /// or years, <c>trend.invalid</c> for anything else that does not fit its kind.
    /// </summary>
    public TrendPeriod Validated(DateOnly today)
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Against) || Unit is { } unit && !Enum.IsDefined(unit)) throw Invalid();
        if (Whole && (Kind != TrendWindowKind.Last || Unit is not (TrendUnit.Weeks or TrendUnit.Months or TrendUnit.Years)))
            throw new DomainException("trend.wholeNeedsUnit", "Whole periods are weeks, months or years.");
        switch (Kind)
        {
            case TrendWindowKind.Last:
                if (Unit is null || Since is not null) throw Invalid();
                if (Count is not (>= 1 and <= MaxCount))
                    throw new DomainException("trend.countRange", $"A period is 1 to {MaxCount} long.", new { Max = MaxCount });
                break;
            case TrendWindowKind.Since:
                if (Unit is not null || Count is not null || Since is not { } since) throw Invalid();
                if (since < Earliest || since > today)
                    throw new DomainException("trend.sinceRange", "A period starts on a day from 2000 to today.", new { Earliest = Earliest.ToString("yyyy-MM-dd"), Latest = today.ToString("yyyy-MM-dd") });
                break;
            default: // ThisYear: nothing else to say
                if (Unit is not null || Count is not null || Since is not null) throw Invalid();
                break;
        }
        return this;
    }

    private static DomainException Invalid() => new("trend.invalid", "This period is not valid.");
}
