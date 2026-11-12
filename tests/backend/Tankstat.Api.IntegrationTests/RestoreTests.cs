using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tankstat.Application.Backups;
using Tankstat.Application.Setup;
using Tankstat.Domain.Backups;
using Tankstat.Infrastructure.Backups;
using Tankstat.Infrastructure.Persistence;

namespace Tankstat.Api.IntegrationTests;

/// <summary>
/// A restore at start: a host whose backups folder holds a backup and a marker starts, replaces its data with the backup's (an empty
/// database too: the restore sets it up as the backup's migration left it, before the migrations), gets a new generation, writes no backup
/// of the data it replaces (user decision: the backup restored is the one there is), and refuses what it must.
/// </summary>
public sealed class RestoreTests : IDisposable
{
    private const string Sync = "mutation($i: SyncChangesInput!) { syncChanges(input: $i) { applied parked results { id status entityId version } } }";
    private const string Instance = "{ session { instance { generation restorePoint } } }";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tankstat-it-{Guid.NewGuid():N}-restore");
    private string Folder(string name) => Path.Combine(_root, name);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private TestApp Host(string name, bool migrated, Dictionary<string, string?>? extra = null, string? backups = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:Mode"] = "None",
            ["Backups:Path"] = backups ?? Folder(name + "-backups"),
            ["Storage:Path"] = Folder(name + "-uploads"),
            ["Storage:PhotosPath"] = Folder(name + "-photos"),
            ["DataProtection:KeysPath"] = Folder(name + "-keys"),
        };
        foreach (var (key, value) in extra ?? []) settings[key] = value;
        return new TestApp(settings, migrated: migrated);
    }

    private static async Task<string> AddVehicle(TestApp app, string name)
    {
        using var client = app.NewClient();
        return (await client.Gql("mutation($n: String!) { addVehicle(input: { name: $n, fuelType: PETROL }) { id } }", new { n = name })).Data().GetProperty("addVehicle").GetProperty("id").GetString()!;
    }

    private static async Task<string[]> Vehicles(TestApp app)
    {
        using var client = app.NewClient();
        return (await client.Gql("{ myVehicles { name } }")).Data().GetProperty("myVehicles").EnumerateArray().Select(v => v.GetProperty("name").GetString()!).Order().ToArray();
    }

    private static async Task<(string Generation, string? RestorePoint)> InstanceOf(TestApp app)
    {
        using var client = app.NewClient();
        var instance = (await client.Gql(Instance)).Data().GetProperty("session").GetProperty("instance");
        return (instance.GetProperty("generation").GetString()!, instance.GetProperty("restorePoint").GetString());
    }

    private static Task<BackupResult> Backup(TestApp app, BackupRequest? request = null) =>
        app.Factory.Services.GetRequiredService<IBackupWriter>().WriteAsync(request ?? new BackupRequest(), default);

    /// <summary>Puts a backup into a folder and the marker that asks for it, as the mutation (or the CLI) does.</summary>
    private static string Stage(string backupsFolder, string backupFile, string? identity = null)
    {
        Directory.CreateDirectory(backupsFolder);
        var staged = Path.Combine(backupsFolder, Path.GetFileName(backupFile));
        File.Copy(backupFile, staged);
        new RestoreMarker(Path.GetFileName(backupFile), Guid.Empty, DateTimeOffset.UtcNow, identity).Write(backupsFolder);
        return staged;
    }

    [Fact]
    public async Task AHostWithAMarker_StartsWithTheBackupsData_FromAnEmptyDatabase_WritesNoBackup_AndHasANewGeneration()
    {
        using var a = Host("a", migrated: true);
        await AddVehicle(a, "Golf");
        await AddVehicle(a, "Polo");
        var (generationA, _) = await InstanceOf(a);
        var backup = await Backup(a);

        var folderB = Folder("b-backups");
        Stage(folderB, backup.Path);
        using var b = Host("b", migrated: false, backups: folderB);

        Assert.Equal(["Golf", "Polo"], await Vehicles(b));
        var (generationB, restorePoint) = await InstanceOf(b);
        Assert.NotEqual(generationA, generationB);
        Assert.Equal(backup.Manifest.CreatedAt, DateTimeOffset.Parse(restorePoint!));
        Assert.False(File.Exists(RestoreMarker.PathIn(folderB)));
        var files = Directory.GetFiles(folderB).Select(Path.GetFileName).ToList();
        Assert.Equal([Path.GetFileName(backup.Path)], files); // the backup restored, nothing more: a restore writes no backup of what it replaces
        var lines = b.Log.From("Tankstat.Infrastructure.Backups.RestoreStep").Where(e => e.Level == LogLevel.Information).ToList();
        Assert.Contains(lines, e => e.Values.ContainsKey("Generation") && (Guid)e.Values["Generation"]! == Guid.Parse(generationB));
        Assert.DoesNotContain(b.Log.From("Tankstat"), e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task ARestore_ReplacesTheDataAsAWhole_PicturesAndKeysIncluded_AndARestoredSessionStillWorks()
    {
        // A Standalone host: its administrator signs in (a session row, the keys that protect the cookies), uploads a picture.
        var standalone = TestApp.StandaloneSettings();
        using var a = Host("a", migrated: true, extra: standalone);
        var client = a.NewClient();
        await client.LoginAs(TestApp.AdminEmail, TestApp.AdminPassword);
        var car = (await client.Gql("mutation { addVehicle(input: { name: \"Golf\", fuelType: PETROL }) { id } }")).Data().GetProperty("addVehicle").GetProperty("id").GetString()!;
        var picture = await client.PutAsync($"/media/vehicles/{car}/picture", new ByteArrayContent([0xff, 0xd8, 0xff, 0xe0, 1, 2, 3]) { Headers = { ContentType = new("image/jpeg") } });
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        var pictureUrl = (await client.Gql("{ myVehicles { pictureUrl } }")).Data().GetProperty("myVehicles")[0].GetProperty("pictureUrl").GetString()!;
        var cookies = await CookiesOf(a);

        var identity = BackupKeys.Identity("hunter2 hunter2", Encoding.ASCII.GetBytes("tankstat-salt-01"), new Argon2idParameters(256, 1, 1));
        var encryption = new BackupEncryption(BackupEncryption.AgeX25519, AgeKeys.FormatRecipient(BackupKeys.Recipient(identity)), Convert.ToBase64String(Encoding.ASCII.GetBytes("tankstat-salt-01")), new Argon2idParameters(256, 1, 1));
        var backup = await Backup(a, new BackupRequest(Encryption: encryption));
        Assert.True(backup.Manifest.Contents.Keys);

        // B has data of its own, a user of its own (its administrator is a copy of A's, both databases being copies of TestApp's set-up
        // template), its own administrator's session, and its own keys: all of it replaced at its restart.
        var folderB = Folder("b-backups");
        using var shared = new SharedData();
        var b = shared.Host(TestApp.StandaloneSettings(new() { ["Backups:Path"] = folderB }));
        var ownClient = b.NewClient();
        await ownClient.LoginAs(TestApp.AdminEmail, TestApp.AdminPassword);
        await ownClient.Gql("mutation { addVehicle(input: { name: \"Own car\", fuelType: PETROL }) { id } }");
        var other = (await ownClient.Gql("mutation($i: CreateUserInput!) { createUser(input: $i) { reset { token } } }",
            new { i = new { email = "other@example.com", displayName = "Other", isAdmin = true } })).Data().GetProperty("createUser");
        (await b.NewClient().Gql("mutation($i: ResetPasswordInput!) { resetPassword(input: $i) }",
            new { i = new { token = other.GetProperty("reset").GetProperty("token").GetString(), newPassword = "other-password-1", newPasswordConfirmation = "other-password-1" } })).Data();
        await b.NewClient().LoginAs("other@example.com", "other-password-1"); // B's own administrator, there before the restore
        var ownCookies = await CookiesOf(b);
        b.Dispose(); // stopped, like a server before its restart

        Stage(folderB, backup.Path, AgeKeys.FormatIdentity(identity));
        using var restarted = shared.Host(TestApp.StandaloneSettings(new() { ["Backups:Path"] = folderB }));

        // A's administrator's cookies work on the restarted B (the restored keys read them, the restored session row is there); B's own are gone.
        var kept = restarted.NewClientHolding(cookies);
        var session = (await kept.Gql("{ session { user { email } } myVehicles { name pictureUrl } }")).Data();
        Assert.Equal(TestApp.AdminEmail, session.GetProperty("session").GetProperty("user").GetProperty("email").GetString());
        Assert.Equal(["Golf"], session.GetProperty("myVehicles").EnumerateArray().Select(v => v.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(pictureUrl, session.GetProperty("myVehicles")[0].GetProperty("pictureUrl").GetString());
        var image = await kept.GetAsync(pictureUrl);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode); // the picture file came along
        var refresh = new HttpRequestMessage(HttpMethod.Post, "/auth/token/refresh") { Headers = { { "X-Requested-With", "fetch" }, { "Cookie", cookies } } };
        Assert.Equal(HttpStatusCode.NoContent, (await restarted.NewClientWithoutRedirects().SendAsync(refresh)).StatusCode);
        // B's own session is gone with B's data (its row and the keys that protected its cookies), and so is B's own administrator.
        var ownRefresh = new HttpRequestMessage(HttpMethod.Post, "/auth/token/refresh") { Headers = { { "X-Requested-With", "fetch" }, { "Cookie", ownCookies } } };
        Assert.Equal(HttpStatusCode.Unauthorized, (await restarted.NewClientWithoutRedirects().SendAsync(ownRefresh)).StatusCode);
        Assert.NotNull((await restarted.NewClient().Gql("mutation($i: LoginInput!) { login(input: $i) { id } }",
            new { i = new { email = "other@example.com", password = "other-password-1" } })).ErrorCode());
    }

    /// <summary>Both cookies of a signed-in client, as a browser would send them to the token endpoints.</summary>
    private static async Task<string> CookiesOf(TestApp app)
    {
        var response = await app.NewClient().PostAsJsonAsync("/graphql", new
        {
            query = "mutation($i: LoginInput!) { login(input: $i) { id } }",
            variables = new { i = new { email = TestApp.AdminEmail, password = TestApp.AdminPassword } },
        });
        return string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(h => h[..h.IndexOf(';')]));
    }

    [Theory]
    [InlineData("otherMigration")]
    [InlineData("missingFile")]
    [InlineData("wrongIdentity")]
    [InlineData("damaged")]
    public async Task ARestoreThatCannotBeApplied_IsRefused_TheMarkerRenamed_AndTheHostServesItsOwnData(string how)
    {
        using var a = Host("a", migrated: true);
        await AddVehicle(a, "Golf");
        var identity = BackupKeys.Identity("hunter2 hunter2", Encoding.ASCII.GetBytes("tankstat-salt-01"), new Argon2idParameters(256, 1, 1));
        var encryption = new BackupEncryption(BackupEncryption.AgeX25519, AgeKeys.FormatRecipient(BackupKeys.Recipient(identity)), Convert.ToBase64String(Encoding.ASCII.GetBytes("tankstat-salt-01")), new Argon2idParameters(256, 1, 1));
        var backup = await Backup(a, new BackupRequest(Encryption: encryption));

        var folderB = Folder("b-backups");
        var staged = Stage(folderB, backup.Path, how == "wrongIdentity" ? AgeKeys.FormatIdentity(BackupKeys.Identity("nope", Encoding.ASCII.GetBytes("tankstat-salt-01"), new Argon2idParameters(256, 1, 1))) : AgeKeys.FormatIdentity(identity));
        switch (how)
        {
            case "otherMigration":
                RewriteManifest(staged, json => json.Replace(backup.Manifest.Migration!, "20200101000000_SomethingElse"));
                break;
            case "missingFile":
                File.Delete(staged);
                break;
            case "damaged":
                var bytes = await File.ReadAllBytesAsync(staged);
                bytes[300] ^= 0xff;
                await File.WriteAllBytesAsync(staged, bytes);
                break;
        }
        using var b = Host("b", migrated: true, backups: folderB);
        await AddVehicle(b, "Own car"); // starts, with its own data

        Assert.Equal(["Own car"], await Vehicles(b));
        Assert.False(File.Exists(RestoreMarker.PathIn(folderB)));
        var refused = RestoreMarkerFile.Read(Assert.Single(Directory.GetFiles(folderB, "restore.refused.*")));
        Assert.Equal(Path.GetFileName(staged), refused.File);
        Assert.Null(refused.Identity); // it opens every backup of the folder: kept nowhere in it
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Directory.GetFiles(folderB, "restore.refused.*")[0]));
        Assert.DoesNotContain(Directory.GetFiles(folderB, "tankstat-backup-*"), f => f != staged && BackupFile.ReadManifest(f).SafetyNet); // no safety net: nothing was about to be replaced
        var refusal = Assert.Single(b.Log.From("Tankstat.Infrastructure.Backups.RestoreStep").Where(e => e.Level == LogLevel.Error));
        Assert.Contains(how switch { "otherMigration" => "otherMigration", "wrongIdentity" => "wrongKey", "damaged" => "corrupt", _ => "cannot be read" }, (string)refusal.Values["Reason"]!);
    }

    /// <summary>
    /// A backup made at an older migration of this release: <paramref name="app"/>'s database taken back to the migration before
    /// <paramref name="before"/> (its rows kept, the newer columns and tables gone), then backed up as it is there.
    /// </summary>
    private static async Task<BackupResult> OlderBackup(TestApp app, string before)
    {
        await using (var db = await app.Factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            var ids = MigrationModels.Ids(db);
            await db.GetService<IMigrator>().MigrateAsync(ids[ids.ToList().FindIndex(id => MigrationNames.Of(id) == before) - 1]);
        }
        return await Backup(app);
    }

    private static async Task LogFillUp(TestApp app, string vehicleId, string date, long odometer, decimal volume)
    {
        using var client = app.NewClient();
        (await client.Gql("mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }",
            new { i = new { vehicleId, date, volume, totalCost = volume * 600, currency = "HUF", odometer, isFullTank = true } })).Data();
    }

    private static async Task<string?> LastMigration(TestApp app)
    {
        await using var db = await DbOf(app);
        return (await db.Database.GetAppliedMigrationsAsync()).LastOrDefault();
    }

    [Fact]
    public async Task ABackupOfAnOlderMigration_IsRestored_AndBroughtUpToDate_WithWhatFollowsTheNewerMigrations()
    {
        using var a = Host("a", migrated: true);
        var golf = await AddVehicle(a, "Golf");
        await LogFillUp(a, golf, "2026-09-01", 10_000, 40);
        await LogFillUp(a, golf, "2026-09-10", 10_500, 30);
        var backup = await OlderBackup(a, "AddRefuelingConsumption");
        Assert.Equal("AddExpenses", MigrationNames.Of(backup.Manifest.Migration));
        Assert.DoesNotContain("UiSettings", backup.Manifest.Contents.Tables.Keys); // dumped as that migration left the database

        var folderB = Folder("b-backups");
        Stage(folderB, backup.Path);
        using var b = Host("b", migrated: true, backups: folderB); // a database at this release's migration

        Assert.Equal(["Golf"], await Vehicles(b));
        using var client = b.NewClient();
        var logs = (await client.Gql("query($v: UUID!) { refuelings(vehicleId: $v) { odometer consumption } }", new { v = golf })).Data().GetProperty("refuelings").EnumerateArray().ToList();
        Assert.Equal(6.0, logs.Single(l => l.GetProperty("odometer").GetDouble() == 10_500).GetProperty("consumption").GetDouble(), 3); // worked out after the newer migrations ran
        await using (var db = await DbOf(b))
            Assert.Equal(MigrationModels.Ids(db).Last(), await LastMigration(b)); // up to date
        Assert.Empty(Directory.GetFiles(folderB, "restore.*"));
        Assert.Single(Directory.GetFiles(folderB, "tankstat-backup-*")); // the backup restored: no backup of the data it replaced, before or after the migrations
        Assert.Contains(b.Log.From("Tankstat.Infrastructure.Backups.RestoreStep"), e => e.Level == LogLevel.Information && e.Values.ContainsKey("Migration"));
        Assert.DoesNotContain(b.Log.From("Tankstat"), e => e.Level >= LogLevel.Warning);
    }

    /// <summary>A host over one database, mode None, with a vehicle of its own: what an older backup's restore must leave as it was when it refuses.</summary>
    private async Task<(SharedData Shared, Dictionary<string, string?> Settings, string Vehicle)> HostWithOwnData(string backups)
    {
        var shared = new SharedData();
        var settings = new Dictionary<string, string?> { ["Auth:Mode"] = "None", ["Backups:Path"] = backups };
        using var first = shared.Host(settings);
        return (shared, settings, await AddVehicle(first, "Own car"));
    }

    [Fact]
    public async Task AnOlderBackup_WhoseTablesTheDatabaseWillNotDrop_IsRefused_AndEveryTableAndRowStays()
    {
        using var a = Host("a", migrated: true);
        await AddVehicle(a, "Golf");
        var backup = await OlderBackup(a, "AddRefuelingConsumption");

        // B's database also holds a table of the operator's own, whose row refers to B's vehicle: Vehicles cannot be dropped while it is
        // there, though the tables that refer to Vehicles (refuellings among them) are dropped before it.
        var folderB = Folder("b-backups");
        var (shared, settings, own) = await HostWithOwnData(folderB);
        using var data = shared;
        using (var first = shared.Host(settings))
        {
            await using var db = await DbOf(first);
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"OperatorNotes\" (\"VehicleId\" TEXT NOT NULL REFERENCES \"Vehicles\" (\"Id\"))");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO \"OperatorNotes\" SELECT \"Id\" FROM \"Vehicles\"");
        }
        Stage(folderB, backup.Path);
        using var b = shared.Host(settings);

        // The drops were undone together: B serves its own data, its refuellings' table included.
        Assert.Equal(["Own car"], await Vehicles(b));
        using var client = b.NewClient();
        Assert.Equal(0, (await client.Gql("query($v: UUID!) { refuelings(vehicleId: $v) { id } }", new { v = own })).Data().GetProperty("refuelings").GetArrayLength());
        Assert.Single(Directory.GetFiles(folderB, "restore.refused.*"));
        Assert.Empty(Directory.GetFiles(folderB, "restore.failed.*"));
    }

    [Fact]
    public async Task AnOlderBackup_WhoseRowsTheLoadWouldRefuse_IsRefusedBeforeAnyTableIsDropped()
    {
        using var a = Host("a", migrated: true);
        await AddVehicle(a, "Golf");
        var backup = await OlderBackup(a, "AddRefuelingConsumption");

        var folderB = Folder("b-backups");
        var (shared, settings, _) = await HostWithOwnData(folderB);
        using var data = shared;
        var staged = Stage(folderB, backup.Path);
        // A manifest whose count is not the file's (the manifest is outside the checksum): the load would refuse it after every table was gone.
        RewriteManifest(staged, json =>
        {
            Assert.Contains("\"Vehicles\": 1", json);
            return json.Replace("\"Vehicles\": 1", "\"Vehicles\": 2");
        });
        using var b = shared.Host(settings);

        Assert.Equal(["Own car"], await Vehicles(b));
        var refusal = Assert.Single(b.Log.From("Tankstat.Infrastructure.Backups.RestoreStep"), e => e.Level == LogLevel.Error);
        Assert.Equal("backup.corrupt", (string)refusal.Values["Reason"]!);
        Assert.Single(Directory.GetFiles(folderB, "restore.refused.*"));
    }

    [Fact]
    public async Task ABackupMadeBeforeTheInstanceRecordedItsSignInMode_EndsEverySessionItBrings()
    {
        // A Standalone host whose administrator is signed in, backed up as the release before the instance recorded its sign-in mode left
        // it: the sessions come along, the mode they were issued in does not.
        using var a = Host("a", migrated: true, extra: TestApp.StandaloneSettings());
        var cookies = await CookiesOf(a);
        var backup = await OlderBackup(a, "AddInstanceState");
        Assert.DoesNotContain("InstanceState", backup.Manifest.Contents.Tables.Keys);
        Assert.True(backup.Manifest.Contents.Tables["UserSessions"] > 0);

        var folderB = Folder("b-backups");
        Stage(folderB, backup.Path);
        using var b = Host("b", migrated: true, extra: TestApp.StandaloneSettings(), backups: folderB);

        _ = b.Factory.Services; // the host starts, and the restore runs at its start
        Assert.Empty(Directory.GetFiles(folderB, "restore.*")); // applied
        await using (var db = await DbOf(b))
            Assert.All(await db.UserSessions.AsNoTracking().ToListAsync(), s => Assert.NotNull(s.RevokedAt));
        var refresh = new HttpRequestMessage(HttpMethod.Post, "/auth/token/refresh") { Headers = { { "X-Requested-With", "fetch" }, { "Cookie", cookies } } };
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.NewClientWithoutRedirects().SendAsync(refresh)).StatusCode);
        Assert.Contains(b.Log.From("Tankstat.Infrastructure.Backups.RestoreStep"), e => e.Level == LogLevel.Information && e.Values.ContainsKey("Count") && !e.Values.ContainsKey("What"));
    }

    private static async Task<AppDbContext> DbOf(TestApp app) => await app.Factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();

    [Fact]
    public async Task ABackupOfAMigrationThisReleaseDoesNotKnow_IsRefused_AndTheHostKeepsItsData()
    {
        using var a = Host("a", migrated: true);
        await AddVehicle(a, "Golf");
        var backup = await Backup(a);
        var folderB = Folder("b-backups");
        var staged = Stage(folderB, backup.Path);
        RewriteManifest(staged, json => json.Replace(backup.Manifest.Migration!, "20990101000000_AddSomethingNewer"));

        using var b = Host("b", migrated: true, backups: folderB);
        await AddVehicle(b, "Own car");

        Assert.Equal(["Own car"], await Vehicles(b));
        Assert.Single(Directory.GetFiles(folderB, "restore.refused.*"));
    }

    [Fact]
    public async Task ABackupOfAnotherProvider_StampedWithItsOwnTimestampsForTheSameMigrations_IsRestored()
    {
        // Each provider's migrations carry their own timestamps (SQLite's 20261001054544_InitialCreate is PostgreSQL's 20261001054547_...):
        // a backup is compared by the name of its last migration, which is what lets it move from one provider to another.
        using var a = Host("a", migrated: true);
        await AddVehicle(a, "Golf");
        var backup = await Backup(a);
        var folderB = Folder("b-backups");
        var staged = Stage(folderB, backup.Path);
        RewriteManifest(staged, json => json.Replace(backup.Manifest.Migration!, "20990101000000_" + MigrationNames.Of(backup.Manifest.Migration)));
        Assert.NotEqual(backup.Manifest.Migration, BackupFile.ReadManifest(staged).Migration);

        using var b = Host("b", migrated: false, backups: folderB);

        Assert.Equal(["Golf"], await Vehicles(b));
        Assert.Empty(Directory.GetFiles(folderB, "restore.refused.*"));
    }

    [Fact]
    public async Task ABackupWithoutThePhotos_RestoredWhereTheyLiveWithThePictures_LeavesThePhotosOnDiskAlone()
    {
        // A keeps its photos apart and backs up without them; B keeps everything in one folder, where the operator put the photos back by hand.
        using var a = Host("a", migrated: true);
        var clientA = a.NewClient();
        var car = await AddVehicle(a, "Golf");
        var log = (await clientA.Gql("mutation($v: UUID!) { logRefueling(input: { vehicleId: $v, date: \"2026-09-01\", volume: 40, totalCost: 60, currency: \"EUR\", odometer: 1000, isFullTank: true }) { id } }", new { v = car }))
            .Data().GetProperty("logRefueling").GetProperty("id").GetString()!;
        var jpeg = () => new ByteArrayContent([0xff, 0xd8, 0xff, 0xe0, 1, 2, 3]) { Headers = { ContentType = new("image/jpeg") } };
        Assert.Equal(HttpStatusCode.OK, (await clientA.PutAsync($"/media/vehicles/{car}/picture", jpeg())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clientA.PutAsync($"/media/refuelings/{log}/photos", jpeg())).StatusCode);
        var urls = (await clientA.Gql("query($v: UUID!) { myVehicles { pictureUrl } refuelings(vehicleId: $v) { photos { url } } }", new { v = car })).Data();
        var pictureUrl = urls.GetProperty("myVehicles")[0].GetProperty("pictureUrl").GetString()!;
        var photoUrl = urls.GetProperty("refuelings")[0].GetProperty("photos")[0].GetProperty("url").GetString()!;
        var backup = await Backup(a);
        Assert.Equal(BackupPhotos.LeftOut, backup.Manifest.Contents.Photos);

        var folderB = Folder("b-backups");
        Stage(folderB, backup.Path);
        var picturesB = Folder("b-uploads");
        foreach (var file in Directory.EnumerateFiles(Folder("a-photos"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(picturesB, Path.GetRelativePath(Folder("a-photos"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        using var b = new TestApp(new() { ["Auth:Mode"] = "None", ["Backups:Path"] = folderB, ["Storage:Path"] = picturesB, ["Storage:PhotosPath"] = null, ["DataProtection:KeysPath"] = Folder("b-keys") }, migrated: false);
        var clientB = b.NewClient();

        Assert.Equal(HttpStatusCode.OK, (await clientB.GetAsync(pictureUrl)).StatusCode); // from the backup
        Assert.Equal(HttpStatusCode.OK, (await clientB.GetAsync(photoUrl)).StatusCode); // not in the backup: the file on disk stayed and matches its log
        Assert.Contains(b.Log.From("Tankstat.Infrastructure.Backups.RestoreStep"), e => e.Values.TryGetValue("Kept", out var kept) && ((string?)kept)?.Length > 0);
    }

    [Fact]
    public async Task ABackupWithoutThePhotos_AlsoLeavesAlonePhotosStillBelowThePicturesRoot_WhereTheyHaveARootOfTheirOwn()
    {
        using var a = Host("a", migrated: true);
        var backup = await Backup(a);
        Assert.Equal(BackupPhotos.LeftOut, backup.Manifest.Contents.Photos);
        var folderB = Folder("b-backups");
        Stage(folderB, backup.Path);
        // A photo not moved to B's photos root yet (PhotosMove runs after the restore, at this very start, and leaves one it cannot move).
        var relative = Path.Combine("vehicles", "aa", "refuelings", "bb", "photo.jpg");
        var waiting = Path.Combine(Folder("b-uploads"), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(waiting)!);
        await File.WriteAllBytesAsync(waiting, [1, 2, 3]);

        using var b = Host("b", migrated: false, backups: folderB);
        await InstanceOf(b); // started: the restore is behind it

        Assert.True(File.Exists(waiting) || File.Exists(Path.Combine(Folder("b-photos"), relative))); // where it was, or moved on afterwards
        Assert.Empty(Directory.GetFiles(folderB, "restore.*"));
    }

    [Fact]
    public async Task ABackupWithAPhotosRootOfItsOwn_ReplacesThePhotos()
    {
        using var a = Host("a", migrated: true);
        var backup = await Backup(a, new BackupRequest(Photos: true));
        Assert.Equal(BackupPhotos.Separate, backup.Manifest.Contents.Photos);
        var folderB = Folder("b-backups");
        Stage(folderB, backup.Path);
        // B's own photo, in its photos root, which Backups:Photos (off, by default) leaves out of B's other backups.
        var own = Path.Combine(Folder("b-photos"), "vehicles", "aa", "refuelings", "bb", "own.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(own)!);
        await File.WriteAllBytesAsync(own, [1, 2, 3]);

        using var b = Host("b", migrated: false, backups: folderB);
        await InstanceOf(b); // started: the restore is behind it

        Assert.False(File.Exists(own)); // the photos root is the backup's now
        Assert.Single(Directory.GetFiles(folderB, "tankstat-backup-*")); // and no backup holds the photo it replaced: a restore writes none
    }

    [Theory]
    [InlineData("unreadable")]
    [InlineData("noFile")]
    public async Task AMarkerTheAppMayNotRead_OrThatNamesNoBackup_IsRefused_AndTheHostStartsWithItsOwnData(string how)
    {
        if (how == "unreadable" && (OperatingSystem.IsWindows() || Environment.UserName == "root")) return; // root reads any file
        var folderB = Folder("b-backups");
        Directory.CreateDirectory(folderB);
        var marker = RestoreMarker.PathIn(folderB);
        if (how == "noFile") await File.WriteAllTextAsync(marker, "{}");
        else
        {
            new RestoreMarker("tankstat-backup-1.zip", Guid.Empty, DateTimeOffset.UtcNow, null).Write(folderB);
            File.SetUnixFileMode(marker, UnixFileMode.None); // written by root (`sudo ... restore`): the app's user may not read it
        }

        using var b = Host("b", migrated: true, backups: folderB);
        await AddVehicle(b, "Own car");

        Assert.Equal(["Own car"], await Vehicles(b));
        Assert.False(File.Exists(marker));
        Assert.Single(Directory.GetFiles(folderB, "restore.refused.*"));
        var refusal = Assert.Single(b.Log.From("Tankstat.Infrastructure.Backups.RestoreStep").Where(e => e.Level == LogLevel.Error));
        Assert.Contains("marker cannot be read", (string)refusal.Values["Reason"]!);
    }

    [Fact]
    public async Task SomethingInAStorageRootTheAppMayNotRemove_StaysAsItIs_AndTheRestoreIsApplied()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return; // root removes anything
        using var a = Host("a", migrated: true);
        await AddVehicle(a, "Golf");
        var backup = await Backup(a);
        var folderB = Folder("b-backups");
        Stage(folderB, backup.Path);
        // The lost+found of a disk mounted as the pictures root: root's, and not empty after a check of the disk.
        var lostFound = Path.Combine(Folder("b-uploads"), "lost+found");
        Directory.CreateDirectory(lostFound);
        await File.WriteAllTextAsync(Path.Combine(lostFound, "#1234"), "recovered");
        File.SetUnixFileMode(lostFound, UnixFileMode.None);
        try
        {
            using var b = Host("b", migrated: false, backups: folderB);

            Assert.Equal(["Golf"], await Vehicles(b));
            Assert.Empty(Directory.GetFiles(folderB, "restore.*")); // applied: the marker is gone, neither refused nor failed
            Assert.True(Directory.Exists(lostFound));
            Assert.Contains(b.Log.From("Tankstat.Infrastructure.Backups.RestoreStep"), e => e.Level == LogLevel.Warning);
        }
        finally
        {
            File.SetUnixFileMode(lostFound, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); // so the test's folder can go
        }
    }

    [Fact]
    public async Task ABackupWithoutAnAdministrator_IsRefusedInStandalone_AndTheStartGoesOn()
    {
        using var a = Host("a", migrated: true); // sign-in off: the backup holds no user at all
        await AddVehicle(a, "Golf");
        var backup = await Backup(a);
        var folderB = Folder("b-backups");
        using var shared = new SharedData();
        using (var first = shared.Host(TestApp.StandaloneSettings(new() { ["Backups:Path"] = folderB })))
            await first.NewClient().LoginAs(TestApp.AdminEmail, TestApp.AdminPassword); // its setup made the administrator
        Stage(folderB, backup.Path);

        // Only the setup page makes a first administrator (#149): the backup, which holds none, is refused, and the instance keeps its own.
        using var restarted = shared.Host(TestApp.StandaloneSettings(new() { ["Backups:Path"] = folderB }));

        await restarted.NewClient().LoginAs(TestApp.AdminEmail, TestApp.AdminPassword); // the start went on, with the instance's own administrator
        Assert.Single(Directory.GetFiles(folderB, "restore.refused.*"));
        var refusal = Assert.Single(restarted.Log.From("Tankstat.Infrastructure.Backups.RestoreStep").Where(e => e.Level == LogLevel.Error));
        Assert.Contains("standaloneNeedsAdministrator", (string)refusal.Values["Reason"]!);
    }

    [Fact]
    public async Task ABackupWhoseSetupWaits_IsRestoredInStandalone_AndTheSetupWaitsOnIt()
    {
        using var a = Host("a", migrated: true, new() { ["Setup:Enabled"] = "true" }); // sign-in off and waiting: no user, no administrator
        var backup = await Backup(a);
        var folderB = Folder("b-backups");
        Stage(folderB, backup.Path);

        using var b = Host("b", migrated: false,
            new() { ["Auth:Mode"] = "Standalone", ["Setup:Enabled"] = "true" }, folderB);

        Assert.True(b.Factory.Services.GetRequiredService<SetupStatus>().Pending); // the start waits for the setup page, which makes the administrator
        Assert.Empty(Directory.GetFiles(folderB, "restore.*")); // applied, neither refused nor failed
    }

    [Fact]
    public async Task ABackupWhoseSetupWaits_IsRefusedInStandalone_WhereTheSetupIsTurnedOff_AndTheStartGoesOn()
    {
        using var a = Host("a", migrated: true, new() { ["Setup:Enabled"] = "true" }); // sign-in off and waiting: no user, no administrator
        var backup = await Backup(a);
        var folderB = Folder("b-backups");
        using var shared = new SharedData();
        Stage(folderB, backup.Path);

        // The setup turned off here (TestApp's own default): the start after the restore would not wait for the setup page but stop, and every
        // start after it, Standalone having no administrator then (#149). So the backup is refused, and the instance keeps its data.
        using var restarted = shared.Host(TestApp.StandaloneSettings(new() { ["Backups:Path"] = folderB }));

        await restarted.NewClient().LoginAs(TestApp.AdminEmail, TestApp.AdminPassword); // the start went on, with the instance's own administrator
        Assert.Single(Directory.GetFiles(folderB, "restore.refused.*"));
        var refusal = Assert.Single(restarted.Log.From("Tankstat.Infrastructure.Backups.RestoreStep"), e => e.Level == LogLevel.Error);
        Assert.Contains("standaloneNeedsAdministrator", (string)refusal.Values["Reason"]!);
    }

    /// <summary>Changes the plain manifest of a backup file (the checksum covers the inner archive only).</summary>
    internal static void RewriteManifest(string file, Func<string, string> change)
    {
        using var zip = ZipFile.Open(file, ZipArchiveMode.Update);
        var entry = zip.GetEntry(BackupManifest.FileName)!;
        string json;
        using (var reader = new StreamReader(entry.Open())) json = reader.ReadToEnd();
        entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry(BackupManifest.FileName, CompressionLevel.NoCompression).Open());
        writer.Write(change(json));
    }

    [Fact]
    public async Task AfterARestore_TheSyncLedgerIsTheBackups_SoARepeatedAddIsAnsweredFromIt_AndAChangeFromAfterTheBackupParks()
    {
        using var a = Host("a", migrated: true);
        var carId = Guid.NewGuid();
        var changeId = Guid.NewGuid();
        var clientA = a.NewClient();
        var first = (await clientA.Gql(Sync, new { i = new { changes = new object[] { new { id = changeId, addVehicle = new { id = carId, name = "Golf", fuelType = "PETROL" } } } } })).Data().GetProperty("syncChanges");
        Assert.Equal(1, first.GetProperty("applied").GetInt32());
        var backup = await Backup(a);
        // Edited after the backup: version 2 on A, version 1 in the backup.
        await clientA.Gql("mutation($i: UpdateVehicleInput!) { updateVehicle(input: $i) { id } }", new { i = new { id = carId, name = "Golf GTI", fuelType = "PETROL" } });

        var folderB = Folder("b-backups");
        Stage(folderB, backup.Path);
        using var b = Host("b", migrated: false, backups: folderB);
        var clientB = b.NewClient();

        var again = (await clientB.Gql(Sync, new { i = new { changes = new object[] { new { id = changeId, addVehicle = new { id = carId, name = "Golf", fuelType = "PETROL" } } } } })).Data().GetProperty("syncChanges");
        Assert.Equal("APPLIED", again.GetProperty("results")[0].GetProperty("status").GetString()); // from the ledger, not applied twice
        Assert.Equal(carId.ToString(), again.GetProperty("results")[0].GetProperty("entityId").GetString());
        Assert.Equal(["Golf"], await Vehicles(b));
        var late = (await clientB.Gql(Sync, new { i = new { changes = new object[] { new { id = Guid.NewGuid(), expectedVersion = 2, updateVehicle = new { id = carId, name = "Polo", fuelType = "PETROL" } } } } })).Data().GetProperty("syncChanges");
        Assert.Equal(1, late.GetProperty("parked").GetInt32()); // the device's version is from the history the restore replaced
    }

    /// <summary>A disk that cannot take the safety net.</summary>
    private sealed class UnwritableBackups : IBackupWriter
    {
        public Task<BackupResult> WriteAsync(BackupRequest request, CancellationToken ct) => throw new IOException("There is no room left on the device.");
    }

    [Fact]
    public async Task AnInstanceRowWrittenBeforeGenerations_GetsOneAtTheNextStart_AndKeepsIt_AndItsLastStartMode()
    {
        using var shared = new SharedData();
        shared.Host(new() { ["Auth:Mode"] = "None", ["Backups:Path"] = Folder("backups") }).Dispose();
        await using (var connection = new SqliteConnection($"Data Source={shared.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var blank = connection.CreateCommand();
            blank.CommandText = "UPDATE InstanceState SET Generation = '00000000-0000-0000-0000-000000000000'"; // as the migration leaves a row of an older release
            Assert.Equal(1, await blank.ExecuteNonQueryAsync());
        }

        string generation;
        using (var restarted = shared.Host(new() { ["Auth:Mode"] = "None", ["Backups:Path"] = Folder("backups") }))
            generation = (await InstanceOf(restarted)).Generation;
        using var again = shared.Host(new() { ["Auth:Mode"] = "None", ["Backups:Path"] = Folder("backups") });

        Assert.NotEqual(Guid.Empty, Guid.Parse(generation));
        Assert.Equal(generation, (await InstanceOf(again)).Generation);
        await using var check = new SqliteConnection($"Data Source={shared.DatabasePath};Pooling=False");
        await check.OpenAsync();
        await using var mode = check.CreateCommand();
        mode.CommandText = "SELECT LastStartMode FROM InstanceState";
        Assert.Equal("None", await mode.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ARestoreWritesNoBackup_SoADiskThatCouldTakeNoneStopsNothing_AndWhatChangedSinceTheBackupIsGone()
    {
        using var a = Host("a", migrated: true);
        await AddVehicle(a, "Golf");
        var backup = await Backup(a);
        var folderB = Folder("b-backups");
        using var shared = new SharedData();
        var first = shared.Host(new() { ["Auth:Mode"] = "None", ["Backups:Path"] = folderB });
        await AddVehicle(first, "Own car");
        var (generation, _) = await InstanceOf(first);
        first.Dispose();
        Stage(folderB, backup.Path);

        using var restarted = shared.Host(new() { ["Auth:Mode"] = "None", ["Backups:Path"] = folderB }, services: s => s.AddSingleton<IBackupWriter, UnwritableBackups>());

        Assert.Equal(["Golf"], await Vehicles(restarted)); // the backup's: the own car is gone, and in no backup (user decision)
        Assert.NotEqual(generation, (await InstanceOf(restarted)).Generation);
        Assert.Empty(Directory.GetFiles(folderB, "restore.*"));
        Assert.DoesNotContain(restarted.Log.From("Tankstat"), e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task AFailureAfterTheTablesWereLoaded_RenamesTheMarkerToFailed_AndTheHostServesWhatWasLoaded()
    {
        using var a = Host("a", migrated: true);
        await AddVehicle(a, "Golf");
        var backup = await Backup(a);
        var folderB = Folder("b-backups");
        using var shared = new SharedData();
        var first = shared.Host(new() { ["Auth:Mode"] = "None", ["Backups:Path"] = folderB });
        await AddVehicle(first, "Own car");
        var (generation, _) = await InstanceOf(first);
        first.Dispose();
        Stage(folderB, backup.Path);
        // The pictures folder is a file: the tables load, then the files cannot be put in place.
        if (Directory.Exists(shared.UploadsPath)) Directory.Delete(shared.UploadsPath, recursive: true);
        await File.WriteAllTextAsync(shared.UploadsPath, "not a folder");

        using var restarted = shared.Host(new() { ["Auth:Mode"] = "None", ["Backups:Path"] = folderB });

        Assert.Equal(["Golf"], await Vehicles(restarted)); // the database was replaced, in its one transaction
        Assert.NotEqual(generation, (await InstanceOf(restarted)).Generation);
        Assert.False(File.Exists(RestoreMarker.PathIn(folderB)));
        Assert.Single(Directory.GetFiles(folderB, "restore.failed.*"));
        Assert.Empty(Directory.GetFiles(folderB, "restore.refused.*"));
        var failure = Assert.Single(restarted.Log.From("Tankstat.Infrastructure.Backups.RestoreStep").Where(e => e.Level == LogLevel.Error));
        Assert.NotNull(failure.Exception);
        Assert.Single(Directory.GetFiles(folderB, "tankstat-backup-*")); // the backup staged, to try again: no backup of the data it replaced
    }

    [Fact]
    public async Task AMarkerThatCannotBeRead_IsRefused_AndTheHostStartsWithItsOwnData()
    {
        var folderB = Folder("b-backups");
        Directory.CreateDirectory(folderB);
        await File.WriteAllTextAsync(RestoreMarker.PathIn(folderB), "this is { not json");

        using var b = Host("b", migrated: true, backups: folderB);
        await AddVehicle(b, "Own car");

        Assert.Equal(["Own car"], await Vehicles(b));
        Assert.Single(Directory.GetFiles(folderB, "restore.refused.*"));
        var refusal = Assert.Single(b.Log.From("Tankstat.Infrastructure.Backups.RestoreStep").Where(e => e.Level == LogLevel.Error));
        Assert.Contains("marker cannot be read", (string)refusal.Values["Reason"]!);
    }
}
