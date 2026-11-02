using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Tankstat.Seeder;
using Tankstat.TestSupport;

namespace Tankstat.Seeder.UnitTests;

public sealed class SeederAppTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"tankstat-seed-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        // Only this database's pooled connections: ClearAllPools would also close the ones of tests running in parallel.
        using (var pooled = new Microsoft.Data.Sqlite.SqliteConnection(Connection)) Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pooled);
        File.Delete(_db);
    }

    private string Connection => $"Data Source={_db}";

    private async Task<(int Code, string Output)> Run(string[] args, string input = "", IDictionary<string, string?>? env = null)
    {
        var output = new StringWriter();
        var config = new ConfigurationBuilder().AddInMemoryCollection(env ?? new Dictionary<string, string?>()).Build();
        // A settings file that does not exist unless the test names one: never the settings.json of the working directory, a developer's maybe.
        string[] settings = args.Contains("--settings") || env?.ContainsKey("Settings:Path") == true ? [] : ["--settings", _db + "-settings.json"];
        var code = await SeederApp.RunAsync([.. args, .. settings, "--connection", Connection], config, output, new StringReader(input), _clock);
        return (code, output.ToString());
    }

    private long Scalar(string sql)
    {
        using var c = new SqliteConnection(Connection);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task CreatesAndMigratesTheDatabase_AndSeedsTheRequestedAmounts()
    {
        var (code, output) = await Run(["--vehicles", "40", "--trashed", "6", "--refuelings", "5-15", "--yes"]);

        Assert.Equal(SeederApp.Success, code);
        Assert.Contains("40 vehicles, 6 in the trash", output);
        Assert.Equal(46, Scalar("select count(*) from Vehicles"));
        Assert.Equal(6, Scalar("select count(*) from Vehicles where DeletedAt is not null"));
        Assert.InRange(Scalar("select count(*) from Refuelings"), 46 * 5, 46 * 15);
        Assert.Equal(0, Scalar("select count(*) from Users")); // no users, ever
        // every reading and cost is a refuelling's or an expense's (an expense always has a cost, a reading only when the odometer was noted)
        Assert.True(Scalar("select count(*) from Expenses") > 0);
        Assert.Equal(Scalar("select count(*) from Refuelings") + Scalar("select count(*) from Expenses where OdometerReadingId is not null"), Scalar("select count(*) from OdometerReadings"));
        Assert.Equal(Scalar("select count(*) from Refuelings") + Scalar("select count(*) from Expenses"), Scalar("select count(*) from Costs"));
        Assert.Contains($" {Scalar("select count(*) from Expenses"):N0} expenses,", output); // as the line writes it, in any culture
        Assert.Equal(0, Scalar("select count(*) from Vehicles where OwnerId <> '00000000-0000-0000-0000-000000000000'"));
        Assert.True(Scalar("select count(*) from __EFMigrationsHistory") >= 2); // really migrated
        Assert.Contains("15 notifications", output);
        Assert.Equal(15, Scalar("select count(*) from Notifications where RecipientId = '00000000-0000-0000-0000-000000000000'"));
        Assert.Equal(0, Scalar("select count(*) from RecurringExpenses r join Vehicles v on v.Id = r.VehicleId where v.DeletedAt is not null or r.OwnerId <> v.OwnerId"));
        Assert.Contains($" {Scalar("select count(*) from RecurringExpenses"):N0} recurring expenses,", output);
    }

    [Fact]
    public async Task DeletesWhateverWasThereBefore()
    {
        await Run(["--vehicles", "50", "--trashed", "10", "--yes"]);
        Assert.Equal(60, Scalar("select count(*) from Vehicles"));

        var (code, _) = await Run(["--vehicles", "3", "--refuelings", "2", "--yes"]);

        Assert.Equal(SeederApp.Success, code);
        Assert.Equal(3, Scalar("select count(*) from Vehicles"));
        Assert.Equal(6, Scalar("select count(*) from Refuelings"));
    }

    [Fact]
    public async Task SeedingZeroVehiclesStillRecreatesAnEmptyDatabase()
    {
        await Run(["--vehicles", "5", "--yes"]);

        await Run(["--vehicles", "0", "--yes"]);

        Assert.Equal(0, Scalar("select count(*) from Vehicles"));
    }

    [Fact]
    public async Task AsksForConfirmation_AndDoesNothingUnlessTheAnswerIsYes()
    {
        await Run(["--vehicles", "4", "--yes"]);

        var (declined, output) = await Run(["--vehicles", "9"], input: "no\n");
        var (silent, _) = await Run(["--vehicles", "9"], input: "");

        Assert.Equal((SeederApp.Refused, SeederApp.Refused), (declined, silent));
        Assert.Contains("Cancelled", output);
        Assert.Contains(_db, output); // tells which database it would delete
        Assert.Equal(4, Scalar("select count(*) from Vehicles"));

        var (confirmed, _) = await Run(["--vehicles", "9"], input: "yes\n");
        Assert.Equal(SeederApp.Success, confirmed);
        Assert.Equal(9, Scalar("select count(*) from Vehicles"));
    }

    [Theory]
    [InlineData("Standalone")]
    [InlineData("Oidc")]
    [InlineData("ProxyHeader")]
    public async Task RefusesToRun_WhenAuthenticationIsOn_AndLeavesTheDatabaseAlone(string mode)
    {
        await Run(["--vehicles", "4", "--yes"]);

        var (code, output) = await Run(["--vehicles", "9", "--yes"], env: new Dictionary<string, string?> { ["Auth:Mode"] = mode });

        Assert.Equal(SeederApp.Refused, code);
        Assert.Contains("authentication mode None only", output);
        Assert.Equal(4, Scalar("select count(*) from Vehicles"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("None")]
    [InlineData("none")]
    public async Task RunsWhenAuthenticationIsOffOrUnset(string? mode)
    {
        var env = mode is null ? null : new Dictionary<string, string?> { ["Auth:Mode"] = mode };

        Assert.Equal(SeederApp.Success, (await Run(["--vehicles", "2", "--yes"], env: env)).Code);
    }

    [Fact]
    public async Task TheAppsOwnSettingsFile_Counts_BelowTheEnvironment()
    {
        var file = Path.Combine(Path.GetTempPath(), $"tankstat-seed-settings-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, """{ "Auth": { "Mode": "Standalone" } }""");
        try
        {
            // a mode set on the settings page (Settings:Path from the environment, or --settings) refuses the seeder
            var (byPath, output) = await Run(["--vehicles", "2", "--yes"], env: new Dictionary<string, string?> { ["Settings:Path"] = file });
            Assert.Equal(SeederApp.Refused, byPath);
            Assert.Contains("authentication mode None only", output);
            Assert.Equal(SeederApp.Refused, (await Run(["--vehicles", "2", "--yes", "--settings", file])).Code);

            // the environment wins over the file, as in the app
            Assert.Equal(SeederApp.Success, (await Run(["--vehicles", "2", "--yes"], env: new Dictionary<string, string?> { ["Settings:Path"] = file, ["Auth:Mode"] = "None" })).Code);

            // a blank --settings is no path (an answer, not a crash): Settings:Path counts then, as a blank one does in the app
            Assert.Equal(SeederApp.Refused, (await Run(["--vehicles", "2", "--yes", "--settings="], env: new Dictionary<string, string?> { ["Settings:Path"] = file })).Code);

            await File.WriteAllTextAsync(file, "{ not json");
            var (broken, said) = await Run(["--vehicles", "2", "--yes", "--settings", file]);
            Assert.Equal(SeederApp.Refused, broken);
            Assert.Contains("settings file", said);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ASettingsFileWhoseNameStartsWithADot_CountsToo_AndOneThatCannotBeRead_Refuses()
    {
        var file = Path.Combine(Path.GetTempPath(), $".tankstat-seed-settings-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, """{ "Auth": { "Mode": "Standalone" } }""");
        try
        {
            Assert.Equal(SeederApp.Refused, (await Run(["--vehicles", "2", "--yes", "--settings", file])).Code); // read as the app reads it

            if (OperatingSystem.IsWindows()) return; // Unix file modes
            File.SetUnixFileMode(file, UnixFileMode.None);
            if (Readable(file)) return; // root reads it anyway
            var (code, output) = await Run(["--vehicles", "2", "--yes", "--settings", file]);
            Assert.Equal(SeederApp.Refused, code); // an answer, not an unhandled exception
            Assert.Contains("cannot be read", output);
        }
        finally
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(file);
        }
    }

    private static bool Readable(string path)
    {
        try
        {
            using var _ = File.OpenRead(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public async Task ClearsTheUploadedPicturesAndPhotos_WhenTheFoldersAreGiven()
    {
        var uploads = Path.Combine(Path.GetTempPath(), $"tankstat-seed-uploads-{Guid.NewGuid():N}");
        var photos = Path.Combine(Path.GetTempPath(), $"tankstat-seed-photos-{Guid.NewGuid():N}");
        Directory.CreateDirectory(uploads);
        Directory.CreateDirectory(photos);
        File.WriteAllText(Path.Combine(uploads, "old-picture"), "x");
        File.WriteAllText(Path.Combine(photos, "old-photo"), "x");

        var (code, output) = await Run(["--vehicles", "2", "--uploads", uploads, "--photos", photos, "--yes"]);

        Assert.Equal(SeederApp.Success, code);
        Assert.False(Directory.Exists(uploads));
        Assert.False(Directory.Exists(photos));
        Assert.Contains(uploads, output); // told the user before deleting
        Assert.Contains(photos, output);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")] // blank counts as unset, as in the app
    public async Task WithoutAPhotosFolderOfTheirOwn_ThePhotosGoWithThePictures_AndNoOtherFolderIsTouched(string? configured)
    {
        var parent = Path.Combine(Path.GetTempPath(), $"tankstat-seed-{Guid.NewGuid():N}");
        var (uploads, photos) = (Path.Combine(parent, "uploads"), Path.Combine(parent, "photos"));
        Directory.CreateDirectory(uploads);
        Directory.CreateDirectory(photos); // a folder next to it that is not the app's
        var env = configured is null ? null : new Dictionary<string, string?> { ["Storage:PhotosPath"] = configured };
        try
        {
            var (code, output) = await Run(["--vehicles", "1", "--uploads", uploads, "--yes"], env: env);

            Assert.Equal(SeederApp.Success, code);
            Assert.False(Directory.Exists(uploads));
            Assert.True(Directory.Exists(photos));
            Assert.DoesNotContain("Photos of logs folder", output);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task LeavesPicturesAloneWhenTheUserDeclines_OrNoFolderIsKnown()
    {
        var uploads = Path.Combine(Path.GetTempPath(), $"tankstat-seed-uploads-{Guid.NewGuid():N}");
        var photos = uploads + "-photos";
        Directory.CreateDirectory(uploads);
        Directory.CreateDirectory(photos);
        try
        {
            await Run(["--vehicles", "2", "--uploads", uploads, "--photos", photos], input: "no\n");
            await Run(["--vehicles", "2", "--yes"]); // no folder given or configured: nothing is deleted

            Assert.True(Directory.Exists(uploads));
            Assert.True(Directory.Exists(photos));
        }
        finally
        {
            Directory.Delete(uploads, recursive: true);
            Directory.Delete(photos, recursive: true);
        }
    }

    [Fact]
    public async Task UsesTheConfiguredStoragePath_WhenNoFolderIsGiven()
    {
        var uploads = Path.Combine(Path.GetTempPath(), $"tankstat-seed-uploads-{Guid.NewGuid():N}");
        Directory.CreateDirectory(uploads);

        var photos = uploads.Replace("-uploads-", "-photos-");
        Directory.CreateDirectory(photos);

        await Run(["--vehicles", "1", "--yes"], env: new Dictionary<string, string?> { ["Storage:Path"] = uploads, ["Storage:PhotosPath"] = photos });

        Assert.False(Directory.Exists(uploads));
        Assert.False(Directory.Exists(photos));
    }

    [Fact]
    public async Task InvalidArguments_AreExplained_AndNothingHappens()
    {
        var (code, output) = await Run(["--vehicles", "abc", "--yes"]);

        Assert.Equal(SeederApp.Refused, code);
        Assert.Contains("--vehicles must be a whole number", output);
        Assert.False(File.Exists(_db));
    }

    [Fact]
    public async Task Help_PrintsUsage_WithoutTouchingTheDatabase()
    {
        var (code, output) = await Run(["--help"]);

        Assert.Equal(SeederApp.Success, code);
        Assert.Contains("--vehicles", output);
        Assert.Contains("--trashed", output);
        Assert.Contains("--refuelings", output);
        Assert.False(File.Exists(_db));
    }

    [Fact]
    public async Task PasswordsInTheConnectionStringAreNotPrinted()
    {
        var output = new StringWriter();
        var config = new ConfigurationBuilder().Build();

        await SeederApp.RunAsync(["--provider", "PostgreSql", "--connection", "Host=nowhere;Database=x;Password=hunter2", "--vehicles", "1"], config, output, new StringReader("no\n"), _clock);

        Assert.DoesNotContain("hunter2", output.ToString());
        Assert.Contains("Host=nowhere", output.ToString());
    }

    [Fact]
    public async Task SameSeedGivesTheSameDatabaseContent()
    {
        await Run(["--vehicles", "20", "--trashed", "3", "--seed", "99", "--yes"]);
        var first = Scalar("select sum(Value) + count(*) from OdometerReadings");

        await Run(["--vehicles", "20", "--trashed", "3", "--seed", "99", "--yes"]);

        Assert.Equal(first, Scalar("select sum(Value) + count(*) from OdometerReadings"));
    }

    [Fact]
    public async Task EveryRefuelingBelongsToAVehicle_AndTheLogsAreConsistentInTheDatabase()
    {
        await Run(["--vehicles", "30", "--trashed", "5", "--refuelings", "3-20", "--yes"]);

        Assert.Equal(0, Scalar("select count(*) from Refuelings r left join Vehicles v on v.Id = r.VehicleId where v.Id is null"));
        Assert.Equal(0, Scalar("select count(*) from Refuelings r join Vehicles v on v.Id = r.VehicleId where r.OwnerId <> v.OwnerId"));
        // every log owns exactly one reading and one cost of its own vehicle, dated like the log
        Assert.Equal(0, Scalar("select count(*) from Refuelings r left join OdometerReadings o on o.Id = r.OdometerReadingId where o.Id is null or o.VehicleId <> r.VehicleId or o.Date <> r.Date"));
        Assert.Equal(0, Scalar("select count(*) from Refuelings r left join Costs c on c.Id = r.CostId where c.Id is null or c.VehicleId <> r.VehicleId or c.Date <> r.Date"));
        // the fill-ups' odometer and date both increase within a vehicle
        Assert.Equal(0, Scalar("""
            select count(*) from Refuelings ra join OdometerReadings a on a.Id = ra.OdometerReadingId
            join Refuelings rb on rb.VehicleId = ra.VehicleId join OdometerReadings b on b.Id = rb.OdometerReadingId
            where a.Date < b.Date and a.Value >= b.Value
            """));
        // and no reading, an expense's included, is below one of an earlier day (equal ones are fine, as in the app)
        Assert.Equal(0, Scalar("""
            select count(*) from OdometerReadings a join OdometerReadings b on a.VehicleId = b.VehicleId
            where a.Date < b.Date and a.Value > b.Value
            """));
        // every expense owns one cost, and at most one reading, of its own vehicle, dated like the expense
        Assert.True(Scalar("select count(*) from Expenses") > 0);
        Assert.Equal(0, Scalar("select count(*) from Expenses e left join Vehicles v on v.Id = e.VehicleId where v.Id is null or e.OwnerId <> v.OwnerId"));
        Assert.Equal(0, Scalar("select count(*) from Expenses e left join Costs c on c.Id = e.CostId where c.Id is null or c.VehicleId <> e.VehicleId or c.Date <> e.Date"));
        Assert.Equal(0, Scalar("select count(*) from Expenses e join OdometerReadings o on o.Id = e.OdometerReadingId where o.VehicleId <> e.VehicleId or o.Date <> e.Date"));
        // every visit links an expense and a schedule of the same vehicle
        Assert.True(Scalar("select count(*) from RecurringCompletions") > 0);
        Assert.Equal(0, Scalar("""
            select count(*) from RecurringCompletions c left join Expenses e on e.Id = c.ExpenseId left join RecurringExpenses r on r.Id = c.RecurringExpenseId
            where e.Id is null or r.Id is null or e.VehicleId <> r.VehicleId
            """));
    }

    [Fact]
    public async Task TheApplicationServesTheSeededData_InNoAuthMode()
    {
        await Run(["--vehicles", "30", "--trashed", "4", "--refuelings", "5-12", "--yes"]);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // a settings file that does not exist, never a developer's: it is read before the host is built, so it goes as an argument
            b.UseSetting("Settings:Path", Path.Combine(Path.GetTempPath(), $"tankstat-seed-app-{Guid.NewGuid():N}-settings.json"));
            // nor a developer's telemetry endpoint: decided before the host is built too, so it goes as an argument as well
            foreach (var key in TelemetryKeys.InEnvironment()) b.UseSetting(key, "");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = Connection,
                ["Auth:Mode"] = "None",
                // a folder that does not exist, never a developer's Storage__Path: the app moves photos at start
                ["Storage:Path"] = Path.Combine(Path.GetTempPath(), $"tankstat-seed-app-{Guid.NewGuid():N}", "uploads"),
            }));
        });
        var client = factory.CreateClient();
        async Task<JsonElement> Gql(string query, object? variables = null) =>
            await (await client.PostAsJsonAsync("/graphql", new { query, variables })).Content.ReadFromJsonAsync<JsonElement>();

        // the paged list is an administrator feature (none exists without authentication), so the home query is used
        var seeded = (await Gql("{ myVehicles { refuelingCount canEdit owner { id } } trashCount }")).GetProperty("data");
        var vehicles = seeded.GetProperty("myVehicles").EnumerateArray().ToList();
        Assert.Equal(30, vehicles.Count);
        Assert.Equal(4, seeded.GetProperty("trashCount").GetInt32());
        Assert.Contains(vehicles, v => v.GetProperty("refuelingCount").GetInt32() > 0);
        Assert.All(vehicles, v => Assert.True(v.GetProperty("canEdit").GetBoolean()));

        // the seeded notifications, plus the reminders the app works out from the seeded schedules when they are looked at
        var inbox = (await Gql("{ notificationCount notifications(take: 100) { kind } }")).GetProperty("data");
        var kinds = inbox.GetProperty("notifications").EnumerateArray().Select(n => n.GetProperty("kind").GetString()).ToList();
        Assert.True(inbox.GetProperty("notificationCount").GetInt32() > 15);
        Assert.Contains("RECURRING_OVERDUE", kinds);
        Assert.Contains("LOG_ACCESS_CHANGED", kinds);

        // the expenses, the visits among them listing the schedule they were for
        var ids = (await Gql("{ myVehicles { id } }")).GetProperty("data").GetProperty("myVehicles").EnumerateArray().Select(v => v.GetProperty("id").GetString()).ToList();
        var expenses = new List<(string? VehicleId, JsonElement Expense)>();
        foreach (var id in ids)
            expenses.AddRange((await Gql("query($id: UUID!) { expenses(vehicleId: $id, take: 100) { category schedules { id } } }", new { id }))
                .GetProperty("data").GetProperty("expenses").EnumerateArray().Select(e => (id, e)));
        Assert.Contains(expenses, e => e.Expense.GetProperty("schedules").GetArrayLength() > 0);
        Assert.Contains(expenses, e => e.Expense.GetProperty("category").GetString() == "Parking");

        // a vehicle with visits goes to the trash too, so emptying it has links to take along (the seeded trashed ones have no schedules)
        var visited = expenses.First(e => e.Expense.GetProperty("schedules").GetArrayLength() > 0).VehicleId;
        Assert.Equal(visited, (await Gql("mutation($id: UUID!) { deleteVehicle(id: $id) { id } }", new { id = visited })).GetProperty("data").GetProperty("deleteVehicle").GetProperty("id").GetString());
        Assert.Equal(5, (await Gql("mutation { emptyTrash }")).GetProperty("data").GetProperty("emptyTrash").GetInt32());
        Assert.Equal(0, Scalar("select count(*) from Vehicles where DeletedAt is not null"));
        Assert.Equal(0, Scalar("select count(*) from Refuelings r left join Vehicles v on v.Id = r.VehicleId where v.Id is null")); // cascade left no orphans
        Assert.Equal(0, Scalar("select count(*) from Expenses e left join Vehicles v on v.Id = e.VehicleId where v.Id is null"));
        Assert.Equal(0, Scalar("select count(*) from Costs c left join Vehicles v on v.Id = c.VehicleId where v.Id is null"));
        Assert.Equal(0, Scalar("select count(*) from OdometerReadings o left join Vehicles v on v.Id = o.VehicleId where v.Id is null"));
        Assert.Equal(0, Scalar("select count(*) from RecurringCompletions c left join Expenses e on e.Id = c.ExpenseId left join RecurringExpenses r on r.Id = c.RecurringExpenseId where e.Id is null or r.Id is null"));
    }
}
