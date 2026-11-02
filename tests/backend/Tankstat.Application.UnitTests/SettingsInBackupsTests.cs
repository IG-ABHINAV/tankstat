using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Tankstat.Application.Backups;

namespace Tankstat.Application.UnitTests;

/// <summary>A backup carries the instance's settings, never the machine's, and a plain one never a secret; a restore keeps this machine's own.</summary>
public sealed class SettingsInBackupsTests
{
    private static readonly Dictionary<string, string?> File = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Auth:Mode"] = "Standalone",
        ["Auth:AdminEmails:0"] = "a@x.co",
        ["Auth:AdminEmails:1"] = "b@x.co",
        ["Smtp:Password"] = "zzzz-secret",
        ["Defaults:Currency"] = "HUF",
        ["Logging:Console:FormatterName"] = "json", // the Logging section, beyond the five levels the page offers
        ["Storage:Path"] = "/srv/pictures", // the machine's: a folder
        ["Database:ConnectionString"] = "Host=db", // the machine's
        ["Auth:Standalone:AdminPassword"] = "bootstrap-password", // retired (#149), as an older file may still hold it: never carried
        ["Backups:Path"] = "/srv/backups",
        ["Setup:Enabled"] = "false", // the environment's only
    };

    [Fact]
    public void APlainBackup_CarriesTheInstanceSettings_WithoutTheSecrets_AndNamesWhatItLeavesOut()
    {
        var (carried, omitted) = SettingsInBackups.Copy(File, carriesSecrets: false);

        Assert.Equal(["Auth:AdminEmails:0", "Auth:AdminEmails:1", "Auth:Mode", "Defaults:Currency", "Logging:Console:FormatterName"], carried.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["Auth:Standalone:AdminPassword", "Backups:Path", "Database:ConnectionString", "Setup:Enabled", "Smtp:Password", "Storage:Path"], omitted);
        Assert.DoesNotContain("zzzz-secret", JsonSerializer.Serialize(omitted)); // names only
    }

    [Fact]
    public void AnEncryptedBackup_CarriesTheSecretsToo_NeverTheMachinesOwn()
    {
        var (carried, omitted) = SettingsInBackups.Copy(File, carriesSecrets: true);

        Assert.Equal("zzzz-secret", carried["Smtp:Password"]);
        Assert.DoesNotContain("Smtp:Password", omitted);
        Assert.Contains("Storage:Path", omitted);
        Assert.False(carried.ContainsKey("Auth:Standalone:AdminPassword"));
    }

    [Fact]
    public void ARestore_LeavesTheBackupsSettings_AndKeepsThisMachinesOwn_AndThePasswordsAPlainBackupCouldNotBring()
    {
        var live = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Auth:Mode"] = "None", // the backup's wins
            ["Defaults:Currency"] = "EUR", // the backup has none: gone with the rest of this server's file
            ["Smtp:Password"] = "this-servers-password",
            ["Storage:Path"] = "/data/uploads",
        };
        var fromBackup = new Dictionary<string, string?> { ["Auth:Mode"] = "Standalone", ["Database:ConnectionString"] = "Host=elsewhere" }; // a key no backup carries: not taken

        var restored = SettingsInBackups.Restored(fromBackup, live, carriesSecrets: false);

        Assert.Equal(new Dictionary<string, string?> { ["Auth:Mode"] = "Standalone", ["Smtp:Password"] = "this-servers-password", ["Storage:Path"] = "/data/uploads" },
            restored.OrderBy(p => p.Key).ToDictionary());
    }

    [Fact]
    public void ARestoreOfAnEncryptedBackup_TakesItsSecrets_EvenWhereItHasNone()
    {
        var live = new Dictionary<string, string?> { ["Smtp:Password"] = "this-servers-password" };

        var restored = SettingsInBackups.Restored(new Dictionary<string, string?>(), live, carriesSecrets: true);

        Assert.Empty(restored); // an encrypted backup carries the secrets it had: none here, so none stays
    }

    [Fact]
    public void TheManifest_ReadsWithAndWithoutTheSettingsMember()
    {
        var manifest = new BackupManifest(
            BackupManifest.CurrentFormat, BackupManifest.Tankstat, "1.0.0", DateTimeOffset.UnixEpoch, "Sqlite", null, false,
            new BackupContents(new Dictionary<string, long>(), true, BackupPhotos.WithPictures, false), new BackupSizes(0, 0, 0, 0, 0), new BackupArchive(BackupArchive.Plain, "ab"), null,
            new BackupSettingsFile(BackupSettingsState.Included, BackupSettingsFile.Plain, "cd", 12, ["Smtp:Password"]));
        var json = manifest.ToJson();

        Assert.Contains("\"state\": \"included\"", json);
        Assert.Equal(["Smtp:Password"], BackupManifest.FromJson(json).Settings!.Omitted);
        var older = json[..json.IndexOf(",\n  \"settings\"", StringComparison.Ordinal)] + "\n}"; // as a release before it wrote them
        Assert.Null(BackupManifest.FromJson(older).Settings);
    }

    /// <summary>The keys a server's environment holds: the instance's, the machine's, and the process's own.</summary>
    private static readonly string[] EnvironmentKeys =
    [
        "Auth:Mode", "Auth:AdminEmails:0", "Auth:Oidc:ClientSecret", "Defaults:Currency", "Logging:LogLevel:Tankstat",
        "Database:ConnectionString", "Storage:Path", "Setup:Enabled", "Auth:Standalone:AdminEmail", "Backups:Path",
        "PATH", "DOTNET_ROOT", "ASPNETCORE_URLS",
    ];

    [Fact]
    public void ABackupRecords_TheEnvironmentsSettingsAndTheMachinesOwn_ByVariableName_AListOnce_NothingElse()
    {
        var recorded = SettingsInBackups.Recorded(BackupEnvironmentMode.Record, EnvironmentKeys);

        Assert.Equal(["Auth__AdminEmails", "Auth__Mode", "Auth__Oidc__ClientSecret", "Defaults__Currency", "Logging__LogLevel__Tankstat"], recorded.Settings);
        // A retired setting (the bootstrap administrator's address, #149) is nobody's: recorded neither way.
        Assert.Equal(["Backups__Path", "Database__ConnectionString", "Setup__Enabled", "Storage__Path"], recorded.Server);
    }

    [Fact]
    public void Merged_TheCopyTakesWhatTheServerRunsWith_AListWhole_NoSecretInPlain_NeverTheMachinesOwn()
    {
        var file = new Dictionary<string, string?> { ["Auth:AdminEmails:0"] = "file-a@x.co", ["Auth:AdminEmails:1"] = "file-b@x.co", ["Defaults:Currency"] = "EUR" };
        // As the configuration runs: the variable's item 0 over the file's, the file's item 1 kept by index.
        var running = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Mode"] = "Standalone", ["Auth:AdminEmails:0"] = "env-a@x.co", ["Auth:AdminEmails:1"] = "file-b@x.co", ["Auth:Oidc:ClientSecret"] = "zzzz-secret",
            ["Defaults:Currency"] = "HUF", ["Logging:LogLevel:Tankstat"] = "Debug", ["Storage:Path"] = "/srv/pictures",
        }).Build();

        var plain = SettingsInBackups.Merged(file, EnvironmentKeys, running, carriesSecrets: false);
        var encrypted = SettingsInBackups.Merged(file, EnvironmentKeys, running, carriesSecrets: true);

        Assert.Equal(new Dictionary<string, string?>
        {
            ["Auth:AdminEmails:0"] = "env-a@x.co", ["Auth:AdminEmails:1"] = "file-b@x.co", ["Auth:Mode"] = "Standalone", ["Defaults:Currency"] = "HUF",
            ["Logging:LogLevel:Tankstat"] = "Debug",
        }, plain.OrderBy(p => p.Key).ToDictionary());
        Assert.Equal("zzzz-secret", encrypted["Auth:Oidc:ClientSecret"]);
        Assert.False(encrypted.ContainsKey("Storage:Path"));
        Assert.Equal("EUR", file["Defaults:Currency"]); // the copy, never the file it came from
    }

    [Fact]
    public void Missing_IsWhatNeitherTheRestoredFileNorThisEnvironmentHolds()
    {
        var recorded = new BackupEnvironment(BackupEnvironmentMode.Record, ["Auth__AdminEmails", "Auth__Mode", "Defaults__Currency", "Smtp__Password"], ["Storage__Path"]);
        var file = new Dictionary<string, string?> { ["Auth:AdminEmails:0"] = "a@x.co", ["Defaults:Currency"] = null };

        var missing = SettingsInBackups.Missing(recorded, file, key => key == "Auth:Mode");

        Assert.Equal(["Defaults__Currency", "Smtp__Password"], missing); // a list by its items, a null is no value; the machine's own never counts
    }

    [Fact]
    public void AManifestSaysWhetherTheSecretsWentIn_AnOlderOneByItsEncryption()
    {
        var plain = new BackupManifest(
            BackupManifest.CurrentFormat, BackupManifest.Tankstat, "1.0.0", DateTimeOffset.UnixEpoch, "Sqlite", null, false,
            new BackupContents(new Dictionary<string, long>(), true, BackupPhotos.WithPictures, false), new BackupSizes(0, 0, 0, 0, 0), new BackupArchive(BackupArchive.Plain, "ab"), null);
        var encrypted = plain with { Encryption = new BackupEncryption(BackupEncryption.AgeX25519, "age1x", "c2FsdHNhbHQ=", Argon2idParameters.Default) };

        Assert.False(plain.CarriesSecrets());
        Assert.True(encrypted.CarriesSecrets());
        Assert.True((plain with { Settings = new BackupSettingsFile(BackupSettingsState.Included, Secrets: true) }).CarriesSecrets()); // asked for in plain
        Assert.DoesNotContain("carriesSecrets", plain.ToJson(), StringComparison.OrdinalIgnoreCase); // a method, never a member of the file
    }

    [Theory]
    [InlineData("Defaults__Currency", true)]
    [InlineData("Auth__Mode", true)]
    [InlineData("Logging__LogLevel__Default", false)] // its levels only change what the log holds: a missing one is a risk, never a refusal
    [InlineData("logging__loglevel__Microsoft.AspNetCore", false)]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", false)] // a collector's address changes where the telemetry goes, never what the data means
    [InlineData("OTEL_EXPORTER_OTLP_HEADERS", false)]
    [InlineData("Telemetry__TraceDatabase", false)]
    public void Refuses_EveryMissingSetting_ButTheLoggingAndTelemetrySettings(string variable, bool refuses) =>
        Assert.Equal(refuses, SettingsInBackups.Refuses(variable));
}
