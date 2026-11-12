using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Tankstat.Application.Auth;
using Tankstat.Application.Notifications;
using Tankstat.Application.Recognition;
using Tankstat.Application.Sync;
using Tankstat.Application.SystemSettings;
using Tankstat.Application.Telemetry;
using Tankstat.Application.Users;
using Tankstat.Application.Vehicles;

namespace Tankstat.Application.UnitTests;

public class SettingsCatalogTests
{
    /// <summary>No file is there (#312: the catalog asks its caller, never the disk).</summary>
    private static bool NoFiles(string path) => false;

    /// <summary>The sections the page manages, as the options classes bind them.</summary>
    private static readonly (Type Options, string Section)[] Sections =
    [
        (typeof(AuthOptions), AuthOptions.SectionName),
        (typeof(PasswordOptions), PasswordOptions.SectionName),
        (typeof(SmtpOptions), SmtpOptions.SectionName),
        (typeof(VehicleDefaultsOptions), VehicleDefaultsOptions.SectionName),
        (typeof(NotificationOptions), NotificationOptions.SectionName),
        (typeof(SyncOptions), SyncOptions.SectionName),
        (typeof(RecognitionOptions), RecognitionOptions.SectionName),
        (typeof(Tankstat.Application.Backups.BackupOptions), Tankstat.Application.Backups.BackupOptions.SectionName),
        (typeof(TelemetryOptions), TelemetryOptions.SectionName),
    ];

