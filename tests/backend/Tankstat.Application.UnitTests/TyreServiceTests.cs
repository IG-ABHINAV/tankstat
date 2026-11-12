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
        await s.Tyres.AddSetAsync(new NewTyreSet(s.Car.Id, Summer(), Fitted: true, D(2026, 3, 28), null, true), default, summer);
        var overview = await s.Tyres.AddSetAsync(new NewTyreSet(s.Car.Id, Winter(), Fitted: false, D(2026, 3, 28), null, null), default, winter);
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
        var installation = Assert.Single(fitted.Installations);
        Assert.Equal((D(2026, 3, 28), (DateOnly?)null, (bool?)true), (installation.Installation.From, installation.Installation.Until, installation.Installation.PairOneFront));
        // 28 Mar lies between 140,000 (1 Mar) and 147,000 (1 Sep), 27 of 184 days in: about 141,027; it runs to 147,000.
        Assert.Equal(32_900 + 147_000 - 141_027, fitted.Figures.Distance);
        Assert.Equal(D(2026, 3, 28), fitted.Set.RoughUntil);
    }

    [Fact]
    public async Task ASwap_WithAReading_KeepsItOnItsLine_AsOneOfTheCarsReadings()
    {
        var s = await Setup();
        var (_, summer, winter) = await TwoSets(s);

        var overview = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), true, false, 148_200, "At the garage"), default, Guid.NewGuid());

        Assert.Equal([(D(2026, 9, 20), TyreEntryKind.Swap, (Guid?)winter, (long?)148_200, "At the garage"), (D(2026, 3, 28), TyreEntryKind.Swap, summer, null, null)],
            overview.Entries.Select(e => (e.Day, e.Kind, e.SetId, e.Odometer, e.Note)));
        Assert.Equal(Set(overview, winter).Installations.Single().Installation.Id, overview.Entries[0].Id); // the swap's line has its installation's id
        Assert.False(Set(overview, summer).OnTheCar);
        Assert.Equal(D(2026, 9, 20), Set(overview, summer).Installations.Single().Installation.Until);
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
        await s.Tyres.AddSetAsync(new NewTyreSet(s.Car.Id, Winter(0) with { Name = "Nokian" }, false, D(2026, 9, 1), null, null), default, third);
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), null, false, null, null), default);

        var refused = await Assert.ThrowsAsync<DomainException>(() => s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, third, D(2026, 9, 20), null, false, null, null), default));
        Assert.Equal(("tyres.swapExists", "Continental WinterContact"), (refused.Key, refused.Args["set"]));

        var overview = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, third, D(2026, 9, 20), null, true, null, null), default);
        Assert.True(Set(overview, third).OnTheCar);
        Assert.Empty(Set(overview, winter).Installations); // its installation is in the trash
        Assert.Single(await s.Tyres.ListTrashAsync(0, 50, default));
        Assert.Equal(D(2026, 9, 20), Set(overview, summer).Installations.Single().Installation.Until);
    }

    [Fact]
    public async Task ASwapSentAgain_WithTheSameInstallationId_ChangesNothing()
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
        Assert.Equal((D(2025, 11, 15), (DateOnly?)D(2026, 3, 1)), (w.Installations.Single().Installation.From, w.Installations.Single().Installation.Until));
        var sm = Set(overview, summer);
        // The 1 Mar swap runs until the installation from 28 Mar; they stay apart, since only the later one has its pairs recorded.
        Assert.Equal([(D(2026, 3, 28), (DateOnly?)null), (D(2026, 3, 1), D(2026, 3, 28))], sm.Installations.Select(x => (x.Installation.From, x.Installation.Until)));
        Assert.NotNull(sm.Figures.Suggestion); // its history reaches back before the day the rough figure stood for
        Assert.Equal(32_900, sm.Set.RoughDistance);

        var twice = await Assert.ThrowsAsync<DomainException>(() =>
            s.Tyres.AddPastSwapsAsync(s.Car.Id, [new(D(2025, 4, 1), summer, null), new(D(2025, 4, 1), winter, null)], false, default));
        Assert.Equal("tyres.pastSwapsSameDay", twice.Key);
    }

    [Fact]
    public async Task TrashingAnInstallation_TakesItsLineAndItsReadingAlong_AndARestoreBringsThemBack()
    {
        var s = await Setup();
        var (_, _, winter) = await TwoSets(s);
        var swapped = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), null, false, 148_200, null), default);
        var installation = Set(swapped, winter).Installations.Single().Installation;

        var trashed = await s.Tyres.TrashInstallationAsync(installation.Id, default, installation.Version);
        Assert.DoesNotContain(trashed.Entries, e => e.Id == installation.Id);
        Assert.Equal(147_000, (await s.W.Odometer.LatestAsync(s.Car.Id, default))!.Value); // its reading is in the trash with it
        var row = Assert.Single(await s.Tyres.ListTrashAsync(0, 50, default));
        Assert.Equal((TyreTrashKind.Entry, installation.Id, (TyreEntryKind?)TyreEntryKind.Swap), (row.Kind, row.Id, row.EntryKind));

        var restored = await s.Tyres.RestoreEntryAsync(installation.Id, default, row.Version);
        Assert.Equal(148_200, restored.Entries.Single(e => e.Id == installation.Id).Odometer);
        Assert.Single(Set(restored, winter).Installations);
        Assert.Equal(148_200, (await s.W.Odometer.LatestAsync(s.Car.Id, default))!.Value);
    }

    [Fact]
    public async Task TrashingASwapsLine_TakesItsInstallationAlong_AndARestoreThatNoLongerFitsIsRefused()
    {
        var s = await Setup();
        var (_, summer, winter) = await TwoSets(s);
        var swapped = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), null, false, null, null), default);
        var line = swapped.Entries.Single(e => e.Day == D(2026, 9, 20));

        var trashed = await s.Tyres.TrashEntryAsync(line.Id, default, line.Version);
        Assert.Empty(Set(trashed, winter).Installations);
        Assert.Equal("tyres.entryNotFound", (await Assert.ThrowsAsync<NotFoundException>(() => s.Tyres.TrashEntryAsync(line.Id, default))).Key);

        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, summer, D(2026, 9, 25), null, false, null, null), default); // fills the time it left
        Assert.Equal("tyres.restoreConflict", (await Assert.ThrowsAsync<DomainException>(() => s.Tyres.RestoreEntryAsync(line.Id, default))).Key);
        Assert.NotNull(await s.Tyres.EntryInTrashAsync(line.Id, default)); // nothing came back
    }

    [Fact]
    public async Task ARestore_ThatNoLongerFits_IsRefusedWithTheReason()
    {
        var s = await Setup();
        var (_, summer, winter) = await TwoSets(s);
        var overview = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), null, false, null, null), default);
        var winterInstallation = Set(overview, winter).Installations.Single().Installation;
        await s.Tyres.TrashInstallationAsync(winterInstallation.Id, default);
        // The summer set's installation now ends on 20 Sep with nothing after it; it is put on again from 25 Sep.
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, summer, D(2026, 9, 25), null, false, null, null), default);

        var refused = await Assert.ThrowsAsync<DomainException>(() => s.Tyres.RestoreInstallationAsync(winterInstallation.Id, default));
        Assert.Equal("tyres.restoreConflict", refused.Key);
    }

    [Fact]
    public async Task TrashingASet_HidesItsInstallations_AndARestoreLeavesOneTrashedOnItsOwnInTheTrash()
    {
        var s = await Setup();
        var (_, summer, winter) = await TwoSets(s);
        await s.Tyres.AddPastSwapsAsync(s.Car.Id, [new(D(2025, 11, 15), winter, null), new(D(2026, 3, 1), summer, null)], false, default);
        var stored = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), null, false, null, null), default);
        var alone = Set(stored, winter).Installations.Single(x => x.Installation.From == D(2025, 11, 15)).Installation;
        await s.Tyres.TrashInstallationAsync(alone.Id, default);

        var trashed = await s.Tyres.TrashSetAsync(winter, default);
        Assert.DoesNotContain(trashed.Sets, x => x.Set.Id == winter);
        var rows = await s.Tyres.ListTrashAsync(0, 50, default);
        Assert.Equal(TyreTrashKind.Set, Assert.Single(rows).Kind); // its installations and lines come back with it; they are not listed alone
        Assert.DoesNotContain(trashed.Entries, e => e.SetId == winter);

        var restored = await s.Tyres.RestoreSetAsync(winter, default);
        Assert.Equal([D(2026, 9, 20)], Set(restored, winter).Installations.Select(x => x.Installation.From));
        Assert.Equal(TyreTrashKind.Entry, Assert.Single(await s.Tyres.ListTrashAsync(0, 50, default)).Kind);
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
        await w.TyreService.AddSetAsync(new NewTyreSet(car.Id, Summer(), false, D(2026, 9, 1), null, null), default);

        var refused = await Assert.ThrowsAsync<DomainException>(() =>
            w.VehicleService.UpdateAsync(car.Id, "Car", null, FuelType.Petrol, MeasurementUnits.Create(DistanceUnit.Miles, VolumeUnit.Liters), default));
        Assert.Equal("vehicle.unitsLocked", refused.Key);
    }

    [Fact]
    public async Task ChangingPositions_SplitsTheInstallation_AndTheSameDayAgainChangesItInPlace()
    {
        var s = await Setup();
        var (_, summer, _) = await TwoSets(s);

        var split = await s.Tyres.ChangePositionsAsync(new TyreSwapInput(s.Car.Id, summer, D(2026, 6, 1), false, false, null, null), default, Guid.NewGuid());
        Assert.Equal([(D(2026, 6, 1), (bool?)false), (D(2026, 3, 28), true)], Set(split, summer).Installations.Select(x => (x.Installation.From, x.Installation.PairOneFront)));

        var again = await s.Tyres.ChangePositionsAsync(new TyreSwapInput(s.Car.Id, summer, D(2026, 6, 1), true, false, null, null), default);
        Assert.Equal(2, Set(again, summer).Installations.Count);
        Assert.Equal(true, Set(again, summer).Installations[0].Installation.PairOneFront);
    }

    [Fact]
    public async Task AInstallation_TypedWithItsReadings_KeepsThemOnItsDays_AndReadingsOfOneChangeMustNotFall()
    {
        var s = await Setup();
        var (_, _, winter) = await TwoSets(s);

        var added = await s.Tyres.AddInstallationAsync(winter, new TyreInstallationInput(D(2025, 11, 15), D(2026, 2, 20), null, 132_000, 139_500), default, Guid.NewGuid());
        // The first day's reading is on the installation's line; nothing starts on its last day, so that reading is a note line of the set.
        Assert.Equal([(D(2026, 3, 28), TyreEntryKind.Swap, (long?)null), (D(2026, 2, 20), TyreEntryKind.Note, 139_500), (D(2025, 11, 15), TyreEntryKind.Swap, 132_000)],
            added.Entries.Select(e => (e.Day, e.Kind, e.Odometer)));
        Assert.Equal(winter, added.Entries[1].SetId);
        var installation = Set(added, winter).Installations.Single().Installation;
        Assert.Equal(7_500, Set(added, winter).Installations.Single().Distance.Distance);

        var edited = await s.Tyres.UpdateInstallationAsync(installation.Id, new TyreInstallationInput(D(2025, 11, 20), D(2026, 2, 20), true, null, null), default, installation.Version);
        Assert.Equal((D(2025, 11, 20), (bool?)true), (Set(edited, winter).Installations.Single().Installation.From, Set(edited, winter).Installations.Single().Installation.PairOneFront));
        Assert.Equal((D(2025, 11, 20), (long?)132_000), (edited.Entries.Single(e => e.Id == installation.Id).Day, edited.Entries.Single(e => e.Id == installation.Id).Odometer)); // its line and reading moved with it

        // Each reading fits the stored ones (none between them), but not each other: refused.
        var falling = await Assert.ThrowsAsync<DomainException>(() =>
            s.Tyres.AddInstallationAsync(winter, new TyreInstallationInput(D(2024, 11, 10), D(2025, 3, 30), null, 131_000, 120_000), default));
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
        var input = new NewTyreSet(s.Car.Id, Winter(0) with { Name = "Nokian" }, Fitted: true, D(2026, 3, 28), null, null);

        Assert.Equal("tyres.swapExists", (await Assert.ThrowsAsync<DomainException>(() => s.Tyres.AddSetAsync(input, default))).Key);
        var replaced = await s.Tyres.AddSetAsync(input with { Replace = true }, default);
        Assert.True(replaced.Sets.Single(x => x.Set.Name == "Nokian").OnTheCar);
    }

    [Fact]
    public async Task PastSwaps_BeforeASetFittedWithoutPairs_KeepItsSwap_AndLeaveItOnTheCar()
    {
        var s = await Setup();
        var summer = Guid.NewGuid();
        var winter = Guid.NewGuid();
        await s.Tyres.AddSetAsync(new NewTyreSet(s.Car.Id, Summer(), Fitted: true, D(2026, 3, 28), null, null), default, summer);
        await s.Tyres.AddSetAsync(new NewTyreSet(s.Car.Id, Winter(), Fitted: false, D(2026, 3, 28), null, null), default, winter);

        var overview = await s.Tyres.AddPastSwapsAsync(s.Car.Id, [new(D(2025, 4, 5), summer, null), new(D(2025, 11, 15), winter, null)], false, default);

        Assert.True(Set(overview, summer).OnTheCar);
        Assert.False(Set(overview, winter).OnTheCar);
        Assert.Equal([(D(2026, 3, 28), (DateOnly?)null), (D(2025, 4, 5), D(2025, 11, 15))], Set(overview, summer).Installations.Select(x => (x.Installation.From, x.Installation.Until)));
        Assert.Equal((D(2025, 11, 15), (DateOnly?)D(2026, 3, 28)), (Set(overview, winter).Installations.Single().Installation.From, Set(overview, winter).Installations.Single().Installation.Until));
    }

    [Fact]
    public async Task AInstallationId_UsedOnAnotherVehicle_IsRefusedAsTaken()
    {
        var s = await Setup();
        var (_, _, winter) = await TwoSets(s);
        var other = await s.W.VehicleService.AddAsync("Van", null, FuelType.Diesel, MeasurementUnits.Metric, default);
        var taken = Guid.NewGuid();
        await s.Tyres.AddSetAsync(new NewTyreSet(other.Id, Summer(0), Fitted: true, D(2026, 5, 1), null, null), default, installationId: taken);

        var refused = await Assert.ThrowsAsync<DomainException>(() => s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), null, false, null, null), default, taken));
        Assert.Equal("sync.idTaken", refused.Key);
        Assert.Equal("sync.idTaken", (await Assert.ThrowsAsync<DomainException>(() =>
            s.Tyres.AddSetAsync(new NewTyreSet(s.Car.Id, Winter(0) with { Name = "Nokian" }, true, D(2026, 9, 25), null, null, Replace: true), default, installationId: taken))).Key);
    }

    [Fact]
    public async Task TheSameSwap_SavedByAnotherRequestAtOnce_IsAnsweredAsTheRepeat()
    {
        var s = await Setup();
        var (_, _, winter) = await TwoSets(s);
        var id = Guid.NewGuid();
        // The other request passed the same id lookup a moment earlier and saved first.
        s.W.Tyres.MeanwhileBeforeSave = save => s.W.Tyres.Installations.AddRange(save.AddedInstallations);

        var overview = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), null, false, null, null), default, id);

        Assert.Contains(Set(overview, winter).Installations, x => x.Installation.Id == id);
    }

    [Fact]
    public async Task ChangingPositionsOnTheFirstDay_SentAgainFromTheQueue_IsAnsweredWithoutAnError_ButOnlineIsUnchanged()
    {
        var s = await Setup();
        var (_, summer, _) = await TwoSets(s); // on since 28 Mar, pair 1 at the front
        var id = Guid.NewGuid();
        var input = new TyreSwapInput(s.Car.Id, summer, D(2026, 3, 28), false, false, null, null);

        await s.Tyres.ChangePositionsAsync(input, default, id, repeat: true);
        var again = await s.Tyres.ChangePositionsAsync(input, default, id, repeat: true);

        Assert.Equal(false, Set(again, summer).Installations.Single().Installation.PairOneFront);
        Assert.Equal("tyres.positionsUnchanged", (await Assert.ThrowsAsync<DomainException>(() => s.Tyres.ChangePositionsAsync(input, default, Guid.NewGuid()))).Key);
    }

    [Fact]
    public async Task ALine_IsAdded_ChangedOnlyFromItsVersion_NotWrittenAgainUnchanged_TrashedAndRestored()
    {
        var s = await Setup();
        var (_, summer, _) = await TwoSets(s);
        var id = Guid.NewGuid();
        var added = await s.Tyres.AddEntryAsync(s.Car.Id, new TyreEntryInput(TyreEntryKind.PunctureRepaired, summer, D(2026, 9, 20), 148_200, "Rear left"), default, id);
        var line = added.Entries.Single(e => e.Id == id);
        Assert.Equal((TyreEntryKind.PunctureRepaired, (Guid?)summer, (long?)148_200, "Rear left"), (line.Kind, line.SetId, line.Odometer, line.Note));
        Assert.Equal(148_200, (await s.W.Odometer.LatestAsync(s.Car.Id, default))!.Value);
        Assert.Single((await s.Tyres.AddEntryAsync(s.Car.Id, new TyreEntryInput(TyreEntryKind.PunctureRepaired, summer, D(2026, 9, 20), 148_200, "Rear left"), default, id)).Entries,
            e => e.Id == id); // sent again: the same line

        var edit = new TyreEntryInput(TyreEntryKind.ValveReplaced, null, D(2026, 9, 21), 148_300, "Valve");
        await s.Tyres.UpdateEntryAsync(id, edit, default, line.Version); // another device's edit
        Assert.Equal("sync.versionMismatch", (await Assert.ThrowsAsync<DomainException>(() => s.Tyres.UpdateEntryAsync(id, edit with { Note = null }, default, line.Version))).Key);
        var same = await s.Tyres.UpdateEntryAsync(id, edit, default);
        Assert.Equal(line.Version + 1, same.Entries.Single(e => e.Id == id).Version); // unchanged: not saved again
        Assert.Equal(D(2026, 9, 21), s.W.Tyres.Entries.Single(e => e.Id == id).OdometerReading!.Date); // the reading moved with the day

        var trashed = await s.Tyres.TrashEntryAsync(id, default);
        Assert.DoesNotContain(trashed.Entries, e => e.Id == id);
        Assert.Equal(147_000, (await s.W.Odometer.LatestAsync(s.Car.Id, default))!.Value);
        var restored = await s.Tyres.RestoreEntryAsync(id, default);
        Assert.Equal(148_300, restored.Entries.Single(e => e.Id == id).Odometer);
    }

    [Fact]
    public async Task ALine_IsNeverASwap_NorOfAnotherVehiclesSet_AndItsIdIsItsOwn()
    {
        var s = await Setup();
        var (overview, summer, _) = await TwoSets(s);
        var other = await s.W.VehicleService.AddAsync("Other", null, FuelType.Petrol, MeasurementUnits.Metric, default);

        Assert.Equal("tyres.entryKindNotAllowed", (await Assert.ThrowsAsync<DomainException>(() =>
            s.Tyres.AddEntryAsync(s.Car.Id, new TyreEntryInput(TyreEntryKind.Swap, summer, D(2026, 9, 20), null, null), default))).Key);
        Assert.Equal("tyres.setNotFound", (await Assert.ThrowsAsync<NotFoundException>(() =>
            s.Tyres.AddEntryAsync(other.Id, new TyreEntryInput(TyreEntryKind.Other, summer, D(2026, 9, 20), null, null), default))).Key);
        var swapLine = overview.Entries.Single();
        Assert.Equal("sync.idTaken", (await Assert.ThrowsAsync<DomainException>(() =>
            s.Tyres.AddEntryAsync(s.Car.Id, new TyreEntryInput(TyreEntryKind.Other, null, D(2026, 9, 20), null, null), default, swapLine.Id))).Key);
        Assert.Equal("refueling.dateInFuture", (await Assert.ThrowsAsync<DomainException>(() =>
            s.Tyres.AddEntryAsync(s.Car.Id, new TyreEntryInput(TyreEntryKind.Other, null, D(2026, 10, 5), null, null), default))).Key);
    }

    [Fact]
    public async Task EveryChangeThatStartsAnInstallation_SavesOneLine_AndTheOnesThatStartNoneSaveNone()
    {
        var s = await Setup();
        var (overview, summer, winter) = await TwoSets(s); // a fitted new set: its swap's line
        Assert.Equal([(D(2026, 3, 28), TyreEntryKind.Swap)], overview.Entries.Select(e => (e.Day, e.Kind)));
        int Lines(TyreOverview o) => o.Entries.Count;

        var swapped = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), true, false, null, null), default, Guid.NewGuid());
        Assert.Equal(2, Lines(swapped));
        var pairs = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), false, false, null, "Pairs"), default, Guid.NewGuid()); // its own first day: pairs only
        Assert.Equal((2, "Pairs"), (Lines(pairs), pairs.Entries[0].Note)); // what was typed went onto the day's line
        var inPlace = await s.Tyres.ChangePositionsAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), true, false, null, null), default, Guid.NewGuid());
        Assert.Equal(2, Lines(inPlace));
        var split = await s.Tyres.ChangePositionsAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 25), false, false, null, null), default, Guid.NewGuid());
        Assert.Equal((3, TyreEntryKind.PositionsChanged), (Lines(split), split.Entries[0].Kind));
        var past = await s.Tyres.AddPastSwapsAsync(s.Car.Id, [new(D(2025, 4, 1), summer, null, Guid.NewGuid()), new(D(2025, 11, 15), winter, null, Guid.NewGuid())], false, default);
        Assert.Equal(5, Lines(past));
        var typed = await s.Tyres.AddInstallationAsync(winter, new TyreInstallationInput(D(2024, 11, 10), D(2025, 3, 30), null, null, null), default, Guid.NewGuid());
        Assert.Equal(6, Lines(typed));
        Assert.Equal(typed.Sets.SelectMany(x => x.Installations).Select(i => i.Installation.Id).Order(), typed.Entries.Select(e => e.Id).Order()); // one per installation, by its id
    }

    [Fact]
    public async Task ReplacingTheSetBetweenTwoOfAnother_JoinsThem_AndTheJoinedSwapsReadingStaysOnItsDay()
    {
        var s = await Setup();
        var (_, summer, winter) = await TwoSets(s);
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 6, 1), true, false, null, null), default, Guid.NewGuid());
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, summer, D(2026, 8, 1), true, false, 146_000, "Summer again"), default, Guid.NewGuid());

        // Summer on 28 Mar, winter on 1 Jun, summer on 1 Aug: replacing the winter swap joins the summer installations.
        var joined = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, summer, D(2026, 6, 1), true, true, null, null), default, Guid.NewGuid());

        Assert.Equal([(D(2026, 3, 28), (DateOnly?)null)], Set(joined, summer).Installations.Select(i => (i.Installation.From, i.Installation.Until)));
        Assert.Equal([(D(2026, 8, 1), TyreEntryKind.Note, (Guid?)summer, (long?)146_000, "Summer again"), (D(2026, 3, 28), TyreEntryKind.Swap, summer, null, null)],
            joined.Entries.Select(e => (e.Day, e.Kind, e.SetId, e.Odometer, e.Note)));
        Assert.Equal(2, (await s.Tyres.ListTrashAsync(0, 50, default)).Count); // the winter swap and the joined summer one, each with its line
        Assert.Contains(new ReadingPoint(D(2026, 8, 1), 146_000), joined.Readings); // still one of the car's readings
    }

    [Fact]
    public async Task ASwapsLine_OnAnotherDay_MovesTheSwap_AndTheSetBeforeItComesOffThen()
    {
        var s = await Setup();
        var (_, summer, winter) = await TwoSets(s);
        var swapped = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), true, false, 148_200, null), default, Guid.NewGuid());
        var line = swapped.Entries[0];

        var moved = await s.Tyres.UpdateEntryAsync(line.Id, new TyreEntryInput(TyreEntryKind.Swap, winter, D(2026, 9, 22), 148_200, "Moved"), default, line.Version);

        Assert.Equal(D(2026, 9, 22), Set(moved, summer).Installations.Single().Installation.Until);
        Assert.Equal(D(2026, 9, 22), Set(moved, winter).Installations.Single().Installation.From);
        Assert.Equal((D(2026, 9, 22), (long?)148_200, "Moved"), (moved.Entries[0].Day, moved.Entries[0].Odometer, moved.Entries[0].Note));
        Assert.Equal("tyres.swapBeforePrevious", (await Assert.ThrowsAsync<DomainException>(() =>
            s.Tyres.UpdateEntryAsync(line.Id, new TyreEntryInput(TyreEntryKind.Swap, winter, D(2026, 3, 28), null, null), default))).Key);
        Assert.Equal("tyres.swapLineLocked", (await Assert.ThrowsAsync<DomainException>(() =>
            s.Tyres.UpdateEntryAsync(line.Id, new TyreEntryInput(TyreEntryKind.Swap, summer, D(2026, 9, 22), null, null), default))).Key);
    }

    [Fact]
    public async Task AReadingMovedWithItsInstallationsFirstDay_OrBroughtBackFromTheTrash_MustStillFitTheCarsReadings()
    {
        var s = await Setup();
        var (_, _, winter) = await TwoSets(s);
        var added = await s.Tyres.AddInstallationAsync(winter, new TyreInstallationInput(D(2025, 11, 15), D(2026, 3, 28), null, 132_000, null), default, Guid.NewGuid());
        var installation = Set(added, winter).Installations.Single().Installation;

        // 132,000 on 10 Mar would be below the 140,000 of 1 Mar.
        var moved = await Assert.ThrowsAsync<DomainException>(() =>
            s.Tyres.UpdateInstallationAsync(installation.Id, new TyreInstallationInput(D(2026, 3, 10), D(2026, 3, 28), null, null, null), default));
        Assert.Equal("odometer.belowPrevious", moved.Key);

        var id = Guid.NewGuid();
        await s.Tyres.AddEntryAsync(s.Car.Id, new TyreEntryInput(TyreEntryKind.Note, null, D(2026, 6, 1), 143_000, null), default, id);
        await s.Tyres.TrashEntryAsync(id, default);
        await s.W.ExpenseService.AddAsync(s.Car.Id, new ExpenseInput(D(2026, 5, 1), "Wash", null, 10, "EUR", 145_000, null), default); // fits while the line is away
        Assert.Equal("odometer.belowPrevious", (await Assert.ThrowsAsync<DomainException>(() => s.Tyres.RestoreEntryAsync(id, default))).Key);
    }

    [Fact]
    public async Task AJoiningSwapsReading_GoesOnANoteLineWithTheSwapsId_SoTheSwapSentAgainWritesItOnce()
    {
        var s = await Setup();
        var (_, summer, winter) = await TwoSets(s);
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 6, 1), true, false, null, null), default, Guid.NewGuid());
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, summer, D(2026, 8, 1), true, false, null, null), default, Guid.NewGuid());
        var change = Guid.NewGuid();
        var joining = new TyreSwapInput(s.Car.Id, summer, D(2026, 6, 1), true, true, 143_000, "Joined");

        var first = await s.Tyres.SwapAsync(joining, default, change, repeat: true);
        var again = await s.Tyres.SwapAsync(joining, default, change, repeat: true);

        var note = Assert.Single(again.Entries, e => e.Day == D(2026, 6, 1));
        Assert.Equal((change, TyreEntryKind.Note, (Guid?)summer, (long?)143_000, "Joined"), (note.Id, note.Kind, note.SetId, note.Odometer, note.Note));
        Assert.Equal(first.Entries.Count, again.Entries.Count);
    }

    [Fact]
    public async Task AJoiningSwapsNoteLine_GivenAnotherKindSince_StillAnswersTheSwapSentAgain()
    {
        var s = await Setup();
        var (_, summer, winter) = await TwoSets(s);
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 6, 1), true, false, null, null), default, Guid.NewGuid());
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, summer, D(2026, 8, 1), true, false, null, null), default, Guid.NewGuid());
        var change = Guid.NewGuid();
        var joining = new TyreSwapInput(s.Car.Id, summer, D(2026, 6, 1), true, true, 143_000, "Joined");
        var first = await s.Tyres.SwapAsync(joining, default, change, repeat: true);
        // The next change of the same queue: the line is a puncture repair's. The answer is lost, so the whole request comes again.
        await s.Tyres.UpdateEntryAsync(change, new TyreEntryInput(TyreEntryKind.PunctureRepaired, summer, D(2026, 6, 1), 143_000, "Joined"), default);

        var again = await s.Tyres.SwapAsync(joining, default, change, repeat: true);

        var line = Assert.Single(again.Entries, e => e.Day == D(2026, 6, 1));
        Assert.Equal((change, TyreEntryKind.PunctureRepaired, (long?)143_000), (line.Id, line.Kind, line.Odometer));
        Assert.Equal(first.Entries.Count, again.Entries.Count);
    }

    [Fact]
    public async Task ANoteOfOnlySpaces_TypedWithAReading_LeavesTheSwapLinesNote()
    {
        var s = await Setup();
        var (_, _, winter) = await TwoSets(s);
        var swap = new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), null, false, null, "Winter on");
        await s.Tyres.SwapAsync(swap, default, Guid.NewGuid());

        var again = await s.Tyres.SwapAsync(swap with { Odometer = 150_000, Note = "  " }, default, Guid.NewGuid());

        var line = Assert.Single(again.Entries, e => e.Day == D(2026, 9, 20));
        Assert.Equal(((long?)150_000, "Winter on"), (line.Odometer, line.Note));
    }

    [Fact]
    public async Task ViewAccess_CannotAddALine_AndEditAccessCannotEmptyTheTrash()
    {
        var s = await Setup();
        var (overview, _, _) = await TwoSets(s);
        var line = overview.Entries.Single();
        await s.Tyres.TrashEntryAsync(line.Id, default);
        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.View));
        s.W.Current.SignInAs(s.Bob);
        await Assert.ThrowsAsync<ForbiddenException>(() => s.Tyres.AddEntryAsync(s.Car.Id, new TyreEntryInput(TyreEntryKind.Note, null, D(2026, 9, 20), null, "x"), default));

        s.W.Grants.Items.Clear();
        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.Edit));
        Assert.Equal(0, await s.Tyres.EmptyTrashAsync(default));
        Assert.NotNull(await s.Tyres.EntryInTrashAsync(line.Id, default));
    }

    [Fact]
    public async Task ARoughFigure_GivenLater_CoversTheTimeBeforeTheFirstInstallation_AndNeverAFutureDay()
    {
        var s = await Setup();
        var summer = Guid.NewGuid();
        await s.Tyres.AddSetAsync(new NewTyreSet(s.Car.Id, Summer(0), Fitted: true, D(2026, 3, 28), null, true), default, summer);

        var set = Set(await s.Tyres.SetRoughAsync(summer, 10_000, null, default), summer);

        Assert.Equal(D(2026, 3, 28), set.Set.RoughUntil);
        Assert.Null(set.Figures.Suggestion); // what was measured since 28 Mar is not counted twice
        Assert.Equal("refueling.dateInFuture", (await Assert.ThrowsAsync<DomainException>(() => s.Tyres.SetRoughAsync(summer, 10_000, D(2099, 1, 1), default))).Key);
    }

    [Fact]
    public async Task ReplacingWithTheSetThatCameOff_SentAgain_ChangesNothing()
    {
        var s = await Setup();
        var (_, summer, winter) = await TwoSets(s);
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 20), true, false, null, null), default);
        var again = new TyreSwapInput(s.Car.Id, summer, D(2026, 9, 20), true, true, null, null);
        var id = Guid.NewGuid();

        var first = await s.Tyres.SwapAsync(again, default, id, repeat: true); // the summer installation is joined again: no installation gets the id
        var saves = s.W.Tyres.Saves;
        var second = await s.Tyres.SwapAsync(again, default, id, repeat: true); // the sync sends it again

        // Online, the same swap is the person's mistake: the set is on the car that day already.
        Assert.Equal("tyres.alreadyOnCar", (await Assert.ThrowsAsync<DomainException>(() => s.Tyres.SwapAsync(again with { Replace = false }, default, Guid.NewGuid()))).Key);

        Assert.True(Set(first, summer).OnTheCar);
        Assert.Single(Set(second, summer).Installations);
        Assert.Equal(saves + 1, s.W.Tyres.Saves); // answered without a change (the day's empty log line saves nothing new)
    }

    private static readonly Guid[] Ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

    /// <summary>The summer set, on the car since 28 Mar 2026 with pair 1 at the front, broken up today (#289).</summary>
    private static async Task<(TyreOverview Overview, Guid Summer, Guid Winter, TyreBreakUpInput BreakUp)> BrokenUp(Scene s)
    {
        var (_, summer, winter) = await TwoSets(s);
        var input = new TyreBreakUpInput(summer, D(2026, 10, 1), [
            new(Guid.NewGuid(), "S1", TyreCorner.FrontLeft), new(Guid.NewGuid(), "S2", TyreCorner.FrontRight),
            new(Guid.NewGuid(), "S3", TyreCorner.RearLeft), new(Guid.NewGuid(), "S4", TyreCorner.RearRight)]);
        return (await s.Tyres.BreakUpAsync(input, default), summer, winter, input);
    }

    private static TyreView Tyre(TyreOverview o, string label) => o.Sets.SelectMany(x => x.Tyres ?? []).Single(t => t.Tyre.Label == label);

    [Fact]
    public async Task BreakingUp_GivesEachTyreTheSetsDistance_AtItsCorner_AndTheSameBreakUpAgainChangesNothing()
    {
        var s = await Setup();
        var (overview, summer, _, input) = await BrokenUp(s);

        var set = Set(overview, summer);
        Assert.Equal(D(2026, 10, 1), set.Set.BrokenUpOn);
        Assert.Equal(["S1", "S2", "S3", "S4"], set.Tyres!.Select(t => t.Tyre.Label));
        // Each starts with the set's rough figure and its installation: the same distance as the set.
        Assert.All(set.Tyres!, t => Assert.Equal((set.Figures.Distance, true), (t.Figures.Distance, t.OnTheCar)));
        Assert.Equal((TyreCorner?)TyreCorner.RearLeft, Tyre(overview, "S3").Corner);
        Assert.Equal((set.Figures.Measured, 0L), (Tyre(overview, "S1").Figures.Front, Tyre(overview, "S1").Figures.Rear));

        var again = await s.Tyres.BreakUpAsync(input, default); // a device's retry
        Assert.Equal(4, Set(again, summer).Tyres!.Count);
        Assert.Equal("tyres.alreadyBrokenUp", (await Assert.ThrowsAsync<DomainException>(() =>
            s.Tyres.BreakUpAsync(input with { Tyres = [.. input.Tyres.Select(t => t with { Id = Guid.NewGuid() })] }, default))).Key);
    }

    [Fact]
    public async Task ASwapOntoABrokenUpSet_PlacesEachTyre_AndTheSwapOffEndsTheirPlaces()
    {
        var s = await Setup();
        var (_, summer, winter, input) = await BrokenUp(s);
        var swapOff = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 10, 1), null, false, null, null), default, Guid.NewGuid());
        Assert.All(Set(swapOff, summer).Tyres!, t => Assert.False(t.OnTheCar));
        Assert.Equal((TyreCorner?)TyreCorner.FrontLeft, Tyre(swapOff, "S1").Corner); // last time

        var s1 = input.Tyres[0].Id!.Value;
        var s3 = input.Tyres[2].Id!.Value;
        var corners = new Dictionary<Guid, TyreCorner?> { [s1] = TyreCorner.RearLeft, [s3] = TyreCorner.FrontLeft, [input.Tyres[1].Id!.Value] = TyreCorner.FrontRight };
        var back = await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, summer, D(2026, 10, 2), null, false, null, null, Corners: corners), default, Guid.NewGuid());

        Assert.Equal(((TyreCorner?)TyreCorner.RearLeft, true), (Tyre(back, "S1").Corner, Tyre(back, "S1").OnTheCar));
        Assert.Null(Tyre(back, "S4").Corner); // left out: not sure
        Assert.Equal(2, Tyre(back, "S1").Places.Count);
    }

    [Fact]
    public async Task ATyresLabel_IsUniqueAmongTheVehiclesTyresNotInTheTrash_AndATrashedTyreLeavesItsCornerEmpty()
    {
        var s = await Setup();
        var (overview, _, _, input) = await BrokenUp(s);
        var s2 = input.Tyres[1].Id!.Value;

        Assert.Equal("tyres.labelTaken", (await Assert.ThrowsAsync<DomainException>(() => s.Tyres.UpdateTyreAsync(s2, "s1", default))).Key);
        Assert.Equal("S2a", Tyre(await s.Tyres.UpdateTyreAsync(s2, " S2a ", default), "S2a").Tyre.Label);

        var trashed = await s.Tyres.TrashTyreAsync(s2, default);
        Assert.DoesNotContain(trashed.Sets.SelectMany(x => x.Tyres ?? []), t => t.Tyre.Id == s2);
        Assert.Equal(TyreTrashKind.Tyre, Assert.Single(await s.Tyres.ListTrashAsync(0, 10, default)).Kind);
        // S1 moves to S2's corner while it is in the trash: S2 can no longer come back.
        var s1Place = Tyre(trashed, "S1").Places.Single().Place;
        await s.Tyres.UpdatePlaceAsync(s1Place.Id, new TyrePlaceInput(s1Place.From, null, TyreCorner.FrontRight), default);
        Assert.Equal("tyres.tyreRestoreConflict", (await Assert.ThrowsAsync<DomainException>(() => s.Tyres.RestoreTyreAsync(s2, default))).Key);
        _ = overview;
    }

    [Fact]
    public async Task ATyresOwnTimeOnTheCar_IsAddedChangedTrashedAndRestored_InsideItsSetsInstallations()
    {
        var s = await Setup();
        var (overview, _, _, input) = await BrokenUp(s);
        var s4 = input.Tyres[3].Id!.Value;
        var place = Tyre(overview, "S4").Places.Single().Place;

        // S4 came off for a while: its time on the car ends, another starts later. The others keep theirs.
        await s.Tyres.UpdatePlaceAsync(place.Id, new TyrePlaceInput(place.From, D(2026, 6, 1), TyreCorner.RearRight), default);
        var placeId = Guid.NewGuid();
        var added = await s.Tyres.AddPlaceAsync(s4, new TyrePlaceInput(D(2026, 7, 1), null, TyreCorner.RearRight), default, placeId);
        Assert.Equal(2, Tyre(added, "S4").Places.Count);
        Assert.Single(Tyre(added, "S1").Places);
        await s.Tyres.AddPlaceAsync(s4, new TyrePlaceInput(D(2026, 7, 1), null, TyreCorner.RearRight), default, placeId); // sent again: nothing new
        Assert.Equal("tyres.placeOutsideSet", (await Assert.ThrowsAsync<DomainException>(() =>
            s.Tyres.AddPlaceAsync(s4, new TyrePlaceInput(D(2026, 1, 1), D(2026, 2, 1), null), default))).Key);

        var trashed = await s.Tyres.TrashPlaceAsync(placeId, default);
        Assert.Single(Tyre(trashed, "S4").Places);
        Assert.Equal(TyreTrashKind.Place, Assert.Single(await s.Tyres.ListTrashAsync(0, 10, default)).Kind);
        Assert.Equal(2, Tyre(await s.Tyres.RestorePlaceAsync(placeId, default), "S4").Places.Count);
    }

    [Fact]
    public async Task RetiringABrokenUpSet_RetiresItsTyres_AndUnretiringBringsThemBack()
    {
        var s = await Setup();
        var (_, summer, winter, _) = await BrokenUp(s);
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 10, 1), null, false, null, null), default, Guid.NewGuid());

        var retired = await s.Tyres.RetireAsync(summer, D(2026, 10, 1), default);
        Assert.All(Set(retired, summer).Tyres!, t => Assert.Equal(((DateOnly?)D(2026, 10, 1), (TyreRetiredWhy?)TyreRetiredWhy.SetRetired), (t.Tyre.RetiredOn, t.Tyre.RetiredWhy)));
        Assert.All(Set(await s.Tyres.UnretireAsync(summer, default), summer).Tyres!, t => Assert.False(t.Tyre.IsRetired));
    }

    [Fact]
    public async Task SomeoneWithoutEditAccess_CannotBreakUpOrChangeTyres()
    {
        var s = await Setup();
        var (_, summer, _, input) = await BrokenUp(s);
        s.W.Current.SignInAs(s.Bob);
        Assert.Equal("tyres.setNotFound", (await Assert.ThrowsAsync<NotFoundException>(() => s.Tyres.BreakUpAsync(input with { SetId = summer }, default))).Key);
        Assert.Equal("tyres.tyreNotFound", (await Assert.ThrowsAsync<NotFoundException>(() => s.Tyres.UpdateTyreAsync(input.Tyres[0].Id!.Value, "X", default))).Key);
        Assert.Equal("tyres.tyreNotFound", (await Assert.ThrowsAsync<NotFoundException>(() => s.Tyres.TrashTyreAsync(input.Tyres[0].Id!.Value, default))).Key);
    }
}
