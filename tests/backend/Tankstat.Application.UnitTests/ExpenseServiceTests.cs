using Tankstat.Application;
using Tankstat.Application.Auth;
using Tankstat.Application.Expenses;
using Tankstat.Domain;
using Tankstat.Domain.Access;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Users;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.UnitTests;

public class ExpenseServiceTests
{
    private static readonly DateOnly Day = new(2026, 9, 1);

    private sealed record Scene(World W, User Alice, User Bob, Vehicle Car);

    private static async Task<Scene> Setup()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        var bob = w.AddUser("bob@x.co");
        w.Current.SignInAs(alice);
        return new Scene(w, alice, bob, await w.VehicleService.AddAsync("Car", null, FuelType.Petrol, MeasurementUnits.Metric, default));
    }

    private static ExpenseInput Input(DateOnly? date = null, string title = "Oil change", string? category = "Service", decimal amount = 100, long? odometer = 1000, decimal? discount = null) =>
        new(date ?? Day, title, category, amount, "EUR", odometer, null, discount);

    [Fact]
    public async Task ADiscount_IsStoredWithTheAmount_AnUpdateWithoutOneClearsIt_AndNoAmountRefusesIt()
    {
        var s = await Setup();

        var added = await s.W.ExpenseService.AddAsync(s.Car.Id, Input(amount: 18800, discount: 1200), default);
        Assert.Equal((18800m, 1200m), (added.Amount, added.Discount));

        var updated = await s.W.ExpenseService.UpdateAsync(added.Id, Input(amount: 18800), default);
        Assert.Null(updated.Discount);

        var refused = await Assert.ThrowsAsync<DomainException>(() =>
            s.W.ExpenseService.AddAsync(s.Car.Id, new ExpenseInput(Day.AddDays(1), "Tyres", null, null, "EUR", null, null, 5), default));
        Assert.Equal("cost.discountWithoutAmount", refused.Key);
    }

    [Fact]
    public async Task Add_RecordsWhoAddedIt_AndListsNewestFirst()
    {
        var s = await Setup();

        await s.W.ExpenseService.AddAsync(s.Car.Id, Input(Day), default);
        var newer = await s.W.ExpenseService.AddAsync(s.Car.Id, Input(Day.AddDays(3), "Tyres", odometer: 1100), default);

        Assert.Equal(s.Alice.Id, newer.CreatedById);
        Assert.Equal(["Tyres", "Oil change"], (await s.W.ExpenseService.ListAsync(s.Car.Id, new ExpenseQuery(), default)).Select(e => e.Title));
        Assert.Equal(2, await s.W.ExpenseService.CountAsync(s.Car.Id, default));
        Assert.Equal(["Service"], await s.W.ExpenseService.CategoriesAsync(s.Car.Id, default));
    }

    [Fact]
    public async Task TheOdometerOfAnExpense_FollowsTheSameRulesAsFuelLogs()
    {
        var s = await Setup();
        await s.W.RefuelingService.LogAsync(s.Car.Id, new Application.Refuelings.RefuelingInput(Day, 40, 60, "EUR", 5000, true, null), default);

        var error = await Assert.ThrowsAsync<DomainException>(() => s.W.ExpenseService.AddAsync(s.Car.Id, Input(Day.AddDays(2), odometer: 4000), default));
        var fine = await s.W.ExpenseService.AddAsync(s.Car.Id, Input(Day.AddDays(2), odometer: 5100), default);
        var noOdometer = await s.W.ExpenseService.AddAsync(s.Car.Id, Input(Day.AddDays(2), "Wash", odometer: null), default);

        Assert.Equal("odometer.belowPrevious", error.Key);
        Assert.Equal((5100L, null), (fine.Odometer, noOdometer.Odometer));
        // and a later fuel log must fit the expense's reading too
        await Assert.ThrowsAsync<DomainException>(() => s.W.RefuelingService.LogAsync(s.Car.Id, new Application.Refuelings.RefuelingInput(Day.AddDays(5), 40, 60, "EUR", 5050, true, null), default));
    }

    [Fact]
    public async Task Update_CanChangeTheDetails_AndAddOrDropTheOdometer()
    {
        var s = await Setup();
        var expense = await s.W.ExpenseService.AddAsync(s.Car.Id, Input(odometer: null), default);

        var updated = await s.W.ExpenseService.UpdateAsync(expense.Id, new ExpenseInput(Day, "Brakes", "Service", 250, null, 1200, "front"), default);
        Assert.Equal(("Brakes", 250m, "EUR", 1200L), (updated.Title, updated.Amount, updated.Currency, updated.Odometer)); // currency kept when omitted

        var dropped = await s.W.ExpenseService.UpdateAsync(expense.Id, new ExpenseInput(Day, "Brakes", null, 250, null, null, null), default);
        Assert.Null(dropped.Odometer);
    }

    [Fact]
    public async Task TrashRestoreAndEmptyTrash_FollowTheDeleteLevels()
    {
        var s = await Setup();
        var expense = await s.W.ExpenseService.AddAsync(s.Car.Id, Input(), default);
        await s.W.Sharing.SetLogAccessAsync(s.Car.Id, s.Bob.Id, AccessLevel.Edit, default);

        s.W.Current.SignInAs(s.Bob);
        await s.W.ExpenseService.DeleteAsync(expense.Id, default); // Edit may trash
        Assert.Equal(1, await s.W.ExpenseService.CountTrashAsync(default));
        Assert.Equal(0, await s.W.ExpenseService.CountDeletableTrashAsync(default));
        Assert.Equal(0, await s.W.ExpenseService.EmptyTrashAsync(default)); // but not purge

        s.W.Current.SignInAs(s.Alice);
        Assert.Equal(1, await s.W.ExpenseService.CountDeletableTrashAsync(default));
        await s.W.ExpenseService.RestoreAsync(expense.Id, default);
        await s.W.ExpenseService.DeleteAsync(expense.Id, default);
        Assert.Equal(1, await s.W.ExpenseService.EmptyTrashAsync(default));
        Assert.Empty(s.W.Expenses.Items);
    }

    [Fact]
    public async Task ViewersAndStrangers_CannotChangeAnything()
    {
        var s = await Setup();
        var expense = await s.W.ExpenseService.AddAsync(s.Car.Id, Input(), default);

        s.W.Current.SignInAs(s.Bob);
        Assert.Null(await s.W.ExpenseService.FindAsync(expense.Id, default));
        Assert.Empty(await s.W.ExpenseService.ListAsync(s.Car.Id, new ExpenseQuery(), default));
        await Assert.ThrowsAsync<NotFoundException>(() => s.W.ExpenseService.AddAsync(s.Car.Id, Input(), default));
        await Assert.ThrowsAsync<NotFoundException>(() => s.W.ExpenseService.DeleteAsync(expense.Id, default));

        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.View));
        Assert.NotNull(await s.W.ExpenseService.FindAsync(expense.Id, default));
        await Assert.ThrowsAsync<ForbiddenException>(() => s.W.ExpenseService.UpdateAsync(expense.Id, Input(), default));
    }

    [Fact]
    public async Task FutureDates_AreRejected_AndTrashedExpensesCannotBeEdited()
    {
        var s = await Setup();
        var future = await Assert.ThrowsAsync<DomainException>(() => s.W.ExpenseService.AddAsync(s.Car.Id, Input(new DateOnly(2026, 12, 1)), default));
        var expense = await s.W.ExpenseService.AddAsync(s.Car.Id, Input(), default);
        await s.W.ExpenseService.DeleteAsync(expense.Id, default);

        Assert.Equal("refueling.dateInFuture", future.Key);
        await Assert.ThrowsAsync<NotFoundException>(() => s.W.ExpenseService.UpdateAsync(expense.Id, Input(), default)); // trashed rows are invisible to normal edits
    }
}
