using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tankstat.Application.Auth;
using Tankstat.Application.Backups;
using Tankstat.Application.Telemetry;
using Tankstat.Application.Health;
using Tankstat.Application.Images;
using Tankstat.Domain;
using Tankstat.Domain.Backups;
using Tankstat.Infrastructure.Persistence;
using Tankstat.Infrastructure.Settings;

namespace Tankstat.Infrastructure.Backups;

/// <summary>
/// Writes a backup: an outer zip (stored) holding the inner archive, <c>data.age</c> (encrypted for the recipient) or <c>data.zip</c>
/// (plain), and after it <c>manifest.json</c> in plain, which says what the file holds (sizes, row counts and the checksum are only known
/// once the inner archive is written, so the manifest comes last and is found by name). The inner zip holds <c>tables/&lt;Table&gt;.jsonl</c>
/// from one consistent read (<see cref="BackupSource"/>, ended before the files are copied), <c>files/pictures/...</c> (everything under
/// <c>Storage:Path</c> but the drafts), <c>files/photos/...</c> (a photos root of its own, only when asked for) and, in an encrypted backup
/// only, <c>keys/*.xml</c> (the Data Protection keys, which are secrets in plain). Beside it, the instance's settings file as it is on disk
/// when the run starts (<see cref="ISettingsBackup"/>: the instance's settings only, the secrets only with Backups:Secrets), as
/// <c>settings.age</c> (for the same recipient) or <c>settings.json</c>, an entry of its own with its checksum in the manifest, so a restore
/// request reads it without the data. Everything is streamed; the file is written under a
/// temporary name this run creates new (so two runs never share one, and a run only removes its own), and renamed once it is complete and
/// on disk, so the folder never holds a half backup (and never the database's plain copy, see <see cref="BackupSource"/>).
/// </summary>
internal sealed class BackupWriter(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<DatabaseOptions> database,
    IOptionsMonitor<BackupOptions> options,
    IOptions<StorageOptions> storage,
    IOptions<KeyRingOptions> keyRing,
    ISettingsBackup settingsFile,
    TimeProvider clock,
    ILogger<BackupWriter> logger) : IBackupWriter
{
    private const string Partial = ".partial";

    /// <summary>The table whose rows are the pictures and photos, and the files below the storage roots.</summary>
    private const string ImagesTable = "Images";

    /// <summary>The backups folder as the app started with it, the store's (see <see cref="FileBackupStore"/>); the other settings are read for every run.</summary>
    private readonly string _folder = options.CurrentValue.Root;

    public async Task<BackupResult> WriteAsync(BackupRequest request, CancellationToken ct)
    {
        // A child of what asked for it (#219): a run's span, the migrations' at start; nothing listens to the backup command.
        using var activity = AppTraces.Source.StartActivity("tankstat.backup.write");
        activity?.SetTag("tankstat.backup.safety_net", request.SafetyNet);
        try
        {
            var result = await WriteFileAsync(request, ct);
            activity?.SetTag("tankstat.backup.encrypted", result.Manifest.Encryption is not null);
            activity?.SetTag("tankstat.backup.photos", result.Manifest.Contents.Photos.ToString());
            activity?.SetTag("tankstat.backup.tables", result.Manifest.Contents.Tables.Count);
            activity?.SetTag("tankstat.backup.rows", result.Manifest.Contents.Tables.Values.Sum());
            activity?.SetTag("tankstat.backup.bytes", new FileInfo(result.Path).Length);
            return result;
        }
        catch (Exception e) when (activity.FailedBy(e))
        {
            throw; // never reached: the filter only marks the span
        }
    }

    private async Task<BackupResult> WriteFileAsync(BackupRequest request, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var roots = storage.Value;
        var createdAt = clock.GetUtcNow();
        var encryption = request.Encryption ?? Configured(o);
        var photos = roots.OneRoot ? BackupPhotos.WithPictures : (request.Photos ?? o.Photos) ? BackupPhotos.Separate : BackupPhotos.LeftOut;
        var named = request.Output is null ? null : Path.GetFullPath(request.Output);
        var folder = named is null ? _folder : Path.GetDirectoryName(named)!;
        Directory.CreateDirectory(folder);
        // A named file is never overwritten, and one that is there already is refused before any of the work.
        if (named is not null && File.Exists(named)) throw new IOException($"The file '{named}' already exists.");
        var (output, file) = named is null
            ? Claim(folder, string.Create(CultureInfo.InvariantCulture, $"{BackupRetention.OwnPrefix}{createdAt:yyyyMMdd'T'HHmmss'Z'}")) // the name retention knows as the app's own
            : (named, CreatePartial(named));
        var partial = output + Partial;
        try
        {
            // Secrets go into a backup only when asked for (Backups:Secrets), encrypted or not (user decision).
            var carriesSecrets = request.Secrets ?? o.Secrets;
            BackupManifest manifest;
            await using (file)
            {
                // The settings as they are when the run starts, before the database is read: what the next start would read.
                var settings = await settingsFile.CopyAsync(carriesSecrets, request.Environment ?? o.Environment, ct);
                manifest = await WriteArchiveAsync(file, roots, createdAt, encryption, photos, request.SafetyNet, settings, carriesSecrets, ct);
            }
            File.Move(partial, output);
            logger.LogInformation("Wrote backup {File}: {Tables} tables with {Rows} rows, {Bytes} bytes of files, {Encryption}",
                Path.GetFileName(output), manifest.Contents.Tables.Count, manifest.Contents.Tables.Values.Sum(), manifest.Sizes.Pictures + manifest.Sizes.Photos,
                encryption is null ? "in plain" : "encrypted");
            return new BackupResult(output, manifest);
        }
        catch
        {
            File.Delete(partial); // this run created it (new), so it is never one another run is writing
            throw;
        }
    }

    private async Task<BackupManifest> WriteArchiveAsync(
        FileStream file, StorageOptions roots, DateTimeOffset createdAt, BackupEncryption? encryption, BackupPhotos photos, bool safetyNet, SettingsCopy settings,
        bool carriesSecrets, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        var migration = applied.LastOrDefault();
        var known = db.Database.GetMigrations().ToList();
        // A database that a newer release migrated holds tables and columns this release's model does not know: the dump would leave them
        // out without a word, under a manifest that names the newer migration.
        if (migration is not null && !known.Contains(migration))
            throw new DomainException("backup.databaseNewer", "The database was migrated by a newer release of Tankstat; back it up with that release.", new { migration });
        // A database with any migration of this release still to run (the command line never migrates first, nor does the start's backup
        // before the migrations) is dumped as its last applied migration left it, so the backup holds what is there and names the migration
        // it was made at; a restore brings it up to date. Today's model only when nothing is pending: a migration merged in after a later one
        // was released is pending below the last applied one, and today's model would ask for its columns, which do not exist yet.
        var tables = TableDump.Tables(migration is null || !known.Except(applied).Any() ? db.Model : MigrationModels.ModelOf(db, migration));
        var sql = db.GetService<ISqlGenerationHelper>();

        var rows = new Dictionary<string, long>();
        long tableBytes = 0, pictureBytes = 0, photoBytes = 0, keyBytes = 0;
        var keyFiles = 0;
        var archiveName = encryption is null ? BackupArchive.Plain : BackupArchive.Encrypted;
        BackupManifest manifest;
        using (var outer = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            var hashing = new HashingStream(outer.CreateEntry(archiveName, CompressionLevel.NoCompression).Open());
            var data = encryption is null ? hashing : AgeStream.Encrypt(hashing, AgeKeys.ParseRecipient(encryption.Recipient));
            using (var inner = new ZipArchive(data, ZipArchiveMode.Create, leaveOpen: false))
            {
                // The one consistent read of the database ends before the files are copied (they are not part of it): writers that SQL
                // Server's SERIALIZABLE read holds up wait for the tables only, a server's snapshot never sits idle through the copy (where an
                // idle-in-transaction timeout would end it), and SQLite's plain copy is gone before the first picture.
                await using (var source = await BackupSource.OpenAsync(db, database.Value.Provider, logger, ct))
                {
                    foreach (var table in tables)
                    {
                        await using var entry = inner.CreateEntry($"tables/{table.Name}.jsonl", CompressionLevel.Optimal).Open();
                        var (count, bytes) = await TableDump.WriteAsync(source, sql, table, entry, ct);
                        rows[table.Name] = count;
                        tableBytes += bytes;
                    }
                }
                if (rows.GetValueOrDefault(ImagesTable) > 0 && !Directory.Exists(roots.PicturesRoot) && !Directory.Exists(roots.PhotosRoot))
                    logger.LogWarning("The database lists {Images} pictures and photos, but neither storage folder exists here, so none of their files is in the backup; is the command run with the server's working directory and settings?",
                        rows[ImagesTable]);
                pictureBytes = await AddFilesAsync(inner, "files/pictures", roots.PicturesRoot, ct);
                if (photos == BackupPhotos.Separate) photoBytes = await AddFilesAsync(inner, "files/photos", roots.PhotosRoot, ct);
                if (encryption is not null) (keyFiles, keyBytes) = await AddKeysAsync(inner, ct);
            }
            var settingsEntry = await AddSettingsAsync(outer, settings, encryption, carriesSecrets, ct);
            manifest = new BackupManifest(
                BackupManifest.CurrentFormat, BackupManifest.Tankstat, HealthReporter.AppVersion, createdAt, database.Value.Provider.ToString(), migration, safetyNet,
                new BackupContents(rows, Pictures: true, photos, Keys: keyFiles > 0),
                new BackupSizes(tableBytes, pictureBytes, photoBytes, keyBytes, hashing.Written),
                new BackupArchive(archiveName, hashing.Sha256Hex),
                encryption,
                settingsEntry,
                settings.Environment);
            await using (var entry = outer.CreateEntry(BackupManifest.FileName, CompressionLevel.NoCompression).Open())
                await entry.WriteAsync(Encoding.UTF8.GetBytes(manifest.ToJson()), ct);
        }
        // On disk before the rename publishes it: after a power cut the final name never holds a file cut short.
        file.Flush(flushToDisk: true);
        return manifest;
    }

    /// <summary>
    /// Adds the files of a root below a folder of the archive; returns their bytes. A file gone meanwhile (a purged vehicle) is left out, and
    /// so is a folder the app cannot read (a disk's lost+found holds nothing the app wrote), with a Warning.
    /// </summary>
    private async Task<long> AddFilesAsync(ZipArchive archive, string prefix, string root, CancellationToken ct)
    {
        long bytes = 0;
        foreach (var (relative, full) in BackupFiles.Walk(root, e => logger.LogWarning(e, "A folder below a storage root cannot be read and is not in the backup")))
        {
            FileStream input;
            try
            {
                input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                logger.LogDebug("A file disappeared while the backup was written and is not in it");
                continue;
            }
            await using (input)
            await using (var entry = archive.CreateEntry($"{prefix}/{relative}", CompressionLevel.NoCompression).Open())
            {
                await input.CopyToAsync(entry, ct);
                bytes += input.Length;
            }
        }
        return bytes;
    }

    /// <summary>
    /// The settings file as an outer entry of its own (encrypted for the backup's recipient in an encrypted backup), and what the manifest
    /// says of it: its entry and checksum as stored, and the names of the keys left out; only the state when no file went in.
    /// </summary>
    private static async Task<BackupSettingsFile> AddSettingsAsync(ZipArchive outer, SettingsCopy settings, BackupEncryption? encryption, bool carriesSecrets, CancellationToken ct)
    {
        if (settings.State != BackupSettingsState.Included) return new BackupSettingsFile(settings.State);
        var name = encryption is null ? BackupSettingsFile.Plain : BackupSettingsFile.Encrypted;
        var json = Encoding.UTF8.GetBytes(SettingsFileStore.ToJson(settings.Values));
        var hashing = new HashingStream(outer.CreateEntry(name, CompressionLevel.NoCompression).Open());
        await using (var entry = encryption is null ? hashing : AgeStream.Encrypt(hashing, AgeKeys.ParseRecipient(encryption.Recipient)))
            await entry.WriteAsync(json, ct);
        return new BackupSettingsFile(BackupSettingsState.Included, name, hashing.Sha256Hex, hashing.Written, settings.Omitted.Count > 0 ? settings.Omitted : null, carriesSecrets);
    }

    /// <summary>The Data Protection key files, directly in the keys folder; how many there were and their bytes.</summary>
    private async Task<(int Files, long Bytes)> AddKeysAsync(ZipArchive archive, CancellationToken ct)
    {
        var files = 0;
        long bytes = 0;
        var root = keyRing.Value.Root;
        if (!Directory.Exists(root)) return (0, 0);
        foreach (var full in Directory.EnumerateFiles(root, "*.xml").OrderBy(f => f, StringComparer.Ordinal))
        {
            await using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var entry = archive.CreateEntry($"keys/{Path.GetFileName(full)}", CompressionLevel.Optimal).Open();
            await input.CopyToAsync(entry, ct);
            bytes += input.Length;
            files++;
        }
        return (files, bytes);
    }

    /// <summary>
    /// The dated name, or the next free one when another backup took it in the same second (a run by hand next to a scheduled one). Its
    /// partial file is created right here, new, so two runs never write the same one.
    /// </summary>
    private static (string Output, FileStream File) Claim(string folder, string stem)
    {
        for (var n = 1; ; n++)
        {
            var path = Path.Combine(folder, n == 1 ? $"{stem}.zip" : $"{stem}-{n}.zip");
            if (File.Exists(path)) continue;
            try
            {
                return (path, CreatePartial(path));
            }
            catch (IOException) when (File.Exists(path + Partial))
            {
                // another run is writing this one (or one cut short left it behind)
            }
        }
    }

    /// <summary>The partial file of an output, created new: a partial of that name that is already there (another run's) is never taken over.</summary>
    private static FileStream CreatePartial(string output) => new(output + Partial, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    /// <summary>
    /// At start: the partial files that runs cut short left in the backups folder (a process stopped while it wrote one: a crash, a restart
    /// loop of a start that failed), removed, each with an Information line. Only one nobody holds open goes: a run keeps its partial open
    /// with <see cref="FileShare.None"/> while it writes, so another process writing one now (the command line, another server on the same
    /// folder) keeps it. On Linux that is the advisory lock .NET takes, honoured by the .NET processes of the same host; on a network share
    /// mounted by several hosts it may not be, and a partial another host is writing could be removed (that backup then fails). Telling the
    /// instances apart (an id of their own) is not done: the id would live in the database, which the start may not have yet.
    /// </summary>
    internal static void RemoveStalePartials(string folder, ILogger logger)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var path in Directory.EnumerateFiles(folder, "*.zip" + Partial))
        {
            try
            {
                // Opened alone, it goes as it is closed; held open by a run, the open fails and it stays.
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                logger.LogInformation("Removed {File}, a backup left half written by a run that was stopped", Path.GetFileName(path));
            }
            catch (IOException)
            {
                logger.LogDebug("{File} is being written by another run, or is gone: left as it is", Path.GetFileName(path));
            }
            catch (UnauthorizedAccessException e)
            {
                logger.LogWarning(e, "{File}, a backup left half written, cannot be removed", Path.GetFileName(path));
            }
        }
    }

    private static BackupEncryption? Configured(BackupOptions o) =>
        o.Encrypted ? new BackupEncryption(BackupEncryption.AgeX25519, o.Encryption.Recipient!.Trim(), o.Encryption.Salt!.Trim(), Argon2idParameters.Default) : null;
}
