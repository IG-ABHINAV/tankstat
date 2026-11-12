using Tankstat.Application.Expenses;
using Tankstat.Application.Tyres;
using Tankstat.Domain;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Tyres;
using Tankstat.Domain.Users;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.UnitTests;

/// <summary>A tyre log line's amount is an ordinary expense, with the line's day and one shared reading (#288 step 3).</summary>
public class TyreExpenseTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private sealed record Scene(World W, User Alice, Vehicle Car, Guid Summer, Guid Winter)
    {
        public TyreService Tyres => W.TyreService;
    }

    private static async Task<Scene> Setup()
    {
        var w = new World(); // today is 1 Oct 2026
        var alice = w.AddUser("alice@x.co");
        w.Current.SignInAs(alice);
        var car = await w.VehicleService.AddAsync("Car", null, FuelType.Petrol, MeasurementUnits.Metric, default);
        await w.ExpenseService.AddAsync(car.Id, new ExpenseInput(D(2026, 3, 1), "Service", null, 100, "EUR", 140_000, null), default);
        await w.ExpenseService.AddAsync(car.Id, new ExpenseInput(D(2026, 9, 1), "Wash", null, 10, "EUR", 147_000, null), default);
        var (summer, winter) = (Guid.NewGuid(), Guid.NewGuid());
        var set = (string name, TyreSeason season) => new TyreSetInput(name, season, null, D(2023, 3, 25), null, null);
        await w.TyreService.AddSetAsync(new NewTyreSet(car.Id, set("Michelin Primacy 4", TyreSeason.Summer), true, D(2026, 3, 28), null, true), default, summer, Guid.NewGuid());
        await w.TyreService.AddSetAsync(new NewTyreSet(car.Id, set("Continental WinterContact", TyreSeason.Winter), false, D(2026, 3, 28), null, null), default, winter);
        return new Scene(w, alice, car, summer, winter);
    }

    private static TyreFee Fee(decimal? amount, Guid? id = null, string category = "Tyres") => new(amount, "EUR", category, "Puncture repaired: Michelin Primacy 4", id ?? Guid.NewGuid());

    private static Expense ExpenseOf(Scene s, Guid lineId) => s.W.Expenses.Items.Single(e => e.Id == s.W.Tyres.Entries.Single(l => l.Id == lineId).ExpenseId);

    /// <summary>
    /// What every change must leave (#288): the line, its expense and the expense's cost on one day; one reading row held by both (or none by
    /// either), on that day; and that reading live exactly while a live holder has it.
    /// </summary>
    private static void PairHolds(Scene s, Guid lineId)
    {
        var line = s.W.Tyres.Entries.Single(l => l.Id == lineId);
        var expense = s.W.Expenses.Items.Single(e => e.Id == line.ExpenseId);
        if (!line.IsDeleted && !expense.IsDeleted) Assert.Equal(line.Day, expense.Date);
        Assert.Equal(expense.Date, expense.Cost!.Date);
        if (line.IsDeleted && expense.OdometerReadingId != line.OdometerReadingId) return; // edited on its own while its line is in the trash
        Assert.Equal(line.OdometerReadingId, expense.OdometerReadingId);
        if (line.OdometerReading is not { } reading) return;
        Assert.Equal(line.Day, reading.Date);
        Assert.Equal(!line.IsDeleted || !expense.IsDeleted, reading.DeletedAt is null);
    }

    private static async Task<Guid> Puncture(Scene s, long? odometer = 148_200, decimal amount = 25, Guid? expenseId = null)
    {
        var id = Guid.NewGuid();
        await s.Tyres.AddEntryAsync(s.Car.Id, new TyreEntryInput(TyreEntryKind.PunctureRepaired, s.Summer, D(2026, 9, 20), odometer, "Rear left", Fee(amount, expenseId)), default, id);
        return id;
    }

    [Fact]
    public async Task ALineWithAnAmount_MakesOneOrdinaryExpense_WithItsDayAndReading_AndTheSameAddSentAgainMakesNoOther()
    {
        var s = await Setup();
        var (line, expenseId) = (Guid.NewGuid(), Guid.NewGuid());
        var input = new TyreEntryInput(TyreEntryKind.PunctureRepaired, s.Summer, D(2026, 9, 20), 148_200, "Rear left", Fee(25, expenseId));

        await s.Tyres.AddEntryAsync(s.Car.Id, input, default, line);
        var again = await s.Tyres.AddEntryAsync(s.Car.Id, input, default, line);

        var expense = Assert.Single(s.W.Expenses.Items, e => e.Id == expenseId);
        Assert.Equal(("Puncture repaired: Michelin Primacy 4", "Tyres", 25m, "EUR", D(2026, 9, 20), (long?)148_200),
            (expense.Title, expense.Category, expense.Amount!.Value, expense.Currency, expense.Date, expense.Odometer));
        Assert.Equal(expenseId, again.Entries.Single(e => e.Id == line).ExpenseId);
        Assert.Equal("Tyres", again.ExpenseCategory); // offered for the next one
        Assert.Single(again.Readings, r => r.Value == 148_200); // one reading of the car
        PairHolds(s, line);
    }

    [Fact]
    public async Task AnAmountTypedOnALine_SavedByAnotherRequestAtOnce_IsAnsweredAsTheRepeat_WithOneExpense()
    {
        var s = await Setup();
        var (line, expenseId) = (Guid.NewGuid(), Guid.NewGuid());
        var input = new TyreEntryInput(TyreEntryKind.PunctureRepaired, s.Summer, D(2026, 9, 20), 148_200, "Rear left", null);
        await s.Tyres.AddEntryAsync(s.Car.Id, input, default, line);
        // The other request passed the same lookups a moment earlier and saved first: its expense and the line that names it are there.
        s.W.Tyres.MeanwhileBeforeSave = save =>
        {
            s.W.Expenses.Items.AddRange(save.AddedExpenses);
            s.W.Tyres.Entries.RemoveAll(e => e.Id == line);
            s.W.Tyres.Entries.AddRange(save.ChangedEntries);
        };

        var overview = await s.Tyres.UpdateEntryAsync(line, input with { Fee = Fee(25, expenseId) }, default);

        Assert.Equal(expenseId, overview.Entries.Single(e => e.Id == line).ExpenseId);
        Assert.Single(s.W.Expenses.Items, e => e.Id == expenseId);
        PairHolds(s, line);
    }

    [Fact]
    public async Task ADayOrAnOdometerChanged_OnEitherSide_ChangesBoth_AndClearedOnEitherSide_ClearsBoth()
    {
        var s = await Setup();
        var line = await Puncture(s);

        await s.Tyres.UpdateEntryAsync(line, new TyreEntryInput(TyreEntryKind.PunctureRepaired, s.Summer, D(2026, 9, 22), 148_300, "Rear left", Fee(30)), default);
        Assert.Equal((D(2026, 9, 22), (long?)148_300, 30m), (ExpenseOf(s, line).Date, ExpenseOf(s, line).Odometer, ExpenseOf(s, line).Amount!.Value));
        PairHolds(s, line);

        var expense = ExpenseOf(s, line);
        await s.W.ExpenseService.UpdateAsync(expense.Id, new ExpenseInput(D(2026, 9, 21), "Puncture", "Tyres", 30, "EUR", 148_250, null), default);
        Assert.Equal((D(2026, 9, 21), (long?)148_250), (s.W.Tyres.Entries.Single(l => l.Id == line).Day, s.W.Tyres.Entries.Single(l => l.Id == line).Odometer));
        PairHolds(s, line);

        await s.W.ExpenseService.UpdateAsync(expense.Id, new ExpenseInput(D(2026, 9, 21), "Puncture", "Tyres", 30, "EUR", null, null), default);
        Assert.Null(s.W.Tyres.Entries.Single(l => l.Id == line).OdometerReading);
        await s.Tyres.UpdateEntryAsync(line, new TyreEntryInput(TyreEntryKind.PunctureRepaired, s.Summer, D(2026, 9, 21), 148_260, null, Fee(30)), default);
        Assert.Equal(148_260, ExpenseOf(s, line).Odometer);
        await s.Tyres.UpdateEntryAsync(line, new TyreEntryInput(TyreEntryKind.PunctureRepaired, s.Summer, D(2026, 9, 21), null, null, Fee(30)), default);
        Assert.Null(ExpenseOf(s, line).OdometerReading);
        PairHolds(s, line);
    }

    [Fact]
    public async Task Unticked_TheExpenseStaysWithTheReading_Ticked_BothGo_AndRestoringTheLineBringsTheExpenseBack()
    {
        var s = await Setup();
        var kept = await Puncture(s);
        await s.Tyres.TrashEntryAsync(kept, default, withExpense: false);
        Assert.False(ExpenseOf(s, kept).IsDeleted);
        Assert.Equal(148_200, (await s.W.Odometer.LatestAsync(s.Car.Id, default))!.Value); // still one of the car's readings, through the expense
        PairHolds(s, kept);

        var both = await Puncture(s, odometer: 148_400, expenseId: Guid.NewGuid());
        await s.Tyres.TrashEntryAsync(both, default);
        Assert.True(ExpenseOf(s, both).IsDeleted);
        PairHolds(s, both);
        await s.Tyres.RestoreEntryAsync(both, default);
        Assert.False(ExpenseOf(s, both).IsDeleted);
        PairHolds(s, both);
    }

    [Fact]
    public async Task RestoringTheExpenseAlone_BringsBackOnlyIt_AndTheLineStaysInTheTrash()
    {
        var s = await Setup();
        var line = await Puncture(s);
        await s.Tyres.TrashEntryAsync(line, default);

        await s.W.ExpenseService.RestoreAsync(ExpenseOf(s, line).Id, default);

        var stored = s.W.Tyres.Entries.Single(l => l.Id == line);
        Assert.True(stored.IsDeleted);
        Assert.False(stored.ExpenseTrashedWithEntry); // restoring the line later leaves the expense as it is
        Assert.False(ExpenseOf(s, line).IsDeleted);
        PairHolds(s, line);
    }

    [Fact]
    public async Task EmptyingTheAmount_MovesTheExpenseToTheTrash_AndTypingOneAgainBringsItBack()
    {
        var s = await Setup();
        var line = await Puncture(s);

        await s.Tyres.UpdateEntryAsync(line, new TyreEntryInput(TyreEntryKind.PunctureRepaired, s.Summer, D(2026, 9, 20), 148_200, null, Fee(null)), default);
        Assert.True(ExpenseOf(s, line).IsDeleted);
        Assert.Equal(148_200, (await s.W.Odometer.LatestAsync(s.Car.Id, default))!.Value); // the line still holds the reading
        PairHolds(s, line);

        await s.Tyres.UpdateEntryAsync(line, new TyreEntryInput(TyreEntryKind.PunctureRepaired, s.Summer, D(2026, 9, 20), 148_200, null, Fee(40)), default);
        Assert.Equal((false, 40m), (ExpenseOf(s, line).IsDeleted, ExpenseOf(s, line).Amount!.Value));
        Assert.Single(s.W.Expenses.Items, e => e.Title.StartsWith("Puncture", StringComparison.Ordinal));
        PairHolds(s, line);
    }

    [Fact]
    public async Task ANoteTakesNoAmount_AndAnExpenseIdOfAnotherIsTaken()
    {
        var s = await Setup();
        Assert.Equal("tyres.noteTakesNoAmount", (await Assert.ThrowsAsync<DomainException>(() =>
            s.Tyres.AddEntryAsync(s.Car.Id, new TyreEntryInput(TyreEntryKind.Note, null, D(2026, 9, 20), null, "x", Fee(5)), default))).Key);
        var other = s.W.Expenses.Items.First().Id;
        Assert.Equal("sync.idTaken", (await Assert.ThrowsAsync<DomainException>(() =>
            s.Tyres.AddEntryAsync(s.Car.Id, new TyreEntryInput(TyreEntryKind.Other, null, D(2026, 9, 20), null, null, Fee(5, other)), default))).Key);
    }

    [Fact]
    public async Task ASwapsFee_IsOnItsLine_AReplaceTakesItAlong_AndRestoringTheSwapBringsItBack()
    {
        var s = await Setup();
        var swap = Guid.NewGuid();
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, s.Winter, D(2026, 9, 20), true, false, 148_200, null, Fee(60)), default, swap);
        Assert.Equal(60m, ExpenseOf(s, swap).Amount);
        PairHolds(s, swap);

        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, s.Summer, D(2026, 9, 20), true, true, null, null), default, Guid.NewGuid()); // replaced
        Assert.True(ExpenseOf(s, swap).IsDeleted); // the owner's choice: it goes along
        PairHolds(s, swap);
    }

    [Fact]
    public async Task ASwapAJoinMoves_TakesItsExpenseToTheNewDay_AndTheReadingStaysOnItsOldDay()
    {
        var s = await Setup();
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, s.Winter, D(2026, 6, 1), true, false, null, null), default, Guid.NewGuid());
        var later = Guid.NewGuid();
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, s.Summer, D(2026, 8, 1), true, false, null, null), default, Guid.NewGuid());
        // A third set's swap cut by the summer one coming back earlier: the winter set's swap on 6 Jun, then summer again on 1 Aug with a fee.
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, s.Winter, D(2026, 9, 10), true, false, 147_500, "Winter", Fee(70)), default, later);
        // The summer set put on on 1 Sep, while the winter one is on from 10 Sep: its next installation (10 Sep, same pairs) is joined to it.
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, s.Winter, D(2026, 8, 20), true, false, null, null), default, Guid.NewGuid());

        var line = s.W.Tyres.Entries.Single(l => l.Id == later);
        Assert.Equal(D(2026, 8, 20), line.Day);
        Assert.Equal((D(2026, 8, 20), (long?)null, false), (ExpenseOf(s, later).Date, ExpenseOf(s, later).Odometer, ExpenseOf(s, later).IsDeleted));
        Assert.Contains(s.W.Tyres.Entries, e => e is { Kind: TyreEntryKind.Note, Odometer: 147_500, Note: "Winter" } && e.Day == D(2026, 9, 10));
        PairHolds(s, later);
    }

    [Fact]
    public async Task AJoiningSwapsFee_GoesOnAnOtherLineOfItsDay()
    {
        var s = await Setup();
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, s.Winter, D(2026, 6, 1), true, false, null, null), default, Guid.NewGuid());
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, s.Summer, D(2026, 8, 1), true, false, null, null), default, Guid.NewGuid());
        var change = Guid.NewGuid();

        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, s.Summer, D(2026, 6, 1), true, true, null, null, Fee(45)), default, change);

        var line = s.W.Tyres.Entries.Single(l => l.Id == change);
        Assert.Equal((TyreEntryKind.Other, D(2026, 6, 1), 45m), (line.Kind, line.Day, ExpenseOf(s, change).Amount!.Value));
        PairHolds(s, change);
    }

    [Fact]
    public async Task ASwapAJoinTakesAway_TakesItsExpenseToTheTrash_AndItsReadingStaysLiveOnTheNoteOfItsDay()
    {
        var s = await Setup();
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, s.Winter, D(2026, 6, 1), true, false, null, null), default, Guid.NewGuid());
        var back = Guid.NewGuid();
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, s.Summer, D(2026, 8, 1), true, false, 145_000, "Back on", Fee(60)), default, back);

        // The winter set's swap replaced by the summer set: its two installations become one, and the swap of 1 Aug goes to the trash.
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, s.Summer, D(2026, 6, 1), true, true, null, null), default, Guid.NewGuid());

        Assert.True(s.W.Tyres.Entries.Single(l => l.Id == back).IsDeleted);
        Assert.True(ExpenseOf(s, back).IsDeleted);
        Assert.Null(ExpenseOf(s, back).OdometerReadingId);
        var kept = s.W.Tyres.Entries.Single(e => e is { Kind: TyreEntryKind.Note, IsDeleted: false } && e.Day == D(2026, 8, 1));
        Assert.Equal(145_000, kept.Odometer);
        Assert.Null(kept.OdometerReading!.DeletedAt); // the live note holds it: still one of the car's readings
    }

    [Fact]
    public async Task ASetsPrice_IsABoughtLineOnTheDayItWasBought()
    {
        var s = await Setup();
        var (set, bought) = (Guid.NewGuid(), Guid.NewGuid());

        var overview = await s.Tyres.AddSetAsync(new NewTyreSet(s.Car.Id, new TyreSetInput("Nokian", TyreSeason.AllSeason, null, D(2024, 5, 1), null, null, 15_000), false,
            D(2026, 10, 1), null, null, Price: Fee(480), BoughtOn: D(2024, 5, 1), BoughtEntryId: bought), default, set);

        var line = overview.Entries.Single(e => e.Id == bought);
        Assert.Equal((TyreEntryKind.Bought, (Guid?)set, D(2024, 5, 1)), (line.Kind, line.SetId, line.Day));
        Assert.Equal((480m, D(2024, 5, 1)), (ExpenseOf(s, bought).Amount!.Value, ExpenseOf(s, bought).Date));
        PairHolds(s, bought);
    }

    [Fact]
    public async Task ASetsBoughtLine_WithAnIdALineHas_IsRefusedAsTaken()
    {
        var s = await Setup();
        var line = await Puncture(s);

        var refused = await Assert.ThrowsAsync<DomainException>(() => s.Tyres.AddSetAsync(
            new NewTyreSet(s.Car.Id, new TyreSetInput("Nokian", TyreSeason.AllSeason, null, D(2024, 5, 1), null, null), false, D(2026, 10, 1), null, null,
                Price: Fee(480), BoughtOn: D(2024, 5, 1), BoughtEntryId: line), default, Guid.NewGuid()));

        Assert.Equal("sync.idTaken", refused.Key);
        Assert.DoesNotContain(s.W.Tyres.Sets, x => x.Name == "Nokian");
    }

    [Fact]
    public async Task AnAmountTypedOnTheLine_WhileItsExpenseWaitsForItsPhotos_MakesTheExpensesCost()
    {
        var s = await Setup();
        var line = await Puncture(s);
        var expense = ExpenseOf(s, line);
        // The Expenses tab left the amount to a photo still being read.
        await s.Tyres.UpdateExpenseAsync(s.W.Tyres.Entries.Single(l => l.Id == line), expense.Id,
            new ExpenseInput(D(2026, 9, 20), "Puncture", "Tyres", null, null, 148_200, null), default, readingPhotos: true);
        Assert.Null(ExpenseOf(s, line).Cost);

        await s.Tyres.UpdateEntryAsync(line, new TyreEntryInput(TyreEntryKind.PunctureRepaired, s.Summer, D(2026, 9, 20), 148_200, "Rear left", Fee(40, expense.Id)), default);

        Assert.Equal((40m, "EUR"), (ExpenseOf(s, line).Amount!.Value, ExpenseOf(s, line).Currency));
        PairHolds(s, line);
    }

    [Fact]
    public async Task TheExpenseEditedWhileItsLineIsInTheTrash_IsEditedOnItsOwn_WithAReadingOfItsOwn()
    {
        var s = await Setup();
        var line = await Puncture(s);
        await s.Tyres.TrashEntryAsync(line, default, withExpense: false);
        var expense = ExpenseOf(s, line);

        await s.W.ExpenseService.UpdateAsync(expense.Id, new ExpenseInput(D(2026, 9, 25), "Puncture", "Tyres", 25, "EUR", 148_500, null), default);

        var stored = s.W.Tyres.Entries.Single(l => l.Id == line);
        Assert.Equal((D(2026, 9, 20), (long?)148_200), (stored.Day, stored.Odometer)); // the line in the trash as it was
        Assert.Equal((D(2026, 9, 25), (long?)148_500), (ExpenseOf(s, line).Date, ExpenseOf(s, line).Odometer));
        Assert.NotEqual(stored.OdometerReadingId, ExpenseOf(s, line).OdometerReadingId);
        Assert.NotNull(stored.OdometerReading!.DeletedAt); // nothing live holds the line's any more
        PairHolds(s, line);

        // Trashed alone, it stays on its own day; restoring the line then parts the two (the owner's choice), each with its own reading.
        await s.W.ExpenseService.DeleteAsync(expense.Id, default);
        Assert.Equal(D(2026, 9, 25), s.W.Expenses.Items.Single(e => e.Id == expense.Id).Date);
        await s.W.ExpenseService.RestoreAsync(expense.Id, default);
        await s.Tyres.RestoreEntryAsync(line, default);
        var restored = s.W.Tyres.Entries.Single(l => l.Id == line);
        Assert.Equal(((Guid?)null, (long?)148_200, D(2026, 9, 20)), (restored.ExpenseId, restored.Odometer, restored.Day));
        Assert.Equal((D(2026, 9, 25), (long?)148_500), (s.W.Expenses.Items.Single(e => e.Id == expense.Id).Date, s.W.Expenses.Items.Single(e => e.Id == expense.Id).Odometer));
    }

    [Fact]
    public async Task TheExpensesAmountChangedWhileItsLineIsInTheTrash_KeepsThePair()
    {
        var s = await Setup();
        var line = await Puncture(s);
        await s.Tyres.TrashEntryAsync(line, default, withExpense: false);
        var expense = ExpenseOf(s, line);

        await s.W.ExpenseService.UpdateAsync(expense.Id, new ExpenseInput(D(2026, 9, 20), "Puncture", "Tyres", 30, "EUR", 148_200, null), default);
        await s.Tyres.RestoreEntryAsync(line, default);

        Assert.Equal(expense.Id, s.W.Tyres.Entries.Single(l => l.Id == line).ExpenseId);
        Assert.Equal(30m, ExpenseOf(s, line).Amount);
        PairHolds(s, line);
    }

    [Fact]
    public async Task AReadingAPhotoFilledIntoTheExpense_BecomesTheLines()
    {
        var s = await Setup();
        var line = await Puncture(s, odometer: null);
        var expense = ExpenseOf(s, line);
        var reading = Domain.Odometers.OdometerReading.Create(s.Car.OwnerId, s.Car.Id, D(2026, 9, 20), 148_200);
        typeof(Expense).GetProperty(nameof(Expense.OdometerReading))!.SetValue(expense, reading); // as the filler leaves it
        typeof(Expense).GetProperty(nameof(Expense.OdometerReadingId))!.SetValue(expense, reading.Id);

        await new TyreExpenseReadings(s.W.Tyres).FilledAsync(expense.Id, reading, default);

        Assert.Equal(reading.Id, s.W.Tyres.Entries.Single(l => l.Id == line).OdometerReadingId);
        PairHolds(s, line);
    }

    [Fact]
    public async Task ASetsSpent_IsTheLiveExpensesOfItsLiveLines_PerCurrency_AndRepairsCountPuncturesAndValves()
    {
        var s = await Setup();
        var add = (TyreEntryKind kind, Guid? set, decimal? amount, string currency) => s.Tyres.AddEntryAsync(s.Car.Id,
            new TyreEntryInput(kind, set, D(2026, 9, 20), null, null, amount is null ? null : new TyreFee(amount, currency, "Tyres", "t", Guid.NewGuid())), default, Guid.NewGuid());
        await add(TyreEntryKind.PunctureRepaired, s.Summer, 25, "EUR");
        await add(TyreEntryKind.ValveReplaced, s.Summer, null, "EUR");
        await add(TyreEntryKind.StorageFee, s.Summer, 15_000, "HUF");
        await add(TyreEntryKind.WheelBalancing, s.Winter, 40, "EUR");
        await add(TyreEntryKind.Other, null, 8, "EUR");
        var gone = Guid.NewGuid();
        await s.Tyres.AddEntryAsync(s.Car.Id, new TyreEntryInput(TyreEntryKind.PunctureRepaired, s.Summer, D(2026, 9, 21), null, null, Fee(99, Guid.NewGuid())), default, gone);
        await s.Tyres.TrashEntryAsync(gone, default); // a line in the trash counts for nothing
        var kept = Guid.NewGuid();
        var overview = await s.Tyres.AddEntryAsync(s.Car.Id, new TyreEntryInput(TyreEntryKind.Other, s.Winter, D(2026, 9, 22), null, null, Fee(77, Guid.NewGuid())), default, kept);
        await s.W.ExpenseService.DeleteAsync(ExpenseOf(s, kept).Id, default); // nor an expense in the trash
        overview = (await s.Tyres.OverviewAsync(s.Car.Id, default))!;

        var summer = overview.Sets.Single(x => x.Set.Id == s.Summer);
        var winter = overview.Sets.Single(x => x.Set.Id == s.Winter);
        Assert.Equal([new TyreSpent("HUF", 15_000), new TyreSpent("EUR", 25)], summer.Spent!);
        Assert.Equal([new TyreSpent("EUR", 40)], winter.Spent!);
        Assert.Equal((2, 0), (summer.Repairs, winter.Repairs));
        Assert.Equal([new TyreSpent("EUR", 8)], overview.SpentNoSet);
        Assert.Equal([new TyreSpent("HUF", 15_000), new TyreSpent("EUR", 73)], overview.SpentAll); // the rows add up
    }

    /// <summary>
    /// A flat on two tyres of the whole summer set (#289): the add for S3 and S4 breaks the set up on save and makes a line each, made
    /// together, sharing one expense of 13,000 and one reading.
    /// </summary>
    private static async Task<(Guid S3Line, Guid S4Line, Guid Expense, Guid[] Tyres)> SharedRepair(Scene s, long? odometer = 148_200)
    {
        Guid[] tyres = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        var breakUp = new TyreBreakUpInput(s.Summer, D(2026, 9, 20), [
            new(tyres[0], "S1", TyreCorner.FrontLeft), new(tyres[1], "S2", TyreCorner.FrontRight), new(tyres[2], "S3", TyreCorner.RearLeft), new(tyres[3], "S4", TyreCorner.RearRight)]);
        var (s3, s4, expense) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await s.Tyres.AddEntryAsync(s.Car.Id, new TyreEntryInput(TyreEntryKind.PunctureRepaired, s.Summer, D(2026, 9, 20), odometer, "Screws in the tread",
            new TyreFee(13_000, "HUF", "Tyres", "Puncture repaired: Michelin Primacy 4", expense), [new(tyres[2], s3), new(tyres[3], s4)], breakUp), default);
        return (s3, s4, expense, tyres);
    }

    private static TyreEntry Line(Scene s, Guid id) => s.W.Tyres.Entries.Single(l => l.Id == id);

    /// <summary>What every change must leave (#289): the live lines holding the expense and the expense on one day, one reading row between them, live while one of them is.</summary>
    private static void GroupHolds(Scene s, Guid expenseId)
    {
        var expense = s.W.Expenses.Items.Single(e => e.Id == expenseId);
        var holders = s.W.Tyres.Entries.Where(l => l.ExpenseId == expenseId && !l.IsDeleted).ToList();
        foreach (var line in holders)
        {
            if (!expense.IsDeleted) Assert.Equal(line.Day, expense.Date);
            Assert.Equal(expense.OdometerReadingId, line.OdometerReadingId);
        }
        if (holders.FirstOrDefault()?.OdometerReading is { } reading)
            Assert.Equal(holders.Count > 0 || !expense.IsDeleted, reading.DeletedAt is null);
    }

    [Fact]
    public async Task AFlatOnTwoTyresOfAWholeSet_BreaksItUp_MakesALineEach_OneExpenseAndOneReading_CountedOnceForTheSet()
    {
        var s = await Setup();
        var (s3, s4, expenseId, tyres) = await SharedRepair(s);

        var (a, b) = (Line(s, s3), Line(s, s4));
        Assert.Equal((tyres[2], tyres[3], (Guid?)s3, (Guid?)s3), (a.TyreId!.Value, b.TyreId!.Value, a.GroupId, b.GroupId)); // the group is the first line's id
        Assert.Equal((expenseId, expenseId), (a.ExpenseId!.Value, b.ExpenseId!.Value));
        Assert.Single(s.W.Expenses.Items.Where(e => e.Id == expenseId));
        GroupHolds(s, expenseId);
        var overview = (await s.Tyres.OverviewAsync(s.Car.Id, default))!;
        var summer = overview.Sets.Single(x => x.Set.Id == s.Summer);
        Assert.Equal(D(2026, 9, 20), summer.Set.BrokenUpOn);
        Assert.Equal([new TyreSpent("HUF", 13_000)], summer.Spent!); // once
        Assert.Equal(2, summer.Repairs);
        var s3View = summer.Tyres!.Single(t => t.Tyre.Id == tyres[2]);
        Assert.Equal([new TyreSpent("HUF", 6_500)], s3View.Spent!);
        Assert.Equal(1, s3View.Repairs);
    }

    [Fact]
    public async Task ANewAmountOnOneLine_ChangesTheOneExpenseForAll_AndTheExpensesNewDay_MovesEveryLiveLine()
    {
        var s = await Setup();
        var (s3, s4, expenseId, _) = await SharedRepair(s);

        await s.Tyres.UpdateEntryAsync(s3, new TyreEntryInput(TyreEntryKind.PunctureRepaired, s.Summer, D(2026, 9, 20), 148_200, "Screws in the tread",
            new TyreFee(15_000, "HUF", "Tyres", null)), default);
        Assert.Equal(15_000, s.W.Expenses.Items.Single(e => e.Id == expenseId).Amount);
        Assert.Equal((s3, s3), (Line(s, s3).GroupId!.Value, Line(s, s4).GroupId!.Value)); // nothing of its own: still one row

        await s.W.ExpenseService.UpdateAsync(expenseId, new ExpenseInput(D(2026, 9, 21), "Puncture repaired", "Tyres", 15_000, "HUF", 148_300, null), default);
        Assert.All([Line(s, s3), Line(s, s4)], l => Assert.Equal((D(2026, 9, 21), 148_300L), (l.Day, l.Odometer!.Value)));
        GroupHolds(s, expenseId);
    }

    [Fact]
    public async Task ADayOfItsOwn_PartsALineFromTheSharedExpense_TheOtherKeepsIt()
    {
        var s = await Setup();
        var (s3, s4, expenseId, _) = await SharedRepair(s);

        await s.Tyres.UpdateEntryAsync(s3, new TyreEntryInput(TyreEntryKind.PunctureRepaired, s.Summer, D(2026, 9, 22), 148_400, "Screws in the tread"), default);

        var (a, b) = (Line(s, s3), Line(s, s4));
        Assert.Equal(((Guid?)null, (Guid?)null), (a.ExpenseId, a.GroupId)); // parted: its own day and reading, no amount
        Assert.Equal((D(2026, 9, 22), 148_400L), (a.Day, a.Odometer!.Value));
        Assert.Equal(((Guid?)expenseId, D(2026, 9, 20), 148_200L), (b.ExpenseId, b.Day, b.Odometer!.Value));
        Assert.NotEqual(a.OdometerReadingId, b.OdometerReadingId);
        GroupHolds(s, expenseId);
    }

    [Fact]
    public async Task AnAmountEmptiedOnOneLineOfASharedExpense_AsksWhatItDoes_LetGoOrTrash()
    {
        var s = await Setup();
        var (s3, s4, expenseId, _) = await SharedRepair(s);
        static TyreEntryInput Emptied(Scene of) => new(TyreEntryKind.PunctureRepaired, of.Summer, D(2026, 9, 20), 148_200, "Screws in the tread", new TyreFee(null, null, null, null));
        var emptied = Emptied(s);

        Assert.Equal("tyres.sharedExpenseChoice", (await Assert.ThrowsAsync<DomainException>(() => s.Tyres.UpdateEntryAsync(s3, emptied, default))).Key);
        await s.Tyres.UpdateEntryAsync(s3, emptied with { SharedExpense = SharedExpenseChoice.LetGo }, default);
        Assert.Equal(((Guid?)null, (Guid?)expenseId), (Line(s, s3).ExpenseId, Line(s, s4).ExpenseId)); // only S3 let go
        Assert.False(s.W.Expenses.Items.Single(e => e.Id == expenseId).IsDeleted);
        GroupHolds(s, expenseId);

        var t = await Setup();
        var (t3, t4, other, _) = await SharedRepair(t);
        await t.Tyres.UpdateEntryAsync(t3, Emptied(t) with { SharedExpense = SharedExpenseChoice.Trash }, default);
        Assert.True(t.W.Expenses.Items.Single(e => e.Id == other).IsDeleted); // no tyre of this repair keeps an amount
        Assert.Equal(((Guid?)other, (Guid?)other), (Line(t, t3).ExpenseId, Line(t, t4).ExpenseId));
    }

    [Fact]
    public async Task OneLineToTheTrash_LeavesTheExpenseWithTheOther_TheLastHoldersTakesItAlong_AndItsRestoreBringsItBack()
    {
        var s = await Setup();
        var (s3, s4, expenseId, _) = await SharedRepair(s);

        await s.Tyres.TrashEntryAsync(s3, default, withExpense: true); // not the last holder: the box does nothing
        Assert.False(s.W.Expenses.Items.Single(e => e.Id == expenseId).IsDeleted);
        Assert.Null(Line(s, s4).OdometerReading!.DeletedAt); // still held live by S4
        GroupHolds(s, expenseId);

        await s.Tyres.TrashEntryAsync(s4, default, withExpense: true); // the last one: the expense goes along
        Assert.True(s.W.Expenses.Items.Single(e => e.Id == expenseId).IsDeleted);
        Assert.True(Line(s, s4).ExpenseTrashedWithEntry);
        Assert.NotNull(Line(s, s4).OdometerReading!.DeletedAt);

        await s.Tyres.RestoreEntryAsync(s4, default); // they went together: both come back
        Assert.False(s.W.Expenses.Items.Single(e => e.Id == expenseId).IsDeleted);
        GroupHolds(s, expenseId);
    }

    [Fact]
    public async Task ALinePartingWhileTheDialogStillSendsTheSharedAmount_KeepsNoAmount_AndMakesNoSecondExpense()
    {
        var s = await Setup();
        var (s3, s4, expenseId, _) = await SharedRepair(s);

        await s.Tyres.UpdateEntryAsync(s3, new TyreEntryInput(TyreEntryKind.PunctureRepaired, s.Summer, D(2026, 9, 22), 148_400, "Screws in the tread",
            new TyreFee(13_000, "HUF", "Tyres", "Puncture repaired: Michelin Primacy 4", Guid.NewGuid())), default);

        Assert.Null(Line(s, s3).ExpenseId); // decision 29: it keeps its day without an amount
        Assert.Equal(expenseId, Line(s, s4).ExpenseId);
        Assert.Single(s.W.Expenses.Items.Where(e => e.Amount == 13_000));
    }

    [Fact]
    public async Task AnOdometerEmptiedOnTheSharedReading_ForAll_RemovesTheOneRow_AndForOneAlone_PartsWithoutAReading()
    {
        var s = await Setup();
        var (s3, s4, expenseId, _) = await SharedRepair(s);
        var shared = Line(s, s3).OdometerReadingId;

        await s.Tyres.UpdateEntryAsync(s3, new TyreEntryInput(TyreEntryKind.PunctureRepaired, s.Summer, D(2026, 9, 20), null, "Screws in the tread", Also: [s4]), default);
        Assert.All([Line(s, s3), Line(s, s4)], l => Assert.Null(l.OdometerReadingId));
        Assert.Null(s.W.Expenses.Items.Single(e => e.Id == expenseId).OdometerReadingId);
        Assert.DoesNotContain(s.W.Tyres.Entries, l => l.OdometerReadingId == shared); // the one row goes, nothing names it

        var t = await Setup();
        var (t3, t4, other, _) = await SharedRepair(t);
        await t.Tyres.UpdateEntryAsync(t3, new TyreEntryInput(TyreEntryKind.PunctureRepaired, t.Summer, D(2026, 9, 20), null, "Screws in the tread"), default);
        Assert.Equal(((Guid?)null, (Guid?)null), (Line(t, t3).OdometerReadingId, Line(t, t3).ExpenseId)); // parted, its own copy gone at once
        Assert.Equal(148_200, Line(t, t4).Odometer);
        GroupHolds(t, other);
    }

    [Fact]
    public async Task ALineTrashedWhileTheExpenseMovesOnTheExpensesTab_PartsWhenRestored_TheOtherKeepsIt()
    {
        var s = await Setup();
        var (s3, s4, expenseId, _) = await SharedRepair(s);
        await s.Tyres.TrashEntryAsync(s3, default, withExpense: false);

        await s.W.ExpenseService.UpdateAsync(expenseId, new ExpenseInput(D(2026, 9, 21), "Puncture repaired", "Tyres", 13_000, "HUF", 148_300, null), default);
        Assert.Equal((D(2026, 9, 21), D(2026, 9, 20)), (Line(s, s4).Day, Line(s, s3).Day)); // the trashed line is left alone

        await s.Tyres.RestoreEntryAsync(s3, default);
        Assert.Null(Line(s, s3).ExpenseId); // #288 as built: restored, it parts
        Assert.Equal(expenseId, Line(s, s4).ExpenseId);
        Assert.NotEqual(Line(s, s3).OdometerReadingId, Line(s, s4).OdometerReadingId);
        GroupHolds(s, expenseId);
    }
}
