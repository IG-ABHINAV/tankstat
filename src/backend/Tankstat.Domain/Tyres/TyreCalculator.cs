namespace Tankstat.Domain.Tyres;

/// <summary>One odometer reading of the vehicle, whatever recorded it.</summary>
public readonly record struct ReadingPoint(DateOnly Date, long Value);

/// <summary>The car's odometer on a day: <see cref="Approximate"/> when it was worked out rather than read that day.</summary>
public readonly record struct OdometerOnDay(long Value, bool Approximate);

/// <summary>How a stretch's distance was found.</summary>
public enum StretchMeasure
{
    /// <summary>From the odometer on both ends.</summary>
    Measured,

    /// <summary>It starts before the vehicle's first reading: counted from that reading.</summary>
    FromFirstReading,

    /// <summary>No reading lies inside it (before the fuel log, or no reading after it went on yet): 0.</summary>
    NotMeasured,
}

/// <summary>A stretch's distance, never negative.</summary>
public readonly record struct StretchDistance(long Distance, bool Approximate, StretchMeasure Measure);

/// <summary>A set's figures: the total (rough figure plus stretches) and the front/rear share of its two pairs.</summary>
/// <param name="PairOneFront">What pair 1 ran at the front (pair 2 ran this at the rear).</param>
/// <param name="PairOneRear">What pair 1 ran at the rear (pair 2 ran this at the front).</param>
/// <param name="PairsUnknown">What ran in stretches without recorded pairs.</param>
public sealed record TyreSetFigures(long Distance, bool Approximate, long Measured, long PairOneFront, long PairOneRear, long PairsUnknown, RoughSuggestion? Suggestion);

/// <summary>
/// The stretches now measure time the rough figure stood for (past swaps were entered): <see cref="Distance"/> would keep the total as it
/// was, standing for the time before <see cref="Until"/>. It is suggested to the user, never applied by itself.
/// </summary>
public sealed record RoughSuggestion(long Distance, DateOnly Until, long MeasuredBefore);

/// <summary>
/// Pure arithmetic of tyre distances (#287), worked out on every read from the vehicle's odometer readings, nothing stored. The odometer
/// on a day is the highest reading of that day (the tie-break of the latest reading), else a straight line between the nearest readings
/// before and after it (approximate), else after the latest reading the latest one (approximate), and before the first reading it is not
/// known. A worked-out value is never saved as a reading.
/// </summary>
public static class TyreCalculator
{
    /// <param name="readings">The vehicle's live readings, any order.</param>
    public static IReadOnlyList<ReadingPoint> Sorted(IEnumerable<ReadingPoint> readings) =>
        [.. readings.OrderBy(r => r.Date).ThenBy(r => r.Value)];

    /// <param name="sorted">From <see cref="Sorted"/>.</param>
    public static OdometerOnDay? OdometerOn(IReadOnlyList<ReadingPoint> sorted, DateOnly day)
    {
        if (sorted.Count == 0 || day < sorted[0].Date) return null;
        ReadingPoint? before = null, after = null;
        long? sameDay = null;
        foreach (var r in sorted)
        {
            if (r.Date == day) sameDay = sameDay is { } s ? Math.Max(s, r.Value) : r.Value;
            else if (r.Date < day) before = r;
            else { after = r; break; }
        }
        if (sameDay is { } exact) return new OdometerOnDay(exact, false);
        if (after is not { } next) return new OdometerOnDay(before!.Value.Value, true);
        var prev = before!.Value;
        var span = next.Date.DayNumber - prev.Date.DayNumber;
        var part = day.DayNumber - prev.Date.DayNumber;
        return new OdometerOnDay(prev.Value + (long)Math.Round((next.Value - prev.Value) * (double)part / span, MidpointRounding.AwayFromZero), true);
    }

    /// <summary>
    /// The distance of the stretch from <paramref name="from"/> to <paramref name="until"/> (null: still on the car, up to the latest reading,
    /// which then counts as exact). A pair of readings that falls (a reading a photo filled in is not checked) gives 0, never less.
    /// </summary>
    public static StretchDistance Distance(IReadOnlyList<ReadingPoint> sorted, DateOnly from, DateOnly? until)
    {
        if (sorted.Count == 0) return new StretchDistance(0, false, StretchMeasure.NotMeasured);
        var latest = sorted[^1];
        OdometerOnDay end;
        if (until is { } u)
        {
            if (OdometerOn(sorted, u) is not { } e) return new StretchDistance(0, false, StretchMeasure.NotMeasured);
            end = e;
        }
        else
        {
            if (latest.Date < from) return new StretchDistance(0, false, StretchMeasure.NotMeasured);
            end = new OdometerOnDay(OdometerOn(sorted, latest.Date)!.Value.Value, false);
        }
        var start = OdometerOn(sorted, from);
        var measure = StretchMeasure.Measured;
        if (start is null && sorted[0].Date >= (until ?? latest.Date))
            return new StretchDistance(0, false, StretchMeasure.NotMeasured); // the fuel log starts on its last day: no reading inside it
        if (start is null)
        {
            start = new OdometerOnDay(OdometerOn(sorted, sorted[0].Date)!.Value.Value, false);
            measure = StretchMeasure.FromFirstReading;
        }
        return new StretchDistance(Math.Max(0, end.Value - start.Value.Value), end.Approximate || start.Value.Approximate, measure);
    }

    /// <summary>A whole set's figures from its live stretches and its rough figure.</summary>
    public static TyreSetFigures SetFigures(IReadOnlyList<ReadingPoint> sorted, TyreSet set, IEnumerable<TyreStretch> stretches)
    {
        long measured = 0, front = 0, rear = 0, unknown = 0, before = 0;
        var approximate = set.RoughDistance > 0;
        DateOnly? firstMeasured = null;
        foreach (var s in stretches)
        {
            var d = Distance(sorted, s.From, s.Until);
            measured += d.Distance;
            approximate |= d.Approximate || d.Measure == StretchMeasure.FromFirstReading;
            switch (s.PairOneFront)
            {
                case true: front += d.Distance; break;
                case false: rear += d.Distance; break;
                default: unknown += d.Distance; break;
            }
            if (d.Measure == StretchMeasure.NotMeasured) continue;
            var measuredFrom = d.Measure == StretchMeasure.FromFirstReading ? sorted[0].Date : s.From;
            if (firstMeasured is null || measuredFrom < firstMeasured) firstMeasured = measuredFrom;
            if (set.RoughUntil is { } roughUntil && measuredFrom < roughUntil)
            {
                var cut = s.Until is { } u && u < roughUntil ? u : roughUntil;
                before += Distance(sorted, s.From, cut).Distance;
            }
        }
        RoughSuggestion? suggestion = before > 0 && set.RoughDistance > 0 && firstMeasured is { } first
            ? new RoughSuggestion(Math.Max(0, (long)Math.Round((set.RoughDistance - before) / 100.0, MidpointRounding.AwayFromZero) * 100), first, before)
            : null;
        return new TyreSetFigures(set.RoughDistance + measured, approximate, measured, front, rear, unknown, suggestion);
    }
}
