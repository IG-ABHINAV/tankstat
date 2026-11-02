using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tankstat.Application.Backups;
using Tankstat.Application.Telemetry;
using Tankstat.Domain;

namespace Tankstat.Infrastructure.Backups;

/// <summary>
/// Writes backups one at a time: a run asked for while one is under way is not started twice (Back up now and the schedule share it). A run
/// goes on in the background, so the request that asked for it answers at once (a backup with photos can take longer than any request may),
/// and it ends with the app: the stopping token cancels it, and the host waits for it to wind down (its partial file and temporary copy
/// removed) before it stops, so a restart in the process never starts the next host under a run of the last one. After a backup is written,
/// retention removes what it says; a run that failed (not one the app's stop cut short: the next start finds that one missed) notifies the
/// administrators.
/// </summary>
/// <param name="lifetime">The app's, whose stop cuts a run short; null where there is no host (the services built on their own, as tests do).</param>
internal sealed class BackupRunner(
    IBackupWriter writer, IBackupStore store, IOptionsMonitor<BackupOptions> options, IServiceScopeFactory scopes, TimeProvider clock, ILogger<BackupRunner> logger,
    AppMetrics metrics, IHostApplicationLifetime? lifetime = null) : IBackupRunner, IHostedService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    // One value, replaced whole, so a reader never pairs "running" with another run's details.
    private volatile BackupRunStatus _status = new(false, null);
    private volatile Task _current = Task.CompletedTask;

    public BackupRunStatus Status => _status;

    public bool Start(Guid? actorId = null)
    {
        if (!_gate.Wait(0)) return false;
        var started = clock.GetUtcNow();
        _status = new BackupRunStatus(true, new BackupRun(started, null, null, null, null));
        _current = Task.Run(() => RunAsync(started, actorId, lifetime?.ApplicationStopping ?? CancellationToken.None));
        return true;
    }

    /// <summary>The run under way, if any, to wait for (the schedule joins one Back up now started; tests wait for it).</summary>
    internal Task WhenIdleAsync() => _current;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The stopping token has cancelled the run by now: waits for it to wind down, within the host's own time to stop, and never throws.</summary>
    public async Task StopAsync(CancellationToken cancellationToken) =>
        await _current.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private async Task RunAsync(DateTimeOffset started, Guid? actorId, CancellationToken ct)
    {
        // A trace of its own (#219), linked to the request of Back up now, which answers long before the run ends.
        var trigger = actorId is null ? "schedule" : "manual";
        using var activity = AppTraces.StartJob("tankstat.backup.run");
        activity?.SetTag("tankstat.backup.trigger", trigger);
        var timestamp = clock.GetTimestamp();
        BackupRun? run = null;
        try
        {
            var result = await writer.WriteAsync(new BackupRequest(), ct);
            run = new BackupRun(started, clock.GetUtcNow(), Path.GetFileName(result.Path), new FileInfo(result.Path).Length, null);
            metrics.BackupSize.Record(run.Bytes!.Value,
                new("tankstat.backup.encrypted", result.Manifest.Encryption is not null), new("tankstat.backup.photos", result.Manifest.Contents.Photos.ToString()));
            Prune(run.File!);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Stopped with the app, whatever the database made of the cancellation (SqlClient reports a cancelled read as a SqlException,
            // not as an OperationCanceledException): no failure, and nobody is told; the next start finds it missed.
            logger.LogInformation("The backup under way was stopped with the app");
            run = new BackupRun(started, clock.GetUtcNow(), null, null, "backup.stopped");
        }
        catch (Exception e)
        {
            if (e is KeyedException keyed) logger.LogWarning(e, "A backup failed: {Key}", keyed.Key);
            else logger.LogError(e, "A backup failed");
            run = new BackupRun(started, clock.GetUtcNow(), null, null, (e as KeyedException)?.Key ?? "backup.failed", (e as KeyedException)?.Args);
            await NotifyAsync(run, actorId);
        }
        finally
        {
            // Before the gate opens: the next run's status, set as it starts, is never overwritten by this one's.
            _status = new BackupRunStatus(false, run ?? new BackupRun(started, clock.GetUtcNow(), null, null, "backup.failed"));
            var outcome = _status.Last!.Error ?? "ok";
            activity?.SetTag("tankstat.backup.outcome", outcome);
            if (outcome is not ("ok" or "backup.stopped")) activity.Failed(outcome); // stopped with the app: no failure
            metrics.BackupDuration.Record(clock.GetElapsedTime(timestamp).TotalSeconds, new("tankstat.backup.outcome", outcome), new("tankstat.backup.trigger", trigger));
            _gate.Release();
        }
    }

    /// <summary>
    /// Retention, after <paramref name="written"/> was written: never that file (a backup dated later than now, copied in or written while
    /// the clock was wrong, would outrank it as the newest), and nothing while a restore waits for the next start, since the backup it names
    /// must still be there then. A file that cannot be removed is a Warning and the rest still goes; nothing here makes the written backup a
    /// failed one.
    /// </summary>
    private void Prune(string written)
    {
        try
        {
            // Read in here: settings that stopped validating since the start (a file edited by hand) fail retention, never the written backup.
            var o = options.CurrentValue;
            if (!o.Pruning) return;
            if (store.RestorePending)
            {
                logger.LogInformation("A restore waits for the next start: retention removes no backup until then");
                return;
            }
            foreach (var point in BackupRetention.ToRemove(store.List(), o.Keep, o.KeepDays, clock.GetUtcNow()).Where(p => p.File != written))
            {
                try
                {
                    store.Delete(point.File);
                    logger.LogInformation("Removed the backup {File} (retention)", point.File);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(e, "The backup {File} could not be removed", point.File);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "The backups folder could not be read to prune it");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Retention could not remove the old backups; the backup was written");
        }
    }

    /// <summary>In a scope of its own (the notifications are stored per request otherwise), and never cancelled: a failure near the app's stop is still told.</summary>
    private async Task NotifyAsync(BackupRun run, Guid? actorId)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<BackupFailureNotifier>().NotifyAsync(run, actorId, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogError(e, "The administrators could not be told that a backup failed");
        }
    }
}
