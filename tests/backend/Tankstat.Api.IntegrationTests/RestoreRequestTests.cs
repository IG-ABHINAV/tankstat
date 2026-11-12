using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Tankstat.Api.Backups;
using Tankstat.Application.Backups;
using Tankstat.Domain.Backups;
using Tankstat.Infrastructure.Backups;

namespace Tankstat.Api.IntegrationTests;

/// <summary>
/// Asking for a restore: an administrator (or, with sign-in off, the one-time code from the log) names a backup of the folder with its
/// passphrase; a wrong one is refused before anything is written, a right one leaves the marker with the identity and restarts the server in
/// its process. The notice says a restore waits. Neither the passphrase, the identity nor the code reaches the log.
/// </summary>
public sealed class RestoreRequestTests : IDisposable
{
    private const string Restore = "mutation($i: RestoreBackupInput!) { restoreBackup(input: $i) { file restorePoint } }";
    private const string Passphrase = "zzcanary passphrase";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"tankstat-it-{Guid.NewGuid():N}-backups");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static readonly byte[] Identity = BackupKeys.Identity(Passphrase, Encoding.ASCII.GetBytes("tankstat-salt-01"), new Argon2idParameters(256, 1, 1));
    private static readonly BackupEncryption Encryption = new(
        BackupEncryption.AgeX25519, AgeKeys.FormatRecipient(BackupKeys.Recipient(Identity)), Convert.ToBase64String(Encoding.ASCII.GetBytes("tankstat-salt-01")), new Argon2idParameters(256, 1, 1));

    private async Task<string> Backup(TestApp app, bool encrypted = true) =>
        Path.GetFileName((await app.Factory.Services.GetRequiredService<IBackupWriter>().WriteAsync(new BackupRequest(Encryption: encrypted ? Encryption : null), default)).Path);

    [Fact]
    public async Task AnAdministrator_WithTheRightPassphrase_LeavesTheMarkerWithTheIdentity_AndTheServerRestarts()
    {
        using var app = TestApp.Standalone(new() { ["Backups:Path"] = _folder });
        var file = await Backup(app);
        var client = app.NewClient();
        await client.LoginAs(TestApp.AdminEmail, TestApp.AdminPassword);

        var pending = (await client.Gql(Restore, new { i = new { file, passphrase = Passphrase } })).Data().GetProperty("restoreBackup");

        Assert.Equal(file, pending.GetProperty("file").GetString());
        var marker = RestoreMarkerFile.Read(RestoreMarker.PathIn(_folder));
        Assert.Equal(file, marker.File);
        Assert.Equal(AgeKeys.FormatIdentity(Identity), marker.Identity);
        Assert.NotEqual(Guid.Empty, marker.RequestedById);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(RestoreMarker.PathIn(_folder)));
        Assert.Equal(1, app.Restarts.Count); // in this process, as restartServer does
        foreach (var needle in new[] { Passphrase, "zzcanary", AgeKeys.FormatIdentity(Identity) }) Assert.False(app.Log.Mentions(needle), needle);
    }

    [Fact]
    public async Task AWrongPassphrase_AMissingOne_AnUnknownFile_AndAPath_AreRefused_AndNothingIsWritten()
    {
        using var app = TestApp.Standalone(new() { ["Backups:Path"] = _folder });
        var file = await Backup(app);
        var client = app.NewClient();
        await client.LoginAs(TestApp.AdminEmail, TestApp.AdminPassword);

        Assert.Equal("backup.wrongKey", (await client.Gql(Restore, new { i = new { file, passphrase = "nope" } })).ErrorKey());
        Assert.Equal("backup.wrongKey", (await client.Gql(Restore, new { i = new { file, identity = AgeKeys.FormatIdentity(new byte[32]) } })).ErrorKey());
        Assert.Equal("backup.keyRequired", (await client.Gql(Restore, new { i = new { file } })).ErrorKey());
        Assert.Equal("backup.notFound", (await client.Gql(Restore, new { i = new { file = "nothing.zip", passphrase = Passphrase } })).ErrorKey());
        Assert.Equal("backup.notFound", (await client.Gql(Restore, new { i = new { file = "../" + file, passphrase = Passphrase } })).ErrorKey());
        Assert.False(File.Exists(RestoreMarker.PathIn(_folder)));
        Assert.Empty((await client.Gql("{ notices { code } }")).Data().GetProperty("notices").EnumerateArray());
        Assert.Equal(0, app.Restarts.Count);

        // Not an administrator: refused before the file is even looked at.
        var visitor = app.NewClient();
        Assert.Equal("UNAUTHENTICATED", (await visitor.Gql(Restore, new { i = new { file, passphrase = Passphrase } })).ErrorCode());
        Assert.False(app.Log.Mentions("zzcanary"));
    }

    [Fact]
    public async Task WithSignInOff_TheOneTimeCodeFromTheLog_IsTheGate_AndANoticeShowsWhileTheRestoreWaits()
    {
        using var app = new TestApp(new() { ["Auth:Mode"] = "None", ["Backups:Path"] = _folder });
        var file = await Backup(app, encrypted: false);
        var client = app.NewClient();
        var code = (string)Assert.Single(app.Log.From<Tankstat.Application.Setup.OneTimeCode.RestoreLine>().Where(e => e.Values.ContainsKey("Code"))).Values["Code"]!;

        Assert.Equal("backup.codeRequired", (await client.Gql(Restore, new { i = new { file } })).ErrorKey());
        Assert.Equal("backup.codeRequired", (await client.Gql(Restore, new { i = new { file, code = "WRONG123" } })).ErrorKey());
        Assert.False(File.Exists(RestoreMarker.PathIn(_folder)));

        // A marker left by an earlier request whose restart never came (a server started by hand): everyone sees the notice.
        new RestoreMarker(file, Guid.Empty, DateTimeOffset.UtcNow, null).Write(_folder);
        Assert.Contains("RESTORE_PENDING", (await client.Gql("{ notices { code } }")).Data().GetProperty("notices").EnumerateArray().Select(n => n.GetProperty("code").GetString()));
        File.Delete(RestoreMarker.PathIn(_folder));

        (await client.Gql(Restore, new { i = new { file, code = code.ToLowerInvariant() } })).Data();
        Assert.Null(RestoreMarkerFile.Read(RestoreMarker.PathIn(_folder)).Identity); // a plain backup needs none
        Assert.Equal(1, app.Restarts.Count);
    }

    [Fact]
    public async Task TheRestoreCommand_WritesTheMarker_OrAppliesTheRestoreRightAway()
    {
        using var app = new TestApp(new() { ["Auth:Mode"] = "None", ["Backups:Path"] = _folder });
        var client = app.NewClient();
        await client.Gql("mutation { addVehicle(input: { name: \"Before\", fuelType: PETROL }) { id } }");
        var file = await Backup(app);
        await client.Gql("mutation { addVehicle(input: { name: \"After\", fuelType: PETROL }) { id } }");

        var (output, error) = (new StringWriter(), new StringWriter());
        var declined = await BackupCommands.RunAsync(["restore", file, "--passphrase-stdin"], app.Factory.Services, new StringReader("no\n" + Passphrase + "\n"), output, error);
        Assert.Equal(1, declined);
        Assert.Contains("Nothing was changed", output.ToString());
        Assert.False(File.Exists(RestoreMarker.PathIn(_folder)));

        var wrong = await BackupCommands.RunAsync(["restore", file, "--yes", "--passphrase-stdin"], app.Factory.Services, new StringReader("nope\n"), output, error);
        Assert.Equal(1, wrong);
        Assert.Contains("does not open", error.ToString());

        var later = await BackupCommands.RunAsync(["restore", file, "--yes", "--passphrase-stdin"], app.Factory.Services, new StringReader(Passphrase + "\n"), output, error);
        Assert.True(later == 0, error.ToString() + output);
        Assert.True(File.Exists(RestoreMarker.PathIn(_folder)));
        Assert.Contains("next start", output.ToString());

        // --now: applied here and now (the server is this very host, which is fine for a test of the step).
        var now = await BackupCommands.RunAsync(["restore", file, "--yes", "--now", "--passphrase-stdin"], app.Factory.Services, new StringReader(Passphrase + "\n"), output, error);
        Assert.Equal(0, now);
        Assert.Contains($"Restored {file}", output.ToString());
        Assert.False(File.Exists(RestoreMarker.PathIn(_folder)));
        Assert.Equal(["Before"], (await client.Gql("{ myVehicles { name } }")).Data().GetProperty("myVehicles").EnumerateArray().Select(v => v.GetProperty("name").GetString()!));
        Assert.False(app.Log.Mentions("zzcanary"));
    }

    [Fact]
    public async Task TheRestoreCommand_RefusesANewerReleasesBackup_BeforeItWritesTheMarker()
    {
        using var app = new TestApp(new() { ["Auth:Mode"] = "None", ["Backups:Path"] = _folder });
        var file = await Backup(app, encrypted: false);
        var path = Path.Combine(_folder, file);
        var migration = BackupFile.ReadManifest(path).Migration!;
        RestoreTests.RewriteManifest(path, json => json.Replace(migration, "29991231235959_FromANewerRelease"));
        var (output, error) = (new StringWriter(), new StringWriter());

        var refused = await BackupCommands.RunAsync(["restore", file, "--yes"], app.Factory.Services, new StringReader(""), output, error);

        Assert.Equal(1, refused);
        Assert.Contains("does not know", error.ToString());
        Assert.False(File.Exists(RestoreMarker.PathIn(_folder))); // as the app refuses it: the start would refuse it anyway
    }

    [Fact]
    public async Task TheIdentityInPlaceOfThePassphrase_Works_AMalformedOneIsTheWrongKey_AndNamesThatAreNoBackupAreNotFound()
    {
        using var app = TestApp.Standalone(new() { ["Backups:Path"] = _folder });
        var file = await Backup(app);
        var client = app.NewClient();
        await client.LoginAs(TestApp.AdminEmail, TestApp.AdminPassword);

        Assert.Equal("backup.wrongKey", (await client.Gql(Restore, new { i = new { file, identity = "AGE-SECRET-KEY-1NOTAKEYATALL" } })).ErrorKey());
        Assert.Equal("backup.wrongKey", (await client.Gql(Restore, new { i = new { file, identity = "" , passphrase = "wrong" } })).ErrorKey()); // an empty identity is none: the passphrase is looked at
        Assert.Equal("backup.notFound", (await client.Gql(Restore, new { i = new { file = "", passphrase = Passphrase } })).ErrorKey());
        Assert.Equal("backup.notFound", (await client.Gql(Restore, new { i = new { file = ".hidden.zip", passphrase = Passphrase } })).ErrorKey());
        Assert.Equal("backup.notFound", (await client.Gql(Restore, new { i = new { file = "sub/" + file, passphrase = Passphrase } })).ErrorKey());
        Assert.False(File.Exists(RestoreMarker.PathIn(_folder)));

        Assert.Equal(0, app.Restarts.Count);
        (await client.Gql(Restore, new { i = new { file, identity = AgeKeys.FormatIdentity(Identity).ToLowerInvariant() } })).Data(); // either case is the same key
        Assert.Equal(AgeKeys.FormatIdentity(Identity), RestoreMarkerFile.Read(RestoreMarker.PathIn(_folder)).Identity);
        Assert.Equal(1, app.Restarts.Count);
        Assert.False(app.Log.Mentions(AgeKeys.FormatIdentity(Identity)));
    }

    [Fact]
    public async Task ABackupOfAnotherMigration_IsRefusedAtOnce_NotAfterARestartThatWouldComeBackWithTheDataAsItWas()
    {
        using var app = TestApp.Standalone(new() { ["Backups:Path"] = _folder });
        var written = await app.Factory.Services.GetRequiredService<IBackupWriter>().WriteAsync(new BackupRequest(Encryption: Encryption), default);
        RestoreTests.RewriteManifest(written.Path, json => json.Replace(written.Manifest.Migration!, "20200101000000_SomethingElse"));
        var client = app.NewClient();
        await client.LoginAs(TestApp.AdminEmail, TestApp.AdminPassword);

        Assert.Equal("backup.otherMigration", (await client.Gql(Restore, new { i = new { file = Path.GetFileName(written.Path), passphrase = Passphrase } })).ErrorKey());

        Assert.False(File.Exists(RestoreMarker.PathIn(_folder)));
        Assert.Equal(0, app.Restarts.Count);
    }

    [Fact]
    public async Task TheRestoreCommand_SaysWhatItMeets_ABackupNotInTheFolder_AndARestoreThatFailedAfterTheTablesWereLoaded()
    {
        using var app = new TestApp(new() { ["Auth:Mode"] = "None", ["Backups:Path"] = _folder });
        var file = await Backup(app, encrypted: false);
        var (output, error) = (new StringWriter(), new StringWriter());

        // A path where a name of the backups folder belongs (decrypt takes one): the reason, never a crash.
        var path = await BackupCommands.RunAsync(["restore", Path.Combine(_folder, file), "--yes"], app.Factory.Services, new StringReader(""), output, error);
        Assert.Equal(1, path);
        Assert.Contains("no such backup", error.ToString());
        Assert.False(File.Exists(RestoreMarker.PathIn(_folder)));

        // The pictures folder is a file: the tables load, then the files cannot be put in place. The database is the backup's now.
        if (Directory.Exists(app.UploadsPath)) Directory.Delete(app.UploadsPath, recursive: true);
        await File.WriteAllTextAsync(app.UploadsPath, "not a folder");
        try
        {
            var failed = await BackupCommands.RunAsync(["restore", file, "--yes", "--now"], app.Factory.Services, new StringReader(""), output, error);
            Assert.Equal(1, failed);
            Assert.Contains("failed after the data was replaced", output.ToString());
            Assert.Contains("no backup of the data from before it", output.ToString());
            Assert.DoesNotContain("not applied", output.ToString());
            Assert.Single(Directory.GetFiles(_folder, "restore.failed.*"));
        }
        finally
        {
            File.Delete(app.UploadsPath);
        }
    }

    [Fact]
    public async Task TheRestoreCommand_TakesAnIdentityFile_AndRefusesOneThatHoldsNone()
    {
        using var app = new TestApp(new() { ["Auth:Mode"] = "None", ["Backups:Path"] = _folder });
        var file = await Backup(app);
        var empty = Path.Combine(_folder, "empty-identity.txt");
        await File.WriteAllTextAsync(empty, "# only a comment\n\n");
        var good = Path.Combine(_folder, "identity.txt");
        await File.WriteAllTextAsync(good, "# created by the test\n" + AgeKeys.FormatIdentity(Identity) + "\n");
        var (output, error) = (new StringWriter(), new StringWriter());

        var none = await BackupCommands.RunAsync(["restore", file, "--yes", "--identity", empty], app.Factory.Services, new StringReader(""), output, error);
        Assert.Equal(1, none);
        Assert.Contains("holds no identity", error.ToString());
        Assert.False(File.Exists(RestoreMarker.PathIn(_folder)));

        var ok = await BackupCommands.RunAsync(["restore", file, "--yes", "--identity", good], app.Factory.Services, new StringReader(""), output, error);
        Assert.Equal(0, ok);
        Assert.Equal(AgeKeys.FormatIdentity(Identity), RestoreMarkerFile.Read(RestoreMarker.PathIn(_folder)).Identity);
    }
}
