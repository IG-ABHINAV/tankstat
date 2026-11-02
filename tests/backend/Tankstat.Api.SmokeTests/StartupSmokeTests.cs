using System.Net;

namespace Tankstat.Api.SmokeTests;

/// <summary>
/// Starts the real app in every authentication mode, the way a self-hoster would (environment variables only),
/// and checks it comes up and behaves like that mode. Misconfigurations must stop the app with a clear message.
/// </summary>
public class StartupSmokeTests
{
    /// <summary>Later entries override earlier ones.</summary>
    private static Dictionary<string, string?> Env(params (string Key, string? Value)[] pairs)
    {
        var settings = new Dictionary<string, string?>();
        foreach (var (key, value) in pairs) settings[key] = value;
        return settings;
    }

    private const string Session = "{ session { mode user { email isAdmin } } notices { code } health { status databaseReachable } }";

    // ---- None ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheShippedDefaults_Start_WithTheSqliteFallbackInTheWorkingDirectory()
    {
        // No environment settings at all: only the appsettings.json that ships with the app, whatever mode it selects.
        // If the default mode needs settings that are not there (e.g. Standalone without an administrator), this fails.
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"tankstat-smoke-{Guid.NewGuid():N}")).FullName;
        await using var app = await AppProcess.StartAsync(Env(), workDirectory: work, useTempDatabase: false);

        var data = (await app.Gql(Session)).Data();

        Assert.Contains(data.GetProperty("session").GetProperty("mode").GetString(), new[] { "NONE", "STANDALONE", "OIDC", "PROXY_HEADER" });
        Assert.Equal("ok", data.GetProperty("health").GetProperty("status").GetString());
        Assert.True(File.Exists(Path.Combine(work, "tankstat.db")), "the default SQLite database should have been created");
        // What an operator reads after a first start: the app's own line, through the shipped logging settings (Production, no overrides),
        // and on a new database the setup's code.
        Assert.True(await app.LogsAsync("database migrations"), app.Log);
        Assert.True(await app.LogsAsync("Setup is waiting"), app.Log);
    }

    [Fact]
    public async Task NoAuthMode_ShowsTheUnsafeNotice()
    {
        await using var app = await AppProcess.StartAsync(Env(("Auth__Mode", "None")));

        var data = (await app.Gql(Session)).Data();

        Assert.Equal("NONE", data.GetProperty("session").GetProperty("mode").GetString());
        Assert.Equal("AUTH_DISABLED", data.GetProperty("notices")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task NoneMode_ServesVehicles_EndToEnd()
    {
        await using var app = await AppProcess.StartAsync(Env(("Auth__Mode", "None")));

        var added = (await app.Gql("mutation { addVehicle(input: { name: \"Smoke car\", fuelType: PETROL }) { id } }")).Data();
        var list = (await app.Gql("{ myVehicles { name } }")).Data();

        Assert.NotNull(added.GetProperty("addVehicle").GetProperty("id").GetString());
        Assert.Equal("Smoke car", list.GetProperty("myVehicles")[0].GetProperty("name").GetString());
        Assert.Equal(1, list.GetProperty("myVehicles").GetArrayLength());
    }

    [Fact]
    public async Task TheSpaFallback_DoesNotCrashWhenTheFrontendIsNotBuilt()
    {
        await using var app = await AppProcess.StartAsync(Env(("Auth__Mode", "None")));

        var response = await app.GetAsync("/vehicles");

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(app.HasExited, app.Log);
    }

    // ---- Standalone ---------------------------------------------------------------------------------------

    private static Dictionary<string, string?> Standalone(params (string Key, string? Value)[] extra) => Env([("Auth__Mode", "Standalone"), .. extra]);

    [Fact]
    public async Task Standalone_SetUpWithoutABrowser_WithTheCodeFromItsLog_Starts_AndTheAdministratorCanSignIn()
    {
        // The unattended way (#149): the code from the log and one setupFinish, as a script would; then nothing waits any more.
        await using var app = await AppProcess.StartStandaloneAsync(Standalone());

        Assert.False((await app.Gql("{ setup { pending } }")).Data().GetProperty("setup").GetProperty("pending").GetBoolean());

        var anonymous = (await app.Gql(Session)).Data();
        Assert.Equal("STANDALONE", anonymous.GetProperty("session").GetProperty("mode").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, anonymous.GetProperty("session").GetProperty("user").ValueKind);
        Assert.Equal("UNAUTHENTICATED", (await app.Gql("{ myVehicles { id } }")).ErrorCode());

        await app.Gql("mutation($i: LoginInput!) { login(input: $i) { id } }", new { i = new { email = "root@example.com", password = "initial-password-1" } });
        var signedIn = (await app.Gql(Session)).Data().GetProperty("session").GetProperty("user");
        Assert.Equal("root@example.com", signedIn.GetProperty("email").GetString());
        Assert.True(signedIn.GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task WithScopesOn_TheConsoleNamesTheSignedInUser_OnTheLinesOfHisRequests()
    {
        // The unit and integration tests read scopes as data; only the process shows how the console prints one (a scope that is a
        // dictionary is printed as its type name). The formatter has to be named for its options to apply.
        await using var app = await AppProcess.StartStandaloneAsync(Standalone(
            ("Logging__LogLevel__Tankstat", "Debug"), ("Logging__Console__FormatterName", "simple"), ("Logging__Console__FormatterOptions__IncludeScopes", "true")));
        var login = (await app.Gql("mutation($i: LoginInput!) { login(input: $i) { id } }", new { i = new { email = "root@example.com", password = "initial-password-1" } })).Data().GetProperty("login");
        var id = login.GetProperty("id").GetString()!;

        await app.Gql("{ myVehicles { id } }"); // a request of the signed-in user: the line that times it is written inside the scope

        Assert.True(await app.LogsAsync($"UserId: {id}"), app.Log);
        Assert.DoesNotContain("Dictionary", app.Log);
    }

    [Fact]
    public async Task Standalone_StartsAgainOnAnExistingDatabase_SetUp_AndKeepsItsSignIns()
    {
        var first = await AppProcess.StartStandaloneAsync(Standalone());
        var work = first.WorkDirectory;
        await first.Gql("mutation($i: LoginInput!) { login(input: $i) { id } }", new { i = new { email = "root@example.com", password = "initial-password-1" } });
        var signedIn = first.CookieHeader;
        await first.StopAsync();

        // Second start: the administrator the setup made is in the database, so nothing waits.
        var settings = Env(("Auth__Mode", "Standalone"), ("Database__ConnectionString", $"Data Source={first.DatabasePath}"));
        var second = await AppProcess.StartAsync(settings, workDirectory: work, useTempDatabase: false);
        try
        {
            // The keys that protect the cookies are kept in the working directory (DataProtection:KeysPath), so a restart signs nobody out.
            var kept = await second.Gql(Session, headers: new Dictionary<string, string> { ["Cookie"] = signedIn });
            Assert.Equal("root@example.com", kept.Data().GetProperty("session").GetProperty("user").GetProperty("email").GetString());
            await second.Gql("mutation($i: LoginInput!) { login(input: $i) { id } }", new { i = new { email = "root@example.com", password = "initial-password-1" } });
            Assert.Equal("root@example.com", (await second.Gql(Session)).Data().GetProperty("session").GetProperty("user").GetProperty("email").GetString());
            Assert.True(await second.LogsAsync("up to date"), second.Log); // nothing to migrate this time, and the log says so
            Assert.DoesNotContain("Applying", second.Log);
        }
        finally
        {
            await second.DisposeAsync();
            await first.DisposeAsync();
        }
    }

    [Fact]
    public async Task Standalone_WithoutAnAdministrator_OnANewDatabase_KeepsRunning_AndWaitsForItsSetup_WithTheCodeInTheLog()
    {
        // Nothing configured and no administrator in a new database: the setup page makes the first one, with the code from the log.
        await using var app = await AppProcess.StartAsync(Standalone());

        Assert.Equal("ok", (await app.Gql("{ health { status } }")).Data().GetProperty("health").GetProperty("status").GetString());
        Assert.True((await app.Gql("{ setup { pending } }")).Data().GetProperty("setup").GetProperty("pending").GetBoolean());
        Assert.Matches("Setup is waiting: open /setup in the browser and enter the one-time setup code [A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}", app.Log);
        Assert.False(app.HasExited, app.Log);
    }

    [Fact]
    public async Task Standalone_WithoutAnAdministrator_AndTheSetupTurnedOff_StopsAtStartupWithAnActionableMessage()
    {
        // Nothing to make the first administrator: the setup page is the only way (#149), and it is turned off. A deliberate fail-fast,
        // pinned down exactly, with the way out.
        var (exitCode, log) = await AppProcess.RunUntilExitAsync(Env(("Auth__Mode", "Standalone"), ("Setup__Enabled", "false")));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("no administrator exists", log);
        Assert.Contains("Setup__Enabled", log);
        Assert.Contains("/setup", log);
    }

    [Fact]
    public async Task ASetUpInstance_SwitchedToStandaloneOutsideTheApp_WithoutAnAdministrator_ReopensItsSetup_WithTheCodeInTheLog()
    {
        // Set up with sign-in off (completed by the environment), then started in Standalone: nobody can sign in, so the setup opens again.
        var none = await AppProcess.StartAsync(Env(("Auth__Mode", "None"), ("Setup__Enabled", "false")));
        await none.StopAsync();
        try
        {
            await using var app = await AppProcess.StartAsync(Standalone(), workDirectory: none.WorkDirectory); // the same database

            Assert.True((await app.Gql("{ setup { pending } }")).Data().GetProperty("setup").GetProperty("pending").GetBoolean());
            Assert.True(await app.LogsAsync("The setup is reopened"), app.Log);
            Assert.Matches("one-time setup code [A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}", app.Log);
            await app.FinishSetupAsync(); // and the setup page makes the administrator again
            Assert.False((await app.Gql("{ setup { pending } }")).Data().GetProperty("setup").GetProperty("pending").GetBoolean());
        }
        finally
        {
            await none.DisposeAsync(); // last: it owns the folder
        }
    }

    [Fact]
    public async Task TheRetiredBootstrapSettings_AreIgnored_WithAWarningThatNamesThem_NeverTheirValues()
    {
        await using var app = await AppProcess.StartAsync(Standalone(("Auth__Standalone__AdminEmail", "zzzz@example.com"), ("Auth__Standalone__AdminPassword", "zzzz-password-1")));

        Assert.True(await app.LogsAsync("Auth:Standalone:AdminEmail, Auth:Standalone:AdminPassword are no longer used"), app.Log);
        Assert.DoesNotContain("zzzz", app.Log);
        // Nobody was made from them: the new instance waits for its setup page.
        Assert.True((await app.Gql("{ setup { pending } }")).Data().GetProperty("setup").GetProperty("pending").GetBoolean());
    }

    [Fact]
    public async Task Standalone_WithSmtpButNoPublicUrl_StopsAtStartup()
    {
        var (exitCode, log) = await AppProcess.RunUntilExitAsync(Standalone(("Smtp__Host", "mail.example.com"), ("Smtp__From", "tank@example.com")));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Auth:PublicUrl", log);
    }

    [Fact]
    public async Task Standalone_WithSmtpAndPublicUrl_Starts()
    {
        await using var app = await AppProcess.StartAsync(Standalone(("Smtp__Host", "mail.example.com"), ("Smtp__From", "tank@example.com"), ("Auth__PublicUrl", "https://tank.example.com")));

        Assert.Equal("STANDALONE", (await app.Gql(Session)).Data().GetProperty("session").GetProperty("mode").GetString());
    }

    // ---- OIDC ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Oidc_Starts_WithoutContactingTheProvider()
    {
        // The authority does not exist: startup must not depend on the identity provider being reachable.
        await using var app = await AppProcess.StartAsync(Env(
            ("Auth__Mode", "Oidc"), ("Auth__Oidc__Authority", "https://id.invalid"), ("Auth__Oidc__ClientId", "tankstat"), ("Auth__Oidc__ClientSecret", "secret")));

        var data = (await app.Gql(Session)).Data();
        Assert.Equal("OIDC", data.GetProperty("session").GetProperty("mode").GetString());
        // No Auth:AdminEmails names the first administrator, so the new instance waits for its setup: that is the only notice.
        Assert.Equal(["SETUP_PENDING"], data.GetProperty("notices").EnumerateArray().Select(n => n.GetProperty("code").GetString()));
        Assert.Equal("UNAUTHENTICATED", (await app.Gql("{ myVehicles { id } }")).ErrorCode());
        Assert.NotEqual(HttpStatusCode.NotFound, (await app.GetAsync("/auth/oidc/login")).StatusCode); // the login endpoint exists in this mode
    }

    [Fact]
    public async Task Oidc_WithoutProviderSettings_StopsAtStartup()
    {
        var (exitCode, log) = await AppProcess.RunUntilExitAsync(Env(("Auth__Mode", "Oidc")));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Auth:Oidc:Authority", log);
    }

    // ---- Proxy header -------------------------------------------------------------------------------------

    private static Dictionary<string, string?> Proxy(string trusted) =>
        Env(("Auth__Mode", "ProxyHeader"), ("Auth__ProxyHeader__TrustedProxies__0", trusted), ("Auth__AdminEmails__0", "boss@example.com"));

    private static Dictionary<string, string> User(string name, string email) =>
        new() { ["X-Forwarded-User"] = name, ["X-Forwarded-Email"] = email };

    [Fact]
    public async Task ProxyHeader_TrustsTheHeaderFromTheConfiguredProxy()
    {
        await using var app = await AppProcess.StartAsync(Proxy("127.0.0.0/8")); // the test connects from loopback

        var anonymous = (await app.Gql(Session)).Data();
        Assert.Equal("PROXY_HEADER", anonymous.GetProperty("session").GetProperty("mode").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, anonymous.GetProperty("session").GetProperty("user").ValueKind);

        var boss = (await app.Gql(Session, headers: User("boss", "boss@example.com"))).Data().GetProperty("session").GetProperty("user");
        Assert.Equal("boss@example.com", boss.GetProperty("email").GetString());
        Assert.True(boss.GetProperty("isAdmin").GetBoolean());
        Assert.Null((await app.Gql("{ myVehicles { id } }", headers: User("boss", "boss@example.com"))).ErrorCode());
    }

    [Fact]
    public async Task ProxyHeader_IgnoresTheHeaderFromAnyoneElse()
    {
        await using var app = await AppProcess.StartAsync(Proxy("10.0.0.0/8")); // the test connects from 127.0.0.1: not trusted

        var forged = (await app.Gql(Session, headers: User("boss", "boss@example.com"))).Data().GetProperty("session").GetProperty("user");

        Assert.Equal(System.Text.Json.JsonValueKind.Null, forged.ValueKind);
        Assert.Equal("UNAUTHENTICATED", (await app.Gql("{ myVehicles { id } }", headers: User("boss", "boss@example.com"))).ErrorCode());
    }

    [Fact]
    public async Task ProxyHeader_WithoutTrustedProxies_StopsAtStartup()
    {
        var (exitCode, log) = await AppProcess.RunUntilExitAsync(Env(("Auth__Mode", "ProxyHeader")));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("TrustedProxies", log);
    }

    // ---- Backups (the command next to the server) -----------------------------------------------------------

    [Fact]
    public async Task TheBackupCommand_WritesAFileOfTheInstance_WhichTheDecryptCommandOpens()
    {
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"tankstat-smoke-{Guid.NewGuid():N}")).FullName;
        var settings = Env(("Auth__Mode", "None"));
        await using var app = await AppProcess.StartAsync(settings, workDirectory: work); // migrates the database and takes a vehicle
        await app.Gql("mutation { addVehicle(input: { name: \"Smoke car\", fuelType: PETROL }) { id } }");
        await app.StopAsync();

        var (code, log) = await AppProcess.RunCommandAsync(settings, work, "smoke passphrase\nsmoke passphrase\n", "backup", "--passphrase-stdin"); // a new one: twice

        Assert.True(code == 0, log);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(work, "backups"), "tankstat-backup-*.zip")); // Backups:Path defaults to the working directory, like the database
        Assert.Contains("encrypted for age1", log);
        Assert.DoesNotContain("smoke passphrase", log);
        var (opened, openLog) = await AppProcess.RunCommandAsync(settings, work, "smoke passphrase\n", "decrypt", file, "--output", Path.Combine(work, "restored"), "--passphrase-stdin");
        Assert.True(opened == 0, openLog);
        Assert.Contains("Smoke car", await File.ReadAllTextAsync(Path.Combine(work, "restored", "tables", "Vehicles.jsonl")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(work, "restored", "keys"), "*.xml")); // the keys of the instance, in an encrypted backup
        Assert.DoesNotContain("Wrote backup", app.Log); // the server wrote none at start
        Assert.Contains("one-time restore code", app.Log); // sign-in is off: the code that guards a restore asked for from the app is in the log
    }

    [Fact]
    public async Task Keygen_AsAProcess_TakesThePassphraseTwice_AndPrintsNoKeysWhenTheTwoDiffer()
    {
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"tankstat-smoke-{Guid.NewGuid():N}")).FullName;
        var settings = Env(("Auth__Mode", "None"));

        var (same, sameLog) = await AppProcess.RunCommandAsync(settings, work, "smoke passphrase\nsmoke passphrase\n", "backup", "keygen", "--passphrase-stdin");
        var (different, differentLog) = await AppProcess.RunCommandAsync(settings, work, "smoke passphrase\nsmoke passphrasf\n", "backup", "keygen", "--passphrase-stdin");

        Assert.True(same == 0, sameLog);
        Assert.Contains("Backups__Encryption__Recipient=age1", sameLog);
        Assert.Equal(1, different);
        Assert.Contains("The two entries are not the same.", differentLog);
        Assert.DoesNotContain("AGE-SECRET-KEY", differentLog);
        Assert.DoesNotContain("smoke passphras", sameLog + differentLog);
    }

    [Fact]
    public async Task TheRestoreCommand_TakesAStoppedServerBackToABackup_AndTheNextStartServesIt()
    {
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"tankstat-smoke-{Guid.NewGuid():N}")).FullName;
        var settings = Env(("Auth__Mode", "None"));
        var first = await AppProcess.StartAsync(settings, workDirectory: work);
        try
        {
            await first.Gql("mutation { addVehicle(input: { name: \"Before\", fuelType: PETROL }) { id } }");
            var (made, log) = await AppProcess.RunCommandAsync(settings, work, "smoke passphrase\nsmoke passphrase\n", "backup", "--passphrase-stdin"); // a new one: twice
            Assert.True(made == 0, log);
            await first.Gql("mutation { addVehicle(input: { name: \"After\", fuelType: PETROL }) { id } }");
        }
        finally
        {
            await first.StopAsync(); // the files stay: the server is stopped for the restore, like an operator would
        }
        var file = Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(work, "backups"), "tankstat-backup-*.zip")));

        var (code, restoreLog) = await AppProcess.RunCommandAsync(settings, work, "smoke passphrase\n", "restore", file, "--yes", "--now", "--passphrase-stdin");

        Assert.True(code == 0, restoreLog);
        Assert.Contains("Restored", restoreLog);
        Assert.DoesNotContain("smoke passphrase", restoreLog);
        await using var second = await AppProcess.StartAsync(settings, workDirectory: work, useTempDatabase: true);
        var names = (await second.Gql("{ myVehicles { name } }")).Data().GetProperty("myVehicles").EnumerateArray().Select(v => v.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Before"], names);
        Assert.True(await second.LogsAsync("up to date"), second.Log); // the restore happened under the command, the start finds nothing to do
        await first.DisposeAsync();
    }

    [Fact]
    public async Task ABackupUploadLargerThanKestrelsOwnLimit_IsReadToTheEnd_ByTheBackupsLimitInstead()
    {
        // Kestrel stops a body at 30 MB unless the endpoint raises its limit; the test server of the integration tests has no such limit, so
        // only the real server shows the raise works: 31 MB of noise is read whole and refused as no backup, not cut off with a bare 413.
        await using var app = await AppProcess.StartStandaloneAsync(Standalone());
        await app.Gql("mutation($i: LoginInput!) { login(input: $i) { id } }", new { i = new { email = "root@example.com", password = "initial-password-1" } });
        var noise = new byte[31 * 1024 * 1024];
        new Random(7).NextBytes(noise);

        using var upload = new HttpRequestMessage(HttpMethod.Put, "/media/backups?name=noise.zip") { Content = new ByteArrayContent(noise) };
        using var response = await app.SendAsync(upload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("backup.corrupt", await response.Content.ReadAsStringAsync());
        Assert.Empty(Directory.GetFiles(Path.Combine(app.WorkDirectory, "backups"))); // nothing left behind
    }

    [Fact]
    public async Task ABackupUploadSentInChunks_AboveTheBackupsLimit_IsRefusedByThatLimit_WithItsReason()
    {
        // A body without a length is counted by Kestrel as it arrives, ahead of what the app has read: under any limit of Kestrel's own, its
        // bare 413 would answer first. The endpoint lifts that limit, so the backups' own count answers, and says why.
        await using var app = await AppProcess.StartStandaloneAsync(Standalone(("Backups__MaxUploadMb", "1")));
        await app.Gql("mutation($i: LoginInput!) { login(input: $i) { id } }", new { i = new { email = "root@example.com", password = "initial-password-1" } });
        var noise = new byte[3 * 1024 * 1024];
        new Random(7).NextBytes(noise);

        using var upload = new HttpRequestMessage(HttpMethod.Put, "/media/backups?name=noise.zip") { Content = new StreamContent(new UnseekableStream(noise)) };
        using var response = await app.SendAsync(upload);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("backup.tooLarge", await response.Content.ReadAsStringAsync());
        Assert.Empty(Directory.GetFiles(Path.Combine(app.WorkDirectory, "backups")));
    }

    /// <summary>A body whose length the client cannot know, so it is sent in chunks.</summary>
    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    // ---- Telemetry (optional: it never stops the app) -------------------------------------------------------

    [Fact]
    public async Task Telemetry_IsOffByDefault_WithoutAWord()
    {
        await using var app = await AppProcess.StartAsync(Env(("Auth__Mode", "None")));

        Assert.Equal("ok", (await app.Gql(Session)).Data().GetProperty("health").GetProperty("status").GetString());
        Assert.DoesNotContain("OpenTelemetry", app.Log); // nothing registered, nothing said (a Debug line, unseen in Production)
    }

    [Fact]
    public async Task Telemetry_WithACollectorThatRefuses_Starts_Serves_AndSaysItIsOn_NeverWhere()
    {
        await using var app = await AppProcess.StartAsync(Env(
            ("Auth__Mode", "None"),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:9"), // nothing listens there
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
            ("OTEL_EXPORTER_OTLP_HEADERS", "x-api-key=zzzz-smoke-key")));

        var added = (await app.Gql("mutation { addVehicle(input: { name: \"Smoke car\", fuelType: PETROL }) { id } }")).Data();

        Assert.NotNull(added.GetProperty("addVehicle").GetProperty("id").GetString());
        Assert.True(await app.LogsAsync("OpenTelemetry export is on: traces, metrics and logs over http/protobuf, as service tankstat"), app.Log);
        Assert.DoesNotContain("127.0.0.1:9", app.Log); // never where it goes, nor what it sends along
        Assert.DoesNotContain("zzzz-smoke-key", app.Log);
        Assert.DoesNotContain("fail: ", app.Log); // a failed export is the SDK's own diagnostics, never an Error of the app's
        Assert.False(app.HasExited);
    }

    [Fact]
    public async Task Telemetry_WithSettingsTheSdkCannotUse_StartsWithoutIt_AndNamesThem()
    {
        await using var app = await AppProcess.StartAsync(Env(("Auth__Mode", "None"), ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:9"), ("OTEL_EXPORTER_OTLP_HEADERS", "zzzz-not-a-pair")));

        Assert.Equal("ok", (await app.Gql(Session)).Data().GetProperty("health").GetProperty("status").GetString()); // the SDK would have thrown on these headers
        Assert.True(await app.LogsAsync("OpenTelemetry export is off because its settings cannot be used: OTEL_EXPORTER_OTLP_HEADERS"), app.Log);
        Assert.DoesNotContain("zzzz-not-a-pair", app.Log);
    }

    // ---- Photo reading (optional: it never stops the app) ---------------------------------------------------

    private const string Recognition = "{ recognitionStatus { available } }";

    private static bool Available(System.Text.Json.JsonElement body) => body.Data().GetProperty("recognitionStatus").GetProperty("available").GetBoolean();

    [Fact]
    public async Task PhotoReading_IsOffByDefault_WithoutAWord()
    {
        await using var app = await AppProcess.StartAsync(Env(("Auth__Mode", "None")));

        Assert.False(Available(await app.Gql(Recognition)));
        Assert.DoesNotContain("Photo reading", app.Log);
    }

    [Fact]
    public async Task PhotoReading_WithAModelServerThatCannotBeReached_StartsAndSaysItIsUnavailable()
    {
        await using var app = await AppProcess.StartAsync(Env(
            ("Auth__Mode", "None"),
            ("Recognition__Provider", "OpenAiCompatible"),
            ("Recognition__OpenAiCompatible__BaseUrl", "http://127.0.0.1:9/v1"), // nothing listens there
            ("Recognition__OpenAiCompatible__Model", "smoke")));

        Assert.False(Available(await app.Gql(Recognition)));
        Assert.True(await app.LogsAsync("Photo reading uses the OpenAiCompatible provider"), app.Log);
        Assert.True(await app.LogsAsync("model smoke at http://127.0.0.1:9/v1"), app.Log); // which model, where: what an operator looks for first
        Assert.True(await app.LogsAsync("System prompt: built-in ("), app.Log);
        Assert.False(app.HasExited);
    }

    [Fact]
    public async Task PhotoReading_WithLogTrafficOn_WarnsAtStart_AndWritesTheCallsToTheModelServer()
    {
        await using var app = await AppProcess.StartAsync(Env(
            ("Auth__Mode", "None"),
            ("Recognition__Provider", "OpenAiCompatible"),
            ("Recognition__OpenAiCompatible__BaseUrl", "http://127.0.0.1:9/v1"), // nothing listens there
            ("Recognition__OpenAiCompatible__Model", "smoke"),
            ("Recognition__OpenAiCompatible__LogTraffic", "true")));

        Assert.False(Available(await app.Gql(Recognition))); // the health check goes to the model server
        Assert.True(await app.LogsAsync("LogTraffic is on"), app.Log);
        Assert.True(await app.LogsAsync("(health check): GET http://127.0.0.1:9/v1/models"), app.Log);
        Assert.True(await app.LogsAsync("failed after"), app.Log);
        Assert.False(app.HasExited);
    }

    [Fact]
    public async Task PhotoReading_WithIncompleteSettings_IsTurnedOffWithAWarning_AndTheAppStarts()
    {
        await using var app = await AppProcess.StartAsync(Env(("Auth__Mode", "None"), ("Recognition__Provider", "OpenAiCompatible"), ("Recognition__MaxConcurrent", "many")));

        Assert.False(Available(await app.Gql(Recognition)));
        Assert.True(await app.LogsAsync("Photo reading is turned off"), app.Log);
        Assert.False(app.HasExited);
    }

    // ---- Misconfiguration ---------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnknownAuthMode_StopsAtStartup()
    {
        var (exitCode, log) = await AppProcess.RunUntilExitAsync(Env(("Auth__Mode", "Kerberos")));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Auth:Mode", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUnknownDatabaseProvider_StopsAtStartup()
    {
        var (exitCode, log) = await AppProcess.RunUntilExitAsync(Env(("Database__Provider", "Oracle")));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Oracle", log);
    }

    [Fact]
    public async Task ANetworkDatabaseWithoutAConnectionString_StopsAtStartup()
    {
        var (exitCode, log) = await AppProcess.RunUntilExitAsync(Env(("Database__Provider", "PostgreSql")), useTempDatabase: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Database:ConnectionString", log);
    }
}
