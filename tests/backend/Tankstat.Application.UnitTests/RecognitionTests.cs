using Microsoft.Extensions.Options;
using Tankstat.Application.Recognition;
using Tankstat.Domain.Access;
using Tankstat.Domain.Recognition;
using Tankstat.Domain.Users;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.UnitTests;

public class RecognitionTests
{
    private static byte[] Jpeg(byte marker = 0) => [0xFF, 0xD8, 0xFF, 0xE0, marker, 1, 2, 3];

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

    private static async Task<Guid> Queued(Scene s, ReadingPurpose purpose = ReadingPurpose.Refueling, byte marker = 0)
    {
        var id = await s.W.Drafts.UploadAsync(s.Car.Id, Jpeg(marker), default);
        await s.W.Recognition.QueueForDraftAsync(id, purpose, "hu", default);
        return id;
    }

    private static RecognizedValue Read(ReadingFieldName name, string value, double confidence = 0.9, ValueSource source = ValueSource.Read) =>
        new(name, value, confidence, source);

    // ---- queueing ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADraft_IsQueuedWithTheHintsTheChecksNeed_AndTheWorkerIsWoken()
    {
        var s = await Setup();
        await s.W.RefuelingService.LogAsync(s.Car.Id, new(new DateOnly(2026, 9, 20), 40, 60, "EUR", 12_345, true, null), default);
        var id = await s.W.Drafts.UploadAsync(s.Car.Id, Jpeg(), default);

        await s.W.Recognition.QueueForDraftAsync(id, ReadingPurpose.Refueling, "hu-HU", default);

        var reading = Assert.Single(s.W.Readings.Items);
        Assert.Equal((id, ReadingPurpose.Refueling, ReadingStatus.Queued, "hu", (long?)12_345, "EUR", new DateOnly(2026, 10, 1)),
            (reading.Id, reading.Purpose, reading.Status, reading.Locale, reading.LastOdometer, reading.Currency, reading.Today));
        Assert.Equal(1, s.W.Signal.Wakes);
    }

    [Fact]
    public async Task AVehicleWithoutLogs_GetsTheDefaultCurrency_AndAnUnknownLanguageCountsAsEnglish()
    {
        var s = await Setup();
        var id = await s.W.Drafts.UploadAsync(s.Car.Id, Jpeg(), default);

        await s.W.Recognition.QueueForDraftAsync(id, ReadingPurpose.Expense, "fr", default);

        var reading = Assert.Single(s.W.Readings.Items);
        Assert.Equal((ReadingPurpose.Expense, "en", (long?)null, "HUF"), (reading.Purpose, reading.Locale, reading.LastOdometer, reading.Currency));

        var unsaid = await s.W.Drafts.UploadAsync(s.Car.Id, Jpeg(), default);
        await s.W.Recognition.QueueForDraftAsync(unsaid, ReadingPurpose.Expense, null, default); // a client that names no language
        Assert.Equal("en", s.W.Readings.Items.Single(r => r.Id == unsaid).Locale);
    }

    [Fact]
    public async Task WithoutAProvider_NothingIsQueued()
    {
        var s = await Setup();
        s.W.Recognizer.Configured = false;
        var id = await s.W.Drafts.UploadAsync(s.Car.Id, Jpeg(), default);

        await s.W.Recognition.QueueForDraftAsync(id, ReadingPurpose.Refueling, "hu", default);

        Assert.Empty(s.W.Readings.Items);
        Assert.Equal(0, s.W.Signal.Wakes);
    }

    [Fact]
    public async Task OnlyTheUploader_CanQueueTheirDraft()
    {
        var s = await Setup();
        var id = await s.W.Drafts.UploadAsync(s.Car.Id, Jpeg(), default);
        s.W.Current.SignInAs(s.Bob);

        await s.W.Recognition.QueueForDraftAsync(id, ReadingPurpose.Refueling, "hu", default);
        await s.W.Recognition.QueueForDraftAsync(Guid.NewGuid(), ReadingPurpose.Refueling, "hu", default);

        Assert.Empty(s.W.Readings.Items);
    }

