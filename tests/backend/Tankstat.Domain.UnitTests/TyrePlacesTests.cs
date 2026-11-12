using Tankstat.Domain.Tyres;

namespace Tankstat.Domain.UnitTests;

/// <summary>Single tyres (#289): the break-up, the places that follow the history, and the six checks.</summary>
public class TyrePlacesTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Car = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2027, 1, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly TyreSet _summer = TyreSet.Create(Owner, Owner, Car, "Michelin Primacy 4", TyreSeason.Summer, null, D(2023, 3, 1), null, null, 0, null);
    private readonly TyreSet _winter = TyreSet.Create(Owner, Owner, Car, "Continental WinterContact", TyreSeason.Winter, null, D(2022, 11, 1), 38, 2022, 12_000, D(2025, 11, 15));

    private TyreInstallation S(TyreSet set, DateOnly from, DateOnly? until, bool? pairs = null) => TyreInstallation.Create(Owner, Owner, Car, set.Id, from, until, pairs);

    private static string Key(Action action) => Assert.Throws<DomainException>(action).Key;

    private static readonly NewTyre[] Labelled =
    [
        new(null, "W1", TyreCorner.FrontLeft), new(null, "W2", TyreCorner.FrontRight), new(null, "W3", TyreCorner.RearLeft), new(null, "W4", TyreCorner.RearRight),
    ];

    private (TyreHistory History, TyrePlaces Places) Book(params TyreInstallation[] installations)
    {
        var history = new TyreHistory(Owner, Car, [_summer, _winter], installations);
        return (history, new TyrePlaces(Owner, Car, history, [_summer, _winter], [], []));
    }

    /// <summary>Winter: 15 Nov 2025 to 28 Mar 2026 with pair 1 at the front, then again from 10 Nov 2026 with pair 1 at the rear, on the car now.</summary>
    private (TyreHistory History, TyrePlaces Places, TyreInstallation Last, TyreInstallation Now, TyreInstallation Summer) BrokenUp()
    {
        var first = S(_winter, D(2025, 11, 15), D(2026, 3, 28), true);
        var summer = S(_summer, D(2026, 3, 28), D(2026, 11, 10));
        var now = S(_winter, D(2026, 11, 10), null, false);
        var (history, places) = Book(first, summer, now);
        places.BreakUp(_winter, Labelled, D(2027, 1, 22), Owner);
        return (history, places, first, now, summer);
    }

    private static Tyre T(TyrePlaces places, string label) => places.Tyres.Single(t => t.Label == label);

    private static TyrePlace P(TyrePlaces places, string label, TyreInstallation installation) =>
        places.LivePlaces.Single(p => p.TyreId == T(places, label).Id && p.InstallationId == installation.Id);

    [Fact]
    public void BreakUp_GivesEachTyreTheSetsHistory_ItsCornerNow_AndFrontOrRearWhereThePairsWereRecorded()
    {
        var (_, places, last, now, _) = BrokenUp();

        Assert.Equal(D(2027, 1, 22), _winter.BrokenUpOn);
        Assert.Equal(["W1", "W2", "W3", "W4"], places.Tyres.Select(t => t.Label));
        Assert.All(places.Tyres, t => Assert.Equal((12_000L, (DateOnly?)D(2025, 11, 15), 38, 2022), (t.RoughDistance, t.RoughUntil, t.MadeWeek!.Value, t.MadeYear!.Value)));
        // Now pair 1 is at the rear, so W1 at the front left is pair 2: at the rear last winter, when pair 1 was at the front.
        Assert.Equal((TyreCorner?)TyreCorner.FrontLeft, P(places, "W1", now).Corner);
        Assert.Equal((TyreCorner?)TyreCorner.Rear, P(places, "W1", last).Corner);
        Assert.Equal((TyreCorner?)TyreCorner.Front, P(places, "W3", last).Corner);
        Assert.Equal((D(2025, 11, 15), (DateOnly?)D(2026, 3, 28)), (P(places, "W1", last).From, P(places, "W1", last).Until));
        // The place's id is worked out from the tyre's and the installation's, so the device names the same row.
        Assert.Equal(TyreIds.PlaceOf(T(places, "W2").Id, now.Id), P(places, "W2", now).Id);
    }

    [Fact]
    public void BreakUp_NotSure_GivesNoCornerAnywhere_AndWithoutRecordedPairsTheOthersHaveNone()
    {
        var first = S(_winter, D(2025, 11, 15), D(2026, 3, 28)); // no pairs recorded
        var now = S(_winter, D(2026, 11, 10), null, true);
        var (_, places) = Book(first, now);
        places.BreakUp(_winter, [new(null, "W1", null), Labelled[1], Labelled[2], Labelled[3]], D(2027, 1, 22), Owner);

        Assert.Null(P(places, "W1", now).Corner);
        Assert.Null(P(places, "W1", first).Corner);
        Assert.Null(P(places, "W2", first).Corner);
    }

    [Fact]
    public void BreakUp_IsRefused_WithItsReason()
    {
        var (_, places) = Book(S(_winter, D(2026, 11, 10), null));
        Assert.Equal("tyres.fourTyres", Key(() => places.BreakUp(_winter, Labelled[..3], D(2027, 1, 22), Owner)));
        Assert.Equal("tyres.labelRequired", Key(() => places.BreakUp(_winter, [new(null, " ", null), .. Labelled[1..]], D(2027, 1, 22), Owner)));
        Assert.Equal("tyres.labelTooLong", Key(() => places.BreakUp(_winter, [new(null, "WINTER1", null), .. Labelled[1..]], D(2027, 1, 22), Owner)));
        Assert.Equal("tyres.labelTwice", Key(() => places.BreakUp(_winter, [new(null, "w2", null), .. Labelled[1..]], D(2027, 1, 22), Owner)));
        Assert.Equal("tyres.cornerTwice", Key(() => places.BreakUp(_winter, [new(null, "W1", TyreCorner.FrontRight), .. Labelled[1..]], D(2027, 1, 22), Owner)));
        places.BreakUp(_winter, Labelled, D(2027, 1, 22), Owner);
        Assert.Equal("tyres.alreadyBrokenUp", Key(() => places.BreakUp(_winter, Labelled, D(2027, 1, 23), Owner)));
        // Labels are the vehicle's: another set cannot take W1, in any case.
        Assert.Equal("tyres.labelTaken", Key(() => places.BreakUp(_summer, [new(null, "w1", null), new(null, "S2", null), new(null, "S3", null), new(null, "S4", null)], D(2027, 1, 22), Owner)));
    }

    [Fact]
    public void Swap_OntoABrokenUpSet_PlacesEachTyreAtTheCornerGiven()
    {
        var (history, places, _, now, _) = BrokenUp();
        var before = TyrePlaces.Ends(history);
        places.Follow(history.Swap(_summer, D(2027, 3, 27), null, false, Owner, Now), before, Owner); // the winter installation comes off: its places with it
        Assert.All(places.LivePlaces.Where(p => p.InstallationId == now.Id), p => Assert.Equal((DateOnly?)D(2027, 3, 27), p.Until));

        var ends = TyrePlaces.Ends(history);
        var corners = places.CornersFor(_winter.Id, D(2027, 11, 6), new Dictionary<Guid, TyreCorner?>
        {
            [T(places, "W1").Id] = TyreCorner.RearLeft, [T(places, "W3").Id] = TyreCorner.FrontLeft, [T(places, "W2").Id] = TyreCorner.FrontRight,
        });
        var swap = history.Swap(_winter, D(2027, 11, 6), null, false, Owner, Now, sameCorners: s => places.SameCorners(s, D(2027, 11, 6), corners));
        var made = places.Follow(swap, ends, Owner, corners);

        var winter = Assert.Single(swap.Created);
        Assert.Equal(4, made.Created.Count);
        Assert.Equal((TyreCorner?)TyreCorner.RearLeft, P(places, "W1", winter).Corner);
        Assert.Null(P(places, "W4", winter).Corner); // left out: not sure
        Assert.Equal((D(2027, 11, 6), (DateOnly?)null), (P(places, "W1", winter).From, P(places, "W1", winter).Until));
    }

    [Fact]
    public void Swap_JoinsTheSetsInstallation_OnlyWhenEveryTyreKeepsItsCorner()
    {
        // Winter off on 27 Mar for summer; a swap typed onto 27 Mar again (replacing summer's) meets winter's installation ending that day.
        var (history, places, _, now, _) = BrokenUp();
        var before = TyrePlaces.Ends(history);
        places.Follow(history.Swap(_summer, D(2027, 3, 27), null, false, Owner, Now), before, Owner);
        var same = places.CornersFor(_winter.Id, D(2027, 3, 27), null); // as before: each tyre's corner
        var ends = TyrePlaces.Ends(history);
        var join = history.Swap(_winter, D(2027, 3, 27), null, replace: true, Owner, Now, sameCorners: s => places.SameCorners(s, D(2027, 3, 27), same));
        places.Follow(join, ends, Owner, same);

        Assert.Empty(join.Created); // joined: one installation on, its places running on with it
        Assert.Null(now.Until);
        Assert.Null(P(places, "W1", now).Until);
    }

    [Fact]
    public void Swap_StaysApart_WhenATyreChangesCorner()
    {
        var (history, places, _, now, _) = BrokenUp();
        var before = TyrePlaces.Ends(history);
        places.Follow(history.Swap(_summer, D(2027, 3, 27), null, false, Owner, Now), before, Owner);
        var crossed = new Dictionary<Guid, TyreCorner?>(places.CornersFor(_winter.Id, D(2027, 3, 27), null))
        {
            [T(places, "W1").Id] = TyreCorner.RearLeft, [T(places, "W3").Id] = TyreCorner.FrontLeft,
        };
        var ends = TyrePlaces.Ends(history);
        var swap = history.Swap(_winter, D(2027, 3, 27), null, replace: true, Owner, Now, sameCorners: s => places.SameCorners(s, D(2027, 3, 27), crossed));
        places.Follow(swap, ends, Owner, crossed);

        var winter = Assert.Single(swap.Created);
        Assert.Equal((DateOnly?)D(2027, 3, 27), now.Until);
        Assert.Equal((TyreCorner?)TyreCorner.RearLeft, P(places, "W1", winter).Corner);
    }

    [Fact]
    public void ChangePositions_SplitsTheInstallation_AndOnItsFirstDayChangesTheCornersInPlace()
    {
        var (history, places, _, now, _) = BrokenUp();
        var crossed = new Dictionary<Guid, TyreCorner?>(places.CornersFor(_winter.Id, D(2027, 1, 5), null))
        {
            [T(places, "W1").Id] = TyreCorner.RearLeft, [T(places, "W3").Id] = TyreCorner.FrontLeft,
        };
        var ends = TyrePlaces.Ends(history);
        var split = history.ChangePositions(_winter, D(2027, 1, 5), null, Owner, sameCorners: s => places.SameCorners(s, D(2027, 1, 5), crossed));
        places.Follow(split, ends, Owner, crossed);
        var after = Assert.Single(split.Created);
        Assert.Equal(((DateOnly?)D(2027, 1, 5), (TyreCorner?)TyreCorner.FrontLeft), (P(places, "W1", now).Until, P(places, "W1", now).Corner));
        Assert.Equal((TyreCorner?)TyreCorner.RearLeft, P(places, "W1", after).Corner);

        // The same corners again: nothing to change.
        Assert.Equal("tyres.positionsUnchanged",
            Key(() => history.ChangePositions(_winter, D(2027, 1, 9), null, Owner, sameCorners: s => places.SameCorners(s, D(2027, 1, 9), crossed))));
        // On the installation's first day: no split, the corners change on its places.
        var swapped = new Dictionary<Guid, TyreCorner?>(crossed) { [T(places, "W1").Id] = TyreCorner.FrontLeft, [T(places, "W3").Id] = TyreCorner.RearLeft };
        Assert.False(history.ChangePositions(_winter, D(2027, 1, 5), null, Owner, sameCorners: s => places.SameCorners(s, D(2027, 1, 5), swapped)).Any);
        places.SetCorners(after, swapped, Owner);
        Assert.Equal((TyreCorner?)TyreCorner.FrontLeft, P(places, "W1", after).Corner);
    }

    [Fact]
    public void AnInstallationsEnds_TakeItsPlacesAlong_AndOneLeftOutsideIsRefused()
    {
        var (history, places, last, now, _) = BrokenUp();
        var ends = TyrePlaces.Ends(history);
        history.Edit(last, D(2025, 11, 8), D(2026, 3, 28), true, Owner);
        places.Follow(Changed(last), ends, Owner);
        Assert.Equal(D(2025, 11, 8), P(places, "W1", last).From);

        // W1 off the car for a while inside the installation (its own time on the car, changed alone): an edit that loses those days is refused.
        var w1 = P(places, "W1", last);
        places.Edit(w1, D(2025, 11, 8), D(2025, 12, 1), w1.Corner, Owner);
        places.Add(T(places, "W1"), D(2026, 2, 1), D(2026, 3, 28), TyreCorner.Rear, Owner);
        ends = TyrePlaces.Ends(history);
        history.Edit(last, D(2025, 11, 8), D(2026, 1, 15), true, Owner);
        Assert.Equal("tyres.placeOutsideSet", Key(() => places.Follow(Changed(last), ends, Owner)));
        _ = now;
    }

    [Fact]
    public void AnInstallationAdded_PlacesEachTyreAtTheCornerItHadBefore()
    {
        var (history, places, _, _, _) = BrokenUp();
        var ends = TyrePlaces.Ends(history);
        var added = history.Add(_winter, D(2024, 11, 2), D(2025, 3, 30), null, Owner);
        var created = new TyreChanges();
        created.Created.Add(added);
        places.Follow(created, ends, Owner);

        // Before it there is nothing: not recorded. A later one, after the winter of 2025, takes that winter's corners.
        Assert.Null(P(places, "W1", added).Corner);
        Assert.Equal(4, places.LivePlaces.Count(p => p.InstallationId == added.Id));
    }

    [Fact]
    public void ATyresTimeOnTheCar_IsItsOwn_AndOutsideItsSetsInstallationsIsRefused_NamingTheSetOnTheCarThen()
    {
        var (_, places, last, _, summer) = BrokenUp();
        var w2 = P(places, "W2", last);
        places.Edit(w2, D(2025, 11, 15), D(2026, 1, 10), w2.Corner, Owner); // changed alone: W1 keeps its days
        Assert.Equal((DateOnly?)D(2026, 3, 28), P(places, "W1", last).Until);

        var refused = Assert.Throws<DomainException>(() => places.Add(T(places, "W2"), D(2026, 5, 1), D(2026, 6, 1), null, Owner));
        Assert.Equal("tyres.placeWhileOtherSet", refused.Key);
        Assert.Equal(("Michelin Primacy 4", "Continental WinterContact"), (refused.Args["other"], refused.Args["set"]));
        _ = summer;
    }

    [Fact]
    public void TheSixChecks_EachRefuseWithItsReason()
    {
        // Each on a fresh book: a refused change has already changed its entities, which the service then never saves.
        static string Refused(Action<TyrePlacesTests, TyrePlaces, TyreInstallation> change)
        {
            var fresh = new TyrePlacesTests();
            var (_, places, _, now, _) = fresh.BrokenUp();
            return Key(() => change(fresh, places, now));
        }
        // On the car twice at once.
        Assert.Equal("tyres.tyreOnTwice", Refused((_, p, _) => p.Add(T(p, "W1"), D(2026, 12, 1), D(2026, 12, 20), null, Owner)));
        // Two tyres at one exact corner.
        Assert.Equal("tyres.cornerTaken", Refused((_, p, now) => p.Edit(P(p, "W1", now), now.From, null, TyreCorner.FrontRight, Owner)));
        // Three at the front or at the rear: a side not recorded counts toward it.
        Assert.Equal("tyres.tooManyFront", Refused((_, p, now) => p.Edit(P(p, "W3", now), now.From, null, TyreCorner.Front, Owner)));
        Assert.Equal("tyres.tooManyRear", Refused((_, p, now) => p.Edit(P(p, "W1", now), now.From, null, TyreCorner.Rear, Owner)));
        // A fifth tyre on the car.
        Assert.Equal("tyres.fifthTyre", Refused((t, p, now) =>
        {
            var fifth = Tyre.Create(Owner, Owner, Car, t._winter.Id, "W5", null, D(2027, 1, 1), null, null, 0, null);
            var withFifth = new TyrePlaces(Owner, Car, new TyreHistory(Owner, Car, [t._summer, t._winter], [now]), [t._summer, t._winter], [.. p.Tyres, fifth], p.LivePlaces);
            withFifth.Add(fifth, D(2027, 1, 1), null, null, Owner);
        }));
        // After the tyre was retired.
        Assert.Equal("tyres.tyreRetired", Refused((_, p, _) =>
        {
            T(p, "W4").Retire(D(2027, 1, 10), TyreRetiredWhy.Damaged, Owner);
            p.Check();
        }));
        // Outside its set's installations.
        Assert.Equal("tyres.placeWhileOtherSet", Refused((_, p, _) => p.Add(T(p, "W1"), D(2026, 5, 1), D(2026, 6, 1), null, Owner)));
        Assert.Equal("tyres.placeOutsideSet", Refused((_, p, _) => p.Add(T(p, "W1"), D(2025, 5, 1), D(2025, 6, 1), null, Owner)));
    }

    [Fact]
    public void ARestore_ThatPutsTwoTyresAtOneCorner_IsRefused_NamingTheOtherTyre()
    {
        var (_, places, _, now, _) = BrokenUp();
        var w1 = P(places, "W1", now);
        w1.MarkDeleted(Now, Owner); // its corner is empty for that time
        var w2 = P(places, "W2", now);
        places.Edit(w2, w2.From, w2.Until, TyreCorner.FrontLeft, Owner);
        w1.Restore(Owner);

        var refused = Assert.Throws<DomainException>(() => places.CheckRestored([w1]));
        Assert.Equal("tyres.tyreRestoreConflict", refused.Key);
        Assert.Equal("W2", refused.Args["label"]);
    }

    [Fact]
    public void ATyreOrAnInstallationInTheTrash_HidesItsPlaces()
    {
        var (history, places, last, _, _) = BrokenUp();
        T(places, "W1").MarkDeleted(Now, Owner);
        Assert.DoesNotContain(places.LivePlaces, p => p.TyreId == T(places, "W1").Id);
        last.MarkDeleted(Now, Owner);
        var after = new TyrePlaces(Owner, Car, new TyreHistory(Owner, Car, [_summer, _winter], history.Live.Where(s => !s.IsDeleted)), [_summer, _winter], places.Tyres, places.LivePlaces);
        Assert.DoesNotContain(after.LivePlaces, p => p.InstallationId == last.Id);
    }

    [Fact]
    public void ATyresFigures_AreItsRoughFigurePlusItsPlaces_FrontRearAndUnknown()
    {
        ReadingPoint[] readings = [new(D(2025, 11, 15), 140_000), new(D(2026, 3, 28), 145_000), new(D(2026, 11, 10), 148_000), new(D(2027, 1, 22), 150_000)];
        var (_, places, _, _, _) = BrokenUp();
        var w1 = T(places, "W1");

        var figures = TyreCalculator.TyreFigures(TyreCalculator.Sorted(readings), w1, places.LivePlaces.Where(p => p.TyreId == w1.Id));

        // Rough 12,000 + 5,000 at the rear (last winter) + 2,000 at the front left (now).
        Assert.Equal((19_000L, 2_000L, 5_000L, 0L), (figures.Distance, figures.Front, figures.Rear, figures.Unknown));
    }

    [Fact]
    public void ThePlaceIds_AreWorkedOutTheSameWayEveryTime_AndDifferPerInstallation()
    {
        var (tyre, a, b) = (Guid.Parse("11111111-2222-3333-4444-555555555555"), Guid.Parse("ffffffff-0000-ffff-0000-ffffffffffff"), Guid.NewGuid());
        Assert.Equal(Guid.Parse("eeeeeeee-2222-cccc-4444-aaaaaaaaaaaa"), TyreIds.PlaceOf(tyre, a));
        Assert.NotEqual(TyreIds.PlaceOf(tyre, a), TyreIds.PlaceOf(tyre, b));
    }

    [Fact]
    public void ATrashedTyresPlace_TheInstallationNoLongerHolds_NeverRefusesTheChange_StaysAsItWas_AndItsRestoreIsRefused()
    {
        var (_, places, _, now, _) = BrokenUp();
        var w4 = T(places, "W4");
        places.Edit(P(places, "W4", now), now.From, D(2026, 12, 1), TyreCorner.RearRight, Owner);
        var later = places.Add(w4, D(2027, 1, 5), null, TyreCorner.RearRight, Owner);
        w4.MarkDeleted(Now);

        places.EditInstallation(now, now.From, D(2027, 1, 1), now.PairOneFront, Owner); // ends before W4's later time began

        Assert.Equal((D(2027, 1, 5), (DateOnly?)null), (later.From, later.Until)); // left as it was
        Assert.Equal(D(2027, 1, 1), P(places, "W1", now).Until);
        w4.Restore();
        Assert.StartsWith("tyres.place", Key(() => places.CheckRestored([later])));
    }

    private static TyreChanges Changed(TyreInstallation installation)
    {
        var changes = new TyreChanges();
        changes.Changed.Add(installation);
        return changes;
    }
}
