using Microsoft.Extensions.Configuration;
using Tankstat.Application.Auth;
using Tankstat.Application.SystemSettings;
using Tankstat.Application.Telemetry;
using Tankstat.Domain;

namespace Tankstat.Application.Backups;

/// <summary>
/// Which keys of the instance's settings file a backup carries, and what a restore makes of them. A backup carries the instance's settings,
/// never the machine's: the settings the Server settings page manages (<see cref="SettingsCatalog.All"/>, a list with its items) and the
/// <c>Logging</c> section; never what describes the machine it ran on (the database, the storage and key folders, the settings file's own
/// path, the backups folder, the prompt file: none of them is in the catalog), which restored onto another
/// machine would point it at a database or folders it lacks. A backup carries no secret (<see cref="SettingDefinition.IsSecret"/>) unless
/// asked to (<c>Backups:Secrets</c>).
/// </summary>
public static class SettingsInBackups
{
    /// <summary>
    /// What a setting is to a backup (null: none the app binds): the instance's, which travels; a secret, which travels only where secrets are asked for
    /// (<c>Backups:Secrets</c>); or the machine's own, which never does. Every option the
    /// app binds has one, pinned by <c>SettingCategoriesTests</c>, so a setting added later is sorted too.
    /// </summary>
    public static SettingCategory? CategoryOf(string key) =>
        IsMachineSetting(key) ? SettingCategory.Machine
        : IsInstanceSetting(key) ? (IsSecret(key) ? SettingCategory.Secret : SettingCategory.Instance)
        : null;

    private const string LoggingSection = "Logging";

    /// <summary>Whether a backup may carry <paramref name="key"/> at all (a secret only where it carries secrets, <see cref="Carries"/>).</summary>
    public static bool IsInstanceSetting(string key) =>
        Under(key, LoggingSection) || SettingsCatalog.All.Any(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || d.IsList && Under(key, d.Key));

    /// <summary>Whether a backup carries <paramref name="key"/>: a secret only where it carries secrets (<c>Backups:Secrets</c>).</summary>
    public static bool Carries(string key, bool carriesSecrets) => IsInstanceSetting(key) && (carriesSecrets || !IsSecret(key));

    private static bool IsSecret(string key) => SettingsCatalog.Find(key)?.IsSecret == true;

