using Tankstat.Application.Auth;
using Tankstat.Application.Expenses;
using Tankstat.Application.Tyres;
using Tankstat.Domain;
using Tankstat.Domain.Access;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Tyres;
using Tankstat.Domain.Users;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.UnitTests;

public class TyreServiceTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private sealed record Scene(World W, User Alice, User Bob, Vehicle Car)
    {
        public TyreService Tyres => W.TyreService;
    }

    private static async Task<Scene> Setup()
    {
        var w = new World(); // today is 1 Oct 2026
        var alice = w.AddUser("alice@x.co");
        var bob = w.AddUser("bob@x.co");
        w.Current.SignInAs(alice);
        var car = await w.VehicleService.AddAsync("Car", null, FuelType.Petrol, MeasurementUnits.Metric, default);
        await w.ExpenseService.AddAsync(car.Id, new ExpenseInput(D(2026, 3, 1), "Service", null, 100, "EUR", 140_000, null), default);
        await w.ExpenseService.AddAsync(car.Id, new ExpenseInput(D(2026, 9, 1), "Wash", null, 10, "EUR", 147_000, null), default);
        return new Scene(w, alice, bob, car);
    }

    private static TyreSetInput Summer(long rough = 32_900) => new("Michelin Primacy 4", TyreSeason.Summer, "205/55 R16", D(2023, 3, 25), 5, 2023, rough);
    private static TyreSetInput Winter(long rough = 20_200) => new("Continental WinterContact", TyreSeason.Winter, null, D(2022, 11, 19), null, null, rough);

    private static async Task<(TyreOverview, Guid Summer, Guid Winter)> TwoSets(Scene s)
    {
        var summer = Guid.NewGuid();
        var winter = Guid.NewGuid();
        await s.Tyres.AddSetAsync(new AddTyreSetInput(s.Car.Id, Summer(), Fitted: true, D(2026, 3, 28), null, true), default, summer);
        var overview = await s.Tyres.AddSetAsync(new AddTyreSetInput(s.Car.Id, Winter(), Fitted: false, D(2026, 3, 28), null, null), default, winter);
        return (overview, summer, winter);
    }

    private static TyreSetView Set(TyreOverview o, Guid id) => o.Sets.Single(s => s.Set.Id == id);

    [Fact]
    public async Task AddingSets_PutsTheFittedOneOnTheCar_WithItsRoughFigure_AndItRunsToTheLatestReading()
    {
        var s = await Setup();

        var (overview, summer, winter) = await TwoSets(s);

        var fitted = Set(overview, summer);
        Assert.True(fitted.OnTheCar);
        Assert.False(Set(overview, winter).OnTheCar);
        var stretch = Assert.Single(fitted.Stretches);
        Assert.Equal((D(2026, 3, 28), (DateOnly?)null, (bool?)true), (stretch.Stretch.From, stretch.Stretch.Until, stretch.Stretch.PairOneFront));
        // 28 Mar lies between 140,000 (1 Mar) and 147,000 (1 Sep), 27 of 184 days in: about 141,027; it runs to 147,000.
        Assert.Equal(32_900 + 147_000 - 141_027, fitted.Figures.Distance);
        Assert.Equal(D(2026, 3, 28), fitted.Set.RoughUntil);
    }

    [Fact]
    public async Task ASwap_WithAReading_KeepsItOnTheTyreLogsDay_AsOneOfTheCarsReadings()
    {
        var s = await Setup();
        var (_, summer, winter) = await TwoSets(s);

        var overview = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), true, false, 148_200, "At the garage"), default, Guid.NewGuid());

        var day = Assert.Single(overview.Days);
        Assert.Equal((D(2026, 9, 20), (long?)148_200, "At the garage"), (day.Day, day.Odometer, day.Note));
        Assert.False(Set(overview, summer).OnTheCar);
        Assert.Equal(D(2026, 9, 20), Set(overview, summer).Stretches.Single().Stretch.Until);
        Assert.Equal(148_200, (await s.W.Odometer.LatestAsync(s.Car.Id, default))!.Value);

        // The reading is checked against the others like any reading.
        var refused = await Assert.ThrowsAsync<DomainException>(() =>
            s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, summer, D(2026, 9, 25), null, false, 100, null), default));
        Assert.Equal("odometer.belowPrevious", refused.Key);
    }

    [Fact]
    public async Task ASecondSwap_OnTheSameDay_IsRefused_ThenReplaces_WhenAsked()
    {
        var s = await Setup();
        var (_, summer, winter) = await TwoSets(s);
        var third = Guid.NewGuid();
        await s.Tyres.AddSetAsync(new AddTyreSetInput(s.Car.Id, Winter(0) with { Name = "Nokian" }, false, D(2026, 9, 1), null, null), default, third);
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), null, false, null, null), default);

        var refused = await Assert.ThrowsAsync<DomainException>(() => s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, third, D(2026, 9, 20), null, false, null, null), default));
        Assert.Equal(("tyres.swapExists", "Continental WinterContact"), (refused.Key, refused.Args["set"]));

        var overview = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, third, D(2026, 9, 20), null, true, null, null), default);
        Assert.True(Set(overview, third).OnTheCar);
        Assert.Empty(Set(overview, winter).Stretches); // its stretch is in the trash
        Assert.Single(await s.Tyres.ListTrashAsync(0, 50, default));
        Assert.Equal(D(2026, 9, 20), Set(overview, summer).Stretches.Single().Stretch.Until);
    }

    [Fact]
    public async Task ASwapSentAgain_WithTheSameStretchId_ChangesNothing()
    {
        var s = await Setup();
        var (_, _, winter) = await TwoSets(s);
        var id = Guid.NewGuid();
        var input = new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), null, false, null, null);

        await s.Tyres.SwapAsync(input, default, id);
        var saves = s.W.Tyres.Saves;
        await s.Tyres.SwapAsync(input, default, id);

        Assert.Equal(saves, s.W.Tyres.Saves);
    }

    [Fact]
    public async Task PastSwaps_GoIntoTheHistory_AndTheRoughFigureIsSuggestedAnew_NeverChanged()
    {
        var s = await Setup();
        var (_, summer, winter) = await TwoSets(s);

        var overview = await s.Tyres.AddPastSwapsAsync(s.Car.Id, [new(D(2025, 11, 15), winter, null), new(D(2026, 3, 1), summer, 140_000)], replace: false, default);

        var w = Set(overview, winter);
        Assert.Equal((D(2025, 11, 15), (DateOnly?)D(2026, 3, 1)), (w.Stretches.Single().Stretch.From, w.Stretches.Single().Stretch.Until));
        var sm = Set(overview, summer);
        // The 1 Mar swap runs until the stretch from 28 Mar; they stay apart, since only the later one has its pairs recorded.
        Assert.Equal([(D(2026, 3, 28), (DateOnly?)null), (D(2026, 3, 1), D(2026, 3, 28))], sm.Stretches.Select(x => (x.Stretch.From, x.Stretch.Until)));
        Assert.NotNull(sm.Figures.Suggestion); // its history reaches back before the day the rough figure stood for
        Assert.Equal(32_900, sm.Set.RoughDistance);

        var twice = await Assert.ThrowsAsync<DomainException>(() =>
            s.Tyres.AddPastSwapsAsync(s.Car.Id, [new(D(2025, 4, 1), summer, null), new(D(2025, 4, 1), winter, null)], false, default));
        Assert.Equal("tyres.pastSwapsSameDay", twice.Key);
    }

    [Fact]
    public async Task RemovingAStretch_KeepsTheReadingOfItsDay_AndClearingTheDayDeletesIt()
    {
        var s = await Setup();
        var (_, _, winter) = await TwoSets(s);
        var swapped = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), null, false, 148_200, null), default);
        var stretch = Set(swapped, winter).Stretches.Single().Stretch;

        var trashed = await s.Tyres.TrashStretchAsync(stretch.Id, default, stretch.Version);
        Assert.Equal(148_200, Assert.Single(trashed.Days).Odometer);

        var cleared = await s.Tyres.SetLogDayAsync(s.Car.Id, D(2026, 9, 20), null, null, default);
        Assert.Empty(cleared.Days);
        Assert.Equal(147_000, (await s.W.Odometer.LatestAsync(s.Car.Id, default))!.Value);
    }

    [Fact]
    public async Task ARestore_ThatNoLongerFits_IsRefusedWithTheReason()
    {
        var s = await Setup();
        var (_, summer, winter) = await TwoSets(s);
        var overview = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), null, false, null, null), default);
        var winterStretch = Set(overview, winter).Stretches.Single().Stretch;
        await s.Tyres.TrashStretchAsync(winterStretch.Id, default);
        // The summer set's stretch now ends on 20 Sep with nothing after it; it is put on again from 25 Sep.
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, summer, D(2026, 9, 25), null, false, null, null), default);

        var refused = await Assert.ThrowsAsync<DomainException>(() => s.Tyres.RestoreStretchAsync(winterStretch.Id, default));
        Assert.Equal("tyres.restoreConflict", refused.Key);
    }

    [Fact]
    public async Task TrashingASet_HidesItsStretches_AndARestoreLeavesOneTrashedOnItsOwnInTheTrash()
    {
        var s = await Setup();
        var (_, summer, winter) = await TwoSets(s);
        await s.Tyres.AddPastSwapsAsync(s.Car.Id, [new(D(2025, 11, 15), winter, null), new(D(2026, 3, 1), summer, null)], false, default);
        var stored = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), null, false, null, null), default);
        var alone = Set(stored, winter).Stretches.Single(x => x.Stretch.From == D(2025, 11, 15)).Stretch;
        await s.Tyres.TrashStretchAsync(alone.Id, default);

        var trashed = await s.Tyres.TrashSetAsync(winter, default);
        Assert.DoesNotContain(trashed.Sets, x => x.Set.Id == winter);
        var rows = await s.Tyres.ListTrashAsync(0, 50, default);
        Assert.Equal(TyreTrashKind.Set, Assert.Single(rows).Kind); // its stretches come back with it; they are not listed alone

        var restored = await s.Tyres.RestoreSetAsync(winter, default);
        Assert.Equal([D(2026, 9, 20)], Set(restored, winter).Stretches.Select(x => x.Stretch.From));
        Assert.Equal(TyreTrashKind.Stretch, Assert.Single(await s.Tyres.ListTrashAsync(0, 50, default)).Kind);
    }

    [Fact]
    public async Task Retiring_IsOnlyForASetInStorage_AndEmptyingTheTrashPurges()
    {
        var s = await Setup();
        var (_, summer, winter) = await TwoSets(s);

        Assert.Equal("tyres.retireOnCar", (await Assert.ThrowsAsync<DomainException>(() => s.Tyres.RetireAsync(summer, D(2026, 10, 1), default))).Key);
        var retired = await s.Tyres.RetireAsync(winter, D(2026, 10, 1), default);
        Assert.True(Set(retired, winter).Set.IsRetired);
        Assert.Equal("tyres.setRetired", (await Assert.ThrowsAsync<DomainException>(() =>
            s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 10, 1), null, false, null, null), default))).Key);

        await s.Tyres.TrashSetAsync(winter, default);
        Assert.Equal(1, await s.Tyres.EmptyTrashAsync(default));
        Assert.DoesNotContain(s.W.Tyres.Sets, x => x.Id == winter);
    }

    [Fact]
    public async Task ViewAccess_SeesTheTyres_ButCannotChangeThem()
    {
        var s = await Setup();
        var (_, _, winter) = await TwoSets(s);
        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.View));
        s.W.Current.SignInAs(s.Bob);

        Assert.Equal(2, (await s.Tyres.OverviewAsync(s.Car.Id, default))!.Sets.Count);
        await Assert.ThrowsAsync<ForbiddenException>(() => s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), null, false, null, null), default));
    }

    [Fact]
    public async Task ARoughFigure_LocksTheVehiclesUnits()
    {
        var w = new World();
        w.Current.SignInAs(w.AddUser("alice@x.co"));
        var car = await w.VehicleService.AddAsync("Car", null, FuelType.Petrol, MeasurementUnits.Metric, default);
        await w.TyreService.AddSetAsync(new AddTyreSetInput(car.Id, Summer(), false, D(2026, 9, 1), null, null), default);

        var refused = await Assert.ThrowsAsync<DomainException>(() =>
            w.VehicleService.UpdateAsync(car.Id, "Car", null, FuelType.Petrol, MeasurementUnits.Create(DistanceUnit.Miles, VolumeUnit.Liters), default));
        Assert.Equal("vehicle.unitsLocked", refused.Key);
    }

    [Fact]
    public async Task ChangingPositions_SplitsTheStretch_AndTheSameDayAgainChangesItInPlace()
    {
        var s = await Setup();
        var (_, summer, _) = await TwoSets(s);

        var split = await s.Tyres.ChangePositionsAsync(new TyreSwapInput(s.Car.Id, summer, D(2026, 6, 1), false, false, null, null), default, Guid.NewGuid());
        Assert.Equal([(D(2026, 6, 1), (bool?)false), (D(2026, 3, 28), true)], Set(split, summer).Stretches.Select(x => (x.Stretch.From, x.Stretch.PairOneFront)));

        var again = await s.Tyres.ChangePositionsAsync(new TyreSwapInput(s.Car.Id, summer, D(2026, 6, 1), true, false, null, null), default);
        Assert.Equal(2, Set(again, summer).Stretches.Count);
        Assert.Equal(true, Set(again, summer).Stretches[0].Stretch.PairOneFront);
    }

    [Fact]
    public async Task AStretch_TypedWithItsReadings_KeepsThemOnItsDays_AndReadingsOfOneChangeMustNotFall()
    {
        var s = await Setup();
        var (_, _, winter) = await TwoSets(s);

        var added = await s.Tyres.AddStretchAsync(winter, new TyreStretchInput(D(2025, 11, 15), D(2026, 2, 20), null, 132_000, 139_500), default, Guid.NewGuid());
        Assert.Equal([(D(2026, 2, 20), (long?)139_500), (D(2025, 11, 15), 132_000)], added.Days.Select(d => (d.Day, d.Odometer)));
        var stretch = Set(added, winter).Stretches.Single().Stretch;
        Assert.Equal(7_500, Set(added, winter).Stretches.Single().Distance.Distance);

        var edited = await s.Tyres.UpdateStretchAsync(stretch.Id, new TyreStretchInput(D(2025, 11, 20), D(2026, 2, 20), true, null, null), default, stretch.Version);
        Assert.Equal((D(2025, 11, 20), (bool?)true), (Set(edited, winter).Stretches.Single().Stretch.From, Set(edited, winter).Stretches.Single().Stretch.PairOneFront));

        // Each reading fits the stored ones (none between them), but not each other: refused.
        var falling = await Assert.ThrowsAsync<DomainException>(() =>
            s.Tyres.AddStretchAsync(winter, new TyreStretchInput(D(2024, 11, 10), D(2025, 3, 30), null, 131_000, 120_000), default));
        Assert.Equal("odometer.belowPrevious", falling.Key);
    }

    [Fact]
    public async Task TheRoughFigure_CanBeChanged_AndItsSuggestionAccepted()
    {
        var s = await Setup();
        var (_, summer, winter) = await TwoSets(s);
        var withPast = await s.Tyres.AddPastSwapsAsync(s.Car.Id, [new(D(2025, 11, 15), winter, null), new(D(2026, 3, 1), summer, null)], false, default);
        var suggestion = Set(withPast, summer).Figures.Suggestion!;

        var accepted = await s.Tyres.SetRoughAsync(summer, suggestion.Distance, suggestion.Until, default);

        Assert.Equal((suggestion.Distance, (DateOnly?)suggestion.Until), (Set(accepted, summer).Set.RoughDistance, Set(accepted, summer).Set.RoughUntil));
        Assert.Null(Set(accepted, summer).Figures.Suggestion);
    }

    [Fact]
    public async Task ASet_IsUpdated_RetiredAndTakenBack()
    {
        var s = await Setup();
        var (_, _, winter) = await TwoSets(s);

        var updated = await s.Tyres.UpdateSetAsync(winter, Winter() with { Name = "Continental TS 870", Size = "205/55 R16" }, default);
        Assert.Equal(("Continental TS 870", "205/55 R16"), (Set(updated, winter).Set.Name, Set(updated, winter).Set.Size));

        await s.Tyres.RetireAsync(winter, D(2026, 10, 1), default);
        var back = await s.Tyres.UnretireAsync(winter, default);
        Assert.False(Set(back, winter).Set.IsRetired);
    }

    [Fact]
    public async Task AFittedSet_OnADayThatHasASwap_ReplacesIt_WhenAsked()
    {
        var s = await Setup();
        await TwoSets(s); // the summer set went on on 28 Mar
        var input = new AddTyreSetInput(s.Car.Id, Winter(0) with { Name = "Nokian" }, Fitted: true, D(2026, 3, 28), null, null);

        Assert.Equal("tyres.swapExists", (await Assert.ThrowsAsync<DomainException>(() => s.Tyres.AddSetAsync(input, default))).Key);
        var replaced = await s.Tyres.AddSetAsync(input with { Replace = true }, default);
        Assert.True(replaced.Sets.Single(x => x.Set.Name == "Nokian").OnTheCar);
    }

    [Fact]
    public async Task ReplacingWithTheSetThatCameOff_SentAgain_ChangesNothing()
    {
        var s = await Setup();
        var (_, summer, winter) = await TwoSets(s);
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), true, false, null, null), default);
        var again = new TyreSwapInput(s.Car.Id, summer, D(2026, 9, 20), true, true, null, null);
        var id = Guid.NewGuid();

        var first = await s.Tyres.SwapAsync(again, default, id); // the summer stretch is joined again: no stretch gets the id
        var saves = s.W.Tyres.Saves;
        var second = await s.Tyres.SwapAsync(again, default, id);

        Assert.True(Set(first, summer).OnTheCar);
        Assert.Single(Set(second, summer).Stretches);
        Assert.Equal(saves + 1, s.W.Tyres.Saves); // answered without a change (the day's empty log line saves nothing new)
    }
}
