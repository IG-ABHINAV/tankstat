using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tankstat.Application.Backups;
using Tankstat.Application.Telemetry;
using Tankstat.Domain.Vehicles;
using Tankstat.Infrastructure.Backups;

namespace Tankstat.Infrastructure.Persistence;

/// <summary>
/// The database's part of every start, before the host starts accepting requests, in this order (user decision): backup files a stopped run
/// left half written are removed; a restore that waits is applied (<see cref="RestoreStep.ApplyWaitingAsync"/>, which sets the database up
/// as the backup's migration left it and writes no backup: the backup restored is the one there is); when none was applied (none waited, or
/// it was refused with the data as it was) and migrations are pending on a database that holds data, a backup of the database as it is
/// (a safety net, kept apart by retention) is written: when it cannot be written nothing is migrated, and the start stops with the reason,
/// since an upgrade is the change a way back is needed for; then the pending migrations run (bringing restored data up to date too), and
/// what has to follow a migration that ran (the stored consumption of logs that existed before it). A new, empty database has nothing to
/// back up, and a restore that failed after it replaced the data writes none either.
/// </summary>
internal sealed class DatabaseMigrator(
    IDbContextFactory<AppDbContext> dbFactory, IBackupWriter backups, IBackupStore store, RestoreStep restore, ILogger<DatabaseMigrator> logger) : IHostedService
{
    /// <summary>The migration that added the stored consumption: logs that already existed get theirs calculated once, right after it ran.</summary>
    private const string ConsumptionMigration = "_AddRefuelingConsumption";

    /// <summary>Done for this host already, before it started (<see cref="StartupSteps.ApplyBeforeHostAsync"/>): the host's start skips it once.</summary>
    private bool _skipNext;

    internal void SkipNextStart() => _skipNext = true;

    public async Task StartAsync(CancellationToken ct)
    {
        if (_skipNext)
        {
            _skipNext = false;
            return;
        }
        BackupWriter.RemoveStalePartials(store.Folder, logger);
        var restored = await restore.ApplyWaitingAsync(ct);
        await MigrateAsync(ct, backupFirst: restored is RestoreOutcome.None or RestoreOutcome.Refused);
    }

    /// <summary>Every pending migration (after a backup of the database as it is, with <paramref name="backupFirst"/>), then what has to follow the ones that ran.</summary>
    public async Task MigrateAsync(CancellationToken ct, bool backupFirst = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        var backfill = pending.Any(m => m.EndsWith(ConsumptionMigration, StringComparison.Ordinal));
        // A span only when the start changes the database (#219): the backup before it and the backfill after it are inside it.
        using var activity = pending.Count > 0 ? AppTraces.StartJob("tankstat.database.migrate") : null;
        activity?.SetTag("tankstat.database.migrations", pending.Count);
        activity?.SetTag("tankstat.database.provider", db.Database.ProviderName);
        try
        {
            if (backupFirst && pending.Count > 0 && (await db.Database.GetAppliedMigrationsAsync(ct)).Any())
                await BackUpAsync(pending.Count, ct);

            // The one line that tells an operator the database was changed by this start, and by what.
            if (pending.Count > 0) logger.LogInformation("Applying {Count} database migrations ({Provider}): {Migrations}", pending.Count, db.Database.ProviderName, string.Join(", ", pending));
            await db.Database.MigrateAsync(ct);
            if (pending.Count > 0) logger.LogInformation("Applied {Count} database migrations", pending.Count);
            else logger.LogInformation("The database schema is up to date ({Provider})", db.Database.ProviderName);

            if (backfill) await BackfillConsumptionAsync(ct);
        }
        catch (Exception e) when (activity.FailedBy(e, ct))
        {
            throw; // never reached: the filter only marks the span
        }
    }

    /// <summary>The backup before the migrations; when it cannot be written, nothing is migrated and the start stops, saying why.</summary>
    private async Task BackUpAsync(int pending, CancellationToken ct)
    {
        logger.LogInformation("Backing up the database before {Count} database migrations are applied", pending);
        try
        {
            await backups.WriteAsync(new BackupRequest(SafetyNet: true), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "The backup before the database migrations could not be written, so the database was not migrated. Fix the cause (the backups folder, its disk, the Backups settings) and start again.", e);
        }
    }

    internal async Task BackfillConsumptionAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var vehicleIds = await db.Refuelings.Select(r => r.VehicleId).Distinct().ToListAsync(ct);
        foreach (var id in vehicleIds)
        {
            var logs = await db.Refuelings.Include(r => r.OdometerReading).Where(r => r.VehicleId == id).ToListAsync(ct);
            ConsumptionCalculator.Apply(logs);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        if (vehicleIds.Count > 0) logger.LogInformation("Calculated the fuel consumption of the existing refuelings of {Vehicles} vehicles", vehicleIds.Count); // a new installation has none
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
