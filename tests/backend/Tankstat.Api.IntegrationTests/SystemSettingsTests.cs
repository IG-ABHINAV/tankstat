using System.Text.Json;
using System.Reflection;
using Microsoft.Extensions.Options;
using Tankstat.Application.SystemSettings;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Tankstat.Infrastructure.Settings;

namespace Tankstat.Api.IntegrationTests;

/// <summary>
/// The system settings an administrator manages through GraphQL: every setting with its source, changes written into the host's own
/// settings file (the defaults, notifications, sync and log levels in effect at once, the rest after a restart), refusals that write nothing,
/// secrets never shown or logged, and the restart that reaches the host's restart service (a fake here: the real one would stop the test host).
/// </summary>
[Collection(ApiCollection.Name)]
public class SystemSettingsTests
{
    private const string Service = "Tankstat.Application.SystemSettings.SystemSettingsService";

    private const string Query = "{ systemSettings { file { path exists ignored problems } restartPending groups { name settings { key kind choices value isSet isSecret source editable restartPending } } } }";

    private const string Update = "mutation($c: [SystemSettingChangeInput!]!) { updateSystemSettings(changes: $c) { restartPending file { exists } groups { name settings { key value isSet isSecret source editable restartPending } } } }";

    private static async Task<HttpClient> Admin(TestApp app)
    {
        var c = app.NewClient();
        await c.LoginAs(TestApp.AdminEmail, TestApp.AdminPassword);
        return c;
    }

