using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tankstat.Application;
using Tankstat.Application.Backups;
using Tankstat.Infrastructure.Backups;
using Tankstat.Infrastructure.Persistence;
using Tankstat.TestSupport;

namespace Tankstat.Infrastructure.UnitTests;

/// <summary>
/// Upgrades, from every migration of the release: a database at it is backed up at start before anything is migrated (the backup holds it as
/// that migration left it), then brought up to date; and that backup, an older migration's, is restored at a later start and brought up to
/// date again, with no backup of what it replaced. A backup that cannot be written stops the migration. The start's order (user decision):
/// a waiting restore first, else (or when it is refused) the backup, then the migrations.
/// </summary>
public sealed class MigrationUpgradeTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tankstat-upgrade-{Guid.NewGuid():N}");
    private readonly CapturedLog _log = new();
    private ServiceProvider? _services;

    private string Database => Path.Combine(_root, "tankstat.db");
    private string Backups => Path.Combine(_root, "backups");

    /// <summary>Every migration of the release but the last, by name (a database at the last one has nothing to upgrade).</summary>
    public static TheoryData<string> OlderMigrations() => [.. OlderMigrationNames()];

    private static List<string> OlderMigrationNames()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:", o => o.MigrationsAssembly("Tankstat.Migrations.Sqlite")).Options);
        var ids = db.Database.GetMigrations().ToList();
        return [.. ids.Take(ids.Count - 1).Select(id => MigrationNames.Of(id)!)];
    }

    private ServiceProvider Services(string? backups = null)
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = $"Data Source={Database}",
            ["Storage:Path"] = Path.Combine(_root, "uploads"),
            ["DataProtection:KeysPath"] = Path.Combine(_root, "keys"),
            ["Backups:Path"] = backups ?? Backups,
        }).Build();
        return _services = new ServiceCollection().AddLogging(b => b.AddProvider(_log)).AddApplication(config).AddInfrastructure(config).BuildServiceProvider();
    }

    private async Task<AppDbContext> Db() => await _services!.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();

    private T Step<T>() => _services!.GetServices<IHostedService>().OfType<T>().Single();

    /// <summary>The database set up as the migration named <paramref name="name"/> leaves it; its provider's id.</summary>
    private async Task<string> AtMigration(string name)
    {
        await using var db = await Db();
        var id = MigrationModels.IdOf(db, name)!;
        await db.GetService<IMigrator>().MigrateAsync(id);
        return id;
    }

    /// <summary>
    /// #288: a database from before the rename (tyre stretches, from v0.21.0-preview-1) comes up with its rows as installations, its tombstones
    /// and its parked tyre changes renamed too (their kind, reason and the payload's field names, and their notifications' arguments), so the
    /// server reads them as before.
    /// </summary>
    [Fact]
    public async Task TyreStretchesOfAnOlderDatabase_ComeUpAsInstallations_WithTheirTombstonesAndParkedChangesRenamed()
    {
        Services();
        await AtMigration("AddTyres");
        string G(Guid g) => g.ToString().ToUpperInvariant(); // how EF Core stores a Guid in SQLite
        var (vehicle, owner, set, stretch, gone, parked, pastSwaps, notice) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using (var db = await Db())
        {
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;"); // the vehicle and the user are not the point
            await using var insert = db.Database.GetDbConnection().CreateCommand(); // a raw command: the JSON's braces are no format placeholders
            insert.CommandText = $$$"""
                INSERT INTO "TyreSets" ("Id", "OwnerId", "VehicleId", "CreatedById", "Name", "Season", "Since", "RoughDistance", "Version", "UpdatedAt")
                VALUES ('{{{G(set)}}}', '{{{G(owner)}}}', '{{{G(vehicle)}}}', '{{{G(owner)}}}', 'Michelin', 'Summer', '2023-03-01', 0, 1, '2026-11-10 12:00:00');
                INSERT INTO "TyreStretches" ("Id", "OwnerId", "VehicleId", "SetId", "CreatedById", "From", "Until", "PairOneFront", "Version", "UpdatedAt")
                VALUES ('{{{G(stretch)}}}', '{{{G(owner)}}}', '{{{G(vehicle)}}}', '{{{G(set)}}}', '{{{G(owner)}}}', '2026-03-28', NULL, 1, 1, '2026-11-10 12:00:00');
                INSERT INTO "Tombstones" ("Id", "EntityId", "EntityType", "PurgedAt", "VehicleId")
                VALUES ('{{{G(Guid.NewGuid())}}}', '{{{G(gone)}}}', 'TyreStretch', '2026-11-10 12:00:00', '{{{G(vehicle)}}}');
                INSERT INTO "SyncChanges" ("Id", "Kind", "OwnerId", "Payload", "ReasonArgs", "ReasonKey", "ReceivedAt", "Status", "SubmittedById", "TargetId", "VehicleId")
                VALUES ('{{{G(parked)}}}', 'UpdateTyreStretch', '{{{G(owner)}}}', '{"id":"{{{parked}}}","updateTyreStretch":{"id":"{{{gone}}}","from":"2026-03-28"}}', '{}', 'tyres.stretchNotFound',
                        '2026-11-10 12:00:00', 'Parked', '{{{G(owner)}}}', '{{{G(gone)}}}', '{{{G(vehicle)}}}');
                INSERT INTO "SyncChanges" ("Id", "Kind", "OwnerId", "Payload", "ReasonArgs", "ReceivedAt", "Status", "SubmittedById", "VehicleId")
                VALUES ('{{{G(pastSwaps)}}}', 'AddPastTyreSwaps', '{{{G(owner)}}}', '{"id":"{{{pastSwaps}}}","addPastTyreSwaps":{"swaps":[{"day":"2025-11-15","stretchId":"{{{gone}}}"}]}}', '{}',
                        '2026-11-10 12:00:00', 'Parked', '{{{G(owner)}}}', '{{{G(vehicle)}}}');
                INSERT INTO "Notifications" ("Id", "RecipientId", "Kind", "Topic", "SubjectType", "SubjectId", "ContextType", "ContextId", "Occurrence", "Count", "Args", "CreatedAt", "UpdatedAt")
                VALUES ('{{{G(notice)}}}', '{{{G(owner)}}}', 'SyncChangeParked', 'Sync', 'SyncChange', '{{{G(parked)}}}', 'Vehicle', '{{{G(vehicle)}}}', 'parked', 1,
                        '{"change":"UPDATE_TYRE_STRETCH","vehicleName":"Octavia","submitterName":"Alice","reason":"tyres.stretchNotFound","reasonArgs":"{}"}',
                        '2026-11-10 12:00:00', '2026-11-10 12:00:00');
                """;
            await insert.ExecuteNonQueryAsync();
        }

        await using (var db = await Db()) await db.Database.MigrateAsync();

        await using var after = await Db();
        var installation = await after.TyreInstallations.IgnoreQueryFilters().SingleAsync(i => i.Id == stretch);
        Assert.Equal((set, new DateOnly(2026, 3, 28), (bool?)true), (installation.SetId, installation.From, installation.PairOneFront));
        Assert.Equal(Tankstat.Domain.Sync.OfflineEntityType.TyreInstallation, (await after.Tombstones.SingleAsync(t => t.EntityId == gone)).EntityType);
        var update = await after.SyncChanges.SingleAsync(c => c.Id == parked);
        Assert.Equal((Tankstat.Domain.Sync.SyncChangeKind.UpdateTyreInstallation, "tyres.installationNotFound"), (update.Kind, update.ReasonKey));
        Assert.Contains("\"updateTyreInstallation\":", update.Payload);
        var past = await after.SyncChanges.SingleAsync(c => c.Id == pastSwaps);
        Assert.Contains("\"installationId\":", past.Payload);
        Assert.DoesNotContain("stretch", past.Payload + update.Payload, StringComparison.OrdinalIgnoreCase);
        // The parked change's notification is worded from its kind and reason: both under the names the app has texts for.
        var args = (await after.Notifications.SingleAsync(n => n.Id == notice)).Args;
        Assert.Equal(("UPDATE_TYRE_INSTALLATION", "tyres.installationNotFound", "Octavia"), (args["change"], args["reason"], args["vehicleName"]));
    }

    private async Task<string?> LastApplied()
    {
        await using var db = await Db();
        return (await db.Database.GetAppliedMigrationsAsync()).LastOrDefault();
    }

    [Theory]
    [MemberData(nameof(OlderMigrations))]
    public async Task ADatabaseAtAnOlderMigration_IsBackedUpBeforeItIsMigrated_AndThatBackupRestoresAndComesUpToDate(string name)
    {
        Services();
        var id = await AtMigration(name);
        string latest;
        await using (var db = await Db()) latest = db.Database.GetMigrations().Last();

        await Step<DatabaseMigrator>().StartAsync(default);

        Assert.Equal(latest, await LastApplied());
        var backup = Assert.Single(Directory.GetFiles(Backups, "*.zip"));
        var manifest = BackupFile.ReadManifest(backup);
        Assert.Equal((id, true), (manifest.Migration, manifest.SafetyNet)); // written before anything was migrated
        var lines = _log.From<DatabaseMigrator>().Select(e => e.Values.ContainsKey("Migrations") ? "applying" : e.Message.StartsWith("Backing up", StringComparison.Ordinal) ? "backup" : "other").ToList();
        Assert.True(lines.IndexOf("backup") is >= 0 and var b && b < lines.IndexOf("applying"), string.Join(", ", lines));

        // That backup is an older migration's: restored at the next start, the database is set up at it, loaded, and brought up to date, and
        // no backup is written of what it replaced.
        new RestoreMarker(Path.GetFileName(backup), Guid.Empty, DateTimeOffset.UtcNow, null).Write(Backups);
        await Step<DatabaseMigrator>().StartAsync(default);
        var restore = Step<RestoreStep>();
        await restore.StartAsync(default);

        Assert.Equal(RestoreOutcome.Applied, restore.Outcome);
        Assert.Equal(latest, await LastApplied());
        Assert.False(File.Exists(RestoreMarker.PathIn(Backups)));
        Assert.Equal([backup], Directory.GetFiles(Backups, "*.zip"));
    }

    [Fact]
    public async Task ABackupThatCannotBeWritten_StopsTheMigration_AndTheDatabaseStaysAsItWas()
    {
        Directory.CreateDirectory(_root);
        var notAFolder = Path.Combine(_root, "backups-is-a-file");
        await File.WriteAllTextAsync(notAFolder, "");
        Services(backups: notAFolder);
        var id = await AtMigration(OlderMigrationNames()[^1]);

        var stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => Step<DatabaseMigrator>().StartAsync(default));

        Assert.Contains("was not migrated", stopped.Message);
        Assert.Equal(id, await LastApplied());
    }

    [Fact]
    public async Task ARestoreRefusedAtAStartThatMigrates_LeavesTheDataAsItWas_SoItIsBackedUpFirst()
    {
        Services();
        var id = await AtMigration(OlderMigrationNames()[^1]);
        Directory.CreateDirectory(Backups);
        new RestoreMarker("tankstat-backup-20260101T000000Z.zip", Guid.Empty, DateTimeOffset.UtcNow, null).Write(Backups); // a backup that is not there

        await Step<DatabaseMigrator>().StartAsync(default);

        Assert.Equal(RestoreOutcome.Refused, Step<RestoreStep>().Outcome);
        Assert.Single(Directory.GetFiles(Backups, "restore.refused.*"));
        var manifest = BackupFile.ReadManifest(Assert.Single(Directory.GetFiles(Backups, "*.zip")));
        Assert.Equal((id, true), (manifest.Migration, manifest.SafetyNet)); // the data stays, so the upgrade has its way back
        await using var db = await Db();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task ARestoreAppliedAtAStartThatMigrates_WritesNoBackup_AndTheMigrationsBringTheBackupUpToDate()
    {
        Services();
        await AtMigration(OlderMigrationNames()[^1]);
        // A backup of the database at that migration (the command, which never migrates first), then the restore of it at the start that upgrades.
        var backup = (await _services!.GetRequiredService<IBackupWriter>().WriteAsync(new BackupRequest(), default)).Path;
        new RestoreMarker(Path.GetFileName(backup), Guid.Empty, DateTimeOffset.UtcNow, null).Write(Backups);
        string latest;
        await using (var db = await Db()) latest = db.Database.GetMigrations().Last();

        await Step<DatabaseMigrator>().StartAsync(default);
        var restore = Step<RestoreStep>();
        await restore.StartAsync(default);

        Assert.Equal(RestoreOutcome.Applied, restore.Outcome);
        Assert.Equal(latest, await LastApplied());
        Assert.Equal([backup], Directory.GetFiles(Backups, "*.zip")); // the backup restored is the one there is
        Assert.False(File.Exists(RestoreMarker.PathIn(Backups)));
        Assert.DoesNotContain(_log.From<DatabaseMigrator>(), e => e.Message.StartsWith("Backing up", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AtStart_ABackupLeftHalfWritten_IsRemoved_AndOneAnotherRunIsWriting_Stays()
    {
        Services();
        Directory.CreateDirectory(Backups);
        var stale = Path.Combine(Backups, "tankstat-backup-20260101T000000Z.zip.partial");
        await File.WriteAllBytesAsync(stale, [1, 2, 3]); // a run stopped while it wrote it
        var writing = Path.Combine(Backups, "tankstat-backup-20260102T000000Z.zip.partial");

        await using (var held = new FileStream(writing, FileMode.CreateNew, FileAccess.Write, FileShare.None)) // as the writer holds its own
            await Step<DatabaseMigrator>().StartAsync(default);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(writing));
        Assert.Contains(_log.From<DatabaseMigrator>(), e => e.Level == LogLevel.Information && (string?)e.Values["File"] == Path.GetFileName(stale));
    }

    [Fact]
    public async Task ANewDatabase_IsMigrated_WithNoBackup_ForItHoldsNothing()
    {
        Services();

        await Step<DatabaseMigrator>().StartAsync(default);

        Assert.False(Directory.Exists(Backups) && Directory.GetFiles(Backups).Length > 0);
        Assert.NotNull(await LastApplied());
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        using (var pooled = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Database}")) Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pooled);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
