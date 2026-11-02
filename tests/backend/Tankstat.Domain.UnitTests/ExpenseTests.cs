using Tankstat.Domain;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Domain.UnitTests;

public class ExpenseTests
{
    private static readonly DateOnly Day = new(2026, 9, 1);
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Car = Guid.NewGuid();

    private static Expense Make(string title = "Oil change", string? category = "Service", decimal amount = 100, long? odometer = 5000, string? note = null)
    {
        var cost = Cost.Create(Owner, Car, Day, amount, "EUR");
        var reading = odometer is { } v ? OdometerReading.Create(Owner, Car, Day, v) : null;
        return Expense.Create(Owner, Owner, Car, Day, title, category, cost, reading, note);
    }

    [Fact]
    public void Create_TrimsText_AndExposesTheLinkedValues()
    {
        var expense = Make(" Oil change ", " Service ", 99.5m, 5000, " with filter ");

        Assert.Equal(("Oil change", "Service", "with filter"), (expense.Title, expense.Category, expense.Note));
        Assert.Equal((99.5m, "EUR", 5000L), (expense.Amount, expense.Currency, expense.Odometer));
    }

    [Fact]
    public void Create_KeepsTheCostsDiscount_AndAnUpdateWithoutOneClearsIt()
    {
        var expense = Expense.Create(Owner, Owner, Car, Day, "Tyres", null, Cost.Create(Owner, Car, Day, 18800, "HUF", 1200), null);
        Assert.Equal((18800m, 1200m), (expense.Amount, expense.Discount)); // Create re-normalises the cost with its discount

        expense.Update(Day, "Tyres", null, 18800, "HUF", null, null, discount: 1500);
        Assert.Equal(1500m, expense.Discount);

        expense.Update(Day, "Tyres", null, 18800, "HUF", null, null);
        Assert.Null(expense.Discount);

        Assert.Equal("cost.discountWithoutAmount", Assert.Throws<DomainException>(() => expense.Update(Day, "Tyres", null, null, "HUF", null, null, readingPhotos: true, discount: 5)).Key);
    }

    [Fact]
    public void TheOdometerIsOptional()
    {
        var expense = Make(odometer: null);

        Assert.Null(expense.Odometer);
        Assert.Null(expense.OdometerReadingId);
    }

    [Fact]
    public void BlankCategoryAndNoteBecomeNull()
    {
        var expense = Make(category: " ", note: "");

        Assert.Equal((null, null), (expense.Category, expense.Note));
    }

    [Theory]
    [InlineData(" ", "expense.titleRequired")]
    [InlineData("TOOLONG", "expense.titleTooLong")]
    public void RejectsBadTitles(string title, string key)
    {
        if (title == "TOOLONG") title = new string('x', Expense.MaxTitleLength + 1);

        Assert.Equal(key, Assert.Throws<DomainException>(() => Make(title)).Key);
    }

    [Fact]
    public void RejectsLongCategoriesNotesAndNegativeAmounts()
    {
        Assert.Equal("expense.categoryTooLong", Assert.Throws<DomainException>(() => Make(category: new string('c', Expense.MaxCategoryLength + 1))).Key);
        Assert.Equal("expense.noteTooLong", Assert.Throws<DomainException>(() => Make(note: new string('n', Expense.MaxNoteLength + 1))).Key);
        Assert.Equal("cost.negative", Assert.Throws<DomainException>(() => Make(amount: -1)).Key);
    }

    [Fact]
    public void ACostOrReadingOfAnotherVehicleIsRejected()
    {
        var cost = Cost.Create(Owner, Guid.NewGuid(), Day, 1, "EUR");

        var error = Assert.Throws<DomainException>(() => Expense.Create(Owner, Owner, Car, Day, "x", null, cost, null));

        Assert.Equal("expense.wrongVehicle", error.Key);
    }

    [Fact]
    public void Update_ChangesEverything_AndKeepsTheReadingInStep()
    {
        var expense = Make();
        var other = new DateOnly(2026, 9, 5);

        var changes = expense.Update(other, "Tyres", "Maintenance", 400, "HUF", 5200, "four");

        Assert.Equal(LinkedChanges.None, changes);
        Assert.Equal((other, "Tyres", "Maintenance", 400m, "HUF", 5200L), (expense.Date, expense.Title, expense.Category, expense.Amount, expense.Currency, expense.Odometer));
        Assert.Equal(other, expense.OdometerReading!.Date);
        Assert.Equal(other, expense.Cost!.Date);
    }

    [Fact]
    public void Update_CanAddAndRemoveTheOdometerReading()
    {
        var expense = Make(odometer: null);

        var created = expense.Update(Day, "x", null, 1, "EUR", 777, null).CreatedReading;
        Assert.Equal((created, 777L), (expense.OdometerReading, expense.Odometer));
        Assert.Equal(created!.Id, expense.OdometerReadingId);

        var removed = expense.Update(Day, "x", null, 1, "EUR", null, null);
        Assert.Same(created, removed.RemovedReading);
        Assert.Null(removed.CreatedReading);
        Assert.Null(expense.OdometerReadingId);
    }

