using Tankstat.Application.Refuelings;
using Tankstat.Application.Recurring;
using Tankstat.Application.Tyres;
using Tankstat.Domain;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Recurring;
using Tankstat.Domain.Tyres;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.UnitTests;

/// <summary>
/// A visit (Mark as done) with tyre details (#290, the owner's decisions 57 and 63): with an amount its one expense is the lines', without
/// one the line (or lines) alone, on the visit's day with its odometer; a failure takes them back with the expense, a try again brings them back.
/// </summary>
public class VisitTyreDetailsTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private sealed record Scene(World W, Vehicle Car, Guid Summer, Guid Schedule)
    {
        public RecurringExpenseService Recurring => W.RecurringService;
        public RecurringExpense Item => W.Recurring.Items.Single(i => i.Id == Schedule);
        public IEnumerable<TyreEntry> Lines => W.Tyres.Entries.Where(e => !e.IsSwapLine);
    }

    private static async Task<Scene> Setup()
    {
        var w = new World(); // today is 1 Oct 2026
        w.Current.SignInAs(w.AddUser("alice@x.co"));
        var car = await w.VehicleService.AddAsync("Car", null, FuelType.Petrol, MeasurementUnits.Metric, default);
        var summer = Guid.NewGuid();
        await w.TyreService.AddSetAsync(new NewTyreSet(car.Id, new TyreSetInput("Michelin Primacy 4", TyreSeason.Summer, null, D(2023, 3, 25), null, null), true, D(2026, 3, 28), null, true), default, summer);
        var schedule = (await w.RecurringService.AddAsync(car.Id, new RecurringExpenseInput(
            "Wheel balancing", "Tyres", null, RecurrenceKind.Time, 12, null, D(2025, 9, 1), null, null, null), default)).Item;
        return new Scene(w, car, summer, schedule.Id);
    }

    private static MarkDoneInput Visit(Guid setId, Guid lineId, decimal? amount = 8000, long? odometer = 148_200, Guid? expenseId = null) =>
        new(D(2026, 9, 20), odometer, amount, amount is null ? null : "HUF", ExpenseId: expenseId,
            Tyre: new ExpenseTyreDetails(TyreEntryKind.WheelBalancing, setId, LineId: lineId));

    [Fact]
    public async Task WithAnAmount_TheVisitsOneExpenseIsTheLines_OnItsDay_HoldingItsReading()
    {
        var s = await Setup();
        var lineId = Guid.NewGuid();

        var done = await s.Recurring.MarkDoneAsync([s.Schedule], Visit(s.Summer, lineId), default);

        var expense = Assert.Single(s.W.Expenses.Items);
        Assert.Same(expense, done.Expense);
        Assert.Equal("Wheel balancing", expense.Title); // the schedules' title stays (decision 64)
        var line = Assert.Single(s.Lines);
        Assert.Equal((lineId, TyreEntryKind.WheelBalancing, s.Summer, D(2026, 9, 20), (Guid?)expense.Id, expense.OdometerReadingId),
            (line.Id, line.Kind, line.SetId, line.Day, line.ExpenseId, line.OdometerReadingId));
        Assert.Equal(D(2026, 9, 20), s.Item.LastDoneDate);
        Assert.Equal([(expense.Id, s.Schedule)], s.W.Recurring.Completions.Select(c => (c.ExpenseId, c.RecurringExpenseId)));
    }

    [Fact]
    public async Task WithoutAnAmount_TheLineAlone_ItsOdometerOneOfTheCarsReadings()
    {
        var s = await Setup();
        var lineId = Guid.NewGuid();

        var done = await s.Recurring.MarkDoneAsync([s.Schedule], Visit(s.Summer, lineId, amount: null), default);

        Assert.Null(done.Expense);
        Assert.Empty(s.W.Expenses.Items);
        var line = Assert.Single(s.Lines);
        Assert.Equal((lineId, (Guid?)null, (long?)148_200), (line.Id, line.ExpenseId, line.Odometer));
        Assert.Single((await s.W.TyreService.OverviewAsync(s.Car.Id, default))!.Readings, r => r.Value == 148_200);
        Assert.Equal((D(2026, 9, 20), (long?)148_200), (s.Item.LastDoneDate, s.Item.LastDoneOdometer));
    }

    [Fact]
    public async Task WithoutAnAmount_AReadingTheNeighboursRefuse_IsRefusedBeforeAnyScheduleMoves()
    {
        var s = await Setup();
        await s.W.RefuelingService.LogAsync(s.Car.Id, D(2026, 9, 25), 40, 60, 140_000, true, default); // later and lower

        await Assert.ThrowsAnyAsync<KeyedException>(() => s.Recurring.MarkDoneAsync([s.Schedule], Visit(s.Summer, Guid.NewGuid(), amount: null), default));

        Assert.Empty(s.Lines);
        Assert.Equal(D(2025, 9, 1), s.Item.LastDoneDate);
    }

    [Fact]
    public async Task AFailure_TakesTheLinesBackWithTheExpense_AndATryAgainBringsThemBack()
    {
        var s = await Setup();
        var (lineId, expenseId) = (Guid.NewGuid(), Guid.NewGuid());
        s.W.Recurring.FailUpdateWith = new InvalidOperationException("database down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Recurring.MarkDoneAsync([s.Schedule], Visit(s.Summer, lineId, expenseId: expenseId), default));

        Assert.True(Assert.Single(s.W.Expenses.Items).IsDeleted);
        Assert.True(Assert.Single(s.Lines).IsDeleted); // nothing left on the tyre log for a visit that did not happen

        s.W.Recurring.FailUpdateWith = null;
        var done = await s.Recurring.MarkDoneAsync([s.Schedule], Visit(s.Summer, lineId, expenseId: expenseId), default);

        Assert.False(done.Expense!.IsDeleted);
        var line = Assert.Single(s.Lines);
        Assert.Equal((false, (Guid?)expenseId), (line.IsDeleted, line.ExpenseId));
        Assert.Single(s.W.Recurring.Completions);
    }

    [Fact]
    public async Task ATryAgainWhoseReadingNoLongerFits_IsRefused_LeavingTheExpenseAndItsLineInTheTrash()
    {
        var s = await Setup();
        var (lineId, expenseId) = (Guid.NewGuid(), Guid.NewGuid());
        s.W.Recurring.FailUpdateWith = new InvalidOperationException("database down");
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Recurring.MarkDoneAsync([s.Schedule], Visit(s.Summer, lineId, expenseId: expenseId), default));
        s.W.Recurring.FailUpdateWith = null;
        // Meanwhile a refuelling after the visit's day reads lower than the visit's odometer: their one reading cannot come back.
        await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(D(2026, 9, 25), 40, 20000, "HUF", 140_000, true, null), default);

        await Assert.ThrowsAnyAsync<KeyedException>(() => s.Recurring.MarkDoneAsync([s.Schedule], Visit(s.Summer, lineId, expenseId: expenseId), default));

        Assert.True(Assert.Single(s.W.Expenses.Items).IsDeleted); // all or nothing: nothing of the visit comes back
        Assert.True(Assert.Single(s.Lines).IsDeleted);
        Assert.Empty(s.W.Recurring.Completions); // no schedule saved as done (the fakes keep a failed save's in-memory change, so its date says nothing)
    }

    [Fact]
    public async Task AFailureWithoutAnAmount_TakesTheLineBack_AndATryAgainBringsItBack()
    {
        var s = await Setup();
        var lineId = Guid.NewGuid();
        s.W.Recurring.FailUpdateWith = new InvalidOperationException("database down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Recurring.MarkDoneAsync([s.Schedule], Visit(s.Summer, lineId, amount: null), default));
        Assert.True(Assert.Single(s.Lines).IsDeleted);

        s.W.Recurring.FailUpdateWith = null;
        await s.Recurring.MarkDoneAsync([s.Schedule], Visit(s.Summer, lineId, amount: null), default);

        Assert.False(Assert.Single(s.Lines).IsDeleted);
    }

    [Fact]
    public async Task ForTyresOfAWholeSet_ALineEach_BrokenUpOnTheVisitsDay_SharingTheExpense()
    {
        var s = await Setup();
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var (l3, l4) = (Guid.NewGuid(), Guid.NewGuid());
        var breakUp = new TyreBreakUpInput(s.Summer, D(2026, 9, 20), [
            new NewTyre(ids[0], "S1", TyreCorner.FrontLeft), new NewTyre(ids[1], "S2", TyreCorner.FrontRight),
            new NewTyre(ids[2], "S3", TyreCorner.RearLeft), new NewTyre(ids[3], "S4", TyreCorner.RearRight)]);
        var tyre = new ExpenseTyreDetails(TyreEntryKind.PunctureRepaired, s.Summer, [new TyreLine(ids[2], l3), new TyreLine(ids[3], l4)], breakUp);

        var done = await s.Recurring.MarkDoneAsync([s.Schedule], new MarkDoneInput(D(2026, 9, 20), 148_200, 13_000, "HUF", Tyre: tyre), default);

        var lines = s.Lines.OrderBy(l => l.Id == l4).ToList();
        Assert.Equal([l3, l4], lines.Select(l => l.Id));
        Assert.All(lines, l => Assert.Equal(done.Expense!.Id, l.ExpenseId));
    }

    [Fact]
    public async Task AFailureWithoutAnAmount_WhereATwinRecordedTheVisit_KeepsItsLine()
    {
        var s = await Setup();
        var lineId = Guid.NewGuid();
        s.W.Recurring.TwinSavesSchedulesNext = true;

        var done = await s.Recurring.MarkDoneAsync([s.Schedule], Visit(s.Summer, lineId, amount: null, odometer: null), default);

        Assert.Equal(D(2026, 9, 20), Assert.Single(done.Schedules).Item.LastDoneDate);
        Assert.False(Assert.Single(s.Lines).IsDeleted); // the visit is done: its line stays
    }
}
