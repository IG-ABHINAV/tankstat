using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Tankstat.Api.GraphQL;
using Tankstat.Application.Backups;
using Tankstat.Application.SystemSettings;
using Tankstat.Domain;
using Tankstat.Domain.Backups;
using Tankstat.Domain.Users;

namespace Tankstat.Api.Backups;

/// <summary>
/// The backup commands of <c>dotnet Tankstat.Api.dll</c> (no server is started; every service exists, no hosted service runs). What they do
/// goes through the Application (#313: <see cref="BackupCommandService"/>, the restore request's own services and ports), as the Backups
/// page's does; here are the arguments, the prompts and the lines they print:
/// <c>backup keygen --passphrase-stdin</c> prints the two settings that make backups encrypted and the identity, once;
/// <c>backup [--output &lt;file&gt;] [--photos] [--passphrase-stdin]</c> writes one; <c>decrypt &lt;file&gt; --output &lt;folder&gt;
/// (--identity &lt;file&gt; | --passphrase-stdin)</c> unpacks one. A passphrase is only ever read from the standard input, never from an
/// argument (which the process list and the shell history would keep): typed without an echo at a terminal, a line each when piped
/// (<see cref="IPassphrasePrompt"/>); a new one (keygen, backup) twice, an existing one (decrypt, restore) once. Exit code 0, or 1 with the reason on the standard error.
/// </summary>
internal static class BackupCommands
{
    public static bool Handles(string[] args) => args is ["backup", ..] or ["decrypt", ..] or ["restore", ..];

    /// <param name="prompt">Where a passphrase is read: the terminal without an echo, else (and in tests) a line of <paramref name="input"/> each.</param>
    public static async Task<int> RunAsync(
        string[] args, IServiceProvider services, TextReader input, TextWriter output, TextWriter error, IPassphrasePrompt? prompt = null)
    {
        prompt ??= new LinePrompt(input);
        try
        {
            return args switch
            {
                ["backup", "keygen", .. var rest] => Keygen(rest, services, prompt, output),
                ["backup", .. var rest] => await BackupAsync(rest, services, prompt, output, error),
                ["decrypt", .. var rest] => Decrypt(rest, services, prompt, output),
                ["restore", .. var rest] => await RestoreAsync(rest, services, input, prompt, output),
                _ => throw new UsageException(),
            };
        }
        catch (UsageException)
        {
            await error.WriteLineAsync(Usage);
            return 1;
        }
        // What a run can meet is reported, never left to crash the process: settings the host never validated (a command does not start it,
        // so they are validated when first read), a recipient no key agrees with, a backup that is not in the folder, a platform without
        // ChaCha20-Poly1305 (Windows 10), a value the dump cannot hold.
        catch (Exception e) when (e is KeyedException or IOException or UnauthorizedAccessException or FormatException or System.Data.Common.DbException
            or OptionsValidationException or InvalidDataException or CryptographicException or NotSupportedException)
        {
            await error.WriteLineAsync(e.Message); // a database's message may quote the table it misses, as its own errors always did
            return 1;
        }
    }

    private const string Usage = """
        Usage:
          backup keygen --passphrase-stdin                       derive the recipient and the salt from a passphrase, typed twice (at a terminal
                                                                 without an echo, or two lines on the standard input)
          backup [--output <file>] [--photos] [--environment record|merge] [--secrets] [--passphrase-stdin]
                                                                 write a backup (encrypted for Backups:Encryption:Recipient, or for a new passphrase typed twice)
          decrypt <file> --output <folder> (--identity <file> | --passphrase-stdin)
                                                                 unpack a backup into a folder
          restore <file> [--yes] [--now] [--identity <file> | --passphrase-stdin]
                                                                 replace ALL data with a backup of the backups folder (by file name) at the next start of the
                                                                 server, or right away with --now while the server is NOT running; --yes skips the question
                                                                 (asked on the standard input, before the passphrase line)
        """;

