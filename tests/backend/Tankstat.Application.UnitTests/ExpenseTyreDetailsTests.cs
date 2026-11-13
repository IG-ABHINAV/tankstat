using Tankstat.Application.Expenses;
using Tankstat.Application.Tyres;
using Tankstat.Domain;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Tyres;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.UnitTests;

/// <summary>
/// An expense with tyre details (#290): the Expense dialog puts it on the tyre log, a line (or a line per tyre) on its day holding it and its
/// reading, saved with it, all or nothing; an edit gives a plain expense its lines, or changes what was done on the lines that made one.
/// </summary>
public class ExpenseTyreDetailsTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private sealed record Scene(World W, Vehicle Car, Guid Summer, Guid Winter, Guid SummerOn)
    {
        public ExpenseService Expenses => W.ExpenseService;
        public TyreService Tyres => W.TyreService;
        public IEnumerable<TyreEntry> LinesOf(Guid expenseId) => W.Tyres.Entries.Where(e => e.ExpenseId == expenseId);
    }

    private static async Task<Scene> Setup()
    {
        var w = new World(); // today is 1 Oct 2026
        w.Current.SignInAs(w.AddUser("alice@x.co"));
        var car = await w.VehicleService.AddAsync("Car", null, FuelType.Petrol, MeasurementUnits.Metric, default);
        var (summer, winter, summerOn) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var set = (string name, TyreSeason season) => new TyreSetInput(name, season, null, D(2023, 3, 25), null, null);
        await w.TyreService.AddSetAsync(new NewTyreSet(car.Id, set("Michelin Primacy 4", TyreSeason.Summer), true, D(2026, 3, 28), null, true), default, summer, summerOn);
        await w.TyreService.AddSetAsync(new NewTyreSet(car.Id, set("Continental WinterContact", TyreSeason.Winter), false, D(2026, 3, 28), null, null), default, winter);
        return new Scene(w, car, summer, winter, summerOn);
    }

    private static ExpenseInput Input(decimal? amount = 25, long? odometer = 148_200, DateOnly? day = null) =>
        new(day ?? D(2026, 9, 20), "Puncture repaired: Michelin Primacy 4", "Tyres", amount, "EUR", odometer, "Rear left");

    private static TyreBreakUpInput BreakUp(Guid setId, params Guid[] ids) =>
        new(setId, D(2026, 9, 20), [
            new NewTyre(ids[0], "S1", TyreCorner.FrontLeft), new NewTyre(ids[1], "S2", TyreCorner.FrontRight),
            new NewTyre(ids[2], "S3", TyreCorner.RearLeft), new NewTyre(ids[3], "S4", TyreCorner.RearRight)]);

    private static async Task<(Guid Expense, Guid Line)> Puncture(Scene s, ExpenseTyreDetails? details = null)
    {
        var (expenseId, lineId) = (Guid.NewGuid(), Guid.NewGuid());
        await s.Expenses.AddAsync(s.Car.Id, Input(), default, id: expenseId, tyre: details ?? new ExpenseTyreDetails(TyreEntryKind.PunctureRepaired, s.Summer, LineId: lineId));
        return (expenseId, lineId);
    }

    // ---- adding --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnExpenseWithTyreDetails_IsSavedWithItsLine_OnItsDay_HoldingItsReading_AndCountedOnce()
    {
        var s = await Setup();

        var (expenseId, lineId) = await Puncture(s);

        var expense = Assert.Single(s.W.Expenses.Items, e => e.Id == expenseId);
        var line = Assert.Single(s.LinesOf(expenseId));
        Assert.Equal((lineId, TyreEntryKind.PunctureRepaired, s.Summer, D(2026, 9, 20), (long?)148_200, (string?)null),
            (line.Id, line.Kind, line.SetId, line.Day, line.Odometer, line.Note)); // the note stays the expense's (the owner's decision 52)
        Assert.Equal(expense.OdometerReadingId, line.OdometerReadingId); // one reading row, held by both
        var overview = (await s.Tyres.OverviewAsync(s.Car.Id, default))!;
        Assert.Single(overview.Readings, r => r.Value == 148_200);
        Assert.Equal([new TyreSpent("EUR", 25)], overview.Sets.Single(v => v.Set.Id == s.Summer).Spent!); // counted once
        Assert.Equal([new TyreSpent("EUR", 25)], overview.SpentAll!);
    }

    [Fact]
    public async Task TheSameAddSentAgain_AnswersWithWhatItMade_AndMakesNothingTwice()
    {
        var s = await Setup();
        var (expenseId, lineId) = (Guid.NewGuid(), Guid.NewGuid());
        var details = new ExpenseTyreDetails(TyreEntryKind.PunctureRepaired, s.Summer, LineId: lineId);

        await s.Expenses.AddAsync(s.Car.Id, Input(), default, id: expenseId, tyre: details);
        var again = await s.Expenses.AddAsync(s.Car.Id, Input(), default, id: expenseId, tyre: details);

        Assert.Equal(expenseId, again.Id);
        Assert.Single(s.W.Expenses.Items, e => e.Id == expenseId);
        Assert.Single(s.LinesOf(expenseId));
    }

    [Fact]
    public async Task ForTwoTyresOfAWholeSet_TheSetIsBrokenUp_AndTheirLinesShareTheExpenseAndItsReading()
    {
        var s = await Setup();
        var tyres = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());

        var (expenseId, _) = await Puncture(s, new ExpenseTyreDetails(TyreEntryKind.PunctureRepaired, s.Summer,
            [new TyreLine(tyres[2], first), new TyreLine(tyres[3], second)], BreakUp(s.Summer, tyres)));

        Assert.NotNull(s.W.Tyres.Sets.Single(x => x.Id == s.Summer).BrokenUpOn);
        var lines = s.LinesOf(expenseId).OrderBy(l => l.Id == second).ToList();
        Assert.Equal([(first, tyres[2]), (second, tyres[3])], lines.Select(l => (l.Id, l.TyreId!.Value)));
        Assert.All(lines, l => Assert.Equal((first, s.W.Expenses.Items.Single(e => e.Id == expenseId).OdometerReadingId), (l.GroupId!.Value, l.OdometerReadingId)));
        var overview = (await s.Tyres.OverviewAsync(s.Car.Id, default))!;
        Assert.Equal([new TyreSpent("EUR", 25)], overview.SpentAll!); // one expense, counted once
    }

    [Fact]
    public async Task WithoutAnOdometer_TheLineHoldsNoReading_AndANoSetLineIsAllowed()
    {
        var s = await Setup();
        var expenseId = Guid.NewGuid();

        await s.Expenses.AddAsync(s.Car.Id, Input(odometer: null), default, id: expenseId, tyre: new ExpenseTyreDetails(TyreEntryKind.Other, null));

        var line = Assert.Single(s.LinesOf(expenseId));
        Assert.Equal(((Guid?)null, (Guid?)null), (line.SetId, line.OdometerReadingId));
    }

    [Theory]
    [InlineData("note")]
    [InlineData("swap")]
    [InlineData("set")]
    [InlineData("lineId")]
    [InlineData("tyre")]
    public async Task ARefusedTyrePart_SavesNeitherTheExpenseNorAnyLine(string what)
    {
        var s = await Setup();
        var (taken, _) = await Puncture(s);
        var takenLine = s.LinesOf(taken).Single().Id;
        var before = (s.W.Expenses.Items.Count, s.W.Tyres.Entries.Count);
        var expenseId = Guid.NewGuid();
        var details = what switch
        {
            "note" => new ExpenseTyreDetails(TyreEntryKind.Note, s.Summer),
            "swap" => new ExpenseTyreDetails(TyreEntryKind.Swap, s.Summer),
            "set" => new ExpenseTyreDetails(TyreEntryKind.PunctureRepaired, Guid.NewGuid()),
            "lineId" => new ExpenseTyreDetails(TyreEntryKind.PunctureRepaired, s.Summer, LineId: takenLine), // another add's line id
            _ => new ExpenseTyreDetails(TyreEntryKind.PunctureRepaired, s.Summer, [new TyreLine(Guid.NewGuid())]), // no such tyre
        };

        var refused = await Assert.ThrowsAnyAsync<KeyedException>(() => s.Expenses.AddAsync(s.Car.Id, Input(), default, id: expenseId, tyre: details));

        Assert.Equal(what switch
        {
            "note" => "tyres.noteTakesNoAmount",
            "swap" => "tyres.entryKindNotAllowed",
            "set" => "tyres.setNotFound",
            "lineId" => "sync.idTaken",
            _ => "tyres.tyreNotFound",
        }, refused.Key);
        Assert.Equal(before, (s.W.Expenses.Items.Count, s.W.Tyres.Entries.Count));
        Assert.DoesNotContain(s.W.Expenses.Items, e => e.Id == expenseId);
    }

    [Fact]
    public async Task AnAddWithoutTyreDetails_MakesNoLine()
    {
        var s = await Setup();

        var expense = await s.Expenses.AddAsync(s.Car.Id, Input(), default, id: Guid.NewGuid());

        Assert.Empty(s.LinesOf(expense.Id));
    }

    // ---- an edit of a plain expense (the owner's decisions 54 and 65) ---------------------------------------

    [Fact]
    public async Task APlainExpenseGivenTyreDetails_IsPutOnTheTyreLog_WithTheDayAndOdometerItIsSavedWith()
    {
        var s = await Setup();
        var plain = await s.Expenses.AddAsync(s.Car.Id, new ExpenseInput(D(2026, 9, 1), "Tyre hotel", "Tyres", 60, "EUR", 147_000, null), default);
        var lineId = Guid.NewGuid();

        await s.Expenses.UpdateAsync(plain.Id, new ExpenseInput(D(2026, 9, 2), "Tyre hotel", "Tyres", 60, "EUR", 147_100, null), default,
            tyre: new ExpenseTyreDetails(TyreEntryKind.StorageFee, s.Winter, LineId: lineId));

        var expense = s.W.Expenses.Items.Single(e => e.Id == plain.Id);
        var line = Assert.Single(s.LinesOf(plain.Id));
        Assert.Equal((lineId, TyreEntryKind.StorageFee, s.Winter, D(2026, 9, 2), (long?)147_100), (line.Id, line.Kind, line.SetId, line.Day, line.Odometer));
        Assert.Equal(expense.OdometerReadingId, line.OdometerReadingId);
        Assert.Equal((D(2026, 9, 2), 147_100L), (expense.Date, expense.Odometer!.Value));
    }

    [Fact]
    public async Task APlainExpenseEditedWithoutTyreDetails_StaysOffTheTyreLog()
    {
        var s = await Setup();
        var plain = await s.Expenses.AddAsync(s.Car.Id, new ExpenseInput(D(2026, 9, 1), "Wash", null, 10, "EUR", 147_000, null), default);

        await s.Expenses.UpdateAsync(plain.Id, new ExpenseInput(D(2026, 9, 1), "Wash", null, 12, "EUR", 147_000, null), default);

        Assert.Empty(s.LinesOf(plain.Id));
        Assert.Equal(12m, s.W.Expenses.Items.Single(e => e.Id == plain.Id).Amount);
    }

    // ---- an edit of an expense the tyre log made (the owner's decisions 53, 60 and 69) -----------------------

    [Fact]
    public async Task WhatWasDone_ChangesEveryLiveLineHoldingIt()
    {
        var s = await Setup();
        var tyres = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var (expenseId, _) = await Puncture(s, new ExpenseTyreDetails(TyreEntryKind.PunctureRepaired, s.Summer,
            [new TyreLine(tyres[0], Guid.NewGuid()), new TyreLine(tyres[1], Guid.NewGuid())], BreakUp(s.Summer, tyres)));

        await s.Expenses.UpdateAsync(expenseId, Input(), default, tyre: new ExpenseTyreDetails(TyreEntryKind.ValveReplaced, s.Summer));

        Assert.All(s.LinesOf(expenseId), l => Assert.Equal((TyreEntryKind.ValveReplaced, s.Summer), (l.Kind, l.SetId)));
        Assert.Equal(2, s.LinesOf(expenseId).Count());
    }

    [Fact]
    public async Task AWholeSetsLine_MovesToAnotherSetOrNone()
    {
        var s = await Setup();
        var (expenseId, lineId) = await Puncture(s);

        await s.Expenses.UpdateAsync(expenseId, Input(), default, tyre: new ExpenseTyreDetails(TyreEntryKind.StorageFee, s.Winter));
        Assert.Equal((TyreEntryKind.StorageFee, (Guid?)s.Winter), s.W.Tyres.Entries.Where(e => e.Id == lineId).Select(e => (e.Kind, e.SetId)).Single());

        await s.Expenses.UpdateAsync(expenseId, Input(), default, tyre: new ExpenseTyreDetails(TyreEntryKind.StorageFee, null));
        Assert.Null(s.W.Tyres.Entries.Single(e => e.Id == lineId).SetId);
    }

    [Fact]
    public async Task ATyresLine_KeepsItsSet_AndANoteIsRefused()
    {
        var s = await Setup();
        var tyres = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var (expenseId, _) = await Puncture(s, new ExpenseTyreDetails(TyreEntryKind.PunctureRepaired, s.Summer,
            [new TyreLine(tyres[0], Guid.NewGuid())], BreakUp(s.Summer, tyres)));

        var otherSet = await Assert.ThrowsAsync<DomainException>(() =>
            s.Expenses.UpdateAsync(expenseId, Input(), default, tyre: new ExpenseTyreDetails(TyreEntryKind.PunctureRepaired, s.Winter)));
        var note = await Assert.ThrowsAsync<DomainException>(() =>
            s.Expenses.UpdateAsync(expenseId, Input(), default, tyre: new ExpenseTyreDetails(TyreEntryKind.Note, s.Summer)));

        Assert.Equal(("tyres.tyreNotOfSet", "tyres.noteTakesNoAmount"), (otherSet.Key, note.Key));
        Assert.Equal((TyreEntryKind.PunctureRepaired, (Guid?)s.Summer), s.LinesOf(expenseId).Select(e => (e.Kind, e.SetId)).Single());
    }

    [Fact]
    public async Task ASwapsLine_PassesWithWhatItSaysAlready_AndRefusesAnythingElse()
    {
        var s = await Setup();
        var expenseId = Guid.NewGuid();
        await s.Tyres.SwapAsync(new TyreSwapInput(s.Car.Id, s.Winter, D(2026, 9, 25), null, false, 148_500, null,
            new TyreFee(40, "EUR", "Tyres", "Swap: Continental WinterContact", expenseId)), default, Guid.NewGuid());
        var swapLine = s.LinesOf(expenseId).Single();

        await s.Expenses.UpdateAsync(expenseId, Input(40, 148_500, D(2026, 9, 25)), default, tyre: new ExpenseTyreDetails(swapLine.Kind, s.Winter));
        var refused = await Assert.ThrowsAsync<DomainException>(() =>
            s.Expenses.UpdateAsync(expenseId, Input(40, 148_500, D(2026, 9, 25)), default, tyre: new ExpenseTyreDetails(TyreEntryKind.WheelBalancing, s.Winter)));

        Assert.Equal("tyres.swapLineLocked", refused.Key);
        Assert.Equal(TyreEntryKind.Swap, s.LinesOf(expenseId).Single().Kind);
    }

    [Fact]
    public async Task AnExpenseWhoseLineIsInTheTrash_TakesNoTyreDetails_ButIsEditedAsBefore()
    {
        var s = await Setup();
        var (expenseId, lineId) = await Puncture(s);
        await s.Tyres.TrashEntryAsync(lineId, default, withExpense: false);

        var refused = await Assert.ThrowsAsync<DomainException>(() =>
            s.Expenses.UpdateAsync(expenseId, Input(), default, tyre: new ExpenseTyreDetails(TyreEntryKind.ValveReplaced, s.Summer)));
        await s.Expenses.UpdateAsync(expenseId, Input(30), default);

        Assert.Equal("tyres.expenseLineInTrash", refused.Key);
        Assert.Single(s.LinesOf(expenseId)); // no second line
        Assert.Equal(30m, s.W.Expenses.Items.Single(e => e.Id == expenseId).Amount);
    }
}