    private static JsonElement Setting(JsonElement settings, string key) =>
        settings.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("settings").EnumerateArray()).Single(s => s.GetProperty("key").GetString() == key);

    private static string? ErrorKey(JsonElement body) => body.GetProperty("errors")[0].GetProperty("extensions").GetProperty("key").GetString();

    [Fact]
    public async Task TheFolders_AreListedInFull_WithTheVariableThatSetsEach_ForAdministratorsOnly()
    {
        using var app = TestApp.Standalone();
        const string Folders = "{ systemSettings { folders { kind path setting variable sameAsPictures } } }";

        var folders = (await (await Admin(app)).Gql(Folders)).Data().GetProperty("systemSettings").GetProperty("folders").EnumerateArray().ToList();

        Assert.Equal(["PICTURES", "PHOTOS", "BACKUPS", "KEYS", "SETTINGS_FILE"], folders.Select(f => f.GetProperty("kind").GetString()));
        Assert.Equal(["Storage__Path", "Storage__PhotosPath", "Backups__Path", "DataProtection__KeysPath", "Settings__Path"], folders.Select(f => f.GetProperty("variable").GetString()));
        Assert.All(folders, f => Assert.True(Path.IsPathFullyQualified(f.GetProperty("path").GetString()!)));
        Assert.Equal(app.SettingsPath, folders[4].GetProperty("path").GetString());
        Assert.False(folders[1].GetProperty("sameAsPictures").GetBoolean()); // the test host keeps its photos apart
        var people = await app.Users();
        Assert.Equal("auth.adminRequired", (await people.Alice.Gql(Folders)).ErrorKey());
    }

    [Fact]
    public async Task TheTelemetrySettings_AreSavedUnderTheirStandardNames_AndWaitForARestart()
    {
        using var app = TestApp.Standalone();
        var admin = await Admin(app);

        var answer = (await admin.Gql(Update, new { c = new object[] { new { key = "OTEL_EXPORTER_OTLP_ENDPOINT", value = "http://collector:4317" }, new { key = "Telemetry:TraceDatabase", value = "true" } } }))
            .Data().GetProperty("updateSystemSettings");

        Assert.True(answer.GetProperty("restartPending").GetBoolean()); // read when the host is built
        var endpoint = Setting(answer, "OTEL_EXPORTER_OTLP_ENDPOINT");
        Assert.Equal(("FILE", "http://collector:4317", true), (endpoint.GetProperty("source").GetString(), endpoint.GetProperty("value").GetString(), endpoint.GetProperty("restartPending").GetBoolean()));
        var written = SettingsFileStore.Read(app.SettingsPath);
        Assert.Equal(("http://collector:4317", "true"), (written["OTEL_EXPORTER_OTLP_ENDPOINT"], written["Telemetry:TraceDatabase"])); // the very names the SDK reads
    }

    [Fact]
    public async Task Administrators_SeeEverySetting_WithItsSource_OthersNothing()
    {
        using var app = TestApp.Standalone();
        var settings = (await (await Admin(app)).Gql(Query)).Data().GetProperty("systemSettings");

        Assert.Equal(["AUTH", "SMTP", "DEFAULTS", "NOTIFICATIONS", "SYNC", "PHOTO_READING", "BACKUPS", "LOGGING", "TELEMETRY"], settings.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("name").GetString()));
        // The OpenTelemetry SDK's own settings, under their standard names: set nowhere here, so editable; the headers a secret.
        var endpoint = Setting(settings, "OTEL_EXPORTER_OTLP_ENDPOINT");
        Assert.Equal(("DEFAULT", true, false, "TEXT"), (endpoint.GetProperty("source").GetString(), endpoint.GetProperty("editable").GetBoolean(), endpoint.GetProperty("isSet").GetBoolean(), endpoint.GetProperty("kind").GetString()));
        Assert.True(Setting(settings, "OTEL_EXPORTER_OTLP_HEADERS").GetProperty("isSecret").GetBoolean());
        Assert.Equal(["grpc", "http/protobuf"], Setting(settings, "OTEL_EXPORTER_OTLP_PROTOCOL").GetProperty("choices").EnumerateArray().Select(c => c.GetString()));
        var mode = Setting(settings, "Auth:Mode");
        Assert.Equal(("ENVIRONMENT", false, "Standalone", "CHOICE"), (mode.GetProperty("source").GetString(), mode.GetProperty("editable").GetBoolean(), mode.GetProperty("value").GetString(), mode.GetProperty("kind").GetString())); // the host's own settings sit above the file, as the environment does
        Assert.Equal(["None", "Standalone", "Oidc", "ProxyHeader"], mode.GetProperty("choices").EnumerateArray().Select(c => c.GetString()));
        var currency = Setting(settings, "Defaults:Currency");
        Assert.Equal(("DEFAULT", true, false, JsonValueKind.Null), (currency.GetProperty("source").GetString(), currency.GetProperty("editable").GetBoolean(), currency.GetProperty("isSet").GetBoolean(), currency.GetProperty("value").ValueKind));
        var level = Setting(settings, "Logging:LogLevel:Default");
        Assert.Equal(("SHIPPED", true, "Information"), (level.GetProperty("source").GetString(), level.GetProperty("editable").GetBoolean(), level.GetProperty("value").GetString())); // appsettings.json: a default the file may override
        var file = settings.GetProperty("file");
        Assert.Equal((app.SettingsPath, false, false), (file.GetProperty("path").GetString(), file.GetProperty("exists").GetBoolean(), file.GetProperty("ignored").GetBoolean()));
        Assert.False(settings.GetProperty("restartPending").GetBoolean());

        var users = await app.Users();
        Assert.Equal("FORBIDDEN", (await users.Alice.Gql(Query)).ErrorCode());
        Assert.Equal("FORBIDDEN", (await users.Alice.Gql("mutation { restartServer }")).ErrorCode());
        Assert.Equal("UNAUTHENTICATED", (await app.NewClient().Gql(Query)).ErrorCode());
        Assert.Equal(0, app.Restarts.Count);

        using var none = new TestApp(new() { ["Auth:Mode"] = "None" });
        Assert.Equal("FORBIDDEN", (await none.NewClient().Gql(Query)).ErrorCode()); // mode None has no administrator: the environment changes its mode
    }

    [Fact]
    public async Task ThePasswordMinimum_AppliesAtOnce_InTheSignInGroupThatWaits_IsPublic_AndStaysWithinTheFloorAndTheCeiling()
    {
        using var app = TestApp.Standalone();
        var admin = await Admin(app);
        const string Requirements = "{ passwordRequirements { minLength maxLength } }";
        var before = (await app.NewClient().Gql(Requirements)).Data().GetProperty("passwordRequirements"); // nobody signed in: the reset page asks it
        Assert.Equal((10, 128), (before.GetProperty("minLength").GetInt32(), before.GetProperty("maxLength").GetInt32()));

        var answer = (await admin.Gql(Update, new { c = new object[] { new { key = "Auth:Standalone:PasswordMinLength", value = "14" } } })).Data().GetProperty("updateSystemSettings");

        Assert.False(Setting(answer, "Auth:Standalone:PasswordMinLength").GetProperty("restartPending").GetBoolean());
        Assert.False(answer.GetProperty("restartPending").GetBoolean()); // nothing waits: the rest of the Sign-in group did not change
        var listed = (await admin.Gql("{ systemSettings { groups { name appliesAtOnce settings { key appliesAtOnce } } } }")).Data().GetProperty("systemSettings").GetProperty("groups")
            .EnumerateArray().Single(g => g.GetProperty("name").GetString() == "AUTH");
        Assert.False(listed.GetProperty("appliesAtOnce").GetBoolean());
        Assert.True(listed.GetProperty("settings").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "Auth:Standalone:PasswordMinLength").GetProperty("appliesAtOnce").GetBoolean());
        Assert.Equal(14, (await app.NewClient().Gql(Requirements)).Data().GetProperty("passwordRequirements").GetProperty("minLength").GetInt32());

        // The next new password is judged by it, without a restart.
        var refused = await admin.Gql("mutation($i: ChangePasswordInput!) { changePassword(input: $i) }",
            new { i = new { currentPassword = TestApp.AdminPassword, newPassword = "thirteen-char", newPasswordConfirmation = "thirteen-char" } });
        Assert.Equal("password.tooShort", refused.ErrorKey());

        foreach (var value in new[] { "9", "129" })
            Assert.Equal("settings.invalid", (await admin.Gql(Update, new { c = new object[] { new { key = "Auth:Standalone:PasswordMinLength", value } } })).ErrorKey());
        Assert.Equal(0, app.Restarts.Count);
    }

    [Fact]
    public async Task Changes_AreWrittenIntoTheFile_TheDefaultsApplyAtOnce_TheMailWaitsForARestart()
    {
        using var app = TestApp.Standalone();
        var admin = await Admin(app);
        const string secret = "hunter2-zzzz-secret";

        // No file at start: the save puts it among the sources.
        var answer = (await admin.Gql(Update, new { c = new object[] { new { key = "Defaults:Currency", value = "HUF" }, new { key = "Smtp:Password", value = secret } } })).Data().GetProperty("updateSystemSettings");

        var currency = Setting(answer, "Defaults:Currency");
        Assert.Equal(("HUF", "FILE", false), (currency.GetProperty("value").GetString(), currency.GetProperty("source").GetString(), currency.GetProperty("restartPending").GetBoolean())); // in effect at once
        var password = Setting(answer, "Smtp:Password");
        Assert.Equal((JsonValueKind.Null, true, true, true), (password.GetProperty("value").ValueKind, password.GetProperty("isSet").GetBoolean(), password.GetProperty("isSecret").GetBoolean(), password.GetProperty("restartPending").GetBoolean())); // the mail is built at start
        Assert.True(answer.GetProperty("restartPending").GetBoolean());
        Assert.True(answer.GetProperty("file").GetProperty("exists").GetBoolean());
        Assert.Equal("HUF", (await admin.Gql("{ vehicleDefaults { currency } }")).Data().GetProperty("vehicleDefaults").GetProperty("currency").GetString()); // the next request reads it

        var written = SettingsFileStore.Read(app.SettingsPath);
        Assert.Equal(("HUF", secret), (written["Defaults:Currency"], written["Smtp:Password"]));
        var line = Assert.Single(app.Log.From(Service), e => e.Level == LogLevel.Information);
        Assert.Contains("Smtp:Password", (string)line.Values["Keys"]!);
        Assert.False(app.Log.Mentions(secret), secret);

        await admin.Gql(Update, new { c = new object[] { new { key = "Defaults:Currency", value = "USD" } } });
        Assert.Equal("HUF", SettingsFileStore.Read(app.SettingsPath + ".bak")["Defaults:Currency"]); // the previous file, kept beside it
        Assert.Equal(secret, SettingsFileStore.Read(app.SettingsPath)["Smtp:Password"]); // the secret stayed: the second change did not mention it
        Assert.Equal("USD", (await admin.Gql("{ vehicleDefaults { currency } }")).Data().GetProperty("vehicleDefaults").GetProperty("currency").GetString()); // the file, now read, read again

        using var restarted = new TestApp(TestApp.StandaloneSettings(new() { ["Settings:Path"] = app.SettingsPath })); // the next start reads the file
        var restartedAdmin = await Admin(restarted);
        Assert.Equal("USD", (await restarted.NewClient().Gql("{ vehicleDefaults { currency } }")).Data().GetProperty("vehicleDefaults").GetProperty("currency").GetString());
        var now = (await restartedAdmin.Gql(Query)).Data().GetProperty("systemSettings");
        Assert.Equal(("FILE", "USD", false), (Setting(now, "Defaults:Currency").GetProperty("source").GetString(), Setting(now, "Defaults:Currency").GetProperty("value").GetString(), Setting(now, "Defaults:Currency").GetProperty("restartPending").GetBoolean()));
        Assert.False(now.GetProperty("restartPending").GetBoolean());

        // A file read at start is read afresh too.
        await restartedAdmin.Gql(Update, new { c = new object[] { new { key = "Defaults:Currency", value = "GBP" } } });
        Assert.Equal("GBP", (await restartedAdmin.Gql("{ vehicleDefaults { currency } }")).Data().GetProperty("vehicleDefaults").GetProperty("currency").GetString());
    }

    [Fact]
    public async Task ClearingTheMailAndThePublicAddress_IsSaved_AndSoIsEverySaveAfterIt()
    {
        // The sign-in is read once. A save reloads the configuration, and the sign-in must not be validated again then against the mail the
        // app started with (Auth:PublicUrl is required while Smtp is configured): that turned this checked save, and every later one, into an error.
        var path = Path.Combine(Path.GetTempPath(), $"tankstat-it-{Guid.NewGuid():N}-settings.json");
        await File.WriteAllTextAsync(path, """{ "Smtp": { "Host": "mail.example.com", "From": "tank@example.com" }, "Auth": { "PublicUrl": "https://tank.test" } }""");
        try
        {
            using var app = TestApp.Standalone(new() { ["Settings:Path"] = path });
            var admin = await Admin(app);

            var cleared = (await admin.Gql(Update, new { c = new object[] { new { key = "Smtp:Host", clear = true }, new { key = "Auth:PublicUrl", clear = true } } })).Data().GetProperty("updateSystemSettings");
            Assert.True(Setting(cleared, "Smtp:Host").GetProperty("restartPending").GetBoolean()); // the mail is built at start
            var next = (await admin.Gql(Update, new { c = new object[] { new { key = "Defaults:Currency", value = "HUF" } } })).Data().GetProperty("updateSystemSettings");

            var currency = Setting(next, "Defaults:Currency");
            Assert.Equal(("HUF", "FILE", false), (currency.GetProperty("value").GetString(), currency.GetProperty("source").GetString(), currency.GetProperty("restartPending").GetBoolean()));
            Assert.Empty(app.Log.From(Service).Where(e => e.Level >= LogLevel.Warning)); // taken in whole, not only saved
            Assert.Empty(app.Log.AtLeast(LogLevel.Error));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    [Fact]
    public async Task ALogLevel_AppliesAtOnce()
    {
        using var app = TestApp.Standalone();
        var admin = await Admin(app);
        var probe = app.Factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Probe.Category"); // under the default level
        Assert.False(probe.IsEnabled(LogLevel.Trace));

        var answer = (await admin.Gql(Update, new { c = new object[] { new { key = "Logging:LogLevel:Default", value = "Trace" } } })).Data().GetProperty("updateSystemSettings");

        Assert.True(probe.IsEnabled(LogLevel.Trace)); // the framework's filters follow the configuration
        Assert.False(Setting(answer, "Logging:LogLevel:Default").GetProperty("restartPending").GetBoolean());
        Assert.False(answer.GetProperty("restartPending").GetBoolean());
    }

    [Fact]
    public async Task WhatCannotBeTaken_IsRefusedByKey_AndWritesNothing()
    {
        using var app = TestApp.Standalone();
        var admin = await Admin(app);

        async Task<string?> Refusal(string key, string value)
        {
            var body = await admin.Gql(Update, new { c = new object[] { new { key = "Defaults:Currency", value = "HUF" }, new { key, value } } });
            Assert.Equal("VALIDATION_FAILED", body.ErrorCode());
            return ErrorKey(body);
        }

        Assert.Equal("settings.unknownKey", await Refusal("Database:Provider", "PostgreSql"));
        Assert.Equal("settings.readOnly", await Refusal("Auth:Mode", "None"));
        Assert.Equal("settings.invalid", await Refusal("Smtp:Port", "zzzz-not-a-port"));
        Assert.Equal("settings.invalid", await Refusal("Notifications:MaxPerHour", "0"));
        Assert.Equal("settings.invalid", await Refusal("Logging:LogLevel:Microsoft.AspNetCore", "Debug"));
        Assert.Equal("settings.invalid", await Refusal("OTEL_EXPORTER_OTLP_ENDPOINT", "zzzz-not-an-address")); // the start would leave telemetry off
        Assert.Equal("settings.invalid", await Refusal("OTEL_EXPORTER_OTLP_HEADERS", "zzzz-not-a-pair")); // the SDK would throw on it

        Assert.False(File.Exists(app.SettingsPath)); // all or nothing: the currency did not land either
        Assert.False(app.Log.Mentions("zzzz-not-an-address") || app.Log.Mentions("zzzz-not-a-pair"));
        Assert.False(app.Log.Mentions("zzzz-not-a-port"));
        Assert.Empty(app.Log.From(Service).Where(e => e.Level >= LogLevel.Information));
    }

    [Fact]
    public async Task AMistakeOfTheEnvironmentsOwn_TheAppRunsWith_HoldsNoSaveBack()
    {
        // Smtp:Security is no name of MailKit's: the sender runs it as Auto, so the app starts, and the next start keeps a file with it too.
        using var app = TestApp.Standalone(new() { ["Smtp:Security"] = "Tls" });
        var admin = await Admin(app);

        var answer = (await admin.Gql(Update, new { c = new object[] { new { key = "Defaults:Currency", value = "HUF" } } })).Data().GetProperty("updateSystemSettings");

        Assert.Equal("HUF", Setting(answer, "Defaults:Currency").GetProperty("value").GetString());
        Assert.Equal("settings.invalid", ErrorKey(await admin.Gql(Update, new { c = new object[] { new { key = "Notifications:MaxPerHour", value = "0" } } }))); // what the file adds still counts
    }

    [Fact]
    public async Task AFileTheAppMayNotOpen_IsShownAsEmpty_NotAsAnError()
    {
        if (OperatingSystem.IsWindows()) return; // file modes
        using var app = TestApp.Standalone();
        var admin = await Admin(app);
        await File.WriteAllTextAsync(app.SettingsPath, """{ "Defaults": { "Currency": "HUF" } }""");
        File.SetUnixFileMode(app.SettingsPath, UnixFileMode.None);
        try
        {
            try
            {
                await File.ReadAllTextAsync(app.SettingsPath);
                return; // root reads anything: there is no unreadable file to show here
            }
            catch (UnauthorizedAccessException)
            {
            }

            var settings = (await admin.Gql(Query)).Data().GetProperty("systemSettings");

            Assert.False(Setting(settings, "Defaults:Currency").GetProperty("isSet").GetBoolean());
        }
        finally
        {
            File.SetUnixFileMode(app.SettingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task RestartServer_ReachesTheHostsRestart_AndIsLogged()
    {
        using var app = TestApp.Standalone();
        var admin = await Admin(app);

        Assert.True((await admin.Gql("mutation { restartServer }")).Data().GetProperty("restartServer").GetBoolean());

        Assert.Equal(1, app.Restarts.Count);
        var line = Assert.Single(app.Log.From(Service), e => e.Level == LogLevel.Information);
        Assert.True(line.Values.ContainsKey("AdminId"));
    }
}

/// <summary>A start in another authentication mode signs every device out: a session issued in one mode never works in the next, nor after a switch back.</summary>
[Collection(ApiCollection.Name)]
public class ModeChangeSignOutTests
{
    private const string Service = "Tankstat.Application.SystemSettings.InstanceStartService";

    private static readonly Dictionary<string, string?> OidcSettings = new()
    {
        ["Auth:Mode"] = "Oidc",
        ["Auth:Oidc:Authority"] = "https://id.example.com",
        ["Auth:Oidc:ClientId"] = "tankstat",
        ["Auth:Oidc:ClientSecret"] = "secret",
    };

    private static void StubDiscovery(IServiceCollection s) => s.PostConfigure<OpenIdConnectOptions>("oidc", o =>
        o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration
        {
            Issuer = "https://id.example.com",
            AuthorizationEndpoint = "https://id.example.com/authorize",
            TokenEndpoint = "https://id.example.com/token",
        }));

    private static async Task<string?> UserEmail(HttpClient c)
    {
        var user = (await c.Gql("{ session { user { email } } }")).Data().GetProperty("session").GetProperty("user");
        return user.ValueKind == JsonValueKind.Null ? null : user.GetProperty("email").GetString();
    }

    [Fact]
    public async Task AStartInAnotherMode_SignsEveryDeviceOut_ForGood()
    {
        using var data = new SharedData();
        var cookies = await data.StandaloneAdminCookiesAsync();

        using (var oidc = data.Host(OidcSettings, StubDiscovery))
        {
            Assert.Null(await UserEmail(oidc.NewClientHolding(cookies))); // the Standalone sign-in does not carry over
            var line = Assert.Single(oidc.Log.From(Service), e => e.Level == LogLevel.Information);
            Assert.Equal(("Standalone", "Oidc", 1), (line.Values["Before"], line.Values["After"], line.Values["Count"]));
        }

        using var standalone = data.Host(TestApp.StandaloneSettings());
        Assert.Null(await UserEmail(standalone.NewClientHolding(cookies))); // nor does it come back with the mode
        Assert.Equal(TestApp.AdminEmail, (await standalone.NewClient().LoginAs(TestApp.AdminEmail, TestApp.AdminPassword)).GetProperty("email").GetString()); // a new sign-in works
    }

    [Fact]
    public async Task AStartInTheSameMode_SaysNothing_AndKeepsEveryoneSignedIn()
    {
        using var data = new SharedData();
        var cookies = await data.StandaloneAdminCookiesAsync();

        using var again = data.Host(TestApp.StandaloneSettings());

        Assert.Equal(TestApp.AdminEmail, await UserEmail(again.NewClientHolding(cookies)));
        Assert.Empty(again.Log.From(Service).Where(e => e.Level >= LogLevel.Information));
    }
}

/// <summary>
/// A group that applies at once must be read afresh wherever it is used: a reader that took a plain <c>IOptions</c> would keep the start's
/// value, and the page would say "in effect" of a change that is not. Logging is the framework's own (it monitors the configuration).
/// </summary>
public class LiveSettingsReadersTests
{
    /// <summary>The options class each live group's settings bind into.</summary>
    private static readonly Dictionary<SettingsGroup, Type> OptionsOf = new()
    {
        [SettingsGroup.Defaults] = typeof(Tankstat.Application.Vehicles.VehicleDefaultsOptions),
        [SettingsGroup.Notifications] = typeof(Tankstat.Application.Notifications.NotificationOptions),
        [SettingsGroup.Sync] = typeof(Tankstat.Application.Sync.SyncOptions),
        [SettingsGroup.Backups] = typeof(Tankstat.Application.Backups.BackupOptions),
    };

    [Fact]
    public void EveryLiveGroup_IsReadAfresh_NeverThroughAKeptIOptions()
    {
        Assert.Equal(SettingsCatalog.LiveGroups.Except([SettingsGroup.Logging]).Order(), OptionsOf.Keys.Order()); // a new live group needs its readers checked here

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var parameters = typeof(Program).Assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("Tankstat.")).Select(Assembly.Load).Append(typeof(Program).Assembly)
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetConstructors(all).Cast<MethodBase>().Concat(t.GetMethods(all)).SelectMany(m => m.GetParameters().Select(p => (Where: $"{t.FullName}.{m.Name}", p.ParameterType))))
            .Where(p => p.ParameterType.IsGenericType && OptionsOf.ContainsValue(p.ParameterType.GetGenericArguments()[0]))
            .ToList();
        Assert.Contains(parameters, p => p.ParameterType == typeof(IOptionsSnapshot<Tankstat.Application.Vehicles.VehicleDefaultsOptions>) && p.Where.Contains("GraphQL")); // the scan sees the resolvers
        Assert.DoesNotContain(parameters, p => p.ParameterType.GetGenericTypeDefinition() == typeof(IOptions<>));
    }

    [Fact]
    public void EveryLiveSettingOfAGroupReadAtStart_IsReadAfresh_NeverThroughAKeptIOptions()
    {
        // The password minimum, in the Sign-in group: its own options class, read through PasswordRequirements' monitor.
        Assert.Equal([Tankstat.Application.Users.PasswordOptions.MinLengthKey], SettingsCatalog.LiveKeys);

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var parameters = typeof(Program).Assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("Tankstat.")).Select(Assembly.Load).Append(typeof(Program).Assembly)
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetConstructors(all).Cast<MethodBase>().Concat(t.GetMethods(all)).SelectMany(m => m.GetParameters().Select(p => p.ParameterType)))
            .Where(p => p.IsGenericType && p.GetGenericArguments()[0] == typeof(Tankstat.Application.Users.PasswordOptions))
            .ToList();
        Assert.Contains(typeof(IOptionsMonitor<Tankstat.Application.Users.PasswordOptions>), parameters);
        Assert.DoesNotContain(parameters, p => p.GetGenericTypeDefinition() == typeof(IOptions<>));
    }
}