    // ---- what the dialog asks for --------------------------------------------------------------------------

    [Fact]
    public async Task TheDialog_GetsItsOwnDraftsInTheOrderAsked_WithTheirReadings()
    {
        var s = await Setup();
        s.W.Grants.Items.Add(AccessGrant.Create(s.Alice.Id, s.Bob.Id, AccessLevel.Edit));
        var first = await Queued(s, marker: 1);
        var second = await s.W.Drafts.UploadAsync(s.Car.Id, Jpeg(2), default); // not queued (picked before reading was set up)
        s.W.Current.SignInAs(s.Bob);
        var bobs = await s.W.Drafts.UploadAsync(s.Car.Id, Jpeg(3), default);
        s.W.Current.SignInAs(s.Alice);

        var listed = await s.W.Recognition.ListDraftsAsync([second, bobs, first, Guid.NewGuid()], default);

        Assert.Equal([second, first], listed.Select(d => d.Draft.Id));
        Assert.Null(listed[0].Reading);
        Assert.Equal(ReadingStatus.Queued, listed[1].Reading!.Status);
    }

    [Fact]
    public async Task ExpiredDrafts_AreNotListed()
    {
        var s = await Setup();
        var id = await Queued(s);
        s.W.Clock.Advance(Domain.Photos.PhotoDraft.Lifetime);

        Assert.Empty(await s.W.Recognition.ListDraftsAsync([id], default));
    }

    [Fact]
    public async Task OnlySureValuesThatWereRead_AreWorthFillingIn()
    {
        var s = await Setup();
        var id = await Queued(s);
        s.W.Recognizer.Answer = _ => new RecognitionResult("fake-1", DocumentKind.FuelReceipt,
            [Read(ReadingFieldName.Total, "24669", 0.93), Read(ReadingFieldName.Volume, "38.52", 0.4), Read(ReadingFieldName.Currency, "HUF", 0.95, ValueSource.Hint),
             Read(ReadingFieldName.UnitPrice, "640.4", 0.6, ValueSource.Derived)]);
        await s.W.Processor.ProcessDueAsync(default);

        var usable = s.W.Recognition.UsableValues(s.W.Readings.Items.Single(r => r.Id == id));

        Assert.Equal([ReadingFieldName.Total, ReadingFieldName.UnitPrice], usable.Select(v => v.Name));
    }

    // ---- the worker ----------------------------------------------------------------------------------------

    [Fact]
    public async Task WhyAPhotoGaveLess_IsKeptWithTheReading_AndTheValuesTooUnsureToFillIn_AreAddedWhenItIsListed()
    {
        var s = await Setup();
        var id = await Queued(s);
        s.W.Recognizer.Answer = _ => new RecognitionResult("fake-1", DocumentKind.FuelReceipt,
            [Read(ReadingFieldName.Total, "24669", 0.93), Read(ReadingFieldName.Volume, "38.52", 0.4), Read(ReadingFieldName.UnitPrice, "640.4", 0.5), Read(ReadingFieldName.Currency, "HUF", 0.95, ValueSource.Hint)])
        {
            Issues = [new ReadingIssue(ReadingFieldName.UnitPrice, ReadingIssueCode.AmountsDoNotAdd)],
        };
        await s.W.Processor.ProcessDueAsync(default);

        var reading = s.W.Readings.Items.Single(r => r.Id == id);
        var shown = s.W.Recognition.IssuesOf(reading);

        Assert.Equal([new ReadingIssue(ReadingFieldName.UnitPrice, ReadingIssueCode.AmountsDoNotAdd)], reading.Issues); // what the provider said is kept as it is
        // The volume is under what is filled in and nothing says why: it is added. The price has its reason already, and the hint is no one's concern.
        Assert.Equal(["UnitPrice:AmountsDoNotAdd", "Volume:Unsure"], shown.Select(i => $"{i.Field}:{i.Code}").Order());
    }