    private static int Keygen(string[] args, IServiceProvider services, IPassphrasePrompt prompt, TextWriter output)
    {
        if (args is not ["--passphrase-stdin"]) throw new UsageException();
        var key = services.GetRequiredService<BackupCommandService>().Keygen(NewPassphrase(services, prompt));
        output.WriteLine("Set these two settings (as environment variables or in the configuration), then every backup is encrypted for this passphrase:");
        output.WriteLine($"  Backups__Encryption__Recipient={key.Recipient}");
        output.WriteLine($"  Backups__Encryption__Salt={key.Salt}");
        output.WriteLine("The identity below opens every backup made for this recipient, with age (age -d -i) or tankstat decrypt --identity; it is derived from the");
        output.WriteLine("passphrase and the salt, so the passphrase alone is enough to keep. Losing the passphrase (or this identity) loses the backups, by design.");
        output.WriteLine("Saved as it is, the next line is an identity file (age takes no space before the key):");
        output.WriteLine(key.Identity);
        return 0;
    }

    private static async Task<int> BackupAsync(string[] args, IServiceProvider services, IPassphrasePrompt prompt, TextWriter output, TextWriter error)
    {
        string? file = null;
        var photos = false;
        var passphrase = false;
        BackupEnvironmentMode? environment = null;
        bool? secrets = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output" when i + 1 < args.Length && args[i + 1].Length > 0: file = args[++i]; break;
                case "--photos": photos = true; break;
                case "--secrets": secrets = true; break;
                case "--environment" when i + 1 < args.Length && Enum.TryParse<BackupEnvironmentMode>(args[i + 1], ignoreCase: true, out var mode) && Enum.IsDefined(mode):
                    environment = mode;
                    i++;
                    break;
                case "--passphrase-stdin": passphrase = true; break;
                default: throw new UsageException();
            }
        }
        // The server says this at start (SettingsFileReporter, a hosted service, which a command never runs). A backup runs without the file
        // too, so it could come out without what the file held, its encryption say: never without a word.
        if (services.GetService<SettingsFileStatus>() is { Ignored: true } settingsFile)
            await error.WriteLineAsync($"The settings file {settingsFile.Path} was left out because the app could not start with it; the backup is written without it: {string.Join(" ", settingsFile.Problems)}");
        var newPassphrase = passphrase ? NewPassphrase(services, prompt) : null;
        var result = await services.GetRequiredService<BackupCommandService>().WriteAsync(
            new BackupRequest(file, photos ? true : null, Environment: environment, Secrets: secrets), newPassphrase, CancellationToken.None);
        var m = result.Manifest;
        output.WriteLine($"Wrote {result.Path}");
        output.WriteLine($"  {m.Contents.Tables.Count} tables with {m.Contents.Tables.Values.Sum()} rows ({m.Sizes.Tables} bytes), pictures {m.Sizes.Pictures} bytes, photos {Photos(m)}, keys {(m.Contents.Keys ? $"{m.Sizes.Keys} bytes" : m.Encryption is null ? "left out (not encrypted)" : "none found")}");
        output.WriteLine(m.Encryption is null ? "  not encrypted" : $"  encrypted for {m.Encryption.Recipient}");
        output.WriteLine($"  settings file {SettingsOf(m)}");
        if (m.Environment is { } recorded)
            output.WriteLine($"  environment of this process {(recorded.Mode == BackupEnvironmentMode.Merge && m.Settings?.State == BackupSettingsState.Included ? "merged into the settings file" : "recorded")}: {(recorded.Settings.Count == 0 ? "no setting" : string.Join(", ", recorded.Settings))}");
        return 0;
    }

    /// <summary>What a backup holds of the settings file, in words; names only, never a value.</summary>
    private static string SettingsOf(BackupManifest m) => m.Settings switch
    {
        // Its secrets as CarriesSecrets says: an older encrypted backup's manifest does not say, and it carried them.
        { State: BackupSettingsState.Included } s =>
            $"{s.Bytes} bytes"
            + (!m.CarriesSecrets() ? "" : m.Encryption is not null ? ", with its secrets (encrypted)" : ", its secrets IN PLAIN (anyone with the file reads them)")
            + (s.Omitted is { Count: > 0 } omitted ? $", without {string.Join(", ", omitted)}" : ""),
        { State: BackupSettingsState.Ignored } => "left out (the server runs without it)",
        { State: BackupSettingsState.None } => "none (the server has none)",
        _ => "not in the backup (written by an older release)",
    };

    private static string Photos(BackupManifest m) => m.Contents.Photos switch
    {
        BackupPhotos.WithPictures => "with the pictures",
        BackupPhotos.Separate => $"{m.Sizes.Photos} bytes",
        _ => "left out (Backups:Photos is off)",
    };

    private static int Decrypt(string[] args, IServiceProvider services, IPassphrasePrompt prompt, TextWriter output)
    {
        if (args.Length == 0 || args[0].StartsWith("--")) throw new UsageException();
        var file = args[0];
        string? folder = null, identityFile = null;
        var passphrase = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output" when i + 1 < args.Length: folder = args[++i]; break;
                case "--identity" when i + 1 < args.Length: identityFile = args[++i]; break;
                case "--passphrase-stdin": passphrase = true; break;
                default: throw new UsageException();
            }
        }
        if (folder is null || (identityFile is not null && passphrase)) throw new UsageException();
        var commands = services.GetRequiredService<BackupCommandService>();
        var manifest = commands.Decrypt(file, folder, m =>
            identityFile is not null ? AgeKeys.ParseIdentity(IdentityLine(identityFile))
            : passphrase ? commands.IdentityOf(m, prompt.Read("Passphrase:"))
            : null);
        output.WriteLine($"Unpacked {file} into {System.IO.Path.GetFullPath(folder)}: {manifest.Contents.Tables.Count} tables, written by Tankstat {manifest.AppVersion} on {manifest.CreatedAt:u} ({manifest.Provider}, {manifest.Migration}), settings file {SettingsOf(manifest)}");
        return 0;
    }

    private static async Task<int> RestoreAsync(string[] args, IServiceProvider services, TextReader input, IPassphrasePrompt prompt, TextWriter output)
    {
        if (args.Length == 0 || args[0].StartsWith("--")) throw new UsageException();
        var file = args[0];
        string? identityFile = null;
        var (passphrase, yes, now) = (false, false, false);
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--identity" when i + 1 < args.Length: identityFile = args[++i]; break;
                case "--passphrase-stdin": passphrase = true; break;
                case "--yes": yes = true; break;
                case "--now": now = true; break;
                default: throw new UsageException();
            }
        }
        if (identityFile is not null && passphrase) throw new UsageException();
        var store = services.GetRequiredService<IBackupStore>();
        var manifest = store.ReadManifest(file); // a file of the backups folder, by name
        // A newer release's backup is refused before anything is written, as the app refuses it: the start would refuse it anyway.
        await store.RequireRestorableMigrationAsync(manifest, CancellationToken.None);
        // What it brings of its server's settings, and what this server lacks of them (the restore is refused for that, below).
        output.WriteLine($"The backup's settings file: {SettingsOf(manifest)}.");
        var here = await RestoreService.HereAsync(store, services.GetRequiredService<ISettingsBackup>(), new RestorePoint(file, 0, manifest), CancellationToken.None);
        if (manifest.Environment is { Settings.Count: > 0 } recorded)
        {
            output.WriteLine($"Its server's environment set {string.Join(", ", recorded.Settings)} ({(recorded.Mode == BackupEnvironmentMode.Merge && manifest.Settings is { State: BackupSettingsState.Included } ? "merged into its settings file" : "recorded by name only")}).");
            if (here.Missing.Count > 0)
                output.WriteLine($"This server sets none of {string.Join(", ", here.Missing)}: the restore is refused until they are set{(here.MissingCertain ? "" : " (unless the backup's encrypted settings file holds them)")}.");
            if (here.LogLevelsMissing.Count > 0)
                output.WriteLine($"RISK: this server sets none of the log levels {string.Join(", ", here.LogLevelsMissing)}: the log holds what this server's levels say.");
        }
        else if (manifest.Environment is null) output.WriteLine("RISK: it says nothing of its server's environment (an older release wrote it): settings its server set there may be missing here.");
        if (manifest.Settings is not { State: BackupSettingsState.Included })
            output.WriteLine("RISK: it brings no settings file, so this server's settings stay, which may not suit the restored data (its sign-in, its e-mail, its backups).");
        else if (BackupSettingsFileInfo.From(manifest.Settings, manifest.CarriesSecrets()) is { Secrets: false, SecretsLeftOut: { Count: > 0 } secrets })
            output.WriteLine($"RISK: its secrets are not in it ({string.Join(", ", secrets)}): this server's own are kept, and what this server has none of stops working.");
        if (here.Overridden.Count > 0)
            output.WriteLine($"RISK: this server's environment overrides {string.Join(", ", here.Overridden)}, which the backup brings: this server's values run{(here.Overridden.Contains("Auth__Mode") ? ", the sign-in mode among them, so the restored users may have no way to sign in" : "")}.");
        if (!yes)
        {
            output.WriteLine($"This replaces ALL data of this instance (every table, the pictures{(manifest.Contents.Photos == BackupPhotos.Separate ? ", the photos" : "")}{(manifest.Contents.Keys ? ", the keys" : "")}) with the backup {file},");
            output.WriteLine($"written by Tankstat {manifest.AppVersion} on {manifest.CreatedAt:u} ({manifest.Provider}, {manifest.Migration}). No backup of the current data is written: whatever changed since it was made is lost. Type yes to continue:");
            if (input.ReadLine()?.Trim().ToLowerInvariant() != "yes")
            {
                output.WriteLine("Nothing was changed.");
                return 1;
            }
        }
        var identity = identityFile is not null ? IdentityLine(identityFile) : null;
        var opened = store.IdentityFor(manifest, passphrase ? prompt.Read("Passphrase:") : null, identity); // refused here when it does not open
        // The settings the restore leaves must let the server start, as the app refuses it before a restart: refused before the marker.
        await RestoreService.RequireStartableAsync(store, services.GetRequiredService<ISettingsBackup>(), services.GetRequiredService<ISystemConfiguration>(),
            file, manifest, opened, CancellationToken.None);
        store.RequestRestore(new RestoreMarker(file, Guid.Empty, DateTimeOffset.UtcNow, opened));
        if (!now)
        {
            output.WriteLine($"The restore of {file} is applied at the next start of the server. Restart it now; a running server shows a notice until then.");
            return 0;
        }
        // Right away, as the server would at its start: the restore, then the migrations; the log above says how it went, and why.
        switch (await services.GetRequiredService<BackupCommandService>().RestoreNowAsync(CancellationToken.None))
        {
            case RestoreOutcome.Applied:
                output.WriteLine($"Restored {file}.");
                return 0;
            case RestoreOutcome.Failed:
                output.WriteLine("The restore failed after the data was replaced, so the files may be half copied; no backup of the data from before it was written, the backup itself is still in the folder to try again, and the log above says why.");
                return 1;
            default:
                output.WriteLine("The restore was not applied, and the data is as it was; the log above says why.");
                return 1;
        }
    }

    /// <summary>The identity in an identity file: its first line that is neither blank nor a comment, as age reads one.</summary>
    private static string IdentityLine(string path) =>
        File.ReadLines(path).FirstOrDefault(l => !l.StartsWith('#') && l.Trim().Length > 0) ?? throw new FormatException("The identity file holds no identity.");

    /// <summary>
    /// A new passphrase (<c>backup keygen</c>, <c>backup --passphrase-stdin</c>): typed twice, a different repetition stopping the command before
    /// any key is derived, and at least <c>Backups:PassphraseMinLength</c> characters, as on the Backups page. Only spaces is refused: the page
    /// makes one up for it, and nothing here can. One that opens a backup is asked once and taken as typed (an older keygen took spaces too).
    /// </summary>
    private static string NewPassphrase(IServiceProvider services, IPassphrasePrompt prompt)
    {
        var passphrase = prompt.Read("Passphrase:");
        PasswordPolicy.RequireRepeated(passphrase, prompt.Read("Repeat it:"));
        if (string.IsNullOrWhiteSpace(passphrase)) throw new DomainException("backup.passphraseRequired", "A passphrase of only spaces is no passphrase.");
        BackupPassphrase.Validate(passphrase, PassphraseMinLength(services));
        return passphrase;
    }

    /// <summary>
    /// <c>Backups:PassphraseMinLength</c> on its own, never through the whole Backups section: keygen needs nothing else of it, and it is what
    /// makes the pair a half-set Recipient or Salt asks for, so settings that do not validate must not stop it (nor a value the binder cannot
    /// read crash it). Not set is the floor; one the start would refuse is refused here too, in the validator's words.
    /// </summary>
    private static int PassphraseMinLength(IServiceProvider services)
    {
        var value = services.GetRequiredService<IConfiguration>()[$"{BackupOptions.SectionName}:{nameof(BackupOptions.PassphraseMinLength)}"];
        if (string.IsNullOrWhiteSpace(value)) return BackupPassphrase.Floor;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var min) && min is >= BackupPassphrase.Floor and <= BackupPassphrase.Ceiling
            ? min
            : throw new FormatException($"Backups:PassphraseMinLength must be from {BackupPassphrase.Floor} to {BackupPassphrase.Ceiling}.");
    }

    private sealed class UsageException : Exception;
}