    /// <summary>
    /// The copy of the file a backup takes: the keys it carries, and the names of those it leaves out (a list once, by its key), sorted.
    /// Never a value of what is left out.
    /// </summary>
    public static (IReadOnlyDictionary<string, string?> Carried, IReadOnlyList<string> Omitted) Copy(IReadOnlyDictionary<string, string?> file, bool carriesSecrets)
    {
        var carried = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var omitted = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in file)
        {
            if (Carries(key, carriesSecrets)) carried[key] = value;
            else omitted.Add(ListKeyOf(key));
        }
        return (carried, [.. omitted]);
    }

    /// <summary>
    /// The settings file a restore leaves: the backup's, as it carried it, and of this server's own file what no backup of that kind would
    /// carry (the machine's own keys, and the secrets of a backup that holds none), so a restore keeps this machine pointed at
    /// its own database and folders and keeps the passwords a plain backup could not bring.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Restored(
        IReadOnlyDictionary<string, string?> fromBackup, IReadOnlyDictionary<string, string?> live, bool carriesSecrets)
    {
        var restored = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in live)
            if (!Carries(key, carriesSecrets)) restored[key] = value;
        foreach (var (key, value) in fromBackup)
            if (Carries(key, carriesSecrets)) restored[key] = value; // a key the backup should not have carried (a file edited by hand) is not taken
        return restored;
    }

    /// <summary>The sections that describe the machine a server runs on, never the instance: a backup never carries them, it only names them.</summary>
    private static readonly string[] MachineSections = ["Database", "Storage", "DataProtection", "Settings", "Setup"];

    /// <summary>Whether <paramref name="key"/> is the machine's own (a folder, the database), which only a manifest names.</summary>
    public static bool IsMachineSetting(string key) =>
        SettingsCatalog.LeftOut.Contains(key, StringComparer.OrdinalIgnoreCase)
        || MachineSections.Any(section => key.Equals(section, StringComparison.OrdinalIgnoreCase) || Under(key, section));

    /// <summary>A configuration key as an environment variable names it (<c>Auth:Mode</c> is <c>Auth__Mode</c>).</summary>
    public static string VariableOf(string key) => key.Replace(":", "__", StringComparison.Ordinal);

    /// <summary>The configuration key an environment variable's name stands for.</summary>
    public static string KeyOf(string variable) => variable.Replace("__", ":", StringComparison.Ordinal);

    /// <summary>
    /// What a backup records of the environment the server runs with (<paramref name="environmentKeys"/>: the keys a variable or the command
    /// line holds): the instance's settings and the machine's own, by variable name, a list once, sorted; anything else (<c>PATH</c>, the
    /// runtime's own) not at all.
    /// </summary>
    public static BackupEnvironment Recorded(BackupEnvironmentMode mode, IEnumerable<string> environmentKeys)
    {
        var keys = environmentKeys.ToList();
        IReadOnlyList<string> Names(Func<string, bool> which) =>
            [.. keys.Where(which).Select(ListKeyOf).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(VariableOf)];
        return new BackupEnvironment(mode, Names(IsInstanceSetting), Names(IsMachineSetting));
    }

    /// <summary>
    /// The backup's copy of the file with the environment's settings merged in (<see cref="BackupEnvironmentMode.Merge"/>): each setting a
    /// variable holds that this kind of backup carries, at the value the server runs with (<paramref name="running"/>), a list whole (the
    /// configuration merges a variable's items over the file's by index, so the file's own items of it go). Never a machine's own key, and a
    /// secret only where the backup carries secrets.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Merged(
        IReadOnlyDictionary<string, string?> file, IEnumerable<string> environmentKeys, IConfiguration running, bool carriesSecrets)
    {
        var merged = new Dictionary<string, string?>(file, StringComparer.OrdinalIgnoreCase);
        foreach (var setting in environmentKeys.Where(k => Carries(k, carriesSecrets)).Select(ListKeyOf).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (SettingsCatalog.Find(setting)?.IsList == true)
            {
                foreach (var item in merged.Keys.Where(k => Under(k, setting)).ToList()) merged.Remove(item);
                foreach (var item in running.GetSection(setting).GetChildren()) merged[item.Path] = item.Value;
            }
            else merged[setting] = running[setting];
        }
        return merged;
    }

    /// <summary>
    /// The settings a backup recorded (by variable name) that a restore would leave without a value: neither the settings file it leaves
    /// (<paramref name="file"/>) holds one, nor this server's environment (<paramref name="fromEnvironment"/>). Such a restore is refused (D4)
    /// for those that <see cref="Refuses"/>: set them first.
    /// </summary>
    public static IReadOnlyList<string> Missing(BackupEnvironment recorded, IReadOnlyDictionary<string, string?> file, Func<string, bool> fromEnvironment) =>
    [
        .. recorded.Settings.Where(variable =>
        {
            var key = KeyOf(variable);
            return !file.Any(p => p.Value is not null && (p.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || Under(p.Key, key))) && !fromEnvironment(key);
        }),
    ];

    /// <summary>
    /// Whether a recorded setting (by variable name) left without a value here refuses the restore: every one but the <c>Logging</c> section's
    /// (user decision), whose levels only change what the log holds, and which a compose file often sets (<c>Logging__LogLevel__Default</c>):
    /// it would otherwise refuse every restore elsewhere. The telemetry settings (the <c>OTEL_*</c> keys and <c>Telemetry:*</c>) are the same
    /// kind: a collector's address in a compose file changes where the telemetry goes, never what the data means, and a move to a machine
    /// without the collector must not be refused for it.
    /// </summary>
    public static bool Refuses(string variable)
    {
        var key = KeyOf(variable);
        return !Under(key, LoggingSection) && !Under(key, TelemetryOptions.SectionName) && SettingsCatalog.Find(key)?.Group != SettingsGroup.Telemetry;
    }

    /// <summary>A list item's list (<c>Auth:AdminEmails:0</c> is <c>Auth:AdminEmails</c>); any other key as it is.</summary>
    public static string ListKeyOf(string key) =>
        SettingsCatalog.All.FirstOrDefault(d => d.IsList && Under(key, d.Key))?.Key ?? key;

    private static bool Under(string key, string section) =>
        key.Length > section.Length && key[section.Length] == ':' && key.StartsWith(section, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A copy of the settings file for a backup: whether there is one (<see cref="State"/>), the keys it carries, the names it leaves out, and what
/// it records of the environment (null where nothing is known of it: a host without the Api).
/// </summary>
public sealed record SettingsCopy(BackupSettingsState State, IReadOnlyDictionary<string, string?> Values, IReadOnlyList<string> Omitted, BackupEnvironment? Environment = null)
{
    public static readonly SettingsCopy None = new(BackupSettingsState.None, new Dictionary<string, string?>(), []);
}

/// <summary>
/// The settings file a restore would leave (<see cref="SettingsInBackups.Restored"/>), and what this server's next start would make of it
/// under its own environment: the problems it would refuse or leave the file out for (names only), and the sign-in it would run.
/// </summary>
public sealed record RestoredSettings(IReadOnlyDictionary<string, string?> Values, IReadOnlyList<string> Problems, AuthOptions Auth);

/// <summary>
/// The instance's settings file as backups carry it (the app's own <c>Settings:Path</c>): the copy a backup takes, what a restore of one
/// would leave and whether this server would start with it, and writing it back. The Api implements it over the running configuration and
/// the file; a host without them (a test of the backups alone) has no file.
/// </summary>
public interface ISettingsBackup
{
    /// <summary>
    /// The copy a backup takes now: the file on disk (what the next start reads), unless the start left it out; with
    /// <see cref="BackupEnvironmentMode.Merge"/> the environment's settings merged into it; and what the backup records of the environment.
    /// </summary>
    Task<SettingsCopy> CopyAsync(bool carriesSecrets, BackupEnvironmentMode mode, CancellationToken ct);

    /// <summary>What a restore of <paramref name="fromBackup"/> (a backup's carried keys) would leave, judged under this server's environment.</summary>
    Task<RestoredSettings> RestoredAsync(IReadOnlyDictionary<string, string?> fromBackup, bool carriesSecrets, CancellationToken ct);

    /// <summary>
    /// The settings file a restore of <paramref name="fromBackup"/> would leave, not judged (<see cref="SettingsInBackups.Restored"/>): the
    /// backup's carried keys over what this server's own file keeps (its machine keys, and from a backup without secrets its secrets).
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> LeavesAsync(IReadOnlyDictionary<string, string?> fromBackup, bool carriesSecrets, CancellationToken ct);

    /// <summary>Writes the restored file (the previous one kept as <c>.bak</c>).</summary>
    Task WriteAsync(IReadOnlyDictionary<string, string?> values, CancellationToken ct);

    /// <summary>
    /// The recorded settings a restore would leave without a value here (<see cref="SettingsInBackups.Missing"/>): judged against the file it
    /// leaves (<paramref name="restored"/>; null for a backup without one, which leaves this server's own) and this server's environment.
    /// </summary>
    Task<IReadOnlyList<string>> MissingAsync(BackupEnvironment recorded, IReadOnlyDictionary<string, string?>? restored, CancellationToken ct);

    /// <summary>
    /// The settings of a backup's file that this server's environment overrides, by variable name, a list once: a restore leaves them in the
    /// file, but the environment's values run (the sign-in mode among them, which can leave the restored users without a way in).
    /// </summary>
    IReadOnlyList<string> Overridden(IReadOnlyDictionary<string, string?> fromBackup);
}

/// <summary>What a setting is to a backup (<see cref="SettingsInBackups.CategoryOf"/>).</summary>
public enum SettingCategory { Instance, Secret, Machine }

public static class RestoredSettingsExtensions
{
    /// <summary>
    /// Refuses what a restore would leave (<see cref="ISettingsBackup.RestoredAsync"/>) as <c>backup.settingsRefused</c> (naming the settings at
    /// fault, never a value) when this server would not start with it: the one judgement the request and the start make alike.
    /// </summary>
    public static void RequireRestorable(this RestoredSettings restored)
    {
        if (restored.Problems.Count > 0)
            throw new DomainException("backup.settingsRefused",
                $"The server would not start with the settings file this backup brings ({string.Join(" ", restored.Problems)}), so it is not restored.",
                new { restored.Problems });
    }
}
