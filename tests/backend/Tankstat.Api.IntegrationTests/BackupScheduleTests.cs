using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Tankstat.Application.Backups;
using Tankstat.Infrastructure.Backups;

namespace Tankstat.Api.IntegrationTests;

/// <summary>
/// The schedule on a fake clock (UTC as the server's local time): a backup missed while the app was down is written at start, then one at
/// every due time, retention after each, a Back up now while the scheduled one is written joins it, a failing backup tells the
/// administrators once a day and removes nothing, a waiting restore holds retention back, and a run the app's stop cuts short is no failure.
/// </summary>
public sealed class BackupScheduleTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The day the tests call the 10th: tomorrow by the real clock. The sign-in cookies get their expiry from the fake clock, and the test
    /// client drops a cookie whose expiry the real clock has passed, so the fake clock never runs behind the real one.
    /// </summary>
    private static readonly DateTimeOffset Tenth = new(DateTime.UtcNow.Date.AddDays(1), TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"tankstat-it-{Guid.NewGuid():N}-backups");
    private readonly FakeTimeProvider _clock = new(At(10, 3)); // 03:00, the night's 02:00 passed

    public BackupScheduleTests() => _clock.SetLocalTimeZone(TimeZoneInfo.Utc);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static DateTimeOffset At(int day, int hour) => Tenth.AddDays(day - 10).AddHours(hour);

    /// <summary>The name the app gives the backup it writes at <paramref name="at"/>.</summary>
    private static string Name(DateTimeOffset at) => string.Create(CultureInfo.InvariantCulture, $"{BackupRetention.OwnPrefix}{at:yyyyMMdd'T'HHmmss'Z'}.zip");

    private TestApp App(Dictionary<string, string?> extra, Action<IServiceCollection>? services = null)
    {
        var settings = new Dictionary<string, string?> { ["Backups:Path"] = _folder, ["Backups:Every"] = "02:00" };
        foreach (var (key, value) in extra) settings[key] = value; // a null is held too: no source below may set it (AppWithSettingsFile for the page's tests)
        var app = TestApp.Standalone(settings, s =>
        {
            s.AddSingleton<TimeProvider>(_clock);
            services?.Invoke(s);
        });
        _ = app.Factory.Services; // starts the host, and the worker with it
        return app;
    }

    private static BackupWorker Worker(TestApp app) => app.Factory.Services.GetServices<IHostedService>().OfType<BackupWorker>().Single();

    private string[] Files() => Directory.Exists(_folder) ? [.. Directory.GetFiles(_folder).Select(Path.GetFileName).Order()!] : [];

    [Fact]
    public async Task AMissedBackupIsWrittenAtStart_ThenOneAtEveryDueTime_AndRetentionKeepsTheNewest()
    {
        using var app = App(new() { ["Backups:Keep"] = "2" });
        var worker = Worker(app);

        await worker.WhenAsleepUntil(At(11, 2)).WaitAsync(Wait);
        Assert.Equal([Name(At(10, 3))], Files()); // the night's, caught up as the app started

        _clock.SetUtcNow(At(11, 2));
        await worker.WhenAsleepUntil(At(12, 2)).WaitAsync(Wait);
        Assert.Equal([Name(At(10, 3)), Name(At(11, 2))], Files());

        _clock.SetUtcNow(At(12, 2));
        await worker.WhenAsleepUntil(At(13, 2)).WaitAsync(Wait);
        Assert.Equal([Name(At(11, 2)), Name(At(12, 2))], Files()); // Keep=2: the oldest went

        var lines = app.Log.From<BackupWorker>().ToList();
        Assert.Contains(lines, e => e.Level == LogLevel.Information && e.Values.TryGetValue("Times", out var t) && (string?)t == "02:00" && e.Values.TryGetValue("Days", out var d) && (string?)d == "every day");
        Assert.DoesNotContain(lines, e => e.Level >= LogLevel.Warning); // retention is set: no warning about a folder that only grows
        Assert.Contains(app.Log.From<BackupRunner>(), e => e.Level == LogLevel.Information && e.Values.TryGetValue("File", out var f) && (string?)f == Name(At(10, 3)));
    }

    [Fact]
    public async Task WhileTheSetupWaits_NothingIsWritten_TheFirstDueTimeAfterItWritesTheFirstBackup()
    {
        using var app = App(new() { ["Setup:Enabled"] = "true", ["Backups:Keep"] = "2" });
        var worker = Worker(app);

        await worker.WhenAsleepUntil(At(11, 2)).WaitAsync(Wait);
        Assert.Empty(Files()); // nothing caught up: an empty instance, whose backup the setup page would offer as a restore point
        _clock.SetUtcNow(At(11, 2));
        await worker.WhenAsleepUntil(At(12, 2)).WaitAsync(Wait);
        Assert.Empty(Files());

        var code = (string)Assert.Single(app.Log.From<Tankstat.Application.Setup.OneTimeCode.SetupLine>(), e => e.Values.ContainsKey("Code")).Values["Code"]!;
        (await app.NewClient().Gql("mutation($i: SetupFinishInput!) { setupFinish(input: $i) { restarting } }",
            new { i = new { code, email = "root@example.com", password = "initial-password-1", passwordConfirmation = "initial-password-1" } })).Data();
        _clock.SetUtcNow(At(12, 2));
        await worker.WhenAsleepUntil(At(13, 2)).WaitAsync(Wait);

        Assert.Equal([Name(At(12, 2))], Files());
        Assert.Equal(2, app.Log.From<BackupWorker>().Count(e => e.Message.Contains("setup is not finished")));
    }

    [Fact]
    public async Task AWaitingSetup_ThatHoldsData_IsBackedUp_AsAnyOtherInstance()
    {
        // Sign-in off: the API is open while the acknowledgement waits, so the instance holds data before anyone finishes its setup.
        using var app = App(new() { ["Auth:Mode"] = "None", ["Setup:Enabled"] = "true", ["Backups:Keep"] = "2" });
        var worker = Worker(app);
        await worker.WhenAsleepUntil(At(11, 2)).WaitAsync(Wait);
        Assert.Empty(Files());

        (await app.NewClient().Gql("mutation { addVehicle(input: { name: \"Car\", fuelType: PETROL }) { id } }")).Data();
        _clock.SetUtcNow(At(11, 2));
        await worker.WhenAsleepUntil(At(12, 2)).WaitAsync(Wait);

        Assert.Equal([Name(At(11, 2))], Files());
    }

    [Fact]
    public async Task AStartAfterTheNightsBackup_WritesNoneAtStart_AndWarnsWhenNothingIsEverRemoved()
    {
        using (var first = App([]))
            await Worker(first).WhenAsleepUntil(At(11, 2)).WaitAsync(Wait);
        Assert.Single(Files());

        _clock.SetUtcNow(At(10, 9)); // the same day, later: today's is written
        using var again = App([]);
        await Worker(again).WhenAsleepUntil(At(11, 2)).WaitAsync(Wait);

        Assert.Single(Files());
        Assert.Contains(again.Log.From<BackupWorker>(), e => e.Level == LogLevel.Warning);
    }

    /// <summary>A writer that waits until it is let go: the scheduled backup under way while Back up now is pressed.</summary>
    private sealed class HeldWriter(IBackupWriter real) : IBackupWriter
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public async Task<BackupResult> WriteAsync(BackupRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return await real.WriteAsync(request, ct);
        }
    }

    [Fact]
    public async Task BackUpNow_WhileTheScheduledOneIsWritten_JoinsIt()
    {
        using var app = App([], s => s.AddSingleton<IBackupWriter>(sp => new HeldWriter(ActivatorUtilities.CreateInstance<BackupWriter>(sp))));
        var admin = app.NewClient();
        await admin.LoginAs(TestApp.AdminEmail, TestApp.AdminPassword);
        var held = (HeldWriter)app.Factory.Services.GetRequiredService<IBackupWriter>();
        await held.Started.Task.WaitAsync(Wait); // the missed one is being written

        Assert.False((await admin.Gql("mutation { runBackup }")).Data().GetProperty("runBackup").GetBoolean());
        held.Release.SetResult();
        await Worker(app).WhenAsleepUntil(At(11, 2)).WaitAsync(Wait);

        Assert.Equal(1, held.Calls);
        Assert.Single(Files());
    }

    private sealed class FailingWriter : IBackupWriter
    {
        public Task<BackupResult> WriteAsync(BackupRequest request, CancellationToken ct) => throw new IOException("The disk is full.");
    }

    [Fact]
    public async Task AFailingBackup_TellsEveryAdministratorOnceADay_AndRemovesNothing()
    {
        Directory.CreateDirectory(_folder);
        // Two older backups of the app, which Keep=1 would thin out, written the day before on a clock of their own (the schedule's clock does
        // not go back): by the real clock they would date from today and count as the night's.
        var dayBefore = new FakeTimeProvider(At(9, 12));
        using (var before = TestApp.Standalone(new() { ["Backups:Path"] = _folder }, s => s.AddSingleton<TimeProvider>(dayBefore)))
        {
            var writer = before.Factory.Services.GetRequiredService<IBackupWriter>();
            await writer.WriteAsync(new BackupRequest(), default);
            await writer.WriteAsync(new BackupRequest(), default);
        }
        var older = Files();
        Assert.Equal(2, older.Length);

        using var app = App(new() { ["Backups:Keep"] = "1" }, s => s.AddSingleton<IBackupWriter>(new FailingWriter()));
        var worker = Worker(app);
        await worker.WhenAsleepUntil(At(11, 2)).WaitAsync(Wait); // the missed one failed
        var people = await app.Users();
        var bob = people.BobId;
        await people.Admin.Gql("mutation($id: UUID!) { setUserAdmin(userId: $id, isAdmin: true) { id } }", new { id = bob });

        _clock.SetUtcNow(At(11, 2));
        await worker.WhenAsleepUntil(At(12, 2)).WaitAsync(Wait); // the next night's failed too

        // A day on, the sign-ins of yesterday have run out: each signs in again to read the inbox.
        async Task<List<System.Text.Json.JsonElement>> Inbox(string email, string password)
        {
            var client = app.NewClient();
            await client.LoginAs(email, password);
            return [.. (await client.Gql("{ notifications { kind args { name value } } }")).Data().GetProperty("notifications").EnumerateArray()
                .Where(n => n.GetProperty("kind").GetString() == "BACKUP_FAILED")];
        }
        var root = await Inbox(TestApp.AdminEmail, TestApp.AdminPassword);
        var bobs = await Inbox("bob@example.com", "bob-password-1");
        var alices = await Inbox("alice@example.com", "alice-password-1");
        Assert.Equal(2, root.Count); // one a night
        Assert.Single(bobs); // an administrator since the second night only
        Assert.Empty(alices); // not an administrator
        Assert.Contains(bobs[0].GetProperty("args").EnumerateArray(), a => a.GetProperty("name").GetString() == "error" && a.GetProperty("value").GetString() == "backup.failed");
        Assert.Equal(older, Files()); // nothing was pruned
    }

    [Fact]
    public async Task WhileARestoreWaits_RetentionRemovesNothing()
    {
        using var app = App(new() { ["Backups:Keep"] = "1" });
        var worker = Worker(app);
        await worker.WhenAsleepUntil(At(11, 2)).WaitAsync(Wait);
        var first = Assert.Single(Files()); // the night's, caught up at start
        new RestoreMarker(first, Guid.NewGuid(), _clock.GetUtcNow(), null).Write(_folder); // a restore of it waits for the next start

        _clock.SetUtcNow(At(11, 2));
        await worker.WhenAsleepUntil(At(12, 2)).WaitAsync(Wait);

        // Keep=1 would have removed the backup the restore names, and the next start would have refused the restore.
        Assert.Equal([RestoreMarker.FileName, first, Name(At(11, 2))], Files());
        Assert.Contains(app.Log.From<BackupRunner>(), e => e.Level == LogLevel.Information);
    }

    [Fact]
    public async Task TheBackupJustWritten_NeverGoes_EvenWhenAnOwnFileIsDatedLater()
    {
        // An own backup dated after the night's run, as one copied in from a server whose clock was ahead: by its date it is the newest.
        using (var ahead = TestApp.Standalone(new() { ["Backups:Path"] = _folder }, s => s.AddSingleton<TimeProvider>(new FakeTimeProvider(At(20, 0)))))
            await ahead.Factory.Services.GetRequiredService<IBackupWriter>().WriteAsync(new BackupRequest(), default);

        using var app = App(new() { ["Backups:Keep"] = "1" });
        var worker = Worker(app);
        await worker.WhenAsleepUntil(At(11, 2)).WaitAsync(Wait);
        _clock.SetUtcNow(At(11, 2));
        await worker.WhenAsleepUntil(At(12, 2)).WaitAsync(Wait);

        Assert.Equal([Name(At(11, 2)), Name(At(20, 0))], Files()); // Keep=1 would have removed the backup it had just written
    }

    /// <summary>
    /// A writer that is under way until the app stops, and then fails the way a database may report the cancelled read: SQL Server's client
    /// throws its own exception ("Operation cancelled by user."), not an OperationCanceledException.
    /// </summary>
    private sealed class StoppedWriter : IBackupWriter
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BackupResult> WriteAsync(BackupRequest request, CancellationToken ct)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("Operation cancelled by user.");
            }
            throw new InvalidOperationException("Never reached: the delay ends only by the stop.");
        }
    }

    [Fact]
    public async Task ARunTheAppsStopCutsShort_IsStopped_HoweverTheStopSurfaces_AndTellsNobody()
    {
        var writer = new StoppedWriter();
        var app = App([], s => s.AddSingleton<IBackupWriter>(writer));
        try
        {
            await writer.Started.Task.WaitAsync(Wait); // the missed one is being written
        }
        finally
        {
            app.Dispose(); // the app stops under it, and waits for it to wind down
        }

        var lines = app.Log.From<BackupRunner>().ToList();
        Assert.Contains(lines, e => e.Level == LogLevel.Information); // stopped with the app
        Assert.DoesNotContain(lines, e => e.Level >= LogLevel.Warning); // no failure, so no administrator is told either
    }

    [Fact]
    public async Task WithoutASchedule_TheWorkerSaysNothing_AndWritesNothing()
    {
        using var app = App(new() { ["Backups:Every"] = null });

        _clock.Advance(TimeSpan.FromDays(3));
        await ((BackupRunner)app.Factory.Services.GetRequiredService<IBackupRunner>()).WhenIdleAsync();

        Assert.Empty(Files());
        Assert.Empty(app.Log.From<BackupWorker>());
    }

    private const string SaveSettings = "mutation($c: [SystemSettingChangeInput!]!) { updateSystemSettings(changes: $c) { restartPending } }";

    /// <summary>A host whose backup settings only its settings file holds (none held in memory, where the page could not save them), on <paramref name="clock"/>.</summary>
    private TestApp AppWithSettingsFile(TimeProvider clock, string? file = null)
    {
        var app = TestApp.Standalone(new() { ["Backups:Path"] = _folder }, s => s.AddSingleton(clock));
        if (file is not null) File.WriteAllText(app.SettingsPath, file); // read as the host is built, below
        _ = app.Factory.Services; // starts the host, and the worker with it
        return app;
    }

    [Fact]
    public async Task ATimeSavedOnTheSettingsPage_SchedulesBackupsAtOnce_AndClearingItStopsThem()
    {
        using var app = AppWithSettingsFile(_clock);
        var admin = app.NewClient();
        await admin.LoginAs(TestApp.AdminEmail, TestApp.AdminPassword);

        var saved = (await admin.Gql(SaveSettings, new { c = new object[] { new { key = "Backups:Every", value = "04:00" }, new { key = "Backups:Keep", value = "3" } } })).Data();

        Assert.False(saved.GetProperty("updateSystemSettings").GetProperty("restartPending").GetBoolean()); // the backups apply at once
        await Worker(app).WhenAsleepUntil(At(10, 4)).WaitAsync(Wait); // tonight's 04:00, planned without a restart
        Assert.Empty(Files()); // a time set while running catches nothing up: only a start does
        _clock.Advance(TimeSpan.FromHours(1));
        await Worker(app).WhenAsleepUntil(At(11, 4)).WaitAsync(Wait);
        Assert.Single(Files());

        await admin.LoginAs(TestApp.AdminEmail, TestApp.AdminPassword); // the hour the clock moved outlived the access cookie
        (await admin.Gql(SaveSettings, new { c = new object[] { new { key = "Backups:Every", value = "" } } })).Data();
        // The save completed the worker's signal before it answered: whether the worker woke before the clock moves or the timer ends first,
        // the cleared time is seen before anything runs, so tomorrow's 04:00 is not written.
        _clock.Advance(TimeSpan.FromDays(2));
        await ((BackupRunner)app.Factory.Services.GetRequiredService<IBackupRunner>()).WhenIdleAsync();
        Assert.Single(Files());
    }

    [Fact]
    public async Task DaysSavedOnTheSettingsPage_LeaveTheOthersOut_AndNoDayStopsTheSchedule()
    {
        using var app = AppWithSettingsFile(_clock);
        var admin = app.NewClient();
        await admin.LoginAs(TestApp.AdminEmail, TestApp.AdminPassword);
        var day = At(12, 0).DayOfWeek; // the day after tomorrow: today and tomorrow are left out

        (await admin.Gql(SaveSettings, new { c = new object[] { new { key = "Backups:Every", value = "16:00, 4:00" }, new { key = "Backups:Days", value = day.ToString() } } })).Data();

        await Worker(app).WhenAsleepUntil(At(12, 4)).WaitAsync(Wait);
        var settings = (await admin.Gql("{ backups { settings { times days timeZone nextRun } } }")).Data().GetProperty("backups").GetProperty("settings");
        Assert.Equal(["04:00", "16:00"], settings.GetProperty("times").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal([day.ToString().ToUpperInvariant()], settings.GetProperty("days").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(("UTC", At(12, 4)), (settings.GetProperty("timeZone").GetString(), settings.GetProperty("nextRun").GetDateTimeOffset()));

        (await admin.Gql(SaveSettings, new { c = new object[] { new { key = "Backups:Days", value = "None" } } })).Data();
        _clock.Advance(TimeSpan.FromDays(8));
        await ((BackupRunner)app.Factory.Services.GetRequiredService<IBackupRunner>()).WhenIdleAsync();
        Assert.Empty(Files());
    }

    /// <summary>
    /// A wall clock that runs ahead of the timers, as after the host was suspended or its clock was stepped forward: the time reads past a
    /// due time whose timer has not ended yet.
    /// </summary>
    private sealed class SteppedClock(FakeTimeProvider timers) : TimeProvider
    {
        public TimeSpan Ahead { get; set; }
        public override DateTimeOffset GetUtcNow() => timers.GetUtcNow() + Ahead;
        public override TimeZoneInfo LocalTimeZone => timers.LocalTimeZone;
        public override long TimestampFrequency => timers.TimestampFrequency;
        public override long GetTimestamp() => timers.GetTimestamp();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => timers.CreateTimer(callback, state, dueTime, period);
    }

    [Fact]
    public async Task ASaveThatLeavesTheTime_StillWritesTheNightsBackup_OnceItsTimeHasPassed_AndClearedRetentionIsWarnedAbout()
    {
        var clock = new SteppedClock(_clock);
        using var app = AppWithSettingsFile(clock, """{ "Backups": { "Every": "02:00", "Keep": "3" } }""");
        var worker = Worker(app);
        await worker.WhenAsleepUntil(At(11, 2)).WaitAsync(Wait); // the night's, caught up at start
        Assert.DoesNotContain(app.Log.From<BackupWorker>(), e => e.Level >= LogLevel.Warning); // retention is set

        // 02:01 on the clock of the day, while the timer of 02:00 still runs: a save then (retention cleared, the time left as it was) writes
        // the night's backup at once, where planning again from now would have left that night without one.
        clock.Ahead = At(11, 2).AddMinutes(1) - _clock.GetUtcNow();
        var admin = app.NewClient();
        await admin.LoginAs(TestApp.AdminEmail, TestApp.AdminPassword);
        (await admin.Gql(SaveSettings, new { c = new object[] { new { key = "Backups:Keep", value = "" } } })).Data();

        await worker.WhenAsleepUntil(At(12, 2)).WaitAsync(Wait);
        Assert.Equal([Name(At(10, 3)), Name(At(11, 2).AddMinutes(1))], Files());
        Assert.Single(app.Log.From<BackupWorker>(), e => e.Level == LogLevel.Warning); // retention cleared while backups are scheduled: the folder only grows now
    }

    [Fact]
    public async Task SettingsThatStopValidatingWhileRunning_LeaveThePublicNoticesAlone_AndTheWorkerSaysSoOnce()
    {
        using var app = App(new() { ["Backups:Keep"] = "3" });
        var worker = Worker(app);
        await worker.WhenAsleepUntil(At(11, 2)).WaitAsync(Wait); // the night's, caught up at start

        // As an appsettings.json edited by hand reloads on its own: nothing checks the value before it is read.
        var configuration = (IConfigurationRoot)app.Factory.Services.GetRequiredService<IConfiguration>();
        configuration.Providers.OfType<MemoryConfigurationProvider>().Last(p => p.TryGet("Backups:Every", out _)).Set("Backups:Keep", "0");
        try
        {
            configuration.Reload();
        }
        catch (AggregateException)
        {
            // the options monitor validates the reloaded settings inside the reload, and its refusal comes out of it
        }

        // The store read its folder at start: the public notices (the sign-in screen's too) still answer.
        Assert.Equal(0, (await app.NewClient().Gql("{ notices { code } }")).Data().GetProperty("notices").GetArrayLength());
        _clock.SetUtcNow(At(11, 2)); // the night's run meets them and fails; the worker says why, once, and plans nothing until they are fixed
        await worker.WhenAsleepUntil(DateTimeOffset.MaxValue).WaitAsync(Wait);

        var lines = app.Log.From<BackupWorker>().ToList();
        Assert.Contains("Backups:Keep", (string?)Assert.Single(lines, e => e.Level == LogLevel.Warning).Values["Problems"]);
        Assert.DoesNotContain(lines, e => e.Level >= LogLevel.Error);
    }
}
