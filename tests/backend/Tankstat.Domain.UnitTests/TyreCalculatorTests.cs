using Tankstat.Domain.Tyres;

namespace Tankstat.Domain.UnitTests;

public class TyreCalculatorTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static readonly IReadOnlyList<ReadingPoint> Log = TyreCalculator.Sorted([
        new(D(2024, 6, 1), 118_000), new(D(2025, 3, 29), 128_150), new(D(2025, 4, 8), 129_150), new(D(2026, 11, 10), 148_200),
    ]);

    [Fact]
    public void Odometer_IsTheReadingOfTheDay_TheHighestWhenThereAreSeveral()
    {
        var sorted = TyreCalculator.Sorted([new(D(2026, 1, 5), 1000), new(D(2026, 1, 5), 1060), new(D(2026, 1, 1), 900)]);
        Assert.Equal(new OdometerOnDay(1060, false), TyreCalculator.OdometerOn(sorted, D(2026, 1, 5)));
    }

    [Fact]
    public void Odometer_BetweenReadings_IsAStraightLine_MarkedApproximate()
    {
        // 29 Mar 2025 at 128,150 and 8 Apr at 129,150: 100 km a day.
        Assert.Equal(new OdometerOnDay(128_550, true), TyreCalculator.OdometerOn(Log, D(2025, 4, 2)));
    }

    [Fact]
    public void Odometer_BeforeTheFirstReading_IsNotKnown_AfterTheLatest_IsTheLatest_Approximate()
    {
        Assert.Null(TyreCalculator.OdometerOn(Log, D(2024, 5, 31)));
        Assert.Equal(new OdometerOnDay(148_200, true), TyreCalculator.OdometerOn(Log, D(2026, 12, 1)));
        Assert.Null(TyreCalculator.OdometerOn([], D(2026, 1, 1)));
    }

    [Fact]
    public void Distance_OfAStretchStillOnTheCar_RunsToTheLatestReading_Exactly()
    {
        Assert.Equal(new StretchDistance(20_050, false, StretchMeasure.Measured), TyreCalculator.Distance(Log, D(2025, 3, 29), null));
    }

    [Fact]
    public void Distance_UsesTheSameValueForTheBoundaryDay_OnBothSides()
    {
        var off = TyreCalculator.Distance(Log, D(2025, 3, 29), D(2025, 4, 2));
        var on = TyreCalculator.Distance(Log, D(2025, 4, 2), D(2025, 4, 8));
        Assert.Equal(1000, off.Distance + on.Distance); // 128,150 to 129,150 split at the worked-out boundary, nothing lost or counted twice
        Assert.True(off.Approximate && on.Approximate);
    }

    [Fact]
    public void Distance_BeforeTheFuelLog_CountsFromItsFirstReading_OrIsNotMeasured()
    {
        Assert.Equal(new StretchDistance(10_150, false, StretchMeasure.FromFirstReading), TyreCalculator.Distance(Log, D(2024, 4, 2), D(2025, 3, 29)));
        Assert.Equal(new StretchDistance(0, false, StretchMeasure.NotMeasured), TyreCalculator.Distance(Log, D(2023, 3, 25), D(2023, 11, 12)));
        Assert.Equal(StretchMeasure.NotMeasured, TyreCalculator.Distance(Log, D(2024, 4, 2), D(2024, 6, 1)).Measure); // the log starts on its last day
    }

    [Fact]
    public void Distance_WithoutAReadingAfterItWentOn_IsNotMeasured()
    {
        Assert.Equal(StretchMeasure.NotMeasured, TyreCalculator.Distance(Log, D(2026, 11, 20), null).Measure);
        Assert.Equal(StretchMeasure.NotMeasured, TyreCalculator.Distance([], D(2026, 1, 1), null).Measure);
    }

    [Fact]
    public void Distance_NeverGoesBelowZero_WhenReadingsFall()
    {
        var falling = TyreCalculator.Sorted([new(D(2026, 1, 1), 2000), new(D(2026, 2, 1), 1500)]); // a reading a photo filled in is not checked
        Assert.Equal(0, TyreCalculator.Distance(falling, D(2026, 1, 1), D(2026, 2, 1)).Distance);
    }

    [Fact]
    public void SetFigures_AddTheRoughFigure_AndSplitThePairs()
    {
        var owner = Guid.NewGuid();
        var car = Guid.NewGuid();
        var set = TyreSet.Create(owner, owner, car, "Michelin Primacy 4", TyreSeason.Summer, null, D(2023, 3, 25), 5, 2023, 11_900, D(2026, 3, 28));
        TyreStretch[] stretches = [
            TyreStretch.Create(owner, owner, car, set.Id, D(2025, 3, 29), D(2025, 4, 8), null),
            TyreStretch.Create(owner, owner, car, set.Id, D(2025, 4, 8), D(2026, 11, 10), true),
        ];

        var figures = TyreCalculator.SetFigures(Log, set, stretches);

        Assert.Equal(11_900 + 1000 + 19_050, figures.Distance);
        Assert.Equal((19_050L, 0L, 1000L), (figures.PairOneFront, figures.PairOneRear, figures.PairsUnknown));
        Assert.True(figures.Approximate); // the rough figure
    }

    [Fact]
    public void SetFigures_SuggestANewRoughFigure_WhenStretchesMeasureTheTimeItStoodFor()
    {
        var owner = Guid.NewGuid();
        var car = Guid.NewGuid();
        // Added on 10 Nov 2026 with 32,900 km, standing for everything before; then a past swap of 29 Mar 2025 is entered.
        var set = TyreSet.Create(owner, owner, car, "Michelin Primacy 4", TyreSeason.Summer, null, D(2023, 3, 25), null, null, 32_900, D(2026, 11, 10));
        var stretch = TyreStretch.Create(owner, owner, car, set.Id, D(2025, 3, 29), D(2026, 11, 10), null);

        var suggestion = TyreCalculator.SetFigures(Log, set, [stretch]).Suggestion;

        Assert.NotNull(suggestion);
        Assert.Equal((20_050L, 12_900L, D(2025, 3, 29)), (suggestion.MeasuredBefore, suggestion.Distance, suggestion.Until));
        Assert.Null(TyreCalculator.SetFigures(Log, set, []).Suggestion);
    }
}
