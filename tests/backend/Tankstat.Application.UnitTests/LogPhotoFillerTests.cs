using Tankstat.Domain.Photos;
using Tankstat.Application.Expenses;
using Tankstat.Application.Recognition;
using Tankstat.Application.Refuelings;
using Tankstat.Domain;
using Tankstat.Domain.Notifications;
using Tankstat.Domain.Recognition;
using Tankstat.Domain.Users;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.UnitTests;

/// <summary>Logs saved while their photo is being read, filled in once it was read (<see cref="LogPhotoFiller"/>).</summary>
public class LogPhotoFillerTests
{
    private static readonly DateOnly Day = new(2026, 9, 30);

    private static byte[] Jpeg(byte marker = 0) => [0xFF, 0xD8, 0xFF, 0xE0, marker, 1, 2, 3];

    private sealed record Scene(World W, User Alice, User Bob, Vehicle Car);

    private static async Task<Scene> Setup()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        var bob = w.AddUser("bob@x.co");
        w.Current.SignInAs(alice);
        var car = await w.VehicleService.AddAsync("Golf", null, FuelType.Petrol, default);
        return new Scene(w, alice, bob, car);
    }

    private static async Task<Guid> Queued(Scene s, ReadingPurpose purpose = ReadingPurpose.Refueling, byte marker = 0)
    {
        var id = await s.W.Drafts.UploadAsync(s.Car.Id, Jpeg(marker), default);
        await s.W.Recognition.QueueForDraftAsync(id, purpose, "en", default);
        return id;
    }

    private static RecognizedValue Read(ReadingFieldName name, string value, double confidence = 0.9, ValueSource source = ValueSource.Read) =>
        new(name, value, confidence, source);

    private static void Answer(Scene s, DocumentKind kind, params RecognizedValue[] values) =>
        s.W.Recognizer.Answer = _ => new RecognitionResult("fake-1", kind, values);

    private static Task<Refueling> LogWithoutOdometer(Scene s, params Guid[] photos) =>
        s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(Day, 40, 60, "EUR", null, true, null), default, photoDraftIds: photos);

    // ---- saving without values -----------------------------------------------------------------------------

    [Fact]
    public async Task ARefuelling_CanBeSavedWithoutOdometer_WhileItsPhotoIsBeingRead()
    {
        var s = await Setup();
        var photo = await Queued(s);

        var log = await LogWithoutOdometer(s, photo);

        Assert.Equal((ReviewState.AwaitingPhotos, (long?)null), (log.ReviewState, log.Odometer));
        Assert.Empty(s.W.Notifications.Items);
    }

    [Fact]
    public async Task WithoutAPhotoBeingRead_EveryValueIsRequired()
    {
        var s = await Setup();
        var notQueued = await s.W.Drafts.UploadAsync(s.Car.Id, Jpeg(), default);

        var noPhoto = await Assert.ThrowsAsync<DomainException>(() => LogWithoutOdometer(s));
        var unread = await Assert.ThrowsAsync<DomainException>(() => LogWithoutOdometer(s, notQueued));
        var noAmount = await Assert.ThrowsAsync<DomainException>(() =>
            s.W.ExpenseService.AddAsync(s.Car.Id, new ExpenseInput(Day, "Oil", null, null, "EUR", null, null), default));

        Assert.All(new[] { noPhoto, unread, noAmount }, e => Assert.Equal("log.valuesRequired", e.Key));
        Assert.Empty(s.W.Refuelings.Items);
    }

    [Fact]
    public async Task APhotoReadLongAgo_NoLongerLetsALogWait()
    {
        var s = await Setup();
        var photo = await Queued(s);
        Answer(s, DocumentKind.Unknown);
        await s.W.Processor.ProcessDueAsync(default);

        s.W.Clock.Advance(LogPhotoFiller.JustRead + TimeSpan.FromSeconds(1));

        Assert.Equal("log.valuesRequired", (await Assert.ThrowsAsync<DomainException>(() => LogWithoutOdometer(s, photo))).Key);
    }

    // ---- filling in ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheOdometerReadLater_IsFilledIn_TheLogAwaitsAReview_AndWhoLoggedItIsTold()
    {
        var s = await Setup();
        var photo = await Queued(s);
        var log = await LogWithoutOdometer(s, photo);
        Answer(s, DocumentKind.Odometer, Read(ReadingFieldName.Odometer, "315193", 0.72));

        await s.W.Processor.ProcessDueAsync(default);

        var filled = s.W.Refuelings.Items.Single(r => r.Id == log.Id);
        Assert.Equal((315193L, ReviewState.NeedsReview, LogValues.Odometer), (filled.Odometer, filled.ReviewState, filled.FilledFromPhoto));
        var told = Assert.Single(s.W.Notifications.Items);
        Assert.Equal((s.Alice.Id, NotificationKind.LogFilledFromPhoto, NotificationRef.Refueling(log.Id), NotificationRef.Vehicle(s.Car.Id)),
            (told.RecipientId, told.Kind, told.Subject, told.Context));
        Assert.Equal(("Golf", "2026-09-30", "ODOMETER"), (told.Args["vehicleName"], told.Args["date"], told.Args["values"]));
    }

    [Fact]
    public async Task ALogSavedMeanwhile_IsReadAgain_InsteadOfStoppingTheRound()
    {
        var s = await Setup();
        var log = await LogWithoutOdometer(s, await Queued(s));
        Answer(s, DocumentKind.Odometer, Read(ReadingFieldName.Odometer, "315193", 0.72));
        s.W.Refuelings.SavedMeanwhileOnNext = 1; // someone saved the log while it was filled

        await s.W.Processor.ProcessDueAsync(default); // does not throw

        Assert.Equal(315193L, s.W.Refuelings.Items.Single(r => r.Id == log.Id).Odometer); // the second try took it
    }

    [Fact]
    public async Task VolumeAndTotal_CanWaitForTheReceipt_WhichKeepsItsCurrency()
    {
        var s = await Setup();
        var receipt = await Queued(s);
        var log = await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(Day, null, null, "HUF", 1000, false, null), default, photoDraftIds: [receipt]);
        Answer(s, DocumentKind.FuelReceipt, Read(ReadingFieldName.Total, "61.40"), Read(ReadingFieldName.Volume, "31.52"), Read(ReadingFieldName.Currency, "EUR"));

        await s.W.Processor.ProcessDueAsync(default);

        var filled = s.W.Refuelings.Items.Single(r => r.Id == log.Id);
        Assert.Equal((31.52m, 61.40m, "EUR"), (filled.Volume, filled.TotalCost, filled.Currency)); // the receipt's own currency
        Assert.Equal((ReviewState.NeedsReview, LogValues.Volume | LogValues.Total), (filled.ReviewState, filled.FilledFromPhoto));
    }

    [Fact]
    public async Task AReceiptsDiscount_IsStoredWithTheTotalItWasTakenOff()
    {
        var s = await Setup();
        var receipt = await Queued(s);
        var log = await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(Day, null, null, "HUF", 1000, false, null), default, photoDraftIds: [receipt]);
        Answer(s, DocumentKind.FuelReceipt,
            Read(ReadingFieldName.Total, "34744"), Read(ReadingFieldName.Volume, "43"), Read(ReadingFieldName.UnitPrice, "818"), Read(ReadingFieldName.Discount, "-430"), Read(ReadingFieldName.Currency, "HUF"));

        await s.W.Processor.ProcessDueAsync(default);

        var filled = s.W.Refuelings.Items.Single(r => r.Id == log.Id);
        Assert.Equal((43m, 34744m, 430m, 808m, 818m), (filled.Volume, filled.TotalCost, filled.Discount, filled.PricePerUnit, filled.PricePerUnitBeforeDiscount)); // the receipt of #204
        Assert.Equal(LogValues.Volume | LogValues.Total | LogValues.Discount, filled.FilledFromPhoto);
    }

    [Fact]
    public async Task TheDiscount_ComesFromThePhotoThatGaveTheTotal_NeverFromAnother()
    {
        var s = await Setup();
        var after = await Queued(s, marker: 1); // the receipt read after its discount
        var before = await Queued(s, marker: 2); // another shot of it, read on the fuel line before the discount, and surer
        s.W.Recognizer.Answer = r => r.ReadingId == after
            ? new RecognitionResult("fake-1", DocumentKind.FuelReceipt, [Read(ReadingFieldName.Total, "34744"), Read(ReadingFieldName.Discount, "430"), Read(ReadingFieldName.Currency, "HUF")])
            : new RecognitionResult("fake-1", DocumentKind.FuelReceipt, [Read(ReadingFieldName.Total, "35174", 0.95), Read(ReadingFieldName.Currency, "HUF")]);
        var log = await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(Day, 43, null, "HUF", 1000, true, null), default, photoDraftIds: [after, before]);

        await s.W.Processor.ProcessDueAsync(default);

        var filled = s.W.Refuelings.Items.Single(r => r.Id == log.Id);
        Assert.Equal(35174m, filled.TotalCost); // the surest total
        Assert.Null(filled.Discount); // its photo shows none: the other photo's 430 was taken off another total
    }

    [Fact]
    public async Task ADiscountOnAPhoto_NeverChangesACostAPersonEntered()
    {
        var s = await Setup();
        var receipt = await Queued(s);
        var log = await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(Day, 43, 34744, "HUF", null, false, null), default, photoDraftIds: [receipt]);
        Answer(s, DocumentKind.FuelReceipt, Read(ReadingFieldName.Total, "34744"), Read(ReadingFieldName.Discount, "430"), Read(ReadingFieldName.Currency, "HUF"));

        await s.W.Processor.ProcessDueAsync(default);

        var filled = s.W.Refuelings.Items.Single(r => r.Id == log.Id);
        Assert.Null(filled.Discount); // what they entered, an empty discount among it, is theirs: the dialog offers the photo's
        Assert.False(filled.FilledFromPhoto.HasFlag(LogValues.Discount));
    }

    [Fact]
    public async Task ATotalWithoutALegibleCurrency_IsLeftForThePerson_NeverStoredInTheUsualCurrency()
    {
        var s = await Setup();
        var receipt = await Queued(s);
        var log = await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(Day, null, null, "HUF", 1000, false, null), default, photoDraftIds: [receipt]);
        Answer(s, DocumentKind.FuelReceipt, Read(ReadingFieldName.Total, "61.40"), Read(ReadingFieldName.Volume, "31.52")); // a foreign receipt, no currency on it

        await s.W.Processor.ProcessDueAsync(default);

        var filled = s.W.Refuelings.Items.Single(r => r.Id == log.Id);
        Assert.Equal((31.52m, (decimal?)null, (string?)null), (filled.Volume, filled.TotalCost, filled.Currency)); // not 61.40 HUF
        Assert.Equal((ReviewState.Incomplete, LogValues.Volume), (filled.ReviewState, filled.FilledFromPhoto));
    }

    [Fact]
    public async Task OnlyValuesTheProviderIsSureOf_AreTaken_AndTheLogIsIncompleteWithoutThem()
    {
        var s = await Setup();
        var photo = await Queued(s);
        var log = await LogWithoutOdometer(s, photo);
        Answer(s, DocumentKind.Odometer, Read(ReadingFieldName.Odometer, "2", 0.56), Read(ReadingFieldName.Odometer, "1234", 0.99, ValueSource.Hint));

        await s.W.Processor.ProcessDueAsync(default);

        var left = s.W.Refuelings.Items.Single(r => r.Id == log.Id);
        Assert.Equal((null, ReviewState.Incomplete), (left.Odometer, left.ReviewState));
        var told = Assert.Single(s.W.Notifications.Items);
        Assert.Equal((NotificationKind.LogNotFilled, "ODOMETER"), (told.Kind, told.Args["values"]));
    }

    [Fact]
    public async Task APhotoThatCannotBeRead_LeavesTheLogIncomplete()
    {
        var s = await Setup();
        var photo = await Queued(s);
        var log = await LogWithoutOdometer(s, photo);
        s.W.Recognizer.Answer = _ => throw new RecognitionRejectedException("not a picture");

        await s.W.Processor.ProcessDueAsync(default);

        Assert.Equal(ReviewState.Incomplete, s.W.Refuelings.Items.Single(r => r.Id == log.Id).ReviewState);
    }

    [Fact]
    public async Task AnOdometerThatDoesNotFitTheOtherReadings_IsStillFilledIn_ForThePersonToCheck()
    {
        var s = await Setup();
        await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(Day.AddDays(-5), 40, 60, "EUR", 50_000, true, null), default);
        var photo = await Queued(s);
        var log = await LogWithoutOdometer(s, photo);
        Answer(s, DocumentKind.Odometer, Read(ReadingFieldName.Odometer, "4000"));

        await s.W.Processor.ProcessDueAsync(default);

        var filled = s.W.Refuelings.Items.Single(r => r.Id == log.Id);
        Assert.Equal((4000L, ReviewState.NeedsReview), (filled.Odometer, filled.ReviewState));
    }

    [Fact]
    public async Task TheLogWaitsForEveryPhoto_AndTakesTheBestValueOfAll()
    {
        var s = await Setup();
        var dashboard = await Queued(s, marker: 1);
        var receipt = await Queued(s, marker: 2);
        var log = await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(Day, 40, null, "EUR", null, true, null), default, photoDraftIds: [dashboard, receipt]);
        s.W.RecognitionOptions.MaxConcurrent = 1;
        Answer(s, DocumentKind.Odometer, Read(ReadingFieldName.Odometer, "20500"));

        await s.W.Processor.ProcessDueAsync(default);
        var halfway = s.W.Refuelings.Items.Single(r => r.Id == log.Id);
        Assert.Equal((20500L, ReviewState.AwaitingPhotos), (halfway.Odometer, halfway.ReviewState)); // filled already, still waiting for the total
        Assert.Empty(s.W.Notifications.Items);

        Answer(s, DocumentKind.FuelReceipt, Read(ReadingFieldName.Total, "61.20"), Read(ReadingFieldName.Currency, "EUR"));
        await s.W.Processor.ProcessDueAsync(default);

        var done = s.W.Refuelings.Items.Single(r => r.Id == log.Id);
        Assert.Equal((61.20m, ReviewState.NeedsReview, LogValues.Odometer | LogValues.Total), (done.TotalCost, done.ReviewState, done.FilledFromPhoto));
        Assert.Single(s.W.Notifications.Items);
    }

    [Fact]
    public async Task AReadingThatFinishedBeforeTheSave_IsTakenRightAway()
    {
        var s = await Setup();
        var photo = await Queued(s);
        Answer(s, DocumentKind.Odometer, Read(ReadingFieldName.Odometer, "7777"));
        await s.W.Processor.ProcessDueAsync(default); // the dialog had not seen it yet

        var log = await LogWithoutOdometer(s, photo);

        Assert.Equal((7777L, ReviewState.NeedsReview), (log.Odometer, log.ReviewState));
    }

    [Fact]
    public async Task ValuesAPersonEntered_AreNeverReplaced()
    {
        var s = await Setup();
        var photo = await Queued(s);
        var log = await LogWithoutOdometer(s, photo);
        Answer(s, DocumentKind.FuelReceipt, Read(ReadingFieldName.Volume, "99"), Read(ReadingFieldName.Total, "99"));

        await s.W.Processor.ProcessDueAsync(default);

        var left = s.W.Refuelings.Items.Single(r => r.Id == log.Id);
        Assert.Equal((40m, 60m, "EUR", ReviewState.Incomplete), (left.Volume, left.TotalCost, left.Currency, left.ReviewState));
    }

    [Fact]
    public async Task ADraftsReading_FillsNothing_UntilItBelongsToALog()
    {
        var s = await Setup();
        await Queued(s);
        Answer(s, DocumentKind.Odometer, Read(ReadingFieldName.Odometer, "1000"));

        Assert.Single(await s.W.Processor.ProcessDueAsync(default));

        Assert.Empty(s.W.Refuelings.Items);
        Assert.Empty(s.W.Notifications.Items);
    }

    // ---- consumption and expenses --------------------------------------------------------------------------

    [Fact]
    public async Task FillingInTheOdometer_RecalculatesConsumption()
    {
        var s = await Setup();
        await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(Day.AddDays(-7), 40, 60, "EUR", 10_000, true, null), default);
        var photo = await Queued(s);
        var log = await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(Day, 30, 45, "EUR", null, true, null), default, photoDraftIds: [photo]);
        Assert.Null(log.Consumption);
        Answer(s, DocumentKind.Odometer, Read(ReadingFieldName.Odometer, "10500"));

        await s.W.Processor.ProcessDueAsync(default);

        Assert.Equal(6m, s.W.Refuelings.Items.Single(r => r.Id == log.Id).Consumption);
    }

    [Fact]
    public async Task AnExpense_GetsItsAmountAndOdometerFromThePhoto()
    {
        var s = await Setup();
        var photo = await Queued(s, ReadingPurpose.Expense);
        var expense = await s.W.ExpenseService.AddAsync(s.Car.Id, new ExpenseInput(Day, "Service", null, null, "EUR", null, null), default, [photo]);
        Assert.Equal(ReviewState.AwaitingPhotos, expense.ReviewState);
        Answer(s, DocumentKind.ExpenseReceipt, Read(ReadingFieldName.Total, "245.00"), Read(ReadingFieldName.Currency, "EUR"));

        await s.W.Processor.ProcessDueAsync(default);

        var filled = s.W.Expenses.Items.Single(e => e.Id == expense.Id);
        Assert.Equal((245m, "EUR", ReviewState.NeedsReview, LogValues.Total), (filled.Amount, filled.Currency, filled.ReviewState, filled.FilledFromPhoto));
        var told = Assert.Single(s.W.Notifications.Items);
        Assert.Equal((NotificationRef.Expense(expense.Id), "Service"), (told.Subject, told.Args["title"]));
    }

    [Fact]
    public async Task AnExpenseReceiptsDiscount_IsStoredWithTheAmountThePhotoGives()
    {
        var s = await Setup();
        var photo = await Queued(s, ReadingPurpose.Expense);
        var expense = await s.W.ExpenseService.AddAsync(s.Car.Id, new ExpenseInput(Day, "Tyres", null, null, "EUR", null, null), default, [photo]);
        Answer(s, DocumentKind.ExpenseReceipt, Read(ReadingFieldName.Total, "245.00"), Read(ReadingFieldName.Discount, "-30"), Read(ReadingFieldName.Currency, "EUR"));

        await s.W.Processor.ProcessDueAsync(default);

        var filled = s.W.Expenses.Items.Single(e => e.Id == expense.Id);
        Assert.Equal((245m, 30m, LogValues.Total | LogValues.Discount), (filled.Amount, filled.Discount, filled.FilledFromPhoto));
    }

    [Fact]
    public async Task AnExpenseWithItsAmount_IsDoneWithoutNotice_WhenThePhotoShowsNoOdometer()
    {
        var s = await Setup();
        var photo = await Queued(s, ReadingPurpose.Expense);
        var expense = await s.W.ExpenseService.AddAsync(s.Car.Id, new ExpenseInput(Day, "Wash", null, 12, "EUR", null, null), default, [photo]);
        Answer(s, DocumentKind.ExpenseReceipt, Read(ReadingFieldName.Total, "12.00"));

        await s.W.Processor.ProcessDueAsync(default);

        Assert.Equal(ReviewState.None, s.W.Expenses.Items.Single(e => e.Id == expense.Id).ReviewState);
        Assert.Empty(s.W.Notifications.Items);
    }

    // ---- the review ----------------------------------------------------------------------------------------

    [Fact]
    public async Task SavingTheLog_FinishesTheReview_AndMarksWhatTheyWereToldRead()
    {
        var s = await Setup();
        s.W.Grants.Items.Add(Domain.Access.AccessGrant.Create(s.Alice.Id, s.Bob.Id, Domain.Access.AccessLevel.Edit));
        var photo = await Queued(s);
        var log = await LogWithoutOdometer(s, photo);
        Answer(s, DocumentKind.Odometer, Read(ReadingFieldName.Odometer, "4000"));
        await s.W.Processor.ProcessDueAsync(default);
        s.W.Current.SignInAs(s.Bob); // anyone who may edit the log can check it

        var saved = await s.W.RefuelingService.UpdateAsync(log.Id, new RefuelingInput(Day, 40, 60, null, 4100, true, null), default);

        Assert.Equal((4100L, ReviewState.None, LogValues.None), (saved.Odometer, saved.ReviewState, saved.FilledFromPhoto));
        Assert.NotNull(Assert.Single(s.W.Notifications.Items).ReadAt);
    }

    [Fact]
    public async Task ALogInTheTrash_IsFilledInWhenItIsRestored()
    {
        var s = await Setup();
        var photo = await Queued(s);
        var log = await LogWithoutOdometer(s, photo);
        await s.W.RefuelingService.DeleteAsync(log.Id, default);
        Answer(s, DocumentKind.Odometer, Read(ReadingFieldName.Odometer, "4000"));
        await s.W.Processor.ProcessDueAsync(default);
        Assert.Null(s.W.Refuelings.Items.Single(r => r.Id == log.Id).Odometer);

        var restored = await s.W.RefuelingService.RestoreAsync(log.Id, default);

        Assert.Equal((4000L, ReviewState.NeedsReview), (restored.Odometer, restored.ReviewState));
    }

    // ---- photos added to a saved log (its edit dialog) -----------------------------------------------------

    private static async Task<Guid> AddedAndQueued(Scene s, LogType type, Guid logId, byte marker = 9)
    {
        var image = await s.W.Photos.AddAsync(type, logId, Jpeg(marker), default);
        await s.W.Recognition.QueueForLogPhotoAsync(type, logId, image, "hu", default);
        return image;
    }

    [Fact]
    public async Task APhotoAddedToASavedLog_IsQueuedWithTheHintsOfThatLog()
    {
        var s = await Setup();
        await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(new DateOnly(2026, 8, 1), 40, 60, "HUF", 10000, true, null), default);
        await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(new DateOnly(2026, 9, 1), 40, 60, "HUF", 12000, true, null), default);
        var expense = await s.W.ExpenseService.AddAsync(s.Car.Id, new ExpenseInput(new DateOnly(2026, 8, 15), "Tyres", null, 300, "EUR", null, null), default);

        var image = await AddedAndQueued(s, LogType.Expense, expense.Id);

        var queued = Assert.Single(s.W.Readings.Items);
        Assert.Equal((image, ReadingPurpose.Expense, "hu"), (queued.Id, queued.Purpose, queued.Locale));
        Assert.Equal(10000L, queued.LastOdometer); // the reading before the log's day, not the vehicle's latest (12000)
        Assert.Equal("EUR", queued.Currency); // the log's own currency
    }

    [Fact]
    public async Task OnlyTheOneWhoAddedThePhoto_ToThatLog_QueuesIt()
    {
        var s = await Setup();
        var expense = await s.W.ExpenseService.AddAsync(s.Car.Id, new ExpenseInput(Day, "Tyres", null, 300, "EUR", null, null), default);
        var other = await s.W.ExpenseService.AddAsync(s.Car.Id, new ExpenseInput(Day, "Wash", null, 10, "EUR", null, null), default);
        var image = await s.W.Photos.AddAsync(LogType.Expense, expense.Id, Jpeg(), default);

        await s.W.Recognition.QueueForLogPhotoAsync(LogType.Expense, other.Id, image, "en", default); // not that log's photo
        await s.W.Recognition.QueueForLogPhotoAsync(LogType.Refueling, expense.Id, image, "en", default); // not that kind
        s.W.Current.SignInAs(s.Bob);
        await s.W.Recognition.QueueForLogPhotoAsync(LogType.Expense, expense.Id, image, "en", default); // not Bob's

        Assert.Empty(s.W.Readings.Items);
    }

    [Fact]
    public async Task ASavedExpense_CanHaveItsAmountEmptied_WhileANewPhotoIsRead_WhichFillsItIn()
    {
        var s = await Setup();
        var expense = await s.W.ExpenseService.AddAsync(s.Car.Id, new ExpenseInput(Day, "Service", null, 100, "EUR", 1000, null), default);
        await AddedAndQueued(s, LogType.Expense, expense.Id);

        var saved = await s.W.ExpenseService.UpdateAsync(expense.Id, new ExpenseInput(Day, "Service", null, null, "EUR", 1000, null), default);
        Assert.Equal((ReviewState.AwaitingPhotos, (decimal?)null), (saved.ReviewState, saved.Amount));

        Answer(s, DocumentKind.ExpenseReceipt, Read(ReadingFieldName.Total, "245.50"), Read(ReadingFieldName.Currency, "EUR"));
        await s.W.Processor.ProcessDueAsync(default);

        var filled = s.W.Expenses.Items.Single(e => e.Id == expense.Id);
        Assert.Equal((245.50m, "EUR", ReviewState.NeedsReview), (filled.Amount, filled.Currency, filled.ReviewState));
    }

    [Fact]
    public async Task ANewPhotoReadJustBeforeTheSave_LetsTheValueBeEmptied_AndIsTakenRightAway()
    {
        var s = await Setup();
        var expense = await s.W.ExpenseService.AddAsync(s.Car.Id, new ExpenseInput(Day, "Service", null, 100, "EUR", 1000, null), default);
        await AddedAndQueued(s, LogType.Expense, expense.Id);
        Answer(s, DocumentKind.ExpenseReceipt, Read(ReadingFieldName.Total, "245.50"), Read(ReadingFieldName.Currency, "EUR"));
        await s.W.Processor.ProcessDueAsync(default); // read while the log did not wait: nothing to fill yet

        var saved = await s.W.ExpenseService.UpdateAsync(expense.Id, new ExpenseInput(Day, "Service", null, null, "EUR", 1000, null), default);

        Assert.Equal((245.50m, ReviewState.NeedsReview), (saved.Amount, saved.ReviewState));
    }

    [Fact]
    public async Task ASavedRefuelling_WithoutANewPhotoBeingRead_StillNeedsEveryValue()
    {
        var s = await Setup();
        var log = await s.W.RefuelingService.LogAsync(s.Car.Id, new RefuelingInput(Day, 40, 60, "EUR", 1000, true, null), default);
        await s.W.Photos.AddAsync(LogType.Refueling, log.Id, Jpeg(), default); // added without reading

        var refused = await Assert.ThrowsAsync<DomainException>(() =>
            s.W.RefuelingService.UpdateAsync(log.Id, new RefuelingInput(Day, 40, null, "EUR", 1000, true, null), default));

        Assert.Equal("log.valuesRequired", refused.Key);
    }
}

