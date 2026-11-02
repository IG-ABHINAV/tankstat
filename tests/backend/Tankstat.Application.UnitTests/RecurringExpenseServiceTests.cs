using Tankstat.Application.Auth;
using Tankstat.Application.Expenses;
using Tankstat.Application.Recurring;
using Tankstat.Application.Vehicles;
using Tankstat.Domain;
using Tankstat.Domain.Access;
using Tankstat.Domain.Recurring;
using Tankstat.Domain.Users;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.UnitTests;

public class RecurringExpenseServiceTests
{
    private static readonly DateOnly Today = new(2026, 10, 1); // the fake clock

    private sealed record Scene(World W, User Alice, User Bob, Vehicle Car);

    private static async Task<Scene> Setup()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        var bob = w.AddUser("bob@x.co");
        w.Current.SignInAs(alice);
        var car = await w.VehicleService.AddAsync("Car", null, FuelType.Petrol, default);
        return new Scene(w, alice, bob, car);
    }

    private static RecurringExpenseInput Oil(RecurrenceKind kind = RecurrenceKind.Combined, string title = "Oil change", DateOnly? last = null, long? odometer = 50000) =>
        new(title, "Service", null, kind, kind == RecurrenceKind.Odometer ? null : 12, kind == RecurrenceKind.Time ? null : 15000, last ?? new DateOnly(2026, 1, 15), odometer, null, null);

    [Fact]
    public async Task Add_StoresTheScheduleWithTheDefaultWarnings_AndReportsItsStatus()
    {
        var s = await Setup();

        var added = await s.W.RecurringService.AddAsync(s.Car.Id, Oil(), default);

        Assert.Equal((s.Alice.Id, s.Car.Id, 30, 500L), (added.Item.CreatedById, added.Item.VehicleId, added.Item.WarnDays, added.Item.WarnDistance));
        Assert.Equal(s.W.Clock.GetUtcNow(), added.Item.CreatedAt); // who and when
        Assert.Equal(new DateOnly(2027, 1, 15), added.Status.DueDate);
        Assert.Equal(RecurrenceState.Upcoming, added.Status.State);
    }

    [Fact]
    public async Task Add_UsesTheConfiguredInstanceDefaults_ForWarningsThatAreNotGiven()
    {
        var s = await Setup();
        s.W.Defaults.RecurringWarnDays = 14;
        s.W.Defaults.RecurringWarnDistance = 1000;

        var defaulted = await s.W.RecurringService.AddAsync(s.Car.Id, Oil(), default);
        var given = await s.W.RecurringService.AddAsync(s.Car.Id, Oil(title: "Tyres") with { WarnDays = 7, WarnDistance = 100 }, default);

        Assert.Equal((14, 1000L), (defaulted.Item.WarnDays, defaulted.Item.WarnDistance));
        Assert.Equal((7, 100L), (given.Item.WarnDays, given.Item.WarnDistance));
    }

    [Fact]
    public async Task Add_StartsCountingTodayFromTheCurrentOdometer_WhenNoneIsGiven()
    {
        var s = await Setup();
        await s.W.RefuelingService.LogAsync(s.Car.Id, new DateOnly(2026, 9, 30), 40, 60, 71500, true, default);

        var added = await s.W.RecurringService.AddAsync(s.Car.Id, Oil(odometer: null) with { LastDoneDate = null }, default);

        Assert.Equal((Today, 71500L), (added.Item.LastDoneDate, added.Item.LastDoneOdometer));
        Assert.Equal(86500L, added.Status.DueOdometer);
        Assert.Equal(15000L, added.Status.DistanceLeft); // the calculations start right away
    }

    [Fact]
    public async Task Add_KeepsAnOdometerThatWasGiven_AndDoesNotNeedOneForTimeOnlySchedules()
    {
        var s = await Setup();
        await s.W.RefuelingService.LogAsync(s.Car.Id, new DateOnly(2026, 9, 30), 40, 60, 71500, true, default);

        var given = await s.W.RecurringService.AddAsync(s.Car.Id, Oil(odometer: 70000) with { LastDoneDate = null }, default);
        var timeOnly = await s.W.RecurringService.AddAsync(s.Car.Id, Oil(RecurrenceKind.Time, "Insurance", odometer: null) with { LastDoneDate = null }, default);

        Assert.Equal(70000L, given.Item.LastDoneOdometer);
        Assert.Null(timeOnly.Item.LastDoneOdometer); // not needed, so not made up
    }

    [Fact]
    public async Task Add_NeedsAnOdometer_WhenDistanceCountsAndTheVehicleHasNoReadingYet()
    {
        var s = await Setup();

        var error = await Assert.ThrowsAsync<DomainException>(() => s.W.RecurringService.AddAsync(s.Car.Id, Oil(odometer: null) with { LastDoneDate = null }, default));

        Assert.Equal("recurring.odometerRequired", error.Key);
        Assert.Empty(s.W.Recurring.Items);
    }

    [Fact]
    public async Task Update_KeepsTheStartDateWhenNoneIsGiven()
    {
        var s = await Setup();
        var item = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(), default)).Item;

        var updated = await s.W.RecurringService.UpdateAsync(item.Id, Oil(title: "Oil and filter") with { LastDoneDate = null }, default);

        Assert.Equal((new DateOnly(2026, 1, 15), "Oil and filter"), (updated.Item.LastDoneDate, updated.Item.Title));
    }

    [Fact]
    public async Task Update_KeepsTheWarningThresholdsWhenNoneAreGiven()
    {
        var s = await Setup();
        var item = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil() with { WarnDays = 14, WarnDistance = 250 }, default)).Item;

        var updated = await s.W.RecurringService.UpdateAsync(item.Id, Oil(title: "Oil and filter") with { WarnDays = null, WarnDistance = null }, default);

        Assert.Equal((14, 250L), (updated.Item.WarnDays, updated.Item.WarnDistance));
    }

    [Fact]
    public async Task List_IsMostUrgentFirst_AndUsesTheLatestOdometerOfTheVehicle()
    {
        var s = await Setup();
        await s.W.RecurringService.AddAsync(s.Car.Id, Oil(RecurrenceKind.Time, "Insurance", new DateOnly(2025, 12, 1)), default); // due 2026-12-01: upcoming
        await s.W.RecurringService.AddAsync(s.Car.Id, Oil(RecurrenceKind.Odometer, "Tyres", odometer: 50000), default); // due at 65000
        await s.W.RecurringService.AddAsync(s.Car.Id, Oil(RecurrenceKind.Time, "Inspection", new DateOnly(2025, 6, 1)), default); // due 2026-06-01: overdue
        await s.W.RefuelingService.LogAsync(s.Car.Id, new DateOnly(2026, 9, 30), 40, 60, 64800, true, default); // the car is 200 from the tyres

        var list = await s.W.RecurringService.ListAsync(s.Car.Id, default);

        Assert.Equal(["Inspection", "Tyres", "Insurance"], list.Select(i => i.Item.Title));
        Assert.Equal([RecurrenceState.Overdue, RecurrenceState.DueSoon, RecurrenceState.Upcoming], list.Select(i => i.Status.State));
        Assert.Equal(200L, list[1].Status.DistanceLeft);
    }

    private static MarkDoneInput Done(long? odometer = 62000, decimal? amount = 35000, IReadOnlyCollection<Guid>? photos = null, DateOnly? date = null, string? title = null, string? category = null) =>
        new(date ?? new DateOnly(2026, 9, 20), odometer, amount, amount is null ? null : "HUF", title, category, photos);

    [Fact]
    public async Task MarkDone_OneSchedule_LogsTheExpenseUnderItsName_AndStartsTheNextInterval()
    {
        var s = await Setup();
        var item = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil() with { Note = "5W-30" }, default)).Item;

        var done = await s.W.RecurringService.MarkDoneAsync([item.Id], Done(), default);

        var expense = Assert.Single(s.W.Expenses.Items);
        Assert.Same(expense, done.Expense);
        Assert.Equal(("Oil change", "Service", "5W-30", 35000m, "HUF", 62000L), (expense.Title, expense.Category, expense.Note, expense.Amount, expense.Currency, expense.Odometer));
        var schedule = Assert.Single(done.Schedules);
        Assert.Equal((new DateOnly(2026, 9, 20), 62000L), (schedule.Item.LastDoneDate, schedule.Item.LastDoneOdometer));
        Assert.Equal((new DateOnly(2027, 9, 20), 77000L), (schedule.Status.DueDate, schedule.Status.DueOdometer));
        Assert.Equal([(expense.Id, item.Id)], s.W.Recurring.Completions.Select(c => (c.ExpenseId, c.RecurringExpenseId)));
    }

    [Fact]
    public async Task MarkDone_SeveralSchedules_LogOneExpenseForAll_NotSplit_LinkedToEach()
    {
        var s = await Setup();
        var oil = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil() with { Note = "5W-30" }, default)).Item;
        var filter = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(title: "Oil filter"), default)).Item;
        var air = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(RecurrenceKind.Time, "Air filter") with { Category = null }, default)).Item;
        var fuel = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(title: "Fuel filter"), default)).Item; // not done at this visit

        var done = await s.W.RecurringService.MarkDoneAsync([oil.Id, filter.Id, air.Id], Done(amount: 60000), default);

        var expense = Assert.Single(s.W.Expenses.Items);
        Assert.Equal(("Oil change, Oil filter, Air filter", "Service", (string?)null, 60000m), (expense.Title, expense.Category, expense.Note, expense.Amount));
        Assert.Equal([oil.Id, filter.Id, air.Id], done.Schedules.Select(i => i.Item.Id)); // in the order asked for
        Assert.All(done.Schedules, i => Assert.Equal(new DateOnly(2026, 9, 20), i.Item.LastDoneDate));
        Assert.Equal(new DateOnly(2026, 1, 15), fuel.LastDoneDate);
        Assert.Equal(new[] { oil.Id, filter.Id, air.Id }.Order(), s.W.Recurring.Completions.Where(c => c.ExpenseId == expense.Id).Select(c => c.RecurringExpenseId).Order());

        var covered = await s.W.RecurringService.ListCompletionsForExpensesAsync([expense.Id], default);
        Assert.Equal(["Air filter", "Oil change", "Oil filter"], covered[expense.Id].Select(c => c.Title));
    }

    [Fact]
    public async Task MarkDone_TheDiscountGoesWithTheLoggedExpense_AndNeedsItsAmount()
    {
        var s = await Setup();
        var oil = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(), default)).Item;

        var refused = await Assert.ThrowsAsync<DomainException>(() => s.W.RecurringService.MarkDoneAsync([oil.Id], Done(amount: null) with { Discount = 2000 }, default));
        Assert.Equal("cost.discountWithoutAmount", refused.Key);
        Assert.Empty(s.W.Expenses.Items);
        Assert.Equal(new DateOnly(2026, 1, 15), oil.LastDoneDate); // refused before anything moved

        await s.W.RecurringService.MarkDoneAsync([oil.Id], Done(amount: 33000) with { Discount = 2000 }, default);

        var expense = Assert.Single(s.W.Expenses.Items);
        Assert.Equal((33000m, 2000m), (expense.Amount, expense.Discount)); // the amount is what was paid
    }

    [Fact]
    public async Task MarkDone_TheTitleAndCategoryGiven_AreTheExpenses()
    {
        var s = await Setup();
        var oil = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(), default)).Item;
        var filter = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(title: "Oil filter"), default)).Item;

        await s.W.RecurringService.MarkDoneAsync([oil.Id, filter.Id], Done(title: "Yearly service", category: "Workshop"), default);

        Assert.Equal(("Yearly service", "Workshop"), (s.W.Expenses.Items.Single().Title, s.W.Expenses.Items.Single().Category));

        var other = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(title: "Wipers"), default)).Item;
        await s.W.RecurringService.MarkDoneAsync([other.Id], Done(category: ""), default); // an empty category is none, not "the schedule's"
        Assert.Null(s.W.Expenses.Items.Single(e => e.Title == "Wipers").Category);
    }

    [Fact]
    public async Task MarkDone_WithoutAnAmount_OnlyMovesTheBaselines()
    {
        var s = await Setup();
        var item = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(RecurrenceKind.Time), default)).Item;
        var other = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(RecurrenceKind.Time, "Wipers"), default)).Item;

        var done = await s.W.RecurringService.MarkDoneAsync([item.Id, other.Id], Done(odometer: null, amount: null), default);

        Assert.Null(done.Expense);
        Assert.Empty(s.W.Expenses.Items);
        Assert.Empty(s.W.Recurring.Completions);
        Assert.Equal([new DateOnly(2026, 9, 20), new DateOnly(2026, 9, 20)], new[] { item.LastDoneDate, other.LastDoneDate });
    }

    [Fact]
    public async Task MarkDone_AttachesThePhotosPickedInTheDialog_ToTheLoggedExpense()
    {
        var s = await Setup();
        var item = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(), default)).Item;
        var invoice = await s.W.Drafts.UploadAsync(s.Car.Id, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1 }, default);

        await s.W.RecurringService.MarkDoneAsync([item.Id], Done(photos: [invoice]), default);

        var expense = Assert.Single(s.W.Expenses.Items);
        Assert.Equal([invoice], s.W.LogPhotos.Items.Where(p => p.LogId == expense.Id).Select(p => p.ImageId));
        Assert.Empty(s.W.PhotoDrafts.Items);
    }

    [Fact]
    public async Task MarkDone_PhotosWithoutAnAmount_ThatNobodyIsReading_AreRefused_AndNothingMoves()
    {
        var s = await Setup();
        var item = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(RecurrenceKind.Time), default)).Item;
        var photo = await s.W.Drafts.UploadAsync(s.Car.Id, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 2 }, default);

        var refused = await Assert.ThrowsAsync<DomainException>(() => s.W.RecurringService.MarkDoneAsync([item.Id], Done(odometer: null, amount: null, photos: [photo]), default));

        Assert.Equal("log.valuesRequired", refused.Key); // the expense rule: an amount, or a photo still being read that may give it
        Assert.Empty(s.W.Expenses.Items);
        Assert.Equal(new DateOnly(2026, 1, 15), item.LastDoneDate);
    }

    [Fact]
    public async Task MarkDone_IsAllOrNothing_AndTheRefusalNamesTheScheduleThatRefused()
    {
        var s = await Setup();
        var oil = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(), default)).Item;
        var tyres = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(RecurrenceKind.Odometer, "Tyres", odometer: 63000), default)).Item;

        var refused = await Assert.ThrowsAsync<DomainException>(() => s.W.RecurringService.MarkDoneAsync([oil.Id, tyres.Id], Done(odometer: 62000), default));

        Assert.Equal(("recurring.odometerBelowLast", "Tyres"), (refused.Key, refused.Args["title"]));
        Assert.Empty(s.W.Expenses.Items);
        Assert.Equal((new DateOnly(2026, 1, 15), 50000L), (oil.LastDoneDate, oil.LastDoneOdometer)); // the one that would have been fine did not move either
    }

    [Fact]
    public async Task MarkDone_FailsAsAWhole_WhenTheExpenseOrTheScheduleRulesAreBroken()
    {
        var s = await Setup();
        var item = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(), default)).Item;
        await s.W.RefuelingService.LogAsync(s.Car.Id, new DateOnly(2026, 9, 1), 40, 60, 60000, true, default);
        var before = (item.LastDoneDate, item.LastDoneOdometer);

        var lowerThanARecentReading = await Assert.ThrowsAsync<DomainException>(() => s.W.RecurringService.MarkDoneAsync([item.Id], Done(odometer: 55000, amount: 10), default));
        var noOdometer = await Assert.ThrowsAsync<DomainException>(() => s.W.RecurringService.MarkDoneAsync([item.Id], Done(odometer: null, amount: null), default));
        var future = await Assert.ThrowsAsync<DomainException>(() => s.W.RecurringService.MarkDoneAsync([item.Id], Done(date: new DateOnly(2026, 11, 1), amount: null), default));
        var none = await Assert.ThrowsAsync<DomainException>(() => s.W.RecurringService.MarkDoneAsync([], Done(), default));

        Assert.Equal(["odometer.belowPrevious", "recurring.doneOdometerRequired", "refueling.dateInFuture", "recurring.noneSelected"],
            [lowerThanARecentReading.Key, noOdometer.Key, future.Key, none.Key]);
        Assert.Empty(s.W.Expenses.Items);
        Assert.Equal(before, (item.LastDoneDate, item.LastDoneOdometer));
    }

    [Fact]
    public async Task MarkDone_OnlyTakesSchedulesOfOneVisibleVehicle_AndToleratesAnIdGivenTwice()
    {
        var s = await Setup();
        var van = await s.W.VehicleService.AddAsync("Van", null, FuelType.Diesel, default);
        var oil = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(), default)).Item;
        var vans = (await s.W.RecurringService.AddAsync(van.Id, Oil(), default)).Item;

        var mixed = await Assert.ThrowsAsync<NotFoundException>(() => s.W.RecurringService.MarkDoneAsync([oil.Id, vans.Id], Done(), default));
        var unknown = await Assert.ThrowsAsync<NotFoundException>(() => s.W.RecurringService.MarkDoneAsync([oil.Id, Guid.NewGuid()], Done(), default));
        var zero = await Assert.ThrowsAsync<NotFoundException>(() => s.W.RecurringService.MarkDoneAsync([Guid.Empty], Done(), default)); // the all-zero id is an id too

        Assert.Equal(("recurring.notFound", vans.Id), (mixed.Key, mixed.Args["id"]));
        Assert.Equal("recurring.notFound", unknown.Key);
        Assert.Equal(("recurring.notFound", Guid.Empty), (zero.Key, zero.Args["id"]));
        Assert.Empty(s.W.Expenses.Items);
        Assert.Equal(new DateOnly(2026, 1, 15), oil.LastDoneDate);

        var done = await s.W.RecurringService.MarkDoneAsync([oil.Id, oil.Id], Done(), default);
        Assert.Single(done.Schedules);
        Assert.Single(s.W.Recurring.Completions);
    }

    [Fact]
    public async Task MarkDone_TrashesTheLoggedExpense_WhenTheSchedulesCannotBeSaved()
    {
        var s = await Setup();
        var item = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(), default)).Item;
        s.W.Recurring.FailUpdateWith = new InvalidOperationException("database down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.W.RecurringService.MarkDoneAsync([item.Id], Done(), default));

        Assert.True(Assert.Single(s.W.Expenses.Items).IsDeleted); // no live cost is left behind to be logged twice by a retry
        Assert.Empty(s.W.Recurring.Completions);
    }

    [Fact]
    public async Task DeletingASchedule_TakesItsLinksAlong_TheExpenseStays()
    {
        var s = await Setup();
        var item = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(), default)).Item;
        var done = await s.W.RecurringService.MarkDoneAsync([item.Id], Done(), default);

        await s.W.RecurringService.DeleteAsync(item.Id, default);

        Assert.Empty((await s.W.RecurringService.ListCompletionsForExpensesAsync([done.Expense!.Id], default))[done.Expense.Id]);
        Assert.False(Assert.Single(s.W.Expenses.Items).IsDeleted);
    }

    [Fact]
    public async Task Update_AndDelete_ChangeTheScheduleItself()
    {
        var s = await Setup();
        var item = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(), default)).Item;

        var updated = await s.W.RecurringService.UpdateAsync(item.Id, Oil(RecurrenceKind.Time, "Insurance") with { WarnDays = 14 }, default);
        Assert.Equal(("Insurance", RecurrenceKind.Time, 14), (updated.Item.Title, updated.Item.Kind, updated.Item.WarnDays));

        await s.W.RecurringService.DeleteAsync(item.Id, default);
        Assert.Empty(s.W.Recurring.Items);
        await Assert.ThrowsAsync<NotFoundException>(() => s.W.RecurringService.DeleteAsync(item.Id, default));
    }

    [Fact]
    public async Task FollowsTheAccessRulesOfTheVehiclesLogs()
    {
        var s = await Setup();
        var item = (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(), default)).Item;

        s.W.Current.SignInAs(s.Bob); // a stranger: nothing is visible, nothing exists
        Assert.Empty(await s.W.RecurringService.ListAsync(s.Car.Id, default));
        await Assert.ThrowsAsync<NotFoundException>(() => s.W.RecurringService.AddAsync(s.Car.Id, Oil(), default));
        await Assert.ThrowsAsync<NotFoundException>(() => s.W.RecurringService.DeleteAsync(item.Id, default));

        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.View)); // may look, not touch
        Assert.Single(await s.W.RecurringService.ListAsync(s.Car.Id, default));
        await Assert.ThrowsAsync<ForbiddenException>(() => s.W.RecurringService.AddAsync(s.Car.Id, Oil(), default));
        await Assert.ThrowsAsync<ForbiddenException>(() => s.W.RecurringService.MarkDoneAsync([item.Id], new MarkDoneInput(Today, 51000, null, null), default));

        s.W.Grants.Items.Clear();
        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.Edit));
        Assert.Equal(s.Bob.Id, (await s.W.RecurringService.AddAsync(s.Car.Id, Oil(RecurrenceKind.Time, "Insurance"), default)).Item.CreatedById);
    }

    [Fact]
    public async Task ListForVehicles_GivesEachVehicleItsOwnSchedulesAndOdometer_InOneQuery()
    {
        var s = await Setup();
        var van = await s.W.VehicleService.AddAsync("Van", null, FuelType.Diesel, default);
        var bare = await s.W.VehicleService.AddAsync("Bare", null, FuelType.Diesel, default);
        await s.W.RecurringService.AddAsync(s.Car.Id, Oil(RecurrenceKind.Odometer, "Tyres", odometer: 50000), default); // due at 65000
        await s.W.RecurringService.AddAsync(van.Id, Oil(RecurrenceKind.Time, "Insurance", new DateOnly(2025, 6, 1)), default);
        await s.W.RefuelingService.LogAsync(s.Car.Id, new DateOnly(2026, 9, 30), 40, 60, 64800, true, default);
        var calls = s.W.Recurring.ListForVehiclesCalls;

        var all = await s.W.RecurringService.ListForVehiclesAsync([s.Car, van, bare], default);

        Assert.Equal(calls + 1, s.W.Recurring.ListForVehiclesCalls);
        Assert.Equal(200L, Assert.Single(all[s.Car.Id]).Status.DistanceLeft);
        Assert.Equal(RecurrenceState.Overdue, Assert.Single(all[van.Id]).Status.State);
        Assert.Empty(all[bare.Id]);
        Assert.Equal((await s.W.RecurringService.ListAsync(s.Car.Id, default)).Select(i => i.Item.Id), all[s.Car.Id].Select(i => i.Item.Id));
    }

    [Fact]
    public async Task ListForVehicles_FollowsTheAccessRulesOfTheVehiclesLogs()
    {
        var s = await Setup();
        await s.W.RecurringService.AddAsync(s.Car.Id, Oil(), default);

        s.W.Current.SignInAs(s.Bob);
        Assert.Empty((await s.W.RecurringService.ListForVehiclesAsync([s.Car], default))[s.Car.Id]); // a stranger sees nothing

        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.View));
        Assert.Single((await s.W.RecurringService.ListForVehiclesAsync([s.Car], default))[s.Car.Id]);

        s.W.Grants.Items.Clear();
        s.W.ResourceGrants.Items.Add(ResourceGrant.Create(ResourceType.Vehicle, s.Car.Id, s.Bob.Id, GrantedFeature.Logs, AccessLevel.Edit));
        Assert.Single((await s.W.RecurringService.ListForVehiclesAsync([s.Car], default))[s.Car.Id]); // a logs grant on that vehicle counts
    }

    [Fact]
    public async Task ATrashedVehicle_ShowsNoSchedules()
    {
        var s = await Setup();
        await s.W.RecurringService.AddAsync(s.Car.Id, Oil(), default);

        await s.W.VehicleService.DeleteAsync(s.Car.Id, default);

        Assert.Empty(await s.W.RecurringService.ListAsync(s.Car.Id, default));
    }
}
