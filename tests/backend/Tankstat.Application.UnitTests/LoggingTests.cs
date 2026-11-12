using Tankstat.Domain.Tyres;
using Tankstat.Application.Tyres;
using System.Text;
using Microsoft.Extensions.Logging;
using Tankstat.Application.Auth;
using Tankstat.Application.Expenses;
using Tankstat.Application.Imports;
using Tankstat.Application.Recognition;
using Tankstat.Application.Refuelings;
using Tankstat.Application.Recurring;
using Tankstat.Application.Stats;
using Tankstat.Application.Users;
using Tankstat.Application.Vehicles;
using Tankstat.Domain;
using Tankstat.Domain.Access;
using Tankstat.Domain.Charts;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Photos;
using Tankstat.Domain.Recognition;
using Tankstat.Domain.Recurring;
using Tankstat.Domain.Users;
using Tankstat.Domain.Vehicles;
using Tankstat.TestSupport;

namespace Tankstat.Application.UnitTests;

/// <summary>
/// What the application says about itself in its log: not the wording of every line, but what an operator relies on. Notable events show at
/// the levels people filter on, failures that used to vanish leave a Warning, a lasting condition is reported when it changes and not on
/// every check, and nothing a person typed (and no secret) ever shows up in a line. Lines are found by their class and level and read
/// through the values of their placeholders, so rewording a message does not break a test. (Sign-ins and administration:
/// <see cref="AccountLoggingTests"/>.)
/// </summary>
public class LoggingTests
{
    private static readonly DateOnly Day = new(2026, 9, 1);

    private static byte[] Jpeg(byte marker = 0) => [0xFF, 0xD8, 0xFF, 0xE0, marker, 1, 2, 3];

    private static ChartConfig Config() => new(ChartMetric.TotalSpend, ChartGrouping.Month, ChartKind.Bar, ChartRange.Last6Months, false);

    private static RecurringExpenseInput Insurance(string title = "Insurance") => new(title, null, null, RecurrenceKind.Time, 12, null, Day, null, null, null);