    [Fact]
    public async Task AReadingWithNothingToExplain_HasNoIssues()
    {
        var s = await Setup();
        var id = await Queued(s);
        s.W.Recognizer.Answer = _ => new RecognitionResult("fake-1", DocumentKind.Odometer, [Read(ReadingFieldName.Odometer, "123789", 0.9)]);
        await s.W.Processor.ProcessDueAsync(default);

        Assert.Empty(s.W.Recognition.IssuesOf(s.W.Readings.Items.Single(r => r.Id == id)));
    }

    [Fact]
    public async Task AFinishedReading_SaysInCountsHowMuchOfItIsSureEnoughToBeFilledIn()
    {
        var s = await Setup();
        await Queued(s);
        s.W.Recognizer.Answer = _ => new RecognitionResult("fake-1", DocumentKind.FuelReceipt,
            [Read(ReadingFieldName.Total, "24669", 0.93), Read(ReadingFieldName.Volume, "38.52", 0.4), Read(ReadingFieldName.Currency, "HUF", 0.95, ValueSource.Hint)]);

        var done = Assert.Single(await s.W.Processor.ProcessDueAsync(default));

        Assert.Equal(new ReadingSummary(DocumentKind.FuelReceipt, 3, 1), done.Summary); // the volume is too unsure, and the currency was only the hint coming back
    }

    [Fact]
    public async Task TheWorker_ReadsDuePhotos_AndKeepsTheNormalisedValues()
    {
        var s = await Setup();
        await s.W.RefuelingService.LogAsync(s.Car.Id, new(new DateOnly(2026, 9, 20), 40, 60, "HUF", 12_345, true, null), default);
        var id = await Queued(s);
        s.W.Recognizer.Answer = _ => new RecognitionResult("rules-1", DocumentKind.FuelReceipt,
            [Read(ReadingFieldName.Total, "24669.00"), Read(ReadingFieldName.Volume, "38,52"), Read(ReadingFieldName.Currency, "huf"), Read(ReadingFieldName.Date, "2026-09-17")]);

        var done = await s.W.Processor.ProcessDueAsync(default);

        Assert.Equal([new ProcessedReading(id, ReadingOutcome.Read, Summary: new ReadingSummary(DocumentKind.FuelReceipt, 3, 3))], done); // "38,52" is no number: three values kept, all of them sure
        var request = Assert.Single(s.W.Recognizer.Requests);
        Assert.Equal(Jpeg(), request.Image.ToArray());
        Assert.Equal(("image/jpeg", "hu", (long?)12_345, "HUF", new DateOnly(2026, 10, 1), (Guid?)id),
            (request.ContentType, request.Locale, request.LastOdometer, request.Currency, request.Today, request.ReadingId)); // the provider's log lines are told apart by it
        Assert.Equal([DocumentKind.Odometer, DocumentKind.FuelReceipt], request.Kinds.Order());
        var reading = s.W.Readings.Items.Single();
        Assert.Equal((ReadingStatus.Read, DocumentKind.FuelReceipt, "fake", "rules-1"), (reading.Status, reading.Kind, reading.Provider, reading.ModelVersion));
        Assert.Equal(["Total=24669", "Currency=HUF", "Date=2026-09-17"], reading.Values.Select(v => $"{v.Name}={v.Value}")); // "38,52" is not a number
    }

    [Fact]
    public async Task WhileTheProviderIsUnavailable_NothingIsClaimed()
    {
        var s = await Setup();
        await Queued(s);
        s.W.Recognizer.Healthy = false;

        Assert.Empty(await s.W.Processor.ProcessDueAsync(default));
        var reading = s.W.Readings.Items.Single();
        Assert.Equal((ReadingStatus.Queued, 0), (reading.Status, reading.Attempts));
        Assert.Empty(s.W.Recognizer.Requests);
    }

    [Fact]
    public async Task ABusyProvider_IsAskedAgainLater_AndItsHealthIsCheckedAgain()
    {
        var s = await Setup();
        var id = await Queued(s);
        s.W.Recognizer.Answer = _ => throw new RecognitionUnavailableException("busy");

        var done = await s.W.Processor.ProcessDueAsync(default);
        var checksAfterFirst = s.W.Recognizer.HealthChecks;
        await s.W.Processor.ProcessDueAsync(default); // not due yet, but the provider is asked whether it is back

        Assert.Equal([new ProcessedReading(id, ReadingOutcome.Retrying, "busy")], done);
        var reading = s.W.Readings.Items.Single();
        Assert.Equal((ReadingStatus.Queued, 1, s.W.Clock.GetUtcNow() + TimeSpan.FromSeconds(10)), (reading.Status, reading.Attempts, reading.DueAt));
        Assert.Equal(checksAfterFirst + 1, s.W.Recognizer.HealthChecks);
    }

