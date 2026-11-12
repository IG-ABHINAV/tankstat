using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tankstat.Application.Auth;
using Tankstat.Application.Backups;
using Tankstat.Application.Images;
using Tankstat.Application.Setup;
using Tankstat.Application.Telemetry;
using Tankstat.Application.SystemSettings;
using Tankstat.Domain;
using Tankstat.Domain.Backups;
using Tankstat.Domain.Images;
using Tankstat.Domain.Instance;
using Tankstat.Domain.Users;
using Tankstat.Infrastructure.Persistence;

namespace Tankstat.Infrastructure.Backups;

/// <summary>What the start did with a restore that waited for it (<see cref="RestoreStep.Outcome"/>).</summary>
public enum RestoreOutcome
{
    /// <summary>No restore waited.</summary>
    None,
    /// <summary>The data is the backup's.</summary>
    Applied,
    /// <summary>Refused before anything was replaced: the data is as it was (<c>restore.refused.&lt;time&gt;</c>).</summary>
    Refused,
    /// <summary>
    /// The data on the server was replaced (or its tables removed), then the restore failed: what was there before is gone, the files may be
    /// half copied (<c>restore.failed.&lt;time&gt;</c>).
    /// </summary>
    Failed,
}

/// <summary>
/// A restore that waits in the backups folder (<see cref="RestoreMarker"/>), applied first thing at start, before the database migrations
/// (<see cref="DatabaseMigrator"/> asks <see cref="ApplyWaitingAsync"/> before anything else touches the data); then, as a hosted step after
/// the migrations, the generation: a restored backup's new one, a fresh instance's first one, which <see cref="InstanceGeneration"/> answers
/// with every session.
/// <para>
/// A restore replaces the instance's data as a whole, never merges, and writes no backup of the data it replaces (user decision: the
/// backup restored is the one there is; whatever changed since it was made is gone). What can be checked comes first, so a refusal leaves
/// everything as it was: the marker, the file, the manifest, the backup's migration (one this release knows: the database's, or any other
/// of the release, since the database is set up as the backup's migration left it; a newer release's is <c>backup.otherMigration</c>), an
/// encrypted backup's identity, the checksum, then, from the backup's own unpacked files, Standalone's administrator and, where the tables
/// are about to be rebuilt, every row as the load will read it. Then the database: at the backup's migration already, its rows are replaced
/// in one transaction (<see cref="TableLoad"/>), so a failure there undoes itself; at another one (or empty), every table is dropped (in one
/// transaction where the provider allows it), the migrations set it up as the backup's migration left it (Up steps only) and the rows are
/// loaded. The pictures (and the photos when the backup holds them), the key files and the settings file the backup carries follow, and the
/// migrations bring it all up to date. That settings file is judged among the checks, as the restore would leave it under this server's
/// environment (<see cref="ISettingsBackup.RestoredAsync"/>: the start it would give, and the sign-in the administrator rule applies to);
/// once it is written, this start ends at the end of the step (<see cref="IServerRestart.RestartNow"/>), so no later step runs under the
/// settings it replaced, and the next host reads it.
/// The generation is stamped inside the load for a backup of this release's last migration, else after the migrations
/// (<see cref="StartAsync"/>, whose instance table is only then as this release knows it), and the marker goes once it is: a stop in
/// between repeats the restore at the next start. A refused restore renames the marker to <c>restore.refused.&lt;time&gt;</c> and the start
/// goes on with the data as it is (and backs it up before migrating it); one that fails after the data was replaced renames it to
/// <c>restore.failed.&lt;time&gt;</c>: the backup is still in the folder to try again, the data from before it is not. A renamed marker
/// never keeps the identity: it opens every backup of the folder. Nothing here ever stops the app.
/// </para>
/// </summary>
internal sealed class RestoreStep(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<DatabaseOptions> database,
    IOptionsMonitor<BackupOptions> options,
    IOptions<StorageOptions> storage,
    IOptions<KeyRingOptions> keyRing,
    IOptions<AuthOptions> auth,
    IOptions<SetupOptions> setup,
    ISettingsBackup settingsFile,
    IServerRestart restart,
    InstanceGeneration state,
    OneTimeCode code,
    TimeProvider clock,
    ILogger<RestoreStep> logger,
    ILogger<OneTimeCode.RestoreLine> codeLogger) : IHostedService
{
    /// <summary>The folders right below a vehicle's that hold the photos of its logs (not their drafts, which go with every restore).</summary>
    private static readonly HashSet<string> LogPhotoFolders = [.. ImageFolders.LogPhotoKinds];

    /// <summary>
    /// A restore whose generation is stamped after the migrations (a backup of an older migration): the file, the backup's time, and the marker
    /// that goes once it is stamped (none when the restore failed, its marker renamed already).
    /// </summary>
    private (string File, DateTimeOffset RestorePoint, string? Marker)? _toStamp;

    /// <summary>What this start did with a restore that waited for it.</summary>
    public RestoreOutcome Outcome { get; private set; }

    /// <summary>The restore wrote the settings file: this start ends once the step is through, and the next one reads it.</summary>
    private bool _settingsWritten;

    /// <summary>Before the migrations: the restore that waits, if any (<see cref="RestoreOutcome.None"/> when none does).</summary>
    public async Task<RestoreOutcome> ApplyWaitingAsync(CancellationToken ct)
    {
        var marker = RestoreMarker.PathIn(options.CurrentValue.Root);
        if (!File.Exists(marker)) return Outcome = RestoreOutcome.None;
        // A trace of its own (#219), only when a restore waits; why one was refused is the log's (its reason quotes the backup's name).
        using var activity = AppTraces.StartJob("tankstat.restore.apply");
        try
        {
            Outcome = await RestoreAsync(marker, ct);
        }
        catch (Exception e) when (activity.FailedBy(e, ct))
        {
            throw; // never reached: the filter only marks the span
        }
        var outcome = Outcome.ToString().ToLowerInvariant();
        activity?.SetTag("tankstat.restore.outcome", outcome);
        if (Outcome is RestoreOutcome.Refused or RestoreOutcome.Failed) activity.Failed($"restore.{outcome}");
        return Outcome;
    }

    /// <summary>Done for this host already, before it started (<see cref="StartupSteps.ApplyBeforeHostAsync"/>): the host's start skips it once.</summary>
    private bool _skipNext;

    internal void SkipNextStart() => _skipNext = true;

    /// <summary>After the migrations: the generation of the data, stamped anew for a restore that waited for them.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (_skipNext)
        {
            _skipNext = false;
            return;
        }
        if (_toStamp is { } stamp)
        {
            InstanceInfo restored;
            await using (var stamping = await dbFactory.CreateDbContextAsync(ct))
                restored = await TableLoad.StampAsync(stamping, clock.GetUtcNow(), stamp.RestorePoint, logger, ct);
            _toStamp = null;
            if (stamp.Marker is not null)
            {
                File.Delete(stamp.Marker);
                Restored(stamp.File, restored, stamp.RestorePoint);
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // A fresh instance, or one whose row was written before generations existed, gets its first generation; InstanceStartService
        // records the start in this row afterwards. Both safe when two instances start on one database at once: the row another one
        // wrote first, and the generation it stamped first, are the ones kept (and read back).
        if (!await db.InstanceState.AnyAsync(ct))
        {
            db.InstanceState.Add(InstanceState.New());
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
            }
        }
        else
        {
            var first = Guid.NewGuid(); // a value, sent as a parameter: no provider has to translate making one
            if (await db.InstanceState.Where(s => s.Generation == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(x => x.Generation, first), ct) > 0)
                logger.LogDebug("The instance got its first generation");
        }
        var instance = await db.InstanceState.AsNoTracking().SingleAsync(ct);
        state.Set(new InstanceInfo(instance.Generation, instance.RestorePoint));

        if (_settingsWritten)
        {
            // Nothing after this step may run under the settings the file replaced (the setup step decides the setup under the sign-in it
            // reads, the sign-out after a mode change compares it): the next host, built at once, reads the restored file.
            _settingsWritten = false;
            restart.RestartNow();
            return;
        }
        // Its own category keeps the code off the telemetry exporter (OneTimeCode.LogCategory).
        if (auth.Value.Mode == AuthMode.None)
            codeLogger.LogInformation("Restoring a backup from the app needs the one-time restore code {Code} (sign-in is off, so there is no administrator to ask)", code.Value);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task<RestoreOutcome> RestoreAsync(string markerPath, CancellationToken ct)
    {
        RestoreMarker marker;
        try
        {
            marker = RestoreMarkerFile.Read(markerPath);
            if (string.IsNullOrWhiteSpace(marker.File)) throw new JsonException("The marker names no backup.");
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return Refuse(markerPath, null, e, "the marker cannot be read");
        }
        var file = Path.Combine(options.CurrentValue.Root, Path.GetFileName(marker.File));
        BackupManifest manifest;
        byte[]? identity = null;
        string target; // this provider's id of the backup's migration
        string? applied; // the database's last applied migration; none in an empty database
        bool last; // whether the backup's migration is this release's last, so the load stamps the generation itself
        RestoredSettings? settings = null; // the settings file the restore leaves, when the backup carries one
        try
        {
            if (!File.Exists(file)) throw new FileNotFoundException("The backup is not in the backups folder.", marker.File);
            manifest = BackupFile.ReadManifest(file);
            if (manifest.Encryption is { } encryption)
            {
                identity = marker.Identity is null ? null : AgeKeys.ParseIdentity(marker.Identity);
                if (identity is null || !BackupKeys.Opens(identity, encryption.Recipient))
                    throw new DomainException("backup.wrongKey", "The identity does not open this backup.");
            }
            target = await FileBackupStore.MigrationOfAsync(dbFactory, manifest, ct);
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                applied = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault();
                last = target == db.Database.GetMigrations().Last();
            }
            BackupFile.VerifyChecksum(file, manifest);
            // The settings file the backup brings, as the restore would leave it under this server's environment (this start's decides): a
            // setting the backup's server set in its environment left without a value, or a file this server would not start with (or would
            // leave out), is refused with the data as it is, as the request refused it.
            settings = await RestoreService.RequireRestorableAsync(settingsFile, manifest, BackupFile.ReadSettings(file, manifest, identity), ct);
        }
        catch (Exception e) when (e is DomainException or IOException or UnauthorizedAccessException or FormatException)
        {
            return Refuse(markerPath, marker, e, e is DomainException d ? d.Key : "the backup cannot be read");
        }

        logger.LogInformation("Restoring the backup {File} (written by Tankstat {Version} on {CreatedAt}, {Provider}), asked for by user {UserId}; no backup of the data it replaces is written",
            Path.GetFileName(file), manifest.AppVersion, manifest.CreatedAt, manifest.Provider, marker.RequestedById);
        DirectoryInfo? scratch = null;
        var replaced = false; // past this point the data from before the restore is gone: a failure is a failed restore, never a refused one
        var loaded = false;
        try
        {
            scratch = Directory.CreateTempSubdirectory("tankstat-restore-");
            BackupFile.Extract(file, manifest, identity, scratch.FullName);
            var tables = Path.Combine(scratch.FullName, "tables");
            IModel model;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                model = last ? db.Model : MigrationModels.ModelOf(db, target);
            // From the backup's own files, before anything changes: Standalone needs an administrator to sign in with afterwards, and tables
            // about to be rebuilt need every row to load (a line that is no row, a value that is not its column's, a count that is not the
            // manifest's); in place, the load's own transaction undoes such a row.
            RequireAdministrator(settings?.Auth ?? auth.Value, model, tables, manifest.Contents.Tables);
            var inPlace = applied is not null && MigrationNames.Same(applied, target);
            if (!inPlace) TableLoad.CheckRows(model, tables, manifest.Contents.Tables);

            InstanceInfo? instance = null;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                if (!inPlace) await SetUpAsync(db, applied, target, () => replaced = true, ct);
                if (last) instance = await TableLoad.LoadAsync(db, tables, manifest.Contents.Tables, clock.GetUtcNow(), manifest.CreatedAt, logger, ct);
                else await TableLoad.LoadRowsAsync(db, model, tables, manifest.Contents.Tables, logger, ct);
            }
            replaced = loaded = true;
            var roots = storage.Value;
            // A backup without the photos: the photos of logs on disk keep matching their logs' ids and stay, also those still below the
            // pictures root (one root, or not moved to a photos root of their own yet); everything else of the root is the backup's.
            var keepPhotos = manifest.Contents.Photos == BackupPhotos.LeftOut;
            if (manifest.Contents.Pictures) ReplaceContents(roots.PicturesRoot, Path.Combine(scratch.FullName, "files", "pictures"), "pictures", keepPhotos);
            if (manifest.Contents.Photos == BackupPhotos.Separate)
            {
                // Their own root now too: replaced; one root now: they join the pictures just restored.
                if (roots.OneRoot) CopyContents(Path.Combine(scratch.FullName, "files", "photos"), roots.PhotosRoot);
                else ReplaceContents(roots.PhotosRoot, Path.Combine(scratch.FullName, "files", "photos"), "photos");
            }
            if (manifest.Contents.Keys) ReplaceKeys(keyRing.Value.Root, Path.Combine(scratch.FullName, "keys"));
            if (settings is not null)
            {
                await settingsFile.WriteAsync(settings.Values, ct); // the previous file kept as .bak
                _settingsWritten = true;
                logger.LogInformation("Restored the settings file with {Count} values; the server restarts once this start step is through, to run with it", settings.Values.Count);
            }
            if (instance is not null)
            {
                File.Delete(markerPath);
                Restored(Path.GetFileName(file), instance, manifest.CreatedAt);
            }
            else _toStamp = (Path.GetFileName(file), manifest.CreatedAt, markerPath); // the marker goes once the migrations ran and the generation is stamped
            return RestoreOutcome.Applied;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (!replaced) return Refuse(markerPath, marker, e, e is DomainException d ? d.Key : "it could not be unpacked or loaded; the database is as it was");
            logger.LogError(e, "The restore of {File} failed after the data on this server was replaced; the files may be half copied. No backup of the data from before it was written: the backup itself is still in the folder to try again", Path.GetFileName(file));
            // Rows of the backup loaded: their history is not the devices' one, so they get a new generation once the migrations ran.
            if (loaded && !last) _toStamp = (Path.GetFileName(file), manifest.CreatedAt, null);
            Rename(markerPath, marker, "restore.failed");
            return RestoreOutcome.Failed;
        }
        finally
        {
            try
            {
                scratch?.Delete(recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // It holds the backup's tables (and keys) in plain: the operator removes it, or the system clears it.
                logger.LogWarning(e, "The temporary folder the backup was unpacked into could not be removed: {Folder}", scratch!.FullName);
            }
        }
    }

    /// <summary>
    /// The database set up as the backup's migration <paramref name="target"/> left it, when it is at another one (or empty): every table
    /// dropped (none in an empty database), then the migrations up to <paramref name="target"/> (Up steps only: the path every new installation
    /// runs; Down steps are never relied on). EF runs each migration in a transaction of its own, so once the tables are gone nothing undoes
    /// it: <paramref name="replaced"/> marks that point (<see cref="DropAllAsync"/>), after which a failure is a failed restore.
    /// </summary>
    private async Task SetUpAsync(AppDbContext db, string? applied, string target, Action replaced, CancellationToken ct)
    {
        if (applied is null)
            logger.LogInformation("The database is empty: it is set up as the backup's migration ({Migration}) left it", MigrationNames.Of(target));
        else
        {
            logger.LogInformation("The database is at another migration ({Database}) than the backup ({Migration}): it is emptied and set up as the backup's migration left it",
                MigrationNames.Of(applied), MigrationNames.Of(target));
            await DropAllAsync(db, replaced, ct);
        }
        await db.GetService<IMigrator>().MigrateAsync(target, ct);
    }

    private void Restored(string file, InstanceInfo instance, DateTimeOffset restorePoint) =>
        logger.LogInformation("Restored the backup {File}: the data is generation {Generation} now, taken back to {RestorePoint}", file, instance.Generation, restorePoint);

    /// <summary>
    /// Every table of this release (its children first) and the migrations' own, so the migrations can set the database up afresh. Where
    /// the provider takes DDL in a transaction (all but MySQL) the drops are one: one that fails (a view or a foreign key of the operator's
    /// over a table, a lock that is not let go, a stop) undoes the others, so the restore is refused with every table and row as they were,
    /// and <paramref name="destructive"/> marks the commit. MySQL commits each DROP by itself, so there the point of no return comes first.
    /// </summary>
    private async Task DropAllAsync(AppDbContext db, Action destructive, CancellationToken ct)
    {
        var sql = db.GetService<ISqlGenerationHelper>();
        var names = TableLoad.InLoadOrder(TableDump.Tables(db.Model)).Reverse().Select(t => sql.DelimitIdentifier(t.Name, t.Schema))
            .Concat([sql.DelimitIdentifier(TableDump.MigrationsHistory), sql.DelimitIdentifier("__EFMigrationsLock")]);
        var transactional = database.Value.Provider != DatabaseProvider.MySql;
        if (!transactional) destructive();
        await using (var transaction = transactional ? await db.Database.BeginTransactionAsync(ct) : null)
        {
            foreach (var name in names)
            {
                var drop = $"DROP TABLE IF EXISTS {name}"; // names from the model, never from the backup
                await db.Database.ExecuteSqlRawAsync(drop, ct);
            }
            if (transaction is null) return;
            destructive(); // a commit that fails may still have happened: from here on the tables count as gone
            await transaction.CommitAsync(ct);
        }
    }

    /// <summary>Whether the backup's users (in <paramref name="model"/>'s shape) hold an enabled local administrator.</summary>
    private static bool BackupHoldsAdministrator(IModel model, string tables) =>
        UsersOf(model, tables).Any(u => u.Local && u.Admin && !u.Disabled);

    /// <summary>
    /// Whether the setup waits on the backup's data once restored, as the start will decide it: its own row says so, or says nothing (a row
    /// from before the setup existed, or none) of data without a user or a vehicle.
    /// </summary>
    private static bool BackupSetupWaits(IModel model, string tables, IReadOnlyDictionary<string, long> counts)
    {
        var state = model.GetEntityTypes().FirstOrDefault(e => e.GetTableName() == "InstanceState");
        var file = Path.Combine(tables, "InstanceState.jsonl");
        if (state is not null && File.Exists(file) && File.ReadLines(file).FirstOrDefault(l => l.Length > 0) is { } line)
        {
            var table = StoreObjectIdentifier.Table("InstanceState", state.GetSchema());
            using var row = JsonDocument.Parse(line);
            bool Set(string property) =>
                state.FindProperty(property)?.GetColumnName(table) is { } column && row.RootElement.TryGetProperty(column, out var v) && v.ValueKind != JsonValueKind.Null;
            if (Set(nameof(InstanceState.SetupCompletedAt))) return false;
            if (Set(nameof(InstanceState.SetupPendingSince))) return true;
        }
        return counts.GetValueOrDefault("Users") == 0 && counts.GetValueOrDefault("Vehicles") == 0;
    }

    /// <summary>The backup's users as the Standalone rule needs them, read from its rows by the columns <paramref name="model"/> names; none before there were users.</summary>
    private static IEnumerable<(bool Local, bool Admin, bool Disabled)> UsersOf(IModel model, string tables)
    {
        var users = model.GetEntityTypes().FirstOrDefault(e => e.GetTableName() == "Users");
        var file = Path.Combine(tables, "Users.jsonl");
        if (users is null || !File.Exists(file)) yield break;
        var table = StoreObjectIdentifier.Table("Users", users.GetSchema());
        string Column(string property) => users.FindProperty(property)?.GetColumnName(table) ?? property;
        var (provider, admin, disabled) = (Column("Provider"), Column("IsAdmin"), Column("IsDisabled"));
        foreach (var line in File.ReadLines(file))
        {
            if (line.Length == 0) continue;
            using var row = JsonDocument.Parse(line);
            var r = row.RootElement;
            yield return (
                r.TryGetProperty(provider, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() == nameof(UserProvider.Local),
                IsTrue(r, admin), IsTrue(r, disabled));
        }
    }

    /// <summary>A flag as a dump holds it: a boolean, or a number where the provider keeps one.</summary>
    private static bool IsTrue(JsonElement row, string column) =>
        row.TryGetProperty(column, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.GetInt64() != 0));

    /// <summary>
    /// Standalone needs an administrator to sign in with afterwards: an enabled local administrator among the users the backup brings (read
    /// from its own rows, before anything changes, so a backup without one is refused, never applied). Data whose setup waits needs none
    /// while this server's setup is on: the start waits for the setup page, the only one that makes the first administrator (#149). With the
    /// setup turned off (<c>Setup:Enabled=false</c>, this server's environment: the backup never brings it) that start would stop instead, and
    /// every start after it, so such a backup is refused too. <paramref name="next"/> is the sign-in the start after the restore runs: the
    /// restored settings file's under this server's environment, else this server's. The backup's files are read for Standalone only.
    /// </summary>
    private void RequireAdministrator(AuthOptions next, IModel model, string tables, IReadOnlyDictionary<string, long> counts)
    {
        if (next.Mode != AuthMode.Standalone || BackupHoldsAdministrator(model, tables) || (setup.Value.Enabled && BackupSetupWaits(model, tables, counts))) return;
        throw new DomainException("settings.standaloneNeedsAdministrator",
            "The backup holds no administrator to sign in with in Standalone mode, and only the setup page makes the first one: restore a backup that has one, or, with the setup on, one whose setup waits.");
    }

    private RestoreOutcome Refuse(string markerPath, RestoreMarker? marker, Exception e, string reason)
    {
        logger.LogError(e, "The restore is refused ({Reason}); the start goes on with the data as it is", reason);
        Rename(markerPath, marker, "restore.refused");
        return RestoreOutcome.Refused;
    }

    /// <summary>
    /// The marker kept as <c>&lt;stem&gt;.&lt;time&gt;</c>, for the operator to see what was asked for; without the identity it carried, which
    /// opens every backup of the folder (a marker that could not be read is moved as it is).
    /// </summary>
    private void Rename(string markerPath, RestoreMarker? marker, string stem)
    {
        var target = Path.Combine(Path.GetDirectoryName(markerPath)!, string.Create(CultureInfo.InvariantCulture, $"{stem}.{clock.GetUtcNow():yyyyMMdd'T'HHmmss'Z'}"));
        try
        {
            if (marker is null) File.Move(markerPath, target, overwrite: true);
            else
            {
                (marker with { Identity = null }).WriteTo(target);
                File.Delete(markerPath);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError(e, "The restore marker could not be renamed; it is tried again at the next start");
        }
    }

    /// <summary>
    /// Everything inside a root replaced by the backup's (the folder itself stays: it may be a mount point); with <paramref name="keepPhotos"/>
    /// the photos of logs below the vehicles (<c>vehicles/&lt;id&gt;/refuelings|expenses</c>) stay, the drafts go like the rest. What the app
    /// may not remove (the lost+found of a disk mounted as the root, which is root's) stays, as a backup leaves it out.
    /// </summary>
    private void ReplaceContents(string root, string source, string what, bool keepPhotos = false)
    {
        Directory.CreateDirectory(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root).ToList())
        {
            if (keepPhotos && Directory.Exists(entry) && Path.GetFileName(entry) == ImageFolders.VehiclesFolder)
            {
                foreach (var vehicle in Directory.EnumerateDirectories(entry))
                    foreach (var below in Directory.EnumerateFileSystemEntries(vehicle).ToList())
                        if (!(Directory.Exists(below) && LogPhotoFolders.Contains(Path.GetFileName(below)))) Delete(below, what);
                continue;
            }
            Delete(entry, what);
        }
        var count = CopyContents(source, root);
        logger.LogInformation("Restored {Count} files of the {What}{Kept}", count, what, keepPhotos ? "; the photos of logs on disk stayed" : "");
    }

    /// <summary>
    /// The key files replaced by the backup's: only what a backup takes of the folder (its <c>*.xml</c> right inside it, as the writer reads
    /// them), never anything else there: the folder may be one the app keeps other things in.
    /// </summary>
    private void ReplaceKeys(string root, string source)
    {
        Directory.CreateDirectory(root);
        foreach (var key in Directory.EnumerateFiles(root, "*.xml")) Delete(key, "keys");
        var count = 0;
        if (Directory.Exists(source))
            foreach (var key in Directory.EnumerateFiles(source, "*.xml"))
            {
                File.Copy(key, Path.Combine(root, Path.GetFileName(key)), overwrite: true);
                count++;
            }
        logger.LogInformation("Restored {Count} files of the {What}{Kept}", count, "keys", "");
    }

    private void Delete(string entry, string what)
    {
        try
        {
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
        catch (UnauthorizedAccessException e)
        {
            logger.LogWarning(e, "Something below the {What} folder that the app may not remove stays as it is", what);
        }
    }

    private static int CopyContents(string source, string target)
    {
        if (!Directory.Exists(source)) return 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            count++;
        }
        return count;
    }
}
