using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Tankstat.Application.Backups;
using Tankstat.Infrastructure.Persistence;

namespace Tankstat.Infrastructure.UnitTests;

/// <summary>
/// Every option the app binds is the instance's, a secret or the machine's to a backup (#140), now and when one is added: a new options
/// class or property without a category fails here, and so does a secret by its name (a password, a secret, a key, a token) that is not
/// one, which a plain backup would otherwise carry.
/// </summary>
public sealed partial class SettingCategoriesTests
{
    /// <summary>Every options class the app binds: those with a <c>SectionName</c>, in the Application and the Infrastructure.</summary>
    private static readonly (Type Options, string Section)[] Sections =
    [
        .. new[] { typeof(BackupOptions).Assembly, typeof(DatabaseOptions).Assembly }
            .SelectMany(a => a.GetTypes())
            .Select(t => (Options: t, Field: t.GetField("SectionName", BindingFlags.Public | BindingFlags.Static)))
            .Where(p => p.Field is { IsLiteral: true } && p.Field.FieldType == typeof(string))
            .Select(p => (p.Options, (string)p.Field!.GetRawConstantValue()!)),
    ];

    /// <summary>Every option of an options class by its configuration key, and whether its value is text (a switch or a number holds no secret).</summary>
    private static IEnumerable<(string Key, bool Text)> Keys(Type type, string prefix)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            var key = $"{prefix}:{property.Name}";
            var t = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (t == typeof(string) || typeof(IEnumerable).IsAssignableFrom(t)) yield return (key, true);
            else if (t.IsPrimitive || t.IsEnum || t == typeof(decimal)) yield return (key, false);
            else foreach (var nested in Keys(t, key)) yield return nested;
        }
    }

    private static IEnumerable<(string Key, bool Text)> AllKeys => Sections.SelectMany(s => Keys(s.Options, s.Section));

    [GeneratedRegex("(Password|Secret|Key|Token)$")]
    private static partial Regex SecretByName();

    [Fact]
    public void TheSectionsFound_AreEveryOneTheAppBinds()
    {
        Assert.Equal(
            ["Auth", "Auth:Standalone", "Backups", "DataProtection", "Database", "Defaults", "Notifications", "Recognition", "Setup", "Smtp", "Storage", "Sync", "Telemetry"],
            Sections.Select(s => s.Section).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryOptionTheAppBinds_HasACategory()
    {
        var unsorted = AllKeys.Select(k => k.Key).Where(k => SettingsInBackups.CategoryOf(k) is null).ToList();

        Assert.True(unsorted.Count == 0, $"Without a category for backups (SettingsInBackups): {string.Join(", ", unsorted)}");
    }

    [Fact]
    public void AnOptionNamedLikeASecret_IsNeverTheInstancesPlainSetting()
    {
        var plain = AllKeys.Where(k => k.Text && SecretByName().IsMatch(k.Key) && SettingsInBackups.CategoryOf(k.Key) == SettingCategory.Instance).Select(k => k.Key).ToList();

        Assert.True(plain.Count == 0, $"Named like a secret, but a plain backup would carry it: {string.Join(", ", plain)} (make it SettingKind.Secret, or the machine's)");
    }

    [Theory]
    [InlineData("Auth:Mode", SettingCategory.Instance)]
    [InlineData("Auth:AdminEmails:0", SettingCategory.Instance)]
    [InlineData("Logging:LogLevel:Tankstat", SettingCategory.Instance)]
    [InlineData("Smtp:Password", SettingCategory.Secret)]
    [InlineData("Recognition:OpenAiCompatible:ApiKey", SettingCategory.Secret)]
    [InlineData("Database:ConnectionString", SettingCategory.Machine)]
    [InlineData("Auth:Standalone:AdminPassword", null)] // retired (#149): nothing binds it, so a backup leaves it out by name, never its value
    [InlineData("Backups:Path", SettingCategory.Machine)]
    [InlineData("Setup:Enabled", SettingCategory.Machine)]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", SettingCategory.Instance)] // the OpenTelemetry SDK's own, edited on the Telemetry tab
    [InlineData("OTEL_EXPORTER_OTLP_HEADERS", SettingCategory.Secret)] // a backend's API key
    [InlineData("Telemetry:TraceDatabase", SettingCategory.Instance)]
    [InlineData("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", null)] // the environment's only: not on the page, so a backup names it, never carries it
    [InlineData("PATH", null)]
    public void ASettingsCategory(string key, SettingCategory? category) => Assert.Equal(category, SettingsInBackups.CategoryOf(key));
}