    [Fact]
    public async Task ARefusedPhoto_Fails_AndSoDoesOneWhoseFileIsMissing()
    {
        var s = await Setup();
        var refused = await Queued(s, marker: 1);
        var missing = await Queued(s, marker: 2);
        s.W.ImageStore.Files.Remove(missing);
        s.W.Recognizer.Answer = _ => throw new RecognitionRejectedException("bad_image");

        var done = await s.W.Processor.ProcessDueAsync(default);

        Assert.Equal([ReadingOutcome.Failed, ReadingOutcome.Failed], done.Select(d => d.Outcome));
        Assert.Contains(done, d => d.Id == refused && d.Reason == "bad_image");
        Assert.All(s.W.Readings.Items, r => Assert.Equal(ReadingStatus.Failed, r.Status));
        Assert.Single(s.W.Recognizer.Requests); // the missing file never reached the provider
    }

    [Fact]
    public async Task AnAttemptCutShortByARestart_IsReadAgain()
    {
        var s = await Setup();
        var id = await Queued(s);
        await s.W.Readings.ClaimAsync(id, s.W.Clock.GetUtcNow(), default); // the app stopped while reading it
        s.W.Clock.Advance(PhotoReadingProcessor.StaleAfter(s.W.RecognitionOptions) + TimeSpan.FromSeconds(1));

        var done = await s.W.Processor.ProcessDueAsync(default);

        Assert.Equal([new ProcessedReading(id, ReadingOutcome.Read, Summary: new ReadingSummary(DocumentKind.Unknown, 0, 0))], done);
        Assert.Equal(2, s.W.Readings.Items.Single().Attempts);
    }

    [Fact]
    public async Task ASlowReadThatMayStillBeGoingOn_IsNotTakenOver()
    {
        var s = await Setup();
        s.W.RecognitionOptions.OpenAiCompatible.TimeoutSeconds = 600; // allowed: one read may take ten minutes
        var id = await Queued(s);
        await s.W.Readings.ClaimAsync(id, s.W.Clock.GetUtcNow(), default); // another instance is reading it
        s.W.Clock.Advance(TimeSpan.FromMinutes(10));

        var done = await s.W.Processor.ProcessDueAsync(default);

        Assert.Empty(done);
        Assert.Equal(ReadingStatus.Reading, s.W.Readings.Items.Single().Status);
        Assert.True(PhotoReadingProcessor.StaleAfter(s.W.RecognitionOptions) > TimeSpan.FromMinutes(10));
    }

