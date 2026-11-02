using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tankstat.Application.SystemSettings;
using Tankstat.TestSupport;

namespace Tankstat.Api.IntegrationTests;

/// <summary>
/// A host with its own settings and throwaway SQLite database. Settings are passed in memory (they outrank
/// appsettings.json and environment variables), so several hosts with different auth modes can be tested. The one exception is
/// <c>Settings:Path</c>: Program.cs decides about the settings file before the host is built, where the in-memory settings do not exist yet,
/// so it travels as a command-line argument (<see cref="IWebHostBuilder.UseSetting"/>) and every host gets a throwaway file of its own.
/// </summary>
internal sealed class TestApp : IDisposable
{
    /// <summary>
    /// A database with every migration applied, made once per test run (by a host of its own) and copied for each host: a host would
    /// otherwise apply all of them to its new file, which was most of the time a test took. The hosts find it up to date and start at once.
    /// </summary>
    private static readonly Lazy<string> Template = new(CreateTemplate);

    /// <summary>
    /// A set-up Standalone database, made once per run from the template through the setup's own last step: its first administrator
    /// (<see cref="AdminEmail"/>) and the setup completed. The only way a Standalone instance gets one (#149), so every Standalone host
    /// starts from a copy of it.
    /// </summary>
    private static readonly Lazy<string> StandaloneTemplate = new(CreateStandaloneTemplate);

    /// <summary>A migrated database at <paramref name="path"/>, from the template every host starts from.</summary>
    public static void CopyMigratedDatabase(string path) => File.Copy(Template.Value, path);

    /// <summary>A set-up Standalone database at <paramref name="path"/> with its first administrator (<see cref="AdminEmail"/>).</summary>
    public static void CopyStandaloneDatabase(string path) => File.Copy(StandaloneTemplate.Value, path);

    private readonly string _db = Path.Combine(Path.GetTempPath(), $"tankstat-it-{Guid.NewGuid():N}.db");

    /// <summary>Where uploaded pictures of this host go.</summary>
    public string UploadsPath { get; } = Path.Combine(Path.GetTempPath(), $"tankstat-it-{Guid.NewGuid():N}-uploads");

    /// <summary>Where the photos of logs (and their drafts) of this host go.</summary>
    public string PhotosPath { get; } = Path.Combine(Path.GetTempPath(), $"tankstat-it-{Guid.NewGuid():N}-photos");

    /// <summary>Where this host keeps the keys that protect its cookies, unless its settings name another folder (<see cref="SharedData"/>).</summary>
    public string KeysPath { get; } = Path.Combine(Path.GetTempPath(), $"tankstat-it-{Guid.NewGuid():N}-keys");

    /// <summary>The backups folder of this host, unless its settings name another.</summary>
    public string BackupsPath { get; } = Path.Combine(Path.GetTempPath(), $"tankstat-it-{Guid.NewGuid():N}-backups");

    /// <summary>The backup settings that make a start write and prune backups: never the developer's.</summary>
    private static readonly string[] ScheduleKeys = ["Backups:Every", "Backups:Days", "Backups:Keep", "Backups:KeepDays"];

    /// <summary>The app's own settings file of this host (none unless a test writes one), unless its settings name another (<see cref="SharedData"/>, a test's file).</summary>
    public string SettingsPath { get; }

    private readonly bool _ownSettingsFile;

    public WebApplicationFactory<Program> Factory { get; }

    /// <summary>Everything this host logged, from its first line on.</summary>
    public CapturedLog Log { get; } = new();

    /// <summary>What <c>restartServer</c> reaches on this host: a real restart would stop it (and Program.cs's loop would start a real server), so it only counts.</summary>
    public FakeServerRestart Restarts { get; } = new();

