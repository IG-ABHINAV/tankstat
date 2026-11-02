using System.Globalization;
using Microsoft.Extensions.Logging;
using Tankstat.Application.Auth;
using Tankstat.Application.Backups;
using Tankstat.Application.Notifications;
using Tankstat.Application.Recognition;
using Tankstat.Application.Sync;
using Tankstat.Application.Telemetry;
using Tankstat.Application.Users;
using Tankstat.Application.Vehicles;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Settings;

namespace Tankstat.Application.SystemSettings;

/// <summary>The groups of the system settings, in the order they are shown.</summary>
public enum SettingsGroup
{
    Auth,
    Smtp,
    Defaults,
    Notifications,
    Sync,
    PhotoReading,
    Backups,
    Logging,
    Telemetry,
}

/// <summary>What a setting's value is, which decides how it is checked and edited.</summary>
public enum SettingKind
{
    Text,
    Multiline,
    Integer,
    Decimal,
    Boolean,
    /// <summary>One of <see cref="SettingDefinition.Choices"/>, matched without regard to case.</summary>
    Choice,
    /// <summary>Several strings (<c>Key:0</c>, <c>Key:1</c>, ... in the configuration).</summary>
    List,
    /// <summary>A password, secret or API key: stored like any text, never shown back.</summary>
    Secret,
    /// <summary>Days of the week, written <c>Monday,Thursday</c> (<c>None</c>: no day); see <see cref="BackupSchedule.ParseDays"/>.</summary>
    Days,
}

/// <summary>One setting the app manages in its own settings file; <see cref="SettingsCatalog"/> lists them all.</summary>
/// <param name="AlsoAccepted">For a choice: spellings a value may still have and the app still runs with (older documentation listed them), never offered.</param>
public sealed record SettingDefinition(SettingsGroup Group, string Key, SettingKind Kind, IReadOnlyList<string>? Choices = null, IReadOnlyList<string>? AlsoAccepted = null)
{
    public bool IsSecret => Kind == SettingKind.Secret;
    public bool IsList => Kind == SettingKind.List;

    /// <summary>
    /// Whether a saved change takes effect at once: the configuration is reloaded after every save, and these settings are read afresh
    /// where they are used (<c>IOptionsSnapshot</c> per request, the logging framework's own monitor). The others are read once, when the
    /// app starts (the sign-in, which signs everybody out when its mode changes; the mail, whose links are built from the sign-in's
    /// public address; photo reading, whose provider is built at start), and wait for a restart.
    /// </summary>
    public bool AppliesAtOnce => SettingsCatalog.LiveGroups.Contains(Group) || SettingsCatalog.LiveKeys.Contains(Key);
}

/// <summary>
/// Every setting the system settings page manages: the typed options classes' values that an administrator may change (the database, the
/// folders, the key ring and the file's own path stay with the environment), each with its kind and, for a choice, its values. The order is
/// the page's order. The catalog is pinned to the options classes by a test, so a property added to one of them is either listed here or
/// left out on purpose (<see cref="LeftOut"/>).
/// </summary>
public static class SettingsCatalog
{
    /// <summary>The log level categories the page offers; the shipped <c>appsettings.json</c> sets exactly these.</summary>
    public const string LoggingDefault = "Logging:LogLevel:Default";
    public const string LoggingTankstat = "Logging:LogLevel:Tankstat";
    public const string LoggingAspNetCore = "Logging:LogLevel:Microsoft.AspNetCore";
    public const string LoggingEfCore = "Logging:LogLevel:Microsoft.EntityFrameworkCore";
    public const string LoggingHttpClient = "Logging:LogLevel:System.Net.Http.HttpClient";

    /// <summary>The ASP.NET Core categories must stay at Warning or above: their request lines hold the token of a password reset link.</summary>
    public const string LogLevelFloorCategory = "Microsoft.AspNetCore";
    public const LogLevel LogLevelFloor = LogLevel.Warning;

    public static readonly IReadOnlyList<string> LogLevels = Enum.GetNames<LogLevel>();