    [Theory]
    [InlineData(30, 5)] // the floor
    [InlineData(600, 11)] // a read that may take ten minutes is not taken over before eleven
    public void StaleAfter_FollowsTheReadTimeout_WithAFloorOfFiveMinutes(int timeoutSeconds, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), PhotoReadingProcessor.StaleAfter(ModelServer(o => o.TimeoutSeconds = timeoutSeconds)));

    [Fact]
    public async Task OnlyAFewPhotosAreReadAtATime()
    {
        var s = await Setup();
        s.W.RecognitionOptions.MaxConcurrent = 2;
        for (byte i = 0; i < 3; i++) await Queued(s, marker: i);

        var first = await s.W.Processor.ProcessDueAsync(default);
        var second = await s.W.Processor.ProcessDueAsync(default);

        Assert.Equal((2, 1), (first.Count, second.Count));
        Assert.Empty(await s.W.Processor.ProcessDueAsync(default));
    }

    // ---- availability --------------------------------------------------------------------------------------

    [Fact]
    public async Task Availability_IsAskedAtMostEvery30Seconds()
    {
        var s = await Setup();

        Assert.True(await s.W.Recognition.IsAvailableAsync(default));
        s.W.Recognizer.Healthy = false;
        Assert.True(await s.W.Recognition.IsAvailableAsync(default)); // kept
        s.W.Clock.Advance(RecognitionAvailability.CacheFor);
        Assert.False(await s.W.Recognition.IsAvailableAsync(default));

        Assert.Equal(2, s.W.Recognizer.HealthChecks);
    }

    [Fact]
    public async Task WithoutAProvider_NothingIsAvailable_AndNobodyIsAsked()
    {
        var s = await Setup();
        s.W.Recognizer.Configured = false;

        Assert.False(await s.W.Recognition.IsAvailableAsync(default));
        Assert.Empty(await s.W.Processor.ProcessDueAsync(default));
        Assert.Equal(0, s.W.Recognizer.HealthChecks);
    }

    // ---- settings ------------------------------------------------------------------------------------------

    private static RecognitionSetup SetupOf(RecognitionOptions o, FakePromptFiles? files = null) => new(Options.Create(o), files ?? new FakePromptFiles());

    /// <summary>Usable settings for a model behind an OpenAI-compatible API, changed as the test needs.</summary>
    private static RecognitionOptions ModelServer(Action<OpenAiCompatibleRecognitionOptions>? change = null)
    {
        var options = new RecognitionOptions { Provider = "OpenAiCompatible", OpenAiCompatible = { BaseUrl = "http://localhost:1234/v1", Model = "qwen2.5-vl" } };
        change?.Invoke(options.OpenAiCompatible);
        return options;
    }

    [Fact]
    public void Settings_ByDefault_TurnNothingOn_AndNeedNoWarning()
    {
        var setup = SetupOf(new RecognitionOptions());

        Assert.Equal((RecognitionProviderKind.None, false, false), (setup.Kind, setup.Enabled, setup.Requested));
        Assert.Empty(setup.Problems);
        Assert.False(SetupOf(new RecognitionOptions { Provider = "none" }).Requested);
    }

    [Fact]
    public void Settings_ForAModelServer_NeedAnAddressAndAModel_ButNoKey()
    {
        var setup = SetupOf(ModelServer());

        Assert.Equal((RecognitionProviderKind.OpenAiCompatible, true), (setup.Kind, setup.Enabled));
        Assert.Null(setup.SystemPrompt); // the built-in prompt
        Assert.Equal(RecognitionProviderKind.OpenAiCompatible, SetupOf(new RecognitionOptions { Provider = "openaicompatible" }).Kind);
    }

    [Theory]
    [InlineData(null, "m", 120, null, "BaseUrl")]
    [InlineData("localhost:1234/v1", "m", 120, null, "BaseUrl")]
    [InlineData("ftp://localhost/v1", "m", 120, null, "BaseUrl")]
    [InlineData("http://localhost:1234/v1", " ", 120, null, "Model")]
    [InlineData("http://localhost:1234/v1", "m", 0, null, "TimeoutSeconds")]
    [InlineData("http://localhost:1234/v1", "m", 120, 2.5, "Temperature")]
    public void ModelServerSettings_ThatCannotBeUsed_TurnReadingOff_AndSayWhy(string? url, string model, int timeout, double? temperature, string named)
    {
        var setup = SetupOf(ModelServer(o => { o.BaseUrl = url; o.Model = model; o.TimeoutSeconds = timeout; o.Temperature = temperature; }));

        Assert.Equal((false, true), (setup.Enabled, setup.Requested));
        Assert.Contains("Recognition:OpenAiCompatible:" + named, Assert.Single(setup.Problems));
    }

    [Fact]
    public void Settings_OutOfRange_OrAnUnknownProvider_TurnReadingOff()
    {
        var options = ModelServer(o => o.TimeoutSeconds = 0);
        options.MinConfidence = 1.5;
        options.MaxConcurrent = 0;
        var limits = SetupOf(options);
        var unknown = SetupOf(new RecognitionOptions { Provider = "Google" });

        Assert.Equal(3, limits.Problems.Count);
        Assert.Equal((RecognitionProviderKind.None, false, true), (unknown.Kind, unknown.Enabled, unknown.Requested));
        Assert.Contains("Google", Assert.Single(unknown.Problems));
    }

    [Fact]
    public void ThePromptFile_WinsOverThePromptSetting_AndEitherReplacesTheBuiltInOne()
    {
        const string file = "/data/prompt.txt";
        var files = new FakePromptFiles { Files = { [file] = PromptFileRead.Of("From the file.") } }; // the reader trims it (PromptFileReaderTests)

        var fromFile = SetupOf(ModelServer(o => { o.SystemPrompt = "Inline."; o.SystemPromptFile = file; }), files);
        var fromSetting = SetupOf(ModelServer(o => o.SystemPrompt = " Inline. "));
        Assert.Equal("From the file.", fromFile.SystemPrompt);
        Assert.Equal("Inline.", fromSetting.SystemPrompt);
        // The log names where the prompt came from, never its text.
        Assert.Equal("Recognition:OpenAiCompatible:SystemPromptFile", fromFile.SystemPromptSetting);
        Assert.Equal("Recognition:OpenAiCompatible:SystemPrompt", fromSetting.SystemPromptSetting);
        Assert.Null(SetupOf(ModelServer()).SystemPromptSetting);
    }

    [Theory]
    [InlineData(PromptFileProblem.Missing)]
    [InlineData(PromptFileProblem.Empty)]
    [InlineData(PromptFileProblem.TooLarge)]
    [InlineData(PromptFileProblem.Unreadable)]
    public void APromptFileThatIsMissing_Empty_FarTooLarge_OrUnreadable_TurnsReadingOff_WithoutQuotingIt(PromptFileProblem kind)
    {
        const string path = "/data/xxx-prompt.txt";
        var files = new FakePromptFiles { Files = { [path] = PromptFileRead.Failed(kind) } };

        var setup = SetupOf(ModelServer(o => o.SystemPromptFile = path), files);

        Assert.False(setup.Enabled);
        var problem = Assert.Single(setup.Problems);
        Assert.Contains("SystemPromptFile", problem);
        Assert.DoesNotContain("xxx", problem); // the setting only: never the path or the file's content
    }

    // ---- normalising ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ReadingFieldName.Odometer, "123456", "123456")]
    [InlineData(ReadingFieldName.Odometer, "12 345", null)]
    [InlineData(ReadingFieldName.Odometer, "-5", null)]
    [InlineData(ReadingFieldName.Odometer, "1.5", null)]
    [InlineData(ReadingFieldName.Total, "24669.00", "24669")]
    [InlineData(ReadingFieldName.Total, "16.954", "16.95")]
    [InlineData(ReadingFieldName.Total, "0", null)]
    [InlineData(ReadingFieldName.Total, "1,5", null)]
    [InlineData(ReadingFieldName.Volume, " 38.5249 ", "38.525")]
    [InlineData(ReadingFieldName.UnitPrice, "1.789", "1.789")]
    [InlineData(ReadingFieldName.Currency, "huf", "HUF")]
    [InlineData(ReadingFieldName.Currency, "Ft", null)]
    [InlineData(ReadingFieldName.Date, "2026-09-17", "2026-09-17")]
    [InlineData(ReadingFieldName.Date, "17.09.2026", null)]
    [InlineData(ReadingFieldName.Date, "2026-02-30", null)]
    [InlineData(ReadingFieldName.Title, "  Shell   Kft. ", "Shell Kft.")]
    [InlineData(ReadingFieldName.Title, " ", null)]
    [InlineData(ReadingFieldName.Total, null, null)]
    [InlineData((ReadingFieldName)99, "42", null)] // a value of no field the app knows
    public void Values_AreNormalised_OrDropped(ReadingFieldName name, string? raw, string? expected) =>
        Assert.Equal(expected, ReadingNormaliser.Normalise(name, raw));

    [Fact]
    public void ALongTitle_IsCutToWhatAnExpenseTitleMayHold()
    {
        var title = ReadingNormaliser.Normalise(ReadingFieldName.Title, new string('a', 200));

        Assert.Equal(Expense.MaxTitleLength, title!.Length);
    }
}
