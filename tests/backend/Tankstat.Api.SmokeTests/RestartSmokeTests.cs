namespace Tankstat.Api.SmokeTests;

/// <summary>
/// An administrator's restart in the real process: the host stops and starts again in the same process, on the same port, and what was saved
/// into the settings file counts afterwards; a restore asked for from the app restarts the same way.
/// </summary>
public class RestartSmokeTests
{
    /// <summary>A Standalone instance; its first administrator (root@example.com) is made by its setup (<see cref="AppProcess.StartStandaloneAsync"/>).</summary>
    private static readonly Dictionary<string, string?> Standalone = new() { ["Auth__Mode"] = "Standalone" };

    private const string Session = "{ session { mode user { email } } vehicleDefaults { currency } }";

    /// <summary>Waits until the app answers again after a restart and the answer passes <paramref name="ready"/>.</summary>
    private static async Task<System.Text.Json.JsonElement> WhenBackAsync(AppProcess app, Func<System.Text.Json.JsonElement, bool> ready, IDictionary<string, string>? headers = null, string query = Session)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            Assert.False(app.HasExited, "the process must not exit for a restart:\n" + app.Log);
            try
            {
                var data = (await app.Gql(query, headers: headers)).Data();
                if (ready(data)) return data;
            }
            catch (Exception) when (!app.HasExited)
            {
                // not back yet, or the old host still answering
            }
            await Task.Delay(200);
        }
        throw new TimeoutException("The app did not come back after the restart:\n" + app.Log);
    }

    [Fact]
    public async Task RestartServer_StartsTheHostAgain_InTheSameProcess_AndWhatWasSavedCounts()
    {
        await using var app = await AppProcess.StartStandaloneAsync(Standalone);
        await app.Gql("mutation($i: LoginInput!) { login(input: $i) { id } }", new { i = new { email = "root@example.com", password = "initial-password-1" } });
        var signedIn = new Dictionary<string, string> { ["Cookie"] = app.CookieHeader };
        // A default applies at once; the mail is built at start, so its port waits for the restart.
        var saved = (await app.Gql("mutation { updateSystemSettings(changes: [{ key: \"Defaults:Currency\", value: \"HUF\" }, { key: \"Smtp:Port\", value: \"2525\" }]) { restartPending } }", headers: signedIn)).Data();
        Assert.True(saved.GetProperty("updateSystemSettings").GetProperty("restartPending").GetBoolean());
        Assert.Equal("HUF", (await app.Gql(Session)).Data().GetProperty("vehicleDefaults").GetProperty("currency").GetString());
        var file = Path.Combine(app.WorkDirectory, "settings.json");
        Assert.True(File.Exists(file), "the settings file lands in the working directory by default");
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        Assert.True((await app.Gql("mutation { restartServer }", headers: signedIn)).Data().GetProperty("restartServer").GetBoolean());

        // Back once nothing waits any more: the new host started with the port the old one saved.
        var back = await WhenBackAsync(app, d => !d.GetProperty("systemSettings").GetProperty("restartPending").GetBoolean(), signedIn,
            "{ session { mode user { email } } systemSettings { restartPending } vehicleDefaults { currency } }");
        Assert.Equal("HUF", back.GetProperty("vehicleDefaults").GetProperty("currency").GetString());
        Assert.False(app.HasExited);
        Assert.Equal("root@example.com", back.GetProperty("session").GetProperty("user").GetProperty("email").GetString()); // the same mode: the sign-in survives the restart
        Assert.Contains("Restarting the server", app.Log);
        Assert.Contains("Settings file", app.Log); // the second host read the file the first one wrote (the first start had none: a Debug line, unseen in Production)
        Assert.Contains("database migrations", app.Log); // the first start migrated...
        Assert.Contains("up to date", app.Log); // ...and the second found the database ready: two starts in one process
    }

    [Fact]
    public async Task RestartServer_WithTelemetryOn_StartsTheNextHostWithItToo()
    {
        // Each host builds its own providers and disposes them as it stops (a collector that refuses: nothing waits for an export).
        await using var app = await AppProcess.StartStandaloneAsync(new Dictionary<string, string?>(Standalone) { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:9" });
        await app.Gql("mutation($i: LoginInput!) { login(input: $i) { id } }", new { i = new { email = "root@example.com", password = "initial-password-1" } });
        var signedIn = new Dictionary<string, string> { ["Cookie"] = app.CookieHeader };
        const string on = "OpenTelemetry export is on";
        Assert.True(await app.LogsAsync(on), app.Log);

        Assert.True((await app.Gql("mutation { restartServer }", headers: signedIn)).Data().GetProperty("restartServer").GetBoolean());

        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (System.Text.RegularExpressions.Regex.Count(app.Log, on) < 2 && DateTime.UtcNow < deadline) await Task.Delay(200);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(app.Log, on));
        await WhenBackAsync(app, d => d.GetProperty("session").GetProperty("user").ValueKind == System.Text.Json.JsonValueKind.Object, signedIn);
        Assert.False(app.HasExited);
    }

    [Fact]
    public async Task RestoreBackup_RestartsTheHostInTheSameProcess_WhichStartsWithTheBackupsData()
    {
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"tankstat-smoke-{Guid.NewGuid():N}")).FullName;
        var settings = new Dictionary<string, string?> { ["Auth__Mode"] = "None" };
        await using var app = await AppProcess.StartAsync(settings, workDirectory: work);
        await app.Gql("mutation { addVehicle(input: { name: \"Before\", fuelType: PETROL }) { id } }");
        var (made, log) = await AppProcess.RunCommandAsync(settings, work, "", "backup");
        Assert.True(made == 0, log);
        await app.Gql("mutation { addVehicle(input: { name: \"After\", fuelType: PETROL }) { id } }");
        var file = Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(work, "backups"), "tankstat-backup-*.zip")));
        var code = System.Text.RegularExpressions.Regex.Match(app.Log, "one-time restore code ([A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4})").Groups[1].Value; // sign-in is off: the log's code is the gate

        var asked = (await app.Gql("mutation($i: RestoreBackupInput!) { restoreBackup(input: $i) { file } }", new { i = new { file, code } })).Data();

        Assert.Equal(file, asked.GetProperty("restoreBackup").GetProperty("file").GetString());
        await WhenBackAsync(app, d => d.GetProperty("myVehicles").EnumerateArray().Select(v => v.GetProperty("name").GetString()).SequenceEqual(["Before"]), query: "{ myVehicles { name } }");
        Assert.False(app.HasExited); // no restart policy needed: the process never ended
        Assert.Contains("Restarting the server", app.Log);
        Assert.Contains("Restored the backup", app.Log);
    }

    /// <summary>
    /// Sign-in off, with the database and the backups folder outside the working directory (a stopped process removes it), whose settings
    /// file is the server's (the default place).
    /// </summary>
    private static (Dictionary<string, string?> Settings, string Work, string Backups) OutsideTheWorkingDirectory()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"tankstat-smoke-{Guid.NewGuid():N}")).FullName;
        var backups = Path.Combine(root, "backups");
        var settings = new Dictionary<string, string?>
        {
            ["Auth__Mode"] = "None",
            ["Database__ConnectionString"] = $"Data Source={Path.Combine(root, "tankstat.db")}",
            ["Backups__Path"] = backups,
        };
        return (settings, Directory.CreateDirectory(Path.Combine(root, "work")).FullName, backups);
    }

    /// <summary>A server started once in <paramref name="work"/> (its database made), backed up by the command while it runs, then stopped; the backup's file name.</summary>
    private static async Task<string> BackupOfAServerWithItsFileAsync(Dictionary<string, string?> settings, string work, string backups, string file)
    {
        await using (var first = await AppProcess.StartAsync(settings, workDirectory: work))
        {
            var (made, log) = await AppProcess.RunCommandAsync(settings, work, "", "backup");
            Assert.True(made == 0, log);
            Assert.Contains("settings file", log);
        }
        Directory.CreateDirectory(work); // gone with the process
        return Path.GetFileName(Assert.Single(Directory.GetFiles(backups, "tankstat-backup-*.zip")));
    }

    [Fact]
    public async Task ARestoreOfABackupWithASettingsFile_EndsInAStartThatRunsWithIt_InTheSameProcess()
    {
        var (settings, work, backups) = OutsideTheWorkingDirectory();
        var file = Path.Combine(work, "settings.json"); // the default place, in the working directory
        File.WriteAllText(file, """{ "Defaults": { "Currency": "HUF" } }""");
        var backup = await BackupOfAServerWithItsFileAsync(settings, work, backups, file); // the backup carries the file as it was
        File.WriteAllText(file, """{ "Defaults": { "Currency": "EUR" } }"""); // then the server runs with another
        await using var app = await AppProcess.StartAsync(settings, workDirectory: work);
        Assert.Equal("EUR", (await app.Gql(Session)).Data().GetProperty("vehicleDefaults").GetProperty("currency").GetString());
        var code = System.Text.RegularExpressions.Regex.Match(app.Log, "one-time restore code ([A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4})").Groups[1].Value;

        await app.Gql("mutation($i: RestoreBackupInput!) { restoreBackup(input: $i) { file } }", new { i = new { file = backup, code } });

        // The restart, the restore, which writes the file and ends that start at once, and the host after it, which reads the file.
        await WhenBackAsync(app, d => d.GetProperty("vehicleDefaults").GetProperty("currency").GetString() == "HUF");
        Assert.False(app.HasExited);
        Assert.Contains("Restored the settings file", app.Log);
        Assert.Contains("Restarting the server at once", app.Log);
        Assert.Equal("""{ "Defaults": { "Currency": "EUR" } }""", File.ReadAllText(file + ".bak"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(app.Log, "Restoring the backup")); // once: the marker went before the start ended
        Assert.True(!app.Log.Contains("fail:"), app.Log); // ending that start is no failure
    }

    [Fact]
    public async Task RestoreNow_OfABackupWithASettingsFile_WritesItAndEndsCleanly()
    {
        var (settings, work, backups) = OutsideTheWorkingDirectory();
        var file = Path.Combine(work, "settings.json");
        File.WriteAllText(file, """{ "Defaults": { "Currency": "HUF" } }""");
        var backup = await BackupOfAServerWithItsFileAsync(settings, work, backups, file);
        File.WriteAllText(file, """{ "Defaults": { "Currency": "EUR" } }"""); // the server is stopped: --now restores at once

        var (restored, output) = await AppProcess.RunCommandAsync(settings, work, "", "restore", backup, "--yes", "--now");

        Assert.True(restored == 0, output);
        Assert.Contains($"Restored {backup}.", output);
        Assert.DoesNotContain("ServerRestartException", output);
        Assert.Contains("HUF", File.ReadAllText(file));
    }

    [Fact]
    public async Task AStopSignal_RightAfterARestart_EndsTheProcess()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX signals, as docker stop sends them
        await using var app = await AppProcess.StartStandaloneAsync(Standalone);
        await app.Gql("mutation($i: LoginInput!) { login(input: $i) { id } }", new { i = new { email = "root@example.com", password = "initial-password-1" } });
        var signedIn = new Dictionary<string, string> { ["Cookie"] = app.CookieHeader };
        Assert.True((await app.Gql("mutation { restartServer }", headers: signedIn)).Data().GetProperty("restartServer").GetBoolean());

        await app.SignalAsync("TERM"); // docker stop, while the host stops for the restart

        Assert.True(await app.ExitsWithinAsync(TimeSpan.FromSeconds(20)), "a stop signal during a restart must end the process, not start a new host:\n" + app.Log);
    }
}