    [Fact]
    public void EveryPropertyOfTheManagedSections_IsInTheCatalog_OrLeftOutOnPurpose()
    {
        var expected = Sections.SelectMany(s => Keys(s.Options, s.Section)).ToDictionary(k => k.Key, k => k, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, kind, choices) in expected.Values)
        {
            if (SettingsCatalog.LeftOut.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                Assert.Null(SettingsCatalog.Find(key));
                continue;
            }
            var definition = SettingsCatalog.Find(key);
            Assert.True(definition is not null, $"{key} is neither in the catalog nor left out on purpose");
            Assert.Equal(key, definition!.Key); // the catalog spells the key as the options class does
            if (kind is SettingKind.Text) Assert.Contains(definition.Kind, new[] { SettingKind.Text, SettingKind.Multiline, SettingKind.Secret, SettingKind.Choice, SettingKind.Days });
            else Assert.Equal(kind, definition.Kind);
            if (choices is not null) Assert.Equal(choices, definition.Choices);
        }
        // The logging framework's settings and the OpenTelemetry SDK's (its standard OTEL_* names, which it reads itself) have no class of ours.
        foreach (var definition in SettingsCatalog.All.Where(d => d.Group != SettingsGroup.Logging && !OtelSettings.IsOtelKey(d.Key)))
            Assert.True(expected.ContainsKey(definition.Key), $"{definition.Key} is in the catalog but no options class binds it");
        Assert.All(SettingsCatalog.All.Where(d => OtelSettings.IsOtelKey(d.Key)), d => Assert.Equal(SettingsGroup.Telemetry, d.Group));
    }

    [Fact]
    public void ChoicesAreThere_ExactlyForChoices_AndSecretsAreTheFour()
    {
        Assert.All(SettingsCatalog.All, d => Assert.Equal(d.Kind == SettingKind.Choice, d.Choices is { Count: > 0 }));
        Assert.All(SettingsCatalog.All.Where(d => d.AlsoAccepted is not null), d =>
        {
            Assert.Equal(SettingKind.Choice, d.Kind);
            Assert.Empty(d.AlsoAccepted!.Intersect(d.Choices!, StringComparer.OrdinalIgnoreCase)); // accepted besides the choices, never offered twice
        });
        Assert.Equal(
            ["Auth:Oidc:ClientSecret", "Smtp:Password", "Recognition:OpenAiCompatible:ApiKey", OtelSettings.Headers],
            SettingsCatalog.All.Where(d => d.IsSecret).Select(d => d.Key));
        Assert.Equal(SettingsCatalog.Groups, SettingsCatalog.All.Select(d => d.Group).Distinct()); // the page's order
        Assert.Equal(SettingsCatalog.All.Count, SettingsCatalog.All.Select(d => d.Key.ToLowerInvariant()).Distinct().Count());
    }

    [Theory]
    [InlineData("Smtp:Port", "587", null)]
    [InlineData("Smtp:Port", " 587 ", null)]
    [InlineData("Smtp:Port", "5.5", "whole number")]
    [InlineData("Smtp:Port", "zzzz", "whole number")]
    [InlineData("Recognition:MinConfidence", "0.5", null)]
    [InlineData("Recognition:MinConfidence", "zzzz", "number")]
    [InlineData("Recognition:MinConfidence", "NaN", "number")]
    [InlineData("Auth:Standalone:AllowAdminSetPassword", "true", null)]
    [InlineData("Auth:Standalone:AllowAdminSetPassword", "False", null)]
    [InlineData("Auth:Standalone:AllowAdminSetPassword", "yes", "true or false")]
    [InlineData("Auth:Mode", "standalone", null)]
    [InlineData("Auth:Mode", "Kerberos", "None, Standalone, Oidc, ProxyHeader")]
    [InlineData("Smtp:Security", "starttls", null)]
    [InlineData("Smtp:Security", "SslOnConnect", null)]
    [InlineData("Smtp:Security", "ssl", null)] // the old spelling: accepted (the sender runs it as Auto), never offered
    [InlineData("Smtp:Security", "zzzz", "Auto, None, StartTls, StartTlsWhenAvailable, SslOnConnect")]
    [InlineData("Logging:LogLevel:Default", "Trace", null)]
    [InlineData("Logging:LogLevel:Default", "Verbose", "Trace, Debug")]
    [InlineData("Logging:LogLevel:Microsoft.AspNetCore", "Warning", null)]
    [InlineData("Logging:LogLevel:Microsoft.AspNetCore", "None", null)]
    [InlineData("Logging:LogLevel:Microsoft.AspNetCore", "Information", "must not be below Warning")]
    [InlineData("Backups:Days", "Monday,thu", null)]
    [InlineData("Backups:Days", "None", null)]
    [InlineData("Backups:Days", "zzzz", "days of the week")]
    [InlineData("Auth:PublicUrl", "anything at all", null)]
    [InlineData(OtelSettings.Endpoint, "http://collector:4317", null)]
    [InlineData(OtelSettings.Endpoint, "https://otlp.example.com/otlp", null)]
    [InlineData(OtelSettings.Endpoint, "collector:4317", "absolute http or https address")]
    [InlineData(OtelSettings.Endpoint, "ftp://collector/zzzz", "absolute http or https address")]
    [InlineData(OtelSettings.Protocol, "http/protobuf", null)]
    [InlineData(OtelSettings.Protocol, "GRPC", "written so")] // the SDK takes only its own spelling: in other letters it would run gRPC unasked
    [InlineData(OtelSettings.Protocol, "zzzz", "grpc, http/protobuf")]
    [InlineData(OtelSettings.Headers, "x-api-key=zzzz,x-team=zzzz2", null)]
    [InlineData(OtelSettings.Headers, "x-api-key%3Dzzzz", null)] // the SDK URL-decodes the whole value first
    [InlineData(OtelSettings.Headers, "zzzz", "key=value pairs")] // the SDK would throw at start
    [InlineData(OtelSettings.Headers, "a=zzzz,", "key=value pairs")] // an empty pair after the comma, too
    [InlineData(OtelSettings.Headers, "a=zzzz,a=zzzz2", "no key twice")] // the exporter adds them to a dictionary
    [InlineData(OtelSettings.Headers, "User-Agent=zzzz", "none User-Agent")] // the exporter's own header
    [InlineData(TelemetryOptions.TraceDatabaseKey, "yes", "true or false")]
    public void Validate_ChecksTheShapeOfAValue_AndNamesTheSettingAndTheRule(string key, string value, string? refusal)
    {
        var problem = SettingsCatalog.Validate(SettingsCatalog.Find(key)!, value, NoFiles);

        if (refusal is null) Assert.Null(problem);
        else
        {
            Assert.StartsWith(key, problem);
            Assert.Contains(refusal, problem);
            Assert.DoesNotContain(value.Trim(), problem); // the message goes to the log: the rule, never the value
        }
    }

    [Theory]
    [InlineData("Logging:LogLevel:Microsoft.AspNetCore", "Debug", true)]
    [InlineData("Logging:LogLevel:Microsoft.AspNetCore.Hosting", "Information", true)] // a category below it
    [InlineData("Logging:LogLevel:microsoft.aspnetcore", "trace", true)]
    [InlineData("Logging:LogLevel:Microsoft.AspNetCore", "Warning", false)]
    [InlineData("Logging:LogLevel:Microsoft.AspNetCoreX", "Debug", false)] // a common prefix is not the category
    [InlineData("Logging:LogLevel:Microsoft", "Debug", false)]
    [InlineData("Logging:LogLevel:Default", "Trace", false)]
    [InlineData("Logging:Console:LogLevel:Microsoft.AspNetCore", "Debug", false)] // not the LogLevel section
    public void TheAspNetCoreCategories_NeverGoBelowWarning(string key, string value, bool below) =>
        Assert.Equal(below, SettingsCatalog.IsBelowLogLevelFloor(key, value));

    [Fact]
    public void Problems_NamesEveryWrongShape_AndSkipsListsUnsetSettingsPhotoReadingTelemetryAndTheFloor()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Smtp:Port"] = "zzzz",
            ["Notifications:MaxPerHour"] = "0", // the shape is right; the range is the validator's business
            ["Auth:AdminEmails"] = "not a list, but lists are never checked here",
            ["Auth:Mode"] = "Kerberos",
            ["Recognition:MinConfidence"] = "zzzz", // photo reading never stops the app: RecognitionSetup turns it off instead
            [OtelSettings.Endpoint] = "zzzz", // nor does telemetry: TelemetryGate leaves it off and says why
            [TelemetryOptions.TraceDatabaseKey] = "zzzz",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Information", // the floor is a rule for what the page writes, not for the start
            ["Backups:Keep"] = "", // empty: the binder reads it as no limit (a whole number it may not be is the binder's to refuse, by name)
        };

        var problems = SettingsCatalog.Problems(key => values.GetValueOrDefault(key));

        Assert.Equal(["Auth:Mode", "Smtp:Port"], problems.Select(p => p.Split(' ')[0]));
        Assert.NotNull(SettingsCatalog.Validate(SettingsCatalog.Find("Logging:LogLevel:Microsoft.AspNetCore")!, "Information", NoFiles)); // the page still refuses it
        Assert.NotNull(SettingsCatalog.Validate(SettingsCatalog.Find("Recognition:MinConfidence")!, "zzzz", NoFiles));
        Assert.NotNull(SettingsCatalog.Validate(SettingsCatalog.Find(OtelSettings.Endpoint)!, "zzzz", NoFiles));
    }

    private static IEnumerable<(string Key, SettingKind Kind, string[]? Choices)> Keys(Type type, string prefix)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            var key = $"{prefix}:{property.Name}";
            var t = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (t == typeof(string)) yield return (key, SettingKind.Text, null);
            else if (t == typeof(int) || t == typeof(long)) yield return (key, SettingKind.Integer, null);
            else if (t == typeof(double)) yield return (key, SettingKind.Decimal, null);
            else if (t == typeof(bool)) yield return (key, SettingKind.Boolean, null);
            else if (t.IsEnum) yield return (key, SettingKind.Choice, Enum.GetNames(t));
            else if (typeof(IEnumerable).IsAssignableFrom(t)) yield return (key, SettingKind.List, null);
            else foreach (var nested in Keys(t, key)) yield return nested;
        }
    }

    [Theory]
    [InlineData("Auth:Mode", "None")]
    [InlineData("Auth:AccessTokenMinutes", "15")]
    [InlineData("Auth:Standalone:MaxFailedAttempts", "5")]
    [InlineData("Auth:Standalone:AllowAdminSetPassword", "false")] // as the page writes a switch
    [InlineData("Auth:ProxyHeader:UserHeader", "X-Forwarded-User")]
    [InlineData("Smtp:Port", "587")]
    [InlineData("Recognition:Provider", "None")]
    [InlineData("Recognition:MinConfidence", "0.6")] // invariantly, whatever the server's culture
    [InlineData("Recognition:OpenAiCompatible:TimeoutSeconds", "120")]
    [InlineData("Logging:LogLevel:Default", "Information")] // the framework's, when no source sets the default category
    [InlineData(OtelSettings.Protocol, "grpc")] // the SDK's
    [InlineData(OtelSettings.ServiceName, "tankstat")] // the app's, which it names the service itself when nothing does
    [InlineData(TelemetryOptions.TraceDatabaseKey, "false")]
    public void TheBuiltInDefault_IsTheOptionsClassesOwnValue_WrittenAsThePageWritesIt(string key, string expected) =>
        Assert.Equal(expected, SettingsCatalog.BuiltInDefault(key));

    [Theory]
    [InlineData("Auth:Oidc:ClientSecret")] // a secret: nothing is said of one
    [InlineData("Auth:Oidc:Scopes")] // a list: what is given is added to the built-in scopes, so they are not the value
    [InlineData("Auth:PublicUrl")] // empty: there is none
    [InlineData(OtelSettings.Endpoint)] // none: no endpoint, no telemetry
    [InlineData(OtelSettings.Headers)] // a secret
    [InlineData("Logging:LogLevel:Microsoft.AspNetCore")] // another category follows the default one: no fixed level of its own
    [InlineData("Database:Provider")] // not a setting the page manages
    public void ThereIsNoBuiltInDefault_ForASecret_AList_AnEmptyValue_AFollowingLogCategory_OrASettingThePageDoesNotManage(string key) =>
        Assert.Null(SettingsCatalog.BuiltInDefault(key));

    [Fact]
    public void EveryBuiltInDefault_IsAValueTheSettingTakes()
    {
        foreach (var setting in SettingsCatalog.All)
            if (SettingsCatalog.BuiltInDefault(setting.Key) is { } value)
                Assert.True(SettingsCatalog.Validate(setting, value, NoFiles) is null, $"{setting.Key}'s default {value} is not a value it takes");
    }
}

