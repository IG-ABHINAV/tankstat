using Tankstat.Domain.Tyres;

namespace Tankstat.Domain.UnitTests;

public class TyreHistoryTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Car = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly TyreSet _summer = TyreSet.Create(Owner, Owner, Car, "Michelin Primacy 4", TyreSeason.Summer, null, D(2023, 3, 1), null, null, 0, null);
    private readonly TyreSet _winter = TyreSet.Create(Owner, Owner, Car, "Continental WinterContact", TyreSeason.Winter, null, D(2022, 11, 1), null, null, 0, null);

    private TyreStretch S(TyreSet set, DateOnly from, DateOnly? until, bool? pairs = null) => TyreStretch.Create(Owner, Owner, Car, set.Id, from, until, pairs);

    private TyreHistory History(params TyreStretch[] stretches) => new(Owner, Car, [_summer, _winter], stretches);

    private static string Key(Action action) => Assert.Throws<DomainException>(action).Key;

    [Fact]
    public void Swap_EndsWhatIsOnTheCar_AndPutsTheSetOn()
    {
        var summer = S(_summer, D(2026, 3, 28), null, true);
        var history = History(summer);

        var changes = history.Swap(_winter, D(2026, 11, 10), true, replace: false, Owner, Now);

        Assert.Equal(D(2026, 11, 10), summer.Until);
        var winter = Assert.Single(changes.Created);
        Assert.Equal((D(2026, 11, 10), (DateOnly?)null, (bool?)true), (winter.From, winter.Until, winter.PairOneFront));
        Assert.Equal([summer], changes.Changed);
    }

    [Fact]
    public void Swap_InThePast_GoesIntoTheHistory_UntilTheStretchItCutWouldHaveEnded()
    {
        // Summer on since 28 Mar 2026; a winter swap of 15 Nov 2025 is entered afterwards, with nothing before it.
        var summer = S(_summer, D(2026, 3, 28), null);
        var history = History(summer);

        var winter = Assert.Single(history.Swap(_winter, D(2025, 11, 15), null, false, Owner, Now).Created);
        Assert.Equal((D(2025, 11, 15), (DateOnly?)D(2026, 3, 28)), (winter.From, winter.Until)); // until the next start

        // A summer swap of 29 Mar 2025 now cuts nothing and runs until the winter stretch.
        var earlier = Assert.Single(history.Swap(_summer, D(2025, 3, 29), null, false, Owner, Now).Created);
        Assert.Equal(D(2025, 11, 15), earlier.Until);
    }

    [Fact]
    public void ASwapIntoAGap_NeverJoinsTheSetsNextStretch_SoTheSwapThatBeganItStaysRecorded()
    {
        // Summer fitted on 28 Mar 2026 without pairs, then the papers: summer on 5 Apr 2025, winter on 15 Nov 2025 (oldest first).
        var fitted = S(_summer, D(2026, 3, 28), null);
        var history = History(fitted);

        var april = Assert.Single(history.Swap(_summer, D(2025, 4, 5), null, false, Owner, Now, pairsGiven: false).Created);
        Assert.Equal((D(2025, 4, 5), (DateOnly?)D(2026, 3, 28)), (april.From, april.Until)); // fitted keeps its own start
        Assert.Equal(D(2026, 3, 28), fitted.From);

        var winter = Assert.Single(history.Swap(_winter, D(2025, 11, 15), null, false, Owner, Now, pairsGiven: false).Created);

        Assert.Equal(
            [(_summer.Id, D(2025, 4, 5), (DateOnly?)D(2025, 11, 15)), (_winter.Id, D(2025, 11, 15), D(2026, 3, 28)), (_summer.Id, D(2026, 3, 28), null)],
            history.Live.OrderBy(s => s.From).Select(s => (s.SetId, s.From, s.Until)));
        Assert.Equal(_summer.Id, history.Covering(D(2026, 10, 1))!.SetId); // summer is still the set on the car
        Assert.Equal(D(2026, 3, 28), winter.Until);
    }

    [Fact]
    public void Swap_InsideAStretch_EndsItThere_AndRunsUntilItWouldHaveEnded()
    {
        var summer = S(_summer, D(2025, 3, 29), D(2025, 11, 15));
        var history = History(summer);

        var winter = Assert.Single(history.Swap(_winter, D(2025, 8, 1), null, false, Owner, Now).Created);

        Assert.Equal((D(2025, 8, 1), (DateOnly?)D(2025, 11, 15)), (summer.Until, winter.Until));
    }

    [Fact]
    public void SecondSwap_OnADayThatHasOne_IsRefused_UnlessReplacing()
    {
        var summer = S(_summer, D(2026, 3, 28), D(2026, 11, 10));
        var winter = S(_winter, D(2026, 11, 10), null);
        var other = TyreSet.Create(Owner, Owner, Car, "Nokian", TyreSeason.Winter, null, D(2026, 1, 1), null, null, 0, null);
        var history = new TyreHistory(Owner, Car, [_summer, _winter, other], [summer, winter]);

        var refused = Assert.Throws<DomainException>(() => history.Swap(other, D(2026, 11, 10), null, false, Owner, Now));
        Assert.Equal(("tyres.swapExists", "2026-11-10", "Continental WinterContact"), (refused.Key, refused.Args["date"], refused.Args["set"]));

        var changes = history.Swap(other, D(2026, 11, 10), null, replace: true, Owner, Now);
        Assert.Equal([winter], changes.Trashed);
        Assert.True(winter.IsDeleted);
        Assert.Equal(D(2026, 11, 10), Assert.Single(changes.Created).From);
    }

    [Fact]
    public void Replacing_WithTheSetThatCameOff_JoinsItsStretchesAgain()
    {
        var summer = S(_summer, D(2026, 3, 28), D(2026, 11, 10));
        var winter = S(_winter, D(2026, 11, 10), null);
        var history = History(summer, winter);

        var changes = history.Swap(_summer, D(2026, 11, 10), null, replace: true, Owner, Now);

        Assert.Empty(changes.Created);
        Assert.Null(summer.Until); // on the car again, one stretch
        Assert.Equal([winter], changes.Trashed);
    }

    [Fact]
    public void TheSameSet_OnTheDayItWentOn_OnlySetsItsPairs()
    {
        var winter = S(_winter, D(2026, 11, 10), null);
        var history = History(winter);

        var changes = history.Swap(_winter, D(2026, 11, 10), false, false, Owner, Now);

        Assert.Equal([winter], changes.Changed);
        Assert.False(winter.PairOneFront);
        Assert.Equal("tyres.alreadyOnCar", Key(() => history.Swap(_winter, D(2026, 11, 20), null, false, Owner, Now)));
        Assert.False(history.Swap(_winter, D(2026, 11, 20), null, false, Owner, Now, repeatable: true).Any); // a retry: nothing to do

        history.Swap(_winter, D(2026, 11, 10), null, false, Owner, Now, pairsGiven: false); // from papers: the pairs stay
        Assert.False(winter.PairOneFront);
    }

    [Fact]
    public void ChangePositions_SplitsTheStretch_OrChangesItOnItsFirstDay()
    {
        var summer = S(_summer, D(2026, 3, 28), null, true);
        var history = History(summer);

        var created = Assert.Single(history.ChangePositions(_summer, D(2026, 7, 1), false, Owner).Created);
        Assert.Equal((D(2026, 7, 1), (bool?)true, (bool?)false), (summer.Until!.Value, summer.PairOneFront, created.PairOneFront));

        history.ChangePositions(_summer, D(2026, 7, 1), true, Owner);
        Assert.Equal(true, created.PairOneFront);
        Assert.Equal("tyres.positionsUnchanged", Key(() => history.ChangePositions(_summer, D(2026, 8, 1), true, Owner)));
        Assert.Equal("tyres.notOnCar", Key(() => history.ChangePositions(_winter, D(2026, 8, 1), true, Owner)));
    }

    [Fact]
    public void ChangePositions_InPlace_SentAgain_IsItsOwnAnswer()
    {
        var summer = S(_summer, D(2026, 3, 28), null, true);
        var history = History(summer);
        history.ChangePositions(_summer, D(2026, 3, 28), false, Owner, Guid.NewGuid(), repeatable: true); // on its first day: no new stretch

        Assert.False(history.ChangePositions(_summer, D(2026, 3, 28), false, Owner, Guid.NewGuid(), repeatable: true).Any);
        Assert.Equal("tyres.positionsUnchanged", Key(() => history.ChangePositions(_summer, D(2026, 3, 28), false, Owner)));
        Assert.Equal("tyres.positionsUnchanged", Key(() => history.ChangePositions(_summer, D(2026, 7, 1), false, Owner, Guid.NewGuid(), repeatable: true)));
    }

    [Fact]
    public void AConflict_NamesTheStretchAlreadyOnTheCar_NotTheOneBeingAddedOrRestored()
    {
        var summer = S(_summer, D(2026, 3, 28), null);
        var history = History(summer);

        var refused = Assert.Throws<DomainException>(() => history.Add(_winter, D(2026, 9, 20), D(2026, 10, 1), null, Owner));

        Assert.Equal(("tyres.twoSetsAtOnce", "Michelin Primacy 4", "2026-03-28"), (refused.Key, refused.Args["set"], refused.Args["from"]));
        var winter = S(_winter, D(2026, 9, 20), null);
        var restoring = Assert.Throws<DomainException>(() => History(summer, winter).CheckRestored([winter]));
        Assert.Equal(("tyres.restoreConflict", "Michelin Primacy 4"), (restoring.Key, restoring.Args["set"]));
    }

    [Fact]
    public void Check_RefusesOverlaps_AndRetiredSetsOnTheCar()
    {
        Assert.Equal("tyres.twoSetsAtOnce", Key(() => History(S(_summer, D(2026, 3, 1), D(2026, 5, 1)), S(_winter, D(2026, 4, 1), null)).Check()));
        Assert.Equal("tyres.setOnTwice", Key(() => History(S(_summer, D(2026, 3, 1), null), S(_summer, D(2026, 4, 1), D(2026, 5, 1))).Check()));
        Assert.Equal("tyres.restoreConflict", Key(() => History(S(_summer, D(2026, 3, 1), null), S(_winter, D(2026, 4, 1), D(2026, 5, 1))).Check(restoring: true)));
        History(S(_summer, D(2023, 2, 1), D(2023, 3, 5))).Check(); // "in use since" is month-level: a stretch may start before it
        History(S(_summer, D(2026, 3, 1), D(2026, 5, 1)), S(_winter, D(2026, 5, 1), null)).Check(); // touching days are fine

        _summer.Retire(D(2026, 6, 1));
        Assert.Equal("tyres.setRetired", Key(() => History(S(_summer, D(2026, 3, 1), null)).Check()));
        Assert.Equal("tyres.setRetired", Key(() => History().Swap(_summer, D(2026, 7, 1), null, false, Owner, Now)));
    }

    [Fact]
    public void AStretch_LastsAtLeastADay()
    {
        Assert.Equal("tyres.untilBeforeFrom", Key(() => S(_summer, D(2026, 3, 1), D(2026, 3, 1))));
    }

    [Fact]
    public void TrashedSets_AndTheirStretches_AreLeftOut()
    {
        _winter.MarkDeleted(Now);
        var history = History(S(_summer, D(2026, 3, 1), null), S(_winter, D(2026, 4, 1), null));

        history.Check(); // the trashed winter set's stretch does not count
        Assert.Single(history.Live);
    }
}