    [Fact]
    public void TrashAndRestore_CarryTheCostAndReading()
    {
        var expense = Make();
        var now = DateTimeOffset.UtcNow;

        expense.MarkDeleted(now);
        Assert.True(expense.IsDeleted && expense.Cost!.IsDeleted && expense.OdometerReading!.IsDeleted);
        Assert.Equal("expense.alreadyTrashed", Assert.Throws<DomainException>(() => expense.MarkDeleted(now)).Key);
        Assert.Equal("expense.trashedCannotEdit", Assert.Throws<DomainException>(() => expense.Update(Day, "x", null, 1, "EUR", null, null)).Key);

        expense.Restore();
        Assert.False(expense.IsDeleted || expense.Cost!.IsDeleted || expense.OdometerReading!.IsDeleted);
        Assert.Equal("expense.notTrashed", Assert.Throws<DomainException>(() => expense.Restore()).Key);
    }

    [Fact]
    public void TheAmountMayBeLeftEmpty_OnlyWhileAPhotoIsBeingRead_WhichMayAlsoFillInTheOdometer()
    {
        Assert.Equal("log.valuesRequired", Assert.Throws<DomainException>(() => Expense.Create(Owner, Owner, Car, Day, "Oil", null, null, null)).Key);

        var expense = Expense.Create(Owner, Owner, Car, Day, "Oil", null, null, null, readingPhotos: true);
        Assert.Equal((ReviewState.AwaitingPhotos, LogValues.Total, LogValues.Total | LogValues.Odometer), (expense.ReviewState, expense.Missing, expense.Fillable));

        expense.FillFromPhoto(new PhotoValues(Odometer: 4321, Volume: 5, Total: 12.5m, Currency: "EUR"));
        expense.FinishReading(readingPhotos: false);

        Assert.Equal((12.5m, "EUR", 4321L), (expense.Amount, expense.Currency, expense.Odometer));
        Assert.Equal((ReviewState.NeedsReview, LogValues.Total | LogValues.Odometer), (expense.ReviewState, expense.FilledFromPhoto));
    }

    [Fact]
    public void AnExpenseWithoutItsAmount_HasNoCurrency_GoesToTheTrashAndBack_AndGetsItsAmountFromAPerson()
    {
        var expense = Expense.Create(Owner, Owner, Car, Day, "Oil", null, null, null, readingPhotos: true);
        Assert.Equal((null, null), (expense.Amount, expense.Currency));

        expense.MarkDeleted(DateTimeOffset.UtcNow);
        expense.Restore();
        Assert.False(expense.IsDeleted);

        var changes = expense.Update(Day, "Oil", null, 45, "EUR", null, null);

        Assert.Same(expense.Cost, changes.CreatedCost);
        Assert.Equal((45m, "EUR", expense.Cost!.Id), (expense.Amount, expense.Currency, expense.CostId));
        Assert.Equal(ReviewState.None, expense.ReviewState);
        Assert.Equal(LinkedChanges.None, expense.FillFromPhoto(new PhotoValues(4321, null, 99, "EUR"))); // no longer waiting for a photo
        Assert.False(expense.FinishReading(readingPhotos: false)); // a reading that ends now changes nothing either
        Assert.Equal(ReviewState.None, expense.ReviewState);
        Assert.Equal((45m, (long?)null), (expense.Amount, expense.Odometer));
    }

    [Fact]
    public void APhotoNeverReplacesTheOdometerAPersonNoted_NorFillsAnExpenseInTheTrash()
    {
        var reading = OdometerReading.Create(Owner, Car, Day, 5000);
        var noted = Expense.Create(Owner, Owner, Car, Day, "Oil", null, null, reading, readingPhotos: true);
        var trashed = Expense.Create(Owner, Owner, Car, Day, "Oil", null, null, null, readingPhotos: true);
        trashed.MarkDeleted(DateTimeOffset.UtcNow);

        var filled = noted.FillFromPhoto(new PhotoValues(Odometer: 9999, Volume: null, Total: 20, Currency: "EUR"));
        var nothing = trashed.FillFromPhoto(new PhotoValues(Odometer: 9999, Volume: null, Total: 20, Currency: "EUR"));

        Assert.Equal((5000L, 20m, LogValues.Total), (noted.Odometer, noted.Amount, noted.FilledFromPhoto));
        Assert.Null(filled.CreatedReading);
        Assert.Equal(LinkedChanges.None, nothing);
        Assert.Null(trashed.Cost);
    }

    [Fact]
    public void ATitleIsRequired_EvenWhenNoneIsGivenAtAll() =>
        Assert.Equal("expense.titleRequired", Assert.Throws<DomainException>(() => Make(title: null!)).Key);

    [Fact]
    public void AnExpenseWithItsAmount_WaitsOnlyForAnOptionalOdometer_AndIsDoneWithoutOne()
    {
        var cost = Cost.Create(Owner, Car, Day, 30, "EUR");
        var expense = Expense.Create(Owner, Owner, Car, Day, "Wash", null, cost, null, readingPhotos: true);
        Assert.Equal(ReviewState.AwaitingPhotos, expense.ReviewState);

        expense.FillFromPhoto(new PhotoValues(null, null, 99, "EUR"));
        expense.FinishReading(readingPhotos: false);

        Assert.Equal((ReviewState.None, 30m), (expense.ReviewState, expense.Amount)); // nothing was filled in, nothing is missing
    }
}