    /// <summary>
    /// The groups whose settings take effect at once after a save (<see cref="SettingDefinition.AppliesAtOnce"/>). A group joins only when
    /// every reader of its settings reads them afresh: an <c>IOptionsSnapshot</c> or an <c>IOptionsMonitor</c>, never a kept <c>IOptions</c>
    /// value; <c>LiveSettingsReadersTests</c> pins that for each.
    /// </summary>
    public static readonly IReadOnlySet<SettingsGroup> LiveGroups = new HashSet<SettingsGroup> { SettingsGroup.Defaults, SettingsGroup.Notifications, SettingsGroup.Sync, SettingsGroup.Backups, SettingsGroup.Logging };

    /// <summary>
    /// Settings that take effect at once in a group read at start: their options class of its own is read afresh (the password
    /// requirements, <c>PasswordOptions</c> through <c>PasswordRequirements</c>); <c>LiveSettingsReadersTests</c> pins it like the groups'.
    /// </summary>
    public static readonly IReadOnlySet<string> LiveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PasswordOptions.MinLengthKey };

    /// <summary>Settings of the managed sections that the page deliberately does not offer (the reflection test skips them).</summary>
    public static readonly IReadOnlyList<string> LeftOut =
    [
        "Recognition:OpenAiCompatible:SystemPromptFile", // a path the app reads: the environment's business, like every other path
        "Backups:Path", // a folder, like Storage:Path: where the volume is, which the environment knows
    ];

    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        new(SettingsGroup.Auth, "Auth:Mode", SettingKind.Choice, Enum.GetNames<AuthMode>()),
        new(SettingsGroup.Auth, "Auth:PublicUrl", SettingKind.Text),
        new(SettingsGroup.Auth, "Auth:AdminEmails", SettingKind.List),
        new(SettingsGroup.Auth, "Auth:AccessTokenMinutes", SettingKind.Integer),
        new(SettingsGroup.Auth, "Auth:RefreshTokenDays", SettingKind.Integer),
        new(SettingsGroup.Auth, "Auth:RefreshRotationGraceSeconds", SettingKind.Integer),
        new(SettingsGroup.Auth, PasswordOptions.MinLengthKey, SettingKind.Integer),
        new(SettingsGroup.Auth, "Auth:Standalone:MaxFailedAttempts", SettingKind.Integer),
        new(SettingsGroup.Auth, "Auth:Standalone:LockoutMinutes", SettingKind.Integer),
        new(SettingsGroup.Auth, "Auth:Standalone:ResetTokenMinutes", SettingKind.Integer),
        new(SettingsGroup.Auth, "Auth:Standalone:ResetCooldownMinutes", SettingKind.Integer),
        new(SettingsGroup.Auth, "Auth:Standalone:AllowAdminSetPassword", SettingKind.Boolean),
        new(SettingsGroup.Auth, "Auth:Oidc:Authority", SettingKind.Text),
        new(SettingsGroup.Auth, "Auth:Oidc:ClientId", SettingKind.Text),
        new(SettingsGroup.Auth, "Auth:Oidc:ClientSecret", SettingKind.Secret),
        new(SettingsGroup.Auth, "Auth:Oidc:Scopes", SettingKind.List),
        new(SettingsGroup.Auth, "Auth:ProxyHeader:UserHeader", SettingKind.Text),
        new(SettingsGroup.Auth, "Auth:ProxyHeader:EmailHeader", SettingKind.Text),
        new(SettingsGroup.Auth, "Auth:ProxyHeader:TrustedProxies", SettingKind.List),

        new(SettingsGroup.Smtp, "Smtp:Host", SettingKind.Text),
        new(SettingsGroup.Smtp, "Smtp:Port", SettingKind.Integer),
        new(SettingsGroup.Smtp, "Smtp:Username", SettingKind.Text),
        new(SettingsGroup.Smtp, "Smtp:Password", SettingKind.Secret),
        new(SettingsGroup.Smtp, "Smtp:From", SettingKind.Text),
        // MailKit's names, which the sender parses (pinned by SmtpEmailSenderTests); "Ssl", which the documentation used to list, is no name of
        // MailKit's and runs as Auto: a file holding it still starts, but it is not offered.
        new(SettingsGroup.Smtp, "Smtp:Security", SettingKind.Choice, ["Auto", "None", "StartTls", "StartTlsWhenAvailable", "SslOnConnect"], AlsoAccepted: ["Ssl"]),

        new(SettingsGroup.Defaults, "Defaults:DistanceUnit", SettingKind.Choice, Enum.GetNames<DistanceUnit>()),
        new(SettingsGroup.Defaults, "Defaults:VolumeUnit", SettingKind.Choice, Enum.GetNames<VolumeUnit>()),
        new(SettingsGroup.Defaults, "Defaults:Currency", SettingKind.Text),
        new(SettingsGroup.Defaults, "Defaults:RecurringWarnDays", SettingKind.Integer),
        new(SettingsGroup.Defaults, "Defaults:RecurringWarnDistance", SettingKind.Integer),
        new(SettingsGroup.Defaults, "Defaults:TimeFormat", SettingKind.Choice, Enum.GetNames<TimeFormat>()),
        new(SettingsGroup.Defaults, "Defaults:DateFormat", SettingKind.Choice, Enum.GetNames<DateFormat>()),

        new(SettingsGroup.Notifications, "Notifications:MaxPerHour", SettingKind.Integer),
        new(SettingsGroup.Notifications, "Notifications:ReadRetentionDays", SettingKind.Integer),

        new(SettingsGroup.Sync, "Sync:RetentionDays", SettingKind.Integer),
        new(SettingsGroup.Sync, "Sync:TombstoneRetentionDays", SettingKind.Integer),

        new(SettingsGroup.PhotoReading, "Recognition:Provider", SettingKind.Choice, Enum.GetNames<RecognitionProviderKind>()),
        new(SettingsGroup.PhotoReading, "Recognition:OpenAiCompatible:BaseUrl", SettingKind.Text),
        new(SettingsGroup.PhotoReading, "Recognition:OpenAiCompatible:Model", SettingKind.Text),
        new(SettingsGroup.PhotoReading, "Recognition:OpenAiCompatible:ApiKey", SettingKind.Secret),
        new(SettingsGroup.PhotoReading, "Recognition:OpenAiCompatible:SystemPrompt", SettingKind.Multiline),
        new(SettingsGroup.PhotoReading, "Recognition:OpenAiCompatible:ResponseFormat", SettingKind.Choice, Enum.GetNames<OpenAiResponseFormat>()),
        new(SettingsGroup.PhotoReading, "Recognition:OpenAiCompatible:Temperature", SettingKind.Decimal),
        new(SettingsGroup.PhotoReading, "Recognition:OpenAiCompatible:TimeoutSeconds", SettingKind.Integer),
        new(SettingsGroup.PhotoReading, "Recognition:OpenAiCompatible:LogTraffic", SettingKind.Boolean),
        new(SettingsGroup.PhotoReading, "Recognition:MinConfidence", SettingKind.Decimal),
        new(SettingsGroup.PhotoReading, "Recognition:MaxConcurrent", SettingKind.Integer),

        new(SettingsGroup.Backups, "Backups:Every", SettingKind.Text),
        new(SettingsGroup.Backups, "Backups:Days", SettingKind.Days),
        new(SettingsGroup.Backups, "Backups:Keep", SettingKind.Integer),
        new(SettingsGroup.Backups, "Backups:KeepDays", SettingKind.Integer),
        new(SettingsGroup.Backups, "Backups:Photos", SettingKind.Boolean),
        new(SettingsGroup.Backups, "Backups:Environment", SettingKind.Choice, Enum.GetNames<BackupEnvironmentMode>()),
        new(SettingsGroup.Backups, "Backups:Secrets", SettingKind.Boolean),
        new(SettingsGroup.Backups, "Backups:Encryption:Recipient", SettingKind.Text),
        new(SettingsGroup.Backups, "Backups:Encryption:Salt", SettingKind.Text),
        new(SettingsGroup.Backups, "Backups:PassphraseMinLength", SettingKind.Integer),
        new(SettingsGroup.Backups, "Backups:MaxUploadMb", SettingKind.Integer),

        new(SettingsGroup.Logging, LoggingDefault, SettingKind.Choice, LogLevels),
        new(SettingsGroup.Logging, LoggingTankstat, SettingKind.Choice, LogLevels),
        new(SettingsGroup.Logging, LoggingAspNetCore, SettingKind.Choice, LogLevels),
        new(SettingsGroup.Logging, LoggingEfCore, SettingKind.Choice, LogLevels),
        new(SettingsGroup.Logging, LoggingHttpClient, SettingKind.Choice, LogLevels),

        // The OpenTelemetry SDK's own settings under their standard names (the SDK reads them from the configuration), like Logging's are the
        // framework's; read when the host is built, so a change waits for a restart. Only the database switch is the app's (TelemetryOptions).
        new(SettingsGroup.Telemetry, OtelSettings.Endpoint, SettingKind.Text),
        new(SettingsGroup.Telemetry, OtelSettings.Protocol, SettingKind.Choice, OtelSettings.Protocols),
        new(SettingsGroup.Telemetry, OtelSettings.Headers, SettingKind.Secret),
        new(SettingsGroup.Telemetry, OtelSettings.ServiceName, SettingKind.Text),
        new(SettingsGroup.Telemetry, TelemetryOptions.TraceDatabaseKey, SettingKind.Boolean),
    ];

    private static readonly Dictionary<string, SettingDefinition> ByKey = All.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The groups in the page's order.</summary>
    public static IEnumerable<SettingsGroup> Groups => Enum.GetValues<SettingsGroup>();

    /// <summary>The definition of <paramref name="key"/> (case does not matter, as in the configuration), or null for a setting the page does not manage.</summary>
    public static SettingDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>The settings of one group, in order.</summary>
    public static IEnumerable<SettingDefinition> Of(SettingsGroup group) => All.Where(d => d.Group == group);

    /// <summary>The options classes the managed sections bind into, by section name.</summary>
    private static readonly (string Section, Type Options)[] OptionsSections =
    [
        (PasswordOptions.SectionName, typeof(PasswordOptions)), // inside Auth: a longer section is looked at first
        (AuthOptions.SectionName, typeof(AuthOptions)),
        (SmtpOptions.SectionName, typeof(SmtpOptions)),
        (VehicleDefaultsOptions.SectionName, typeof(VehicleDefaultsOptions)),
        (NotificationOptions.SectionName, typeof(NotificationOptions)),
        (SyncOptions.SectionName, typeof(SyncOptions)),
        (RecognitionOptions.SectionName, typeof(RecognitionOptions)),
        (BackupOptions.SectionName, typeof(BackupOptions)),
        (TelemetryOptions.SectionName, typeof(TelemetryOptions)),
    ];

    /// <summary>The level the logging framework uses when no source sets the default category (every other category follows that one).</summary>
    private const string FrameworkLogLevel = nameof(LogLevel.Information);

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Defaults = new(() =>
    {
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in All)
            if (DefaultOf(setting) is { } value) defaults[setting.Key] = value;
        return defaults;
    });

    /// <summary>
    /// What the app runs with when no source sets <paramref name="key"/>: the options class's own value (the default log category: the
    /// framework's), as the page writes such a value. Null for a secret (nothing is said of one), a list (the items given are added to the
    /// built-in ones, so showing those as the value would be misleading), another log category (it follows the default one), a setting
    /// without one, and one the page does not manage.
    /// </summary>
    public static string? BuiltInDefault(string key) => Defaults.Value.GetValueOrDefault(key);

    private static string? DefaultOf(SettingDefinition setting)
    {
        if (setting.IsSecret || setting.IsList) return null;
        if (setting.Group == SettingsGroup.Logging) return setting.Key == LoggingDefault ? FrameworkLogLevel : null;
        if (OtelSettings.IsOtelKey(setting.Key)) return OtelSettings.DefaultOf(setting.Key); // the SDK's own, not an options class's
        // The options class whose section holds the key; a section inside another (Auth:Standalone of the password requirements) is tried
        // before the outer one, which does not know the key.
        object? current = null;
        foreach (var (section, options) in OptionsSections.Where(s => setting.Key.StartsWith(s.Section + ":", StringComparison.OrdinalIgnoreCase)))
        {
            current = Activator.CreateInstance(options);
            foreach (var name in setting.Key[(section.Length + 1)..].Split(':'))
            {
                var property = current?.GetType().GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                current = property?.GetValue(current);
                if (property is null)
                {
                    current = null;
                    break;
                }
            }
            if (current is not null) break;
        }
        return current switch
        {
            // A choice the options class leaves empty is read as its None, where it offers one (the photo provider: not set means none).
            null when setting.Kind == SettingKind.Choice && setting.Choices!.Contains("None") => "None",
            null => null,
            string text => string.IsNullOrWhiteSpace(text) ? null : text,
            bool flag => flag ? "true" : "false",
            Enum choice => choice.ToString(),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    /// <summary>
    /// Why <paramref name="value"/> is not a value for <paramref name="setting"/> (null when it is): the shape its kind needs, a choice among the
    /// choices, and the log levels' floor. The message names the setting and the rule, never the value: it goes into the log.
    /// </summary>
    public static string? Validate(SettingDefinition setting, string value) =>
        ShapeProblem(setting, value)
        ?? (IsBelowLogLevelFloor(setting.Key, value) ? $"{setting.Key} must not be below {LogLevelFloor}: the request lines of ASP.NET Core hold the tokens of password reset links." : null);

    /// <summary>The shape <paramref name="setting"/>'s kind needs, a choice among the choices (or a spelling still accepted); no rule beyond that.</summary>
    private static string? ShapeProblem(SettingDefinition setting, string value)
    {
        switch (setting.Kind)
        {
            case SettingKind.Integer:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return $"{setting.Key} must be a whole number.";
                break;
            case SettingKind.Decimal:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) return $"{setting.Key} must be a number.";
                break;
            case SettingKind.Boolean:
                if (!bool.TryParse(value, out _)) return $"{setting.Key} must be true or false.";
                break;
            case SettingKind.Choice:
                var choice = value.Trim();
                if (!setting.Choices!.Contains(choice, StringComparer.OrdinalIgnoreCase) && setting.AlsoAccepted?.Contains(choice, StringComparer.OrdinalIgnoreCase) != true)
                    return $"{setting.Key} must be one of {string.Join(", ", setting.Choices!)}.";
                break;
            case SettingKind.Days:
                if (BackupSchedule.ParseDays(value) is null) return $"{setting.Key} must be days of the week such as Monday,Thursday, or {BackupSchedule.NoDay}.";
                break;
        }
        return OtelProblem(setting.Key, value);
    }

    /// <summary>The OpenTelemetry settings' own shapes, as the SDK reads them: it throws on some (headers) and quietly drops others (a protocol in other letters).</summary>
    private static string? OtelProblem(string key, string value)
    {
        if (key.Equals(OtelSettings.Endpoint, StringComparison.OrdinalIgnoreCase) && !OtelSettings.IsEndpoint(value))
            return $"{key} must be an absolute http or https address.";
        if (key.Equals(OtelSettings.Protocol, StringComparison.OrdinalIgnoreCase) && !OtelSettings.IsProtocol(value))
            return $"{key} must be one of {string.Join(", ", OtelSettings.Protocols)}, written so.";
        if (key.Equals(OtelSettings.Headers, StringComparison.OrdinalIgnoreCase) && !OtelSettings.AreHeaders(value))
            return $"{key} must be key=value pairs separated by commas.";
        return null;
    }

    /// <summary>A log level category under <see cref="LogLevelFloorCategory"/> set below <see cref="LogLevelFloor"/>.</summary>
    public static bool IsBelowLogLevelFloor(string key, string value)
    {
        const string prefix = "Logging:LogLevel:";
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var category = key[prefix.Length..];
        var floored = category.Equals(LogLevelFloorCategory, StringComparison.OrdinalIgnoreCase)
            || category.StartsWith(LogLevelFloorCategory + ".", StringComparison.OrdinalIgnoreCase);
        return floored && Enum.TryParse<LogLevel>(value.Trim(), ignoreCase: true, out var level) && level < LogLevelFloor;
    }

    /// <summary>
    /// The wrong shapes among the managed settings that <paramref name="valueOf"/> has a value for, as the start sees them: only what the app
    /// could not start with, named by its setting. Lists are not checked (every string is one); photo reading and telemetry are not either,
    /// because their settings never stop the app (RecognitionSetup turns reading off and says why, TelemetryGate the export); nor is the log levels' floor, a rule for what the page
    /// writes (<see cref="Validate"/>), not something the app refuses to start with; nor an empty value, which the app runs with where a
    /// setting may be unset (the binder reads <c>Backups:Keep</c> as no limit, the logging filter skips an empty level) and the binder refuses
    /// elsewhere, naming the setting itself.
    /// </summary>
    public static IReadOnlyList<string> Problems(Func<string, string?> valueOf)
    {
        var problems = new List<string>();
        foreach (var setting in All)
        {
            if (setting.IsList || setting.Group is SettingsGroup.PhotoReading or SettingsGroup.Telemetry) continue;
            var value = valueOf(setting.Key);
            if (string.IsNullOrEmpty(value)) continue;
            if (ShapeProblem(setting, value) is { } problem) problems.Add(problem);
        }
        return problems;
    }
}