public class SettingsFileNoticeTests
{
    [Fact]
    public void AFileLeftOut_IsAPublicWarning_ThatNamesNoSetting()
    {
        var status = SettingsFileStatus.LeftOut("/data/settings.json", ["Smtp:Port must be a whole number."]);

        var notices = new NoticeService(new AuthOptions { Mode = AuthMode.None }.Create(), new NoBackups(), status).GetNotices();

        Assert.Equal(["AUTH_DISABLED", "SETTINGS_FILE_IGNORED"], notices.Select(n => n.Code)); // the unsafe warning stays first
        var notice = notices[1];
        Assert.Equal(NoticeSeverity.Warning, notice.Severity);
        Assert.DoesNotContain("Smtp", notice.Message);
        Assert.DoesNotContain("/data", notice.Message);
        Assert.Equal(["SETTINGS_FILE_IGNORED"], new NoticeService(new AuthOptions { Mode = AuthMode.Standalone }.Create(), new NoBackups(), status).GetNotices().Select(n => n.Code));
    }

    [Fact]
    public void AFileInUse_OrNone_OrNoDecision_AddsNoNotice()
    {
        var options = new AuthOptions { Mode = AuthMode.Standalone }.Create();

        Assert.Empty(new NoticeService(options, new NoBackups(), SettingsFileStatus.Used("/data/settings.json", exists: true)).GetNotices());
        Assert.Empty(new NoticeService(options, new NoBackups(), SettingsFileStatus.Used("/data/settings.json", exists: false)).GetNotices());
        Assert.Empty(new NoticeService(options, new NoBackups()).GetNotices());
    }

    [Fact]
    public void TheLogLevelFloor_IsTheOneTheConfigurationDocumentationPromises() => Assert.Equal(LogLevel.Warning, SettingsCatalog.LogLevelFloor);

    [Theory]
    [InlineData("en")]
    [InlineData("hu")]
    public void EverySetting_HasItsLabelAndHelp_InTheWebApp(string language)
    {
        // The page words a setting by its key (admin/settingsText.ts labelKey, helpKey): one left out shows as its key.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tankstat.slnx"))) dir = dir.Parent;
        using var texts = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "src", "frontend", "tankstat-react", "src", "i18n", "locales", $"{language}.json")));
        var settings = texts.RootElement.GetProperty("systemSettings");
        static string Name(string key) => key.Replace(":", "__").Replace(".", "_");

        var unworded = SettingsCatalog.All.Select(d => Name(d.Key))
            .Where(n => !settings.GetProperty("fields").TryGetProperty(n, out _) || !settings.GetProperty("help").TryGetProperty(n, out _)).ToList();

        Assert.True(unworded.Count == 0, $"Without a label or a help text in {language}.json: {string.Join(", ", unworded)}");
    }
}
