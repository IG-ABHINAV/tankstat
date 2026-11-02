using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tankstat.Application.Backups;
using Tankstat.Application.Setup;

namespace Tankstat.Api.IntegrationTests;

/// <summary>
/// A new instance waits for its setup: the start logs a one-time code, the notices end with <c>SETUP_PENDING</c>, and the code opens the
/// setup's steps (the first administrator in Standalone, the acknowledgement with sign-in off, a restore of the backups folder), which no
/// administrator could, since none exists. What the environment says completes it at start; a database with data counts as set up.
/// </summary>
public sealed class SetupTests : IDisposable
{
    private const string Notices = "{ notices { code severity } }";
    private const string Pending = "{ setup { pending } }";
    private const string Finish = "mutation($i: SetupFinishInput!) { setupFinish(input: $i) { restarting signedIn administrator { email isAdmin } } }";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"tankstat-it-{Guid.NewGuid():N}-backups");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Standalone with the setup on: what <c>docker run</c> with nothing but the mode starts as.</summary>
    private static Dictionary<string, string?> WaitingStandalone(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:Mode"] = "Standalone",
            ["Setup:Enabled"] = "true",
        };
        foreach (var (key, value) in extra ?? []) settings[key] = value;
        return settings;
    }

    private static string CodeOf(TestApp app) =>
        (string)Assert.Single(app.Log.From<OneTimeCode.SetupLine>(), e => e.Values.ContainsKey("Code")).Values["Code"]!;

    private static IEnumerable<string?> CodesOf(JsonElement body) =>
        body.Data().GetProperty("notices").EnumerateArray().Select(n => n.GetProperty("code").GetString());

    [Fact]
    public async Task StandaloneWithoutAnAdministrator_Starts_Waits_AndTheCodeMakesTheFirstAdministrator_WhoIsSignedIn()
    {
        using var app = new TestApp(WaitingStandalone());
        var browser = app.NewClient();

        Assert.True((await browser.Gql(Pending)).Data().GetProperty("setup").GetProperty("pending").GetBoolean());
        var notice = (await browser.Gql(Notices)).Data().GetProperty("notices").EnumerateArray().Last();
        Assert.Equal(("SETUP_PENDING", "INFO"), (notice.GetProperty("code").GetString(), notice.GetProperty("severity").GetString()));
        var code = CodeOf(app);
        Assert.Equal(LogLevel.Warning, Assert.Single(app.Log.From<OneTimeCode.SetupLine>(), e => e.Values.ContainsKey("Code")).Level);

        Assert.Equal("setup.codeInvalid", (await browser.Gql(Finish, new { i = new { code = "ZZZZ-ZZZZ-ZZZZ", email = "root@example.com", password = "zzzz-password-1" } })).ErrorKey());
        Assert.False(app.Log.Mentions("zzzz-password-1"));
        Assert.Contains(app.Log.From("Tankstat.Api.GraphQL.GraphQLLoggingListener"), e => e.Level == LogLevel.Warning && e.Values.TryGetValue("Key", out var k) && (string?)k == "setup.codeInvalid");

        // The password typed once (an older page sends no repetition): refused, nothing made, the setup still waits.
        Assert.Equal("password.mismatch", (await browser.Gql(Finish, new { i = new { code, email = "Root@Example.com", password = "initial-password-1" } })).ErrorKey());
        Assert.True((await browser.Gql(Pending)).Data().GetProperty("setup").GetProperty("pending").GetBoolean());

        var finished = (await browser.Gql(Finish, new { i = new { code = code.ToLowerInvariant(), email = "Root@Example.com", password = "initial-password-1", passwordConfirmation = "initial-password-1" } }))
            .Data().GetProperty("setupFinish");
        var made = finished.GetProperty("administrator");
        Assert.Equal(("root@example.com", true), (made.GetProperty("email").GetString(), made.GetProperty("isAdmin").GetBoolean()));
        // Standalone runs already and nothing saved waits for a restart: signed in at once, no restart.
        Assert.Equal((false, true), (finished.GetProperty("restarting").GetBoolean(), finished.GetProperty("signedIn").GetBoolean()));
        Assert.Equal(0, app.Restarts.Count);

        // Signed in on this browser at once, the setup completed, the notice gone, and the code opens nothing any more.
        var session = (await browser.Gql("{ session { user { email isAdmin } } }")).Data().GetProperty("session").GetProperty("user");
        Assert.Equal("root@example.com", session.GetProperty("email").GetString());
        Assert.False((await browser.Gql(Pending)).Data().GetProperty("setup").GetProperty("pending").GetBoolean());
        Assert.DoesNotContain("SETUP_PENDING", CodesOf(await browser.Gql(Notices)));
        Assert.Equal("setup.completed", (await app.NewClient().Gql(Finish, new { i = new { code, email = "other@example.com", password = "initial-password-1", passwordConfirmation = "initial-password-1" } })).ErrorKey());
        await app.NewClient().LoginAs("root@example.com", "initial-password-1");
        Assert.False(app.Log.Mentions("initial-password-1"));
    }

    [Fact]
    public async Task ASetUpStandaloneInstance_ThatLostItsAdministrators_ReopensItsSetup_AndTheCodeMakesANewOne()
    {
        // A set-up database whose administrators are gone (or a switch to Standalone outside the app): nobody can sign in, and only the setup
        // page makes the first administrator (#149), so its start reopens the setup.
        using var data = new SharedData();
        TestApp.RemoveAdministrators(data.DatabasePath);
        using var app = data.Host(WaitingStandalone());
        var browser = app.NewClient();

        Assert.True((await browser.Gql(Pending)).Data().GetProperty("setup").GetProperty("pending").GetBoolean());
        Assert.Contains("SETUP_PENDING", CodesOf(await browser.Gql(Notices)));
        // The reopen's line: SetupService's one Warning without a value (the code's line names the code).
        Assert.Single(app.Log.From<SetupService>(), e => e.Level == LogLevel.Warning && e.Values.Count == 0);
        // The former administrator is a plain user now: signing in does not get past the setup page's guard, the setup makes a new one.
        var finished = (await browser.Gql(Finish, new { i = new { code = CodeOf(app), email = "new-root@example.com", password = "initial-password-2", passwordConfirmation = "initial-password-2" } }))
            .Data().GetProperty("setupFinish");

        Assert.True(finished.GetProperty("administrator").GetProperty("isAdmin").GetBoolean());
        Assert.False((await browser.Gql(Pending)).Data().GetProperty("setup").GetProperty("pending").GetBoolean());
        await app.NewClient().LoginAs("new-root@example.com", "initial-password-2");
    }

    [Fact]
    public async Task ASetupAStartInStandaloneReopened_IsCompletedAgain_WhenTheModeIsSwitchedBack()
    {
        // Set up with sign-in off (by the environment: TestApp turns the setup off), started once in Standalone, which reopens it, then
        // started in its own mode again: the way back is the environment, so nothing waits.
        using var data = new SharedData(standalone: false);
        using (var none = data.Host(new() { ["Auth:Mode"] = "None" }))
            _ = none.Factory.Services; // starts the host
        using (var standalone = data.Host(WaitingStandalone()))
            Assert.True((await standalone.NewClient().Gql(Pending)).Data().GetProperty("setup").GetProperty("pending").GetBoolean());

        using var back = data.Host(new() { ["Auth:Mode"] = "None", ["Setup:Enabled"] = "true" });

        Assert.False((await back.NewClient().Gql(Pending)).Data().GetProperty("setup").GetProperty("pending").GetBoolean());
        Assert.DoesNotContain(back.Log.From<OneTimeCode.SetupLine>(), e => e.Values.ContainsKey("Code"));
    }

    [Fact]
    public async Task WithSignInOff_TheAcknowledgement_CompletesTheSetup_AndAuthDisabledStays()
    {
        using var app = new TestApp(new() { ["Auth:Mode"] = "None", ["Setup:Enabled"] = "true" });
        var client = app.NewClient();
        Assert.Equal(["AUTH_DISABLED", "SETUP_PENDING"], CodesOf(await client.Gql(Notices)));
        // With sign-in off the one code is also the restore code its own line names.
        Assert.Equal(CodeOf(app), (string)Assert.Single(app.Log.From<OneTimeCode.RestoreLine>(), e => e.Values.ContainsKey("Code")).Values["Code"]!);

        Assert.Equal("setup.codeInvalid", (await client.Gql(Finish, new { i = new { code = "nope", acknowledgeOpenAccess = true } })).ErrorKey());
        Assert.Equal("setup.acknowledgementRequired", (await client.Gql(Finish, new { i = new { code = CodeOf(app) } })).ErrorKey());
        var done = (await client.Gql(Finish, new { i = new { code = CodeOf(app), acknowledgeOpenAccess = true } })).Data().GetProperty("setupFinish");

        Assert.False(done.GetProperty("restarting").GetBoolean());
        Assert.Equal(["AUTH_DISABLED"], CodesOf(await client.Gql(Notices)));
    }

    [Fact]
    public async Task TheWizard_SavesTheSettingsWithTheCode_ThenItsLastStepMakesTheAdministrator_AndRestartsOnce()
    {
        // In this process the sign-in mode is held like an environment variable (read-only to a save), so the wizard saves a setting of the
        // e-mail, which waits for a restart as the sign-in does; the unit tests choose the mode itself.
        using var app = new TestApp(WaitingStandalone());
        var browser = app.NewClient();
        var code = CodeOf(app);
        const string Save = "mutation($c: [SystemSettingChangeInput!]!, $code: String) { updateSystemSettings(changes: $c, code: $code) { restartPending } }";
        var change = new[] { new { key = "Smtp:Host", value = "mail.example.com" } };

        Assert.Equal("UNAUTHENTICATED", (await browser.Gql("{ systemSettings { restartPending } }")).ErrorCode()); // without the code: administrators only
        Assert.Equal("setup.codeInvalid", (await browser.Gql(Save, new { c = change, code = "nope" })).ErrorKey());
        Assert.True((await browser.Gql(Save, new { c = change, code })).Data().GetProperty("updateSystemSettings").GetProperty("restartPending").GetBoolean());
        Assert.Equal(0, app.Restarts.Count); // one restart, at the end

        var finished = (await browser.Gql(Finish, new { i = new { code, email = "root@example.com", password = "initial-password-1", passwordConfirmation = "initial-password-1" } })).Data().GetProperty("setupFinish");

        // Standalone runs already, so the administrator is signed in on this browser, and a restart in the same mode keeps the session.
        Assert.Equal((true, true), (finished.GetProperty("restarting").GetBoolean(), finished.GetProperty("signedIn").GetBoolean()));
        Assert.Equal(1, app.Restarts.Count);
        Assert.Contains("mail.example.com", await File.ReadAllTextAsync(app.SettingsPath));
        Assert.Equal("root@example.com", (await browser.Gql("{ session { user { email } } }")).Data().GetProperty("session").GetProperty("user").GetProperty("email").GetString());
        Assert.Equal("setup.completed", (await browser.Gql(Save, new { c = change, code })).ErrorKey());
    }

    [Fact]
    public async Task WhileTheSetupWaits_TheCode_UploadsABackupIntoTheFolder_ItsLimitToldWithTheCodeOnly()
    {
        var elsewhere = _folder + "-elsewhere";
        byte[] body;
        using (var old = TestApp.Standalone(new() { ["Backups:Path"] = elsewhere }))
            body = await File.ReadAllBytesAsync((await old.Factory.Services.GetRequiredService<IBackupWriter>().WriteAsync(new BackupRequest(), default)).Path);
        Directory.Delete(elsewhere, recursive: true);
        using var app = new TestApp(WaitingStandalone(new() { ["Backups:Path"] = _folder }));
        var client = app.NewClient();
        var code = CodeOf(app);
        app.Log.Clear(); // the start's own line holds the code; nothing after it may
        HttpRequestMessage Upload(string? with)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, "/media/backups?name=moved.zip") { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            if (with is not null) request.Headers.Add("X-Setup-Code", with);
            return request;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Upload(null))).StatusCode); // no administrator, no code
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Upload("ZZZZ-ZZZZ-ZZZZ"))).StatusCode);
        Assert.Equal("setup.codeInvalid", (await client.Gql("query($c: String!) { setupUploadLimitMb(code: $c) }", new { c = "nope" })).ErrorKey());
        Assert.Equal(1024, (await client.Gql("query($c: String!) { setupUploadLimitMb(code: $c) }", new { c = code })).Data().GetProperty("setupUploadLimitMb").GetInt32());

        var stored = await client.SendAsync(Upload(code));

        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        var file = Path.GetFileName(Assert.Single(Directory.GetFiles(_folder, "uploaded-*.zip")));
        Assert.Contains((await client.Gql("query($c: String!) { setupRestorePoints(code: $c) { file } }", new { c = code })).Data().GetProperty("setupRestorePoints").EnumerateArray(),
            p => p.GetProperty("file").GetString() == file); // listed to restore
        Assert.False(app.Log.Mentions(code)); // the header is never logged
        Assert.Contains(app.Log.From<BackupAdminService>(), e => e.Values.TryGetValue("File", out var f) && (string?)f == file);
    }

    [Fact]
    public async Task WhileTheSetupWaits_TheCode_ListsTheBackupsFolder_AndAsksForARestore_WithoutAnAdministrator()
    {
        string file;
        using (var old = TestApp.Standalone(new() { ["Backups:Path"] = _folder }))
            file = Path.GetFileName((await old.Factory.Services.GetRequiredService<IBackupWriter>().WriteAsync(new BackupRequest(), default)).Path);
        using var app = new TestApp(WaitingStandalone(new() { ["Backups:Path"] = _folder }));
        var client = app.NewClient();
        var code = CodeOf(app);

        Assert.Equal("setup.codeInvalid", (await client.Gql("query($c: String!) { setupRestorePoints(code: $c) { file } }", new { c = "nope" })).ErrorKey());
        Assert.Equal("auth.unauthenticated", (await client.Gql("{ backups { restorePoints { file } } }")).ErrorKey()); // the administrators' list stays theirs
        var point = Assert.Single((await client.Gql("query($c: String!) { setupRestorePoints(code: $c) { file sameMigration } }", new { c = code }))
            .Data().GetProperty("setupRestorePoints").EnumerateArray());
        Assert.Equal((file, true), (point.GetProperty("file").GetString(), point.GetProperty("sameMigration").GetBoolean()));
        // Opened before it is restored, with the same code.
        const string Details = "query($c: String!, $f: String!) { setupBackupDetails(code: $c, file: $f) { file tables { name rows } } }";
        Assert.Equal("setup.codeInvalid", (await client.Gql(Details, new { c = "nope", f = file })).ErrorKey());
        Assert.Equal(file, (await client.Gql(Details, new { c = code, f = file })).Data().GetProperty("setupBackupDetails").GetProperty("file").GetString());

        const string Restore = "mutation($i: RestoreBackupInput!) { restoreBackup(input: $i) { file } }";
        Assert.Equal("backup.codeRequired", (await client.Gql(Restore, new { i = new { file } })).ErrorKey());
        Assert.Equal(file, (await client.Gql(Restore, new { i = new { file, code } })).Data().GetProperty("restoreBackup").GetProperty("file").GetString());

        Assert.Equal(file, RestoreMarker.Read(RestoreMarker.PathIn(_folder)).File);
        Assert.Equal(1, app.Restarts.Count);
    }

    [Fact]
    public async Task ADatabaseWithData_FromBeforeTheSetupPage_CountsAsSetUp()
    {
        using var data = new SharedData(standalone: false); // no user: the vehicle is the data
        using (var before = data.Host(new() { ["Auth:Mode"] = "None" }))
            (await before.NewClient().Gql("mutation { addVehicle(input: { name: \"Old car\", fuelType: PETROL }) { id } }")).Data();
        // As the release before the setup page left it: the setup columns empty.
        TestApp.ReleaseDatabase(data.DatabasePath);
        TestApp.UndecideSetup(data.DatabasePath);

        using var upgraded = data.Host(new() { ["Auth:Mode"] = "None", ["Setup:Enabled"] = "true" });

        Assert.False((await upgraded.NewClient().Gql(Pending)).Data().GetProperty("setup").GetProperty("pending").GetBoolean());
        Assert.DoesNotContain(upgraded.Log.From<OneTimeCode.SetupLine>(), e => e.Values.ContainsKey("Code"));
    }

    [Fact]
    public async Task AdministratorsTheEnvironmentNames_CompleteTheSetup_WithoutThemThereIsASetupToDo()
    {
        var proxy = new Dictionary<string, string?> { ["Auth:Mode"] = "ProxyHeader", ["Auth:ProxyHeader:TrustedProxies:0"] = "10.0.0.0/8", ["Setup:Enabled"] = "true" };
        using (var waiting = new TestApp(proxy))
            Assert.True((await waiting.NewClient().Gql(Pending)).Data().GetProperty("setup").GetProperty("pending").GetBoolean());

        using var named = new TestApp(new Dictionary<string, string?>(proxy) { ["Auth:AdminEmails:0"] = "boss@example.com" });
        Assert.False((await named.NewClient().Gql(Pending)).Data().GetProperty("setup").GetProperty("pending").GetBoolean());
    }

    [Fact]
    public async Task BehindAProxy_TheSignedInUser_ClaimsTheAdministrator_WithTheCode()
    {
        using var app = new TestApp(
            new() { ["Auth:Mode"] = "ProxyHeader", ["Auth:ProxyHeader:TrustedProxies:0"] = "10.0.0.0/8", ["Setup:Enabled"] = "true" },
            s => s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, FakeRemoteIpStartupFilter>());
        HttpClient Through(string? user)
        {
            var client = app.NewClient();
            client.DefaultRequestHeaders.Add("X-Test-Remote-Ip", "10.1.2.3");
            if (user is not null) client.DefaultRequestHeaders.Add("X-Forwarded-User", user);
            return client;
        }
        const string Claim = "mutation($c: String!) { setupClaimAdministrator(code: $c) { email isAdmin } }";
        _ = app.Factory.Services; // starts the host, which logs the code
        var code = CodeOf(app);

        Assert.Equal("auth.unauthenticated", (await Through(null).Gql(Claim, new { c = code })).ErrorKey());
        Assert.Equal("setup.codeInvalid", (await Through("alice").Gql(Claim, new { c = "nope" })).ErrorKey());
        var claimed = (await Through("alice").Gql(Claim, new { c = code })).Data().GetProperty("setupClaimAdministrator");

        Assert.True(claimed.GetProperty("isAdmin").GetBoolean());
        Assert.True((await Through("alice").Gql("{ session { user { isAdmin } } }")).Data().GetProperty("session").GetProperty("user").GetProperty("isAdmin").GetBoolean());
        Assert.Equal("setup.completed", (await Through("bob").Gql(Claim, new { c = code })).ErrorKey());
        Assert.False((await Through("bob").Gql("{ session { user { isAdmin } } }")).Data().GetProperty("session").GetProperty("user").GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task SetupTurnedOff_NeverWaits()
    {
        using var app = new TestApp(new() { ["Auth:Mode"] = "None" }); // the test hosts' default: Setup:Enabled=false

        Assert.False((await app.NewClient().Gql(Pending)).Data().GetProperty("setup").GetProperty("pending").GetBoolean());
        Assert.DoesNotContain(app.Log.From<OneTimeCode.SetupLine>(), e => e.Values.ContainsKey("Code"));
    }
}