    private static async Task<(World W, User Alice, Vehicle Car)> SignedInOwner()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        w.Current.SignInAs(alice);
        var car = await w.VehicleService.AddAsync("Car", null, FuelType.Petrol, default);
        return (w, alice, car);
    }

    // ---- what is worth a line at Information, and what is not --------------------------------------------------

    [Fact]
    public async Task AnImportAndEveryPurge_AreInformation_WithWhatTheyDid()
    {
        var (w, alice, car) = await SignedInOwner();
        var upload = await w.Imports.UploadAsync("fuelio", new MemoryStream(Encoding.UTF8.GetBytes(FuelioSample.Csv)), default);
        var result = await w.Imports.CommitAsync(upload.Token, new ImportTarget(car.Id, null), new ImportOptions("EUR", false), default);
        foreach (var fill in w.Refuelings.Items.ToList()) await w.RefuelingService.DeleteAsync(fill.Id, default);
        foreach (var cost in w.Expenses.Items.ToList()) await w.ExpenseService.DeleteAsync(cost.Id, default);
        var fills = await w.RefuelingService.EmptyTrashAsync(default);
        var costs = await w.ExpenseService.EmptyTrashAsync(default);
        await w.VehicleService.DeleteAsync(car.Id, default);
        await w.VehicleService.EmptyTrashAsync(default);

        var information = w.Log.From("Tankstat").Where(e => e.Level >= LogLevel.Information).ToList();
        Assert.Equal(5, information.Count); // upload, commit, three purges
        Assert.All(information, e => Assert.Equal(alice.Id, e.Values["UserId"]));
        var commit = Assert.Single(w.Log.From<ImportService>(), e => e.Values.ContainsKey("NewVehicle"));
        Assert.Equal<object?[]>([car.Id, false, result.FuelImported, result.ExpensesImported, result.RecurringImported],
            [commit.Values["VehicleId"], commit.Values["NewVehicle"], commit.Values["Fuel"], commit.Values["Expenses"], commit.Values["Recurring"]]);
        Assert.Equal(fills, Assert.Single(w.Log.From<RefuelingService>(), e => e.Level == LogLevel.Information).Values["Count"]);
        Assert.Equal(costs, Assert.Single(w.Log.From<ExpenseService>(), e => e.Level == LogLevel.Information).Values["Count"]);
        var vehicles = Assert.Single(w.Log.From<VehicleService>(), e => e.Level == LogLevel.Information);
        Assert.Equal(1, vehicles.Values["Count"]);
        Assert.Contains(car.Id.ToString(), (string)vehicles.Values["VehicleIds"]!); // what was deleted for good is on record
    }

    [Fact]
    public async Task APurgeOfManyVehicles_IsStillOneShortLine()
    {
        var (w, _, _) = await SignedInOwner();
        for (var i = 0; i < 24; i++) await w.VehicleService.AddAsync($"Car {i}", null, FuelType.Petrol, default);
        foreach (var car in w.Vehicles.Items.ToList()) await w.VehicleService.DeleteAsync(car.Id, default);

        await w.VehicleService.EmptyTrashAsync(default);

        var line = Assert.Single(w.Log.From<VehicleService>(), e => e.Level == LogLevel.Information);
        Assert.Equal(25, line.Values["Count"]);
        var listed = (string)line.Values["VehicleIds"]!;
        Assert.Equal(20, listed.Split(", ").Length); // the first twenty ids...
        Assert.EndsWith(" and 5 more", listed);      // ...and how many more there were
    }

    [Fact]
    public async Task EmptyingATrashThatIsEmpty_SaysNothing()
    {
        var (w, _, _) = await SignedInOwner();

        await w.VehicleService.EmptyTrashAsync(default);
        await w.RefuelingService.EmptyTrashAsync(default);
        await w.ExpenseService.EmptyTrashAsync(default);

        Assert.Empty(w.Log.AtLeast(LogLevel.Information));
    }

    [Fact]
    public async Task OrdinaryChanges_AreOnlyDebug()
    {
        var (w, _, car) = await SignedInOwner();

        await w.VehicleService.UpdateAsync(car.Id, "Car 2", "AB-2", FuelType.Diesel, default);
        var fill = await w.RefuelingService.LogAsync(car.Id, new RefuelingInput(Day, 40, 60, "EUR", 1000, true, "note"), default);
        await w.RefuelingService.UpdateAsync(fill.Id, new RefuelingInput(Day, 41, 61, "EUR", 1000, true, null), default);
        await w.RefuelingService.DeleteAsync(fill.Id, default);
        await w.RefuelingService.RestoreAsync(fill.Id, default);
        var cost = await w.ExpenseService.AddAsync(car.Id, new ExpenseInput(Day.AddDays(1), "Oil", "Service", 100, "EUR", 1100, null), default);
        await w.ExpenseService.UpdateAsync(cost.Id, new ExpenseInput(Day.AddDays(1), "Oil 2", "Service", 101, "EUR", 1100, null), default);
        await w.ExpenseService.DeleteAsync(cost.Id, default);
        await w.ExpenseService.RestoreAsync(cost.Id, default);
        var schedule = (await w.RecurringService.AddAsync(car.Id, Insurance(), default)).Item;
        await w.RecurringService.UpdateAsync(schedule.Id, Insurance("Insurance 2"), default);
        await w.RecurringService.MarkDoneAsync([schedule.Id], new MarkDoneInput(Day.AddDays(10), null, 200, "EUR"), default);
        await w.RecurringService.DeleteAsync(schedule.Id, default);
        var chart = await w.ChartService.CreateAsync(car.Id, new ChartInput("Spend", Config(), false), default);
        await w.ChartService.UpdateAsync(chart.Id, new ChartInput("Spend 2", Config(), false), default);
        await w.ChartService.DeleteAsync(chart.Id, default);
        var draft = await w.Drafts.UploadAsync(car.Id, Jpeg(1), default);
        await w.Drafts.RemoveAsync(draft, default);
        var photo = await w.Photos.AddAsync(LogType.Expense, cost.Id, Jpeg(2), default);
        await w.Photos.RemoveAsync(LogType.Expense, cost.Id, photo, default);
        await w.ImageService.SetAvatarAsync(Jpeg(3), default);
        await w.ImageService.RemoveAvatarAsync(default);
        await w.ImageService.SetVehiclePictureAsync(car.Id, Jpeg(4), default);
        await w.ImageService.RemoveVehiclePictureAsync(car.Id, default);
        await w.VehicleService.DeleteAsync(car.Id, default);
        await w.VehicleService.RestoreAsync(car.Id, default);

        var lines = w.Log.From("Tankstat.Application").ToList();
        Assert.True(lines.Count > 25, "every change above should leave a line");
        Assert.All(lines, l => Assert.True(l.Level == LogLevel.Debug, $"{l.Level}: {l.Message}"));
    }

    // ---- failures that used to vanish -------------------------------------------------------------------------

    [Fact]
    public async Task AFolderThatCannotBeRemoved_IsAWarning_NotSilence()
    {
        var (w, _, car) = await SignedInOwner();
        await w.VehicleService.DeleteAsync(car.Id, default);
        w.ImageStore.FailFolderDeletes = true;

        var purged = await w.VehicleService.EmptyTrashAsync(default);

        Assert.Equal(1, purged); // the purge itself went through
        var warning = Assert.Single(w.Log.Entries, e => e.Level == LogLevel.Warning);
        Assert.IsType<IOException>(warning.Exception);
        Assert.Equal($"vehicles/{car.Id:N}", warning.Values["Folder"]);
    }

    [Fact]
    public async Task APhotoThatCannotBeAttached_IsAWarning_AndTheSaveStillGoesThrough()
    {
        var (w, _, car) = await SignedInOwner();
        var draft = await w.Drafts.UploadAsync(car.Id, Jpeg(), default);
        w.ImageStore.FailMoves = true;

        var fill = await w.RefuelingService.LogAsync(car.Id, new RefuelingInput(Day, 40, 60, "EUR", 1000, true, null), default, photoDraftIds: [draft]);

        var warning = Assert.Single(w.Log.Entries, e => e.Level == LogLevel.Warning);
        Assert.IsType<IOException>(warning.Exception);
        Assert.Equal<object?[]>([draft, fill.Id], [warning.Values["DraftId"], warning.Values["LogId"]]);
    }

    [Fact]
    public async Task ARecurringExpenseThatCannotMoveOn_SaysWhereItsExpenseWent()
    {
        var (w, _, car) = await SignedInOwner();
        var schedule = (await w.RecurringService.AddAsync(car.Id, Insurance(), default)).Item;
        w.Recurring.FailUpdateWith = new InvalidOperationException("database down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => w.RecurringService.MarkDoneAsync([schedule.Id], new MarkDoneInput(Day, null, 100, "EUR"), default));

        var warning = Assert.Single(w.Log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal([schedule.Id], Assert.IsAssignableFrom<IEnumerable<Guid>>(warning.Values["RecurringIds"]));
        Assert.Equal(w.Expenses.Items.Single().Id, warning.Values["ExpenseId"]);
    }

    [Fact]
    public async Task AReadingCutShortByARestart_IsLogged()
    {
        var (w, _, car) = await SignedInOwner();
        var id = await w.Drafts.UploadAsync(car.Id, Jpeg(), default);
        await w.Recognition.QueueForDraftAsync(id, ReadingPurpose.Refueling, "hu", default);
        await w.Readings.ClaimAsync(id, w.Clock.GetUtcNow(), default); // the app stopped while reading it
        w.Clock.Advance(PhotoReadingProcessor.StaleAfter(w.RecognitionOptions) + TimeSpan.FromSeconds(1));

        await w.Processor.ProcessDueAsync(default);

        var line = Assert.Single(w.Log.From<PhotoReadingProcessor>());
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal<object?[]>([id, "queued again"], [line.Values["Id"], line.Values["Outcome"]]);
    }

    [Fact]
    public async Task ALogFilledInFromItsPhoto_IsADebugLine_NamingTheValuesButNeverWhatWasRead()
    {
        var (w, _, car) = await SignedInOwner();
        var id = await w.Drafts.UploadAsync(car.Id, Jpeg(), default);
        await w.Recognition.QueueForDraftAsync(id, ReadingPurpose.Refueling, "hu", default);
        var log = await w.RefuelingService.LogAsync(car.Id, new RefuelingInput(Day, 40, 60, "EUR", null, true, null), default, photoDraftIds: [id]);
        w.Recognizer.Answer = _ => new RecognitionResult("fake-1", DocumentKind.Odometer, [new(ReadingFieldName.Odometer, "987654", 0.9, ValueSource.Read)]);

        await w.Processor.ProcessDueAsync(default);

        var line = Assert.Single(w.Log.From<LogPhotoFiller>());
        Assert.Equal(LogLevel.Debug, line.Level);
        Assert.Equal<object?[]>([log.Id, car.Id, "ODOMETER", ReviewState.NeedsReview], [line.Values["RefuelingId"], line.Values["VehicleId"], line.Values["Values"], line.Values["ReviewState"]]);
        Assert.False(w.Log.Mentions("987654"));
    }

    // ---- a condition is reported when it changes, not on every check -------------------------------------------

    [Fact]
    public async Task TheProviderGoingAndComingBack_IsLoggedOnce_EachWay()
    {
        var w = new World();
        async Task Check(bool healthy)
        {
            w.Recognizer.Healthy = healthy;
            w.Clock.Advance(RecognitionAvailability.CacheFor + TimeSpan.FromSeconds(1)); // the kept answer is stale: the provider is asked again
            Assert.Equal(healthy, await w.Availability.IsAvailableAsync(default));
        }

        await Check(true);
        await Check(true);
        await Check(false);
        await Check(false);
        await Check(false);
        await Check(true);

        Assert.Equal([LogLevel.Information, LogLevel.Warning, LogLevel.Information], w.Log.Entries.Select(e => e.Level));
    }

    [Fact]
    public async Task AProviderThatCannotBeUsed_IsAWarning_WithTheCauseTheProviderGives()
    {
        var w = new World();
        w.Recognizer.HealthFailure = new RecognitionUnavailableException("The model server answered 503 when asked for its models.");

        Assert.False(await w.Availability.IsAvailableAsync(default));

        var warning = Assert.Single(w.Log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Same(w.Recognizer.HealthFailure, warning.Exception); // why, for whoever has to fix it
    }

    [Fact]
    public async Task AProviderThatStaysUnavailable_ForAnotherReason_IsAWarningAgain()
    {
        var w = new World();
        async Task Check(string? problem)
        {
            w.Recognizer.HealthFailure = problem is null ? null : new RecognitionUnavailableException(problem);
            w.Clock.Advance(RecognitionAvailability.CacheFor + TimeSpan.FromSeconds(1));
            await w.Availability.IsAvailableAsync(default);
        }

        await Check("The model server could not be reached."); // started before the server
        await Check("The model server could not be reached.");
        await Check("The model server does not list the model: Recognition:OpenAiCompatible:Model must be one of the names GET /v1/models shows."); // up now, but misnamed
        await Check("The model server does not list the model: Recognition:OpenAiCompatible:Model must be one of the names GET /v1/models shows.");
        await Check(null);

        Assert.Equal([LogLevel.Warning, LogLevel.Warning, LogLevel.Information], w.Log.Entries.Select(e => e.Level));
    }

    [Fact]
    public async Task WithoutAProvider_NothingIsSaidAboutIt()
    {
        var w = new World();
        w.Recognizer.Configured = false;

        Assert.False(await w.Availability.IsAvailableAsync(default));

        Assert.Empty(w.Log.Entries);
    }

    // ---- privacy ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task NothingAPersonTyped_AndNoSecret_EverReachesALogLine()
    {
        const string Mail = "canary.alice@canary-mail.example";
        var w = new World(smtp: true);
        var admin = w.AddUser("canary.admin@canary-mail.example", admin: true, password: "canary-admin-pass-1");
        var bob = w.AddUser("canary.bob@canary-mail.example", password: "canary-bob-pass-12");
        var canaries = new List<string>
        {
            "canary.admin@canary-mail.example", "canary-admin-pass-1", "canary.bob@canary-mail.example", "canary-bob-pass-12", Mail,
            "canary.ghost@canary-mail.example", "canary-guess-1", "canary-wrong-pass-1",
        };

        // Signing in, and failing to.
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => w.Auth.LoginAsync("canary.ghost@canary-mail.example", "canary-guess-1", default));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => w.Auth.LoginAsync("canary.admin@canary-mail.example", "canary-wrong-pass-1", default));
        await w.Auth.LoginAsync("canary.admin@canary-mail.example", "canary-admin-pass-1", default);
        w.Current.SignInAs(admin);

        // An administrator creates a user: the setup link and its token are secrets.
        var (alice, issued) = await w.UserService.CreateLocalAsync(Mail, "Canary Alice", false, default);
        await w.Auth.ResetPasswordAsync(issued.Token, "canary-alice-pass-1", "canary-alice-pass-1", default);
        canaries.AddRange(["Canary Alice", "canary-alice-pass-1", issued.Token, issued.Token.Split('.')[1], issued.Url!]);
        await w.UserService.UpdateAsync(alice.Id, "canary.renamed@canary-mail.example", "Canary Renamed", default);
        await w.UserService.SetAdminAsync(alice.Id, true, default);
        await w.UserService.SetAdminAsync(alice.Id, false, default);
        await w.UserService.SetDisabledAsync(alice.Id, true, default);
        await w.UserService.SetDisabledAsync(alice.Id, false, default);
        var reissued = await w.UserService.IssueResetAsync(alice.Id, default);
        canaries.AddRange(["canary.renamed@canary-mail.example", "Canary Renamed", reissued.Token, reissued.Url!]);
        w.Email.FailNext = 1; // the mail server is down: the link is kept for the administrator, and the Warning must not name the address
        var unsent = await w.UserService.IssueResetAsync(alice.Id, default);
        canaries.AddRange([unsent.Token, unsent.Url!]);

        // The user asks for a link herself, changes her password, and mistypes things.
        w.Current.SignInAs(alice);
        await w.Auth.RequestPasswordResetAsync("canary.renamed@canary-mail.example", default); // within the cool-down of the link above
        await w.ResetQueue.DrainAsync();
        w.Clock.Advance(TimeSpan.FromMinutes(10));
        w.Email.FailNext = 1; // her own request while the mail server is down: a Warning that must not name the address either
        await w.Auth.RequestPasswordResetAsync("canary.renamed@canary-mail.example", default);
        await w.ResetQueue.DrainAsync();
        await w.Auth.RequestPasswordResetAsync("canary.renamed@canary-mail.example", default);
        await w.ResetQueue.DrainAsync();
        foreach (var mail in w.Email.Sent) canaries.Add(mail.Body.Split("resetToken=")[1].Split('\n')[0]);
        await w.Auth.ChangePasswordAsync("canary-alice-pass-1", "canary-alice-pass-2", "canary-alice-pass-2", default);
        await Assert.ThrowsAsync<DomainException>(() => w.Auth.ChangePasswordAsync("canary-wrong-current-1", "canary-alice-pass-3", "canary-alice-pass-3", default));
        await Assert.ThrowsAsync<DomainException>(() => w.Auth.ResetPasswordAsync("canary-garbage-token", "canary-alice-pass-3", "canary-alice-pass-3", default));
        canaries.AddRange(["canary-alice-pass-2", "canary-wrong-current-1", "canary-alice-pass-3", "canary-garbage-token"]);

        // A user from an identity provider.
        await w.Auth.ProvisionExternalAsync(UserProvider.Oidc, "canary-subject-77", "canary.oidc@canary-mail.example", "Canary Oidc", default);
        await w.Auth.ProvisionExternalAsync(UserProvider.Oidc, "canary-subject-77", "canary.oidc@canary-mail.example", "Canary Oidc", default);
        canaries.AddRange(["canary-subject-77", "canary.oidc@canary-mail.example", "Canary Oidc"]);

        // Her data: a vehicle, its logs, schedule, chart, photos and pictures.
        var car = await w.VehicleService.AddAsync("Canary Car", "CANARY-001", FuelType.Petrol, default);
        await w.VehicleService.UpdateAsync(car.Id, "Canary Car Renamed", "CANARY-002", FuelType.Petrol, default);
        var fill = await w.RefuelingService.LogAsync(car.Id, new RefuelingInput(Day, 41.37m, 98765.43m, "EUR", 123456789, true, "canary refueling note"), default);
        await w.RefuelingService.UpdateAsync(fill.Id, new RefuelingInput(Day, 41.37m, 98765.43m, "EUR", 123456789, true, "canary refueling note 2"), default);
        var cost = await w.ExpenseService.AddAsync(car.Id, new ExpenseInput(Day.AddDays(1), "Canary expense title", "Canary expense category", 5555.55m, "EUR", 987654321, "canary expense note"), default);
        var schedule = (await w.RecurringService.AddAsync(car.Id, new RecurringExpenseInput("Canary schedule", "Canary schedule category", "canary schedule note", RecurrenceKind.Time, 12, null, Day, null, null, null), default)).Item;
        await w.RecurringService.MarkDoneAsync([schedule.Id], new MarkDoneInput(Day.AddDays(10), null, 4444.44m, "EUR"), default);
        await w.ChartService.CreateAsync(car.Id, new ChartInput("Canary chart title", Config(), false), default);
        var draft = await w.Drafts.UploadAsync(car.Id, Jpeg(1), default);
        var photo = await w.Photos.AddAsync(LogType.Expense, cost.Id, Jpeg(2), default);
        await w.Photos.RemoveAsync(LogType.Expense, cost.Id, photo, default);
        await w.Drafts.RemoveAsync(draft, default);
        await w.ImageService.SetAvatarAsync(Jpeg(3), default);
        await w.ImageService.SetVehiclePictureAsync(car.Id, Jpeg(4), default);
        canaries.AddRange(["Canary Car", "CANARY-001", "CANARY-002", "canary refueling note", "98765.43", "41.37", "123456789", "Canary expense title",
            "Canary expense category", "5555.55", "987654321", "canary expense note", "Canary schedule", "canary schedule note", "4444.44", "Canary chart title"]);

        // A tyre set, a swap with its fee and a line of the tyre log with an amount (#288): the set's name, the notes, titles, categories,
        // amounts and readings never reach a line.
        var tyreSet = Guid.NewGuid();
        await w.TyreService.AddSetAsync(new NewTyreSet(car.Id, new TyreSetInput("Canary Tyre Set", TyreSeason.Summer, "Canary Size", Day, null, null, 31415), true, Day, null, null),
            default, tyreSet, Guid.NewGuid());
        var tyreLine = Guid.NewGuid();
        await w.TyreService.AddEntryAsync(car.Id, new TyreEntryInput(TyreEntryKind.PunctureRepaired, tyreSet, Day.AddDays(2), 987654400, "canary tyre note",
            new TyreFee(2718.28m, "EUR", "Canary tyre category", "Canary tyre title", Guid.NewGuid())), default, tyreLine);
        await w.TyreService.UpdateEntryAsync(tyreLine, new TyreEntryInput(TyreEntryKind.ValveReplaced, tyreSet, Day.AddDays(2), 987654400, "canary tyre note 2",
            new TyreFee(1618.03m, "EUR", "Canary tyre category", "Canary tyre title", null)), default);
        await w.TyreService.TrashEntryAsync(tyreLine, default);
        await w.TyreService.RestoreEntryAsync(tyreLine, default);
        canaries.AddRange(["Canary Tyre Set", "Canary Size", "31415", "987654400", "canary tyre note", "Canary tyre category", "Canary tyre title", "2718.28", "1618.03"]);

        // Sharing, an import with a file full of somebody's life, and the access settings.
        await w.Sharing.SetLogAccessAsync(car.Id, bob.Id, AccessLevel.Edit, default);
        var upload = await w.Imports.UploadAsync("fuelio", new MemoryStream(Encoding.UTF8.GetBytes(FuelioSample.Csv)), default);
        await w.Imports.PreviewAsync(upload.Token, null, default);
        await w.Imports.CommitAsync(upload.Token, new ImportTarget(null, new NewVehicleSpec("Canary Import Car", "CANARY-IMP", FuelType.Diesel, MeasurementUnits.Metric)), new ImportOptions("EUR", false), default);
        canaries.AddRange(["Canary Import Car", "CANARY-IMP", "abc-123", "Holiday", "Oil change", "with filters", "Brake pads", "Tyres", "Garage"]);
        w.Current.SignInAs(admin);
        await w.AccessAdmin.SetDefaultLevelAsync(AccessLevel.View, default);
        await w.AccessAdmin.SetGrantAsync(alice.Id, bob.Id, AccessLevel.Edit, default);

        // Trash, purge, and finally the deletion of the user with everything they own.
        w.Current.SignInAs(alice);
        await w.RefuelingService.DeleteAsync(fill.Id, default);
        await w.ExpenseService.DeleteAsync(cost.Id, default);
        await w.RefuelingService.EmptyTrashAsync(default);
        await w.ExpenseService.EmptyTrashAsync(default);
        await w.VehicleService.DeleteAsync(car.Id, default);
        await w.VehicleService.EmptyTrashAsync(default);
        w.Current.SignInAs(admin);
        w.UserData.Owners.Add(alice.Id);
        await w.UserService.DeleteAsync(alice.Id, UserDataDisposition.Purge, null, default);

        Assert.True(w.Log.Entries.Count > 40, "the flow should have left plenty of lines to check");
        foreach (var canary in canaries) Assert.False(w.Log.Mentions(canary), $"a log line mentions '{canary}'");
    }
}
