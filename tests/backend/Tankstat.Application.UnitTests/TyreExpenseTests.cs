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

        await s.Tyres.ExpenseReadingFilledAsync(expense.Id, reading, default);

        Assert.Equal(reading.Id, s.W.Tyres.Entries.Single(l => l.Id == line).OdometerReadingId);
        PairHolds(s, line);
    }
}