    /// <param name="settings">Over the host's own; a <c>Database:ConnectionString</c> among them is the database to use, nothing is copied then.</param>
    /// <param name="host">Last word on the web host, e.g. a web root with static files to serve.</param>
    /// <param name="migrated">False: a new, empty database, so the host applies every migration itself (the template is made that way).</param>
    public TestApp(Dictionary<string, string?> settings, Action<IServiceCollection>? services = null, Action<IWebHostBuilder>? host = null, bool migrated = true)
    {
        // A Standalone host with the setup off needs its administrator before it starts (it stops without one): the set-up template's.
        var standalone = string.Equals(settings.GetValueOrDefault("Auth:Mode"), "Standalone", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(settings.GetValueOrDefault("Setup:Enabled"), "true", StringComparison.OrdinalIgnoreCase);
        if (migrated && !settings.ContainsKey("Database:ConnectionString"))
        {
            if (standalone) CopyStandaloneDatabase(_db);
            else CopyMigratedDatabase(_db);
        }
        var all = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = $"Data Source={_db}",
            ["Storage:Path"] = UploadsPath,
            ["Storage:PhotosPath"] = PhotosPath,
            ["DataProtection:KeysPath"] = KeysPath, // the cookies' keys of this host only, never in the build output
            ["Backups:Path"] = BackupsPath, // a backups folder of its own
            ["Logging:LogLevel:Tankstat"] = "Debug", // the Debug lines are part of what is tested
            // Every host starts on an empty database, which would wait for its setup (a notice more, the setup's code): only the setup's
            // own tests turn it on.
            ["Setup:Enabled"] = "false",
        };
        // No schedule or retention of the developer's environment (Backups__Every, ...): a start writes a missed backup and retention prunes
        // the folder. A null here wins over the variable; only one the environment sets is overridden, since a key held here is read-only on
        // the settings page, which the tests of these settings save through. The environment is read as the configuration reads it, so a
        // variable spelled in other letters (BACKUPS__EVERY) counts too.
        var environment = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        foreach (var key in ScheduleKeys)
            if (environment[key] is not null) all[key] = null;
        // Nor the retired bootstrap administrator (#149), which an older environment may still export: every start would warn of it, and the
        // tests that a start logs no Warning would fail on that machine only.
        foreach (var key in Tankstat.Application.Setup.SetupService.RetiredSettings)
            if (environment[key] is not null) all[key] = null;
        // Nor the developer's or the runner's telemetry (an OTLP endpoint of their own): a test host never exports to it. Program.cs decides
        // on telemetry before the host is built, where these settings do not exist yet, so they travel as command-line arguments (below), an
        // empty one standing for none; the telemetry tests set theirs through their settings, which come after this.
        var telemetry = environment.AsEnumerable().Select(e => e.Key).Where(IsTelemetryKey).ToDictionary(k => k, _ => (string?)"", StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings)
        {
            all[key] = value;
            if (IsTelemetryKey(key)) telemetry[key] = value ?? "";
        }
        _ownSettingsFile = settings.GetValueOrDefault("Settings:Path") is null;
        SettingsPath = settings.GetValueOrDefault("Settings:Path") ?? Path.Combine(Path.GetTempPath(), $"tankstat-it-{Guid.NewGuid():N}-settings.json");

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development"); // introspection etc.; never uses the dev appsettings of a real database
            b.UseSetting("Settings:Path", SettingsPath); // read before the host is built: a command-line argument, see the class comment
            foreach (var (key, value) in telemetry) b.UseSetting(key, value); // read before the host is built too (TelemetryGate)
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(all));
            b.ConfigureLogging(l => l.AddProvider(Log));
            b.ConfigureServices(s =>
            {
                s.AddSingleton<IServerRestart>(Restarts); // the app registers its own with a plain Add, so this later one wins
                services?.Invoke(s);
            });
            host?.Invoke(b);
        });
    }

    private static bool IsTelemetryKey(string key) => TelemetryKeys.Is(key);

    /// <summary>The first administrator of a Standalone host (<see cref="StandaloneSettings"/>), made by the setup in its database's template.</summary>
    public const string AdminEmail = "root@example.com";
    public const string AdminPassword = "initial-password-1";

    /// <summary>The settings of a Standalone host (its database a copy of the set-up template, with <see cref="AdminEmail"/>), <paramref name="extra"/> over them.</summary>
    public static Dictionary<string, string?> StandaloneSettings(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?> { ["Auth:Mode"] = "Standalone" };
        foreach (var (key, value) in extra ?? []) settings[key] = value;
        return settings;
    }

    public static TestApp Standalone(Dictionary<string, string?>? extra = null, Action<IServiceCollection>? services = null) => new(StandaloneSettings(extra), services);

    /// <summary>A client with its own cookie jar, i.e. its own browser session.</summary>
    public HttpClient NewClient() => Factory.CreateClient();

    public HttpClient NewClientWithoutRedirects() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Like <see cref="NewClientWithoutRedirects"/>, over https: the OIDC correlation cookie is Secure and a browser sends it only then.</summary>
    public HttpClient NewHttpsClientWithoutRedirects() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

    /// <summary>A client without a jar that sends <paramref name="cookies"/> (a <c>Cookie</c> header) with every request: a browser still holding the cookies of an earlier sign-in.</summary>
    public HttpClient NewClientHolding(string cookies)
    {
        var c = Factory.CreateDefaultClient();
        c.DefaultRequestHeaders.Add("Cookie", cookies);
        return c;
    }

    /// <summary>
    /// Leaves the setup of the database at <paramref name="path"/> undecided, as a release before the setup page left it. The template's start
    /// decided its setup (completed, setup being off); every copy decides its own afresh, as a new database does, so a host with the setup
    /// on starts with it waiting.
    /// </summary>
    internal static void UndecideSetup(string path)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE InstanceState SET SetupPendingSince = NULL, SetupCompletedAt = NULL, SetupCompletedVia = NULL";
        command.ExecuteNonQuery();
    }

    /// <summary>Closes the pooled connections to the database at <paramref name="path"/>, so the file can go. Only that database's: ClearAllPools would also close the ones of tests running in parallel.</summary>
    public static void ReleaseDatabase(string path)
    {
        using var pooled = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pooled);
    }

    public void Dispose()
    {
        Factory.Dispose();
        ReleaseDatabase(_db);
        File.Delete(_db);
        if (Directory.Exists(UploadsPath)) Directory.Delete(UploadsPath, recursive: true);
        if (Directory.Exists(PhotosPath)) Directory.Delete(PhotosPath, recursive: true);
        if (Directory.Exists(KeysPath)) Directory.Delete(KeysPath, recursive: true);
        if (Directory.Exists(BackupsPath)) Directory.Delete(BackupsPath, recursive: true);
        if (_ownSettingsFile)
        {
            File.Delete(SettingsPath);
            File.Delete(SettingsPath + ".bak");
        }
    }

    /// <summary>Starts a host on a new database (no auth, nothing else to set up), lets it migrate, and keeps the file until the run ends.</summary>
    private static string CreateTemplate()
    {
        string path;
        using (var app = new TestApp(new Dictionary<string, string?> { ["Auth:Mode"] = "None" }, migrated: false))
        {
            _ = app.Factory.Services; // starts the host: the migrations run as it starts
            path = Path.Combine(Path.GetTempPath(), $"tankstat-it-template-{Guid.NewGuid():N}.db");
            app.Factory.Dispose();
            UndecideSetup(app._db);
            ReleaseDatabase(app._db);
            File.Copy(app._db, path);
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => File.Delete(path);
        return path;
    }

    /// <summary>
    /// Starts a Standalone host with the setup on, on a copy of the template (it waits for its setup), finishes the setup as the setup page
    /// does, through its own service with the process's code (not through GraphQL, whose answer would sign the administrator in and leave a
    /// session every copy would inherit), and keeps the file until the run ends.
    /// </summary>
    private static string CreateStandaloneTemplate()
    {
        var source = Path.Combine(Path.GetTempPath(), $"tankstat-it-standalone-{Guid.NewGuid():N}.db");
        CopyMigratedDatabase(source);
        string path;
        using (var app = new TestApp(new Dictionary<string, string?>
        {
            ["Auth:Mode"] = "Standalone",
            ["Setup:Enabled"] = "true",
            ["Database:ConnectionString"] = $"Data Source={source}",
        }))
        {
            using (var scope = app.Factory.Services.CreateScope())
            {
                var setup = scope.ServiceProvider.GetRequiredService<Tankstat.Application.Setup.SetupService>();
                var code = scope.ServiceProvider.GetRequiredService<Tankstat.Application.Setup.OneTimeCode>().Value;
                setup.FinishAsync(code, AdminEmail, AdminPassword, AdminPassword, acknowledgeOpenAccess: false, CancellationToken.None).GetAwaiter().GetResult();
            }
            app.Factory.Dispose();
            ReleaseDatabase(source);
            path = Path.Combine(Path.GetTempPath(), $"tankstat-it-standalone-template-{Guid.NewGuid():N}.db");
            File.Copy(source, path);
            File.Delete(source);
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => File.Delete(path);
        return path;
    }

    /// <summary>
    /// Takes the administrators of the database at <paramref name="path"/> away (they become plain users), as a database whose instance lost
    /// them: a set-up Standalone instance without an administrator, which reopens its setup at start (#149).
    /// </summary>
    internal static void RemoveAdministrators(string path)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Users SET IsAdmin = 0";
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// One database, one key ring and one set of upload folders for several hosts in turn: the same instance restarted with other settings (another
/// auth mode, say). The database starts as a copy of the set-up Standalone template (its administrator, the setup completed), or of the
/// migrated one (no user, the setup undecided); every host gets the shared paths over its own settings, and starting a host lets go of the
/// connections the host before held, so dispose that one first.
/// </summary>
internal sealed class SharedData : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tankstat-it-{Guid.NewGuid():N}-shared");

    public string DatabasePath => _root + ".db";
    public string KeysPath => _root + "-keys";
    public string UploadsPath => _root + "-uploads";
    public string PhotosPath => _root + "-photos";
    public string SettingsPath => _root + "-settings.json";

    /// <param name="standalone">
    /// A copy of the set-up Standalone template: its administrator (<see cref="TestApp.AdminEmail"/>) is there for a Standalone host in turn.
    /// False: of the migrated template, which holds no user (a test about what data a database holds).
    /// </param>
    public SharedData(bool standalone = true)
    {
        if (standalone) TestApp.CopyStandaloneDatabase(DatabasePath);
        else TestApp.CopyMigratedDatabase(DatabasePath);
    }

    public TestApp Host(Dictionary<string, string?> settings, Action<IServiceCollection>? services = null, Action<IWebHostBuilder>? host = null)
    {
        TestApp.ReleaseDatabase(DatabasePath); // the pooled connections of the host before
        return new(new Dictionary<string, string?>(settings)
        {
            ["Database:ConnectionString"] = $"Data Source={DatabasePath}",
            ["DataProtection:KeysPath"] = KeysPath,
            ["Storage:Path"] = UploadsPath,
            ["Storage:PhotosPath"] = PhotosPath,
            ["Settings:Path"] = SettingsPath,
        }, services, host);
    }

    /// <summary>
    /// Signs the first administrator (<see cref="TestApp.AdminEmail"/>, the set-up template's) in on a Standalone host over this data and gives back what the browser then holds, as a
    /// <c>Cookie</c> header with both cookies (what a browser sends to <c>/auth/token/...</c>; <c>/graphql</c> reads the access cookie only):
    /// a sign-in made before the instance was restarted in another mode.
    /// </summary>
    public async Task<string> StandaloneAdminCookiesAsync()
    {
        using var standalone = Host(TestApp.StandaloneSettings());
        var login = await standalone.Factory.CreateDefaultClient().PostAsJsonAsync("/graphql", new // the cookies are read off the response: no jar needed
        {
            query = "mutation($i: LoginInput!) { login(input: $i) { id } }",
            variables = new { i = new { email = TestApp.AdminEmail, password = TestApp.AdminPassword } },
        });
        (await login.Content.ReadFromJsonAsync<JsonElement>()).Data().GetProperty("login"); // signed in
        return string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
    }

    public void Dispose()
    {
        TestApp.ReleaseDatabase(DatabasePath);
        File.Delete(DatabasePath);
        File.Delete(SettingsPath);
        File.Delete(SettingsPath + ".bak");
        foreach (var folder in new[] { KeysPath, UploadsPath, PhotosPath })
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}

internal sealed class FakeServerRestart : IServerRestart
{
    public int Count { get; private set; }

    /// <summary>The restarts a start step asked for at once (a restore that wrote the settings file): counted, never thrown, since a test host cannot take a start step that throws.</summary>
    public int NowCount { get; private set; }

    public void Restart() => Count++;

    public void RestartNow() => NowCount++;
}

/// <summary>Two signed-in users of a Standalone host, created by its administrator, who stays signed in too.</summary>
internal sealed record TwoUsers(HttpClient Admin, HttpClient Alice, string AliceId, HttpClient Bob, string BobId);

internal static class TestAppUsers
{
    /// <summary>Alice and Bob, each in their own browser session (passwords <c>alice-password-1</c> / <c>bob-password-1</c>).</summary>
    public static async Task<TwoUsers> Users(this TestApp app)
    {
        var admin = app.NewClient();
        await admin.LoginAs("root@example.com", "initial-password-1");
        async Task<(HttpClient Client, string Id)> Create(string name)
        {
            var created = (await admin.Gql("mutation($i: CreateUserInput!) { createUser(input: $i) { user { id } reset { token } } }",
                new { i = new { email = $"{name}@example.com", displayName = name, isAdmin = false } })).Data().GetProperty("createUser");
            await app.NewClient().Gql("mutation($i: ResetPasswordInput!) { resetPassword(input: $i) }",
                new { i = new { token = created.GetProperty("reset").GetProperty("token").GetString(), newPassword = name + "-password-1", newPasswordConfirmation = name + "-password-1" } });
            var client = app.NewClient();
            await client.LoginAs($"{name}@example.com", name + "-password-1");
            return (client, created.GetProperty("user").GetProperty("id").GetString()!);
        }
        var (alice, aliceId) = await Create("alice");
        var (bob, bobId) = await Create("bob");
        return new TwoUsers(admin, alice, aliceId, bob, bobId);
    }
}

internal static class GraphQLClient
{
    /// <param name="extensions">The request's <c>extensions</c>, a client's own JSON; only the telemetry tests send any.</param>
    public static async Task<JsonElement> Gql(this HttpClient client, string query, object? variables = null, object? extensions = null)
    {
        var response = await client.PostAsJsonAsync("/graphql", new { query, variables, extensions });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The translation key of the first error (<c>extensions.key</c>), or null without errors.</summary>
    public static string? ErrorKey(this JsonElement body) =>
        body.TryGetProperty("errors", out var errors) && errors[0].GetProperty("extensions").TryGetProperty("key", out var key) ? key.GetString() : null;

    public static string? ErrorCode(this JsonElement body) =>
        body.TryGetProperty("errors", out var errors)
            ? errors[0].GetProperty("extensions").GetProperty("code").GetString()
            : null;

    public static JsonElement Data(this JsonElement body)
    {
        Assert.False(body.TryGetProperty("errors", out var errors), errors.ToString());
        return body.GetProperty("data");
    }

    public static async Task<JsonElement> LoginAs(this HttpClient client, string email, string password)
    {
        var body = await client.Gql(
            "mutation($i: LoginInput!) { login(input: $i) { id displayName email isAdmin } }",
            new { i = new { email, password } });
        return body.Data().GetProperty("login");
    }
}
