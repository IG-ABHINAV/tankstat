using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tankstat.Application.Backups;
using Tankstat.Application.SystemSettings;
using Tankstat.Infrastructure.Persistence;

namespace Tankstat.Infrastructure.Backups;

/// <summary>
/// The start-up steps a restore from the command line runs when the server is not running, as a start would: the restore and then the
/// migrations (<see cref="DatabaseMigrator"/>, which applies a waiting restore first), then the generation (<see cref="RestoreStep"/>).
/// </summary>
public static class StartupSteps
{
    /// <summary>Runs them; what the restore step did with the restore that waited.</summary>
    public static async Task<RestoreOutcome> RestoreAndMigrateAsync(IServiceProvider services, CancellationToken ct)
    {
        var steps = services.GetServices<IHostedService>().ToList();
        await steps.OfType<DatabaseMigrator>().Single().StartAsync(ct);
        var restore = steps.OfType<RestoreStep>().Single();
        try
        {
            await restore.StartAsync(ct);
        }
        catch (ServerRestartException)
        {
            // The restore wrote the settings file and asks for a start that reads it: there is no host here, the next start of the server will.
        }
        return restore.Outcome;
    }

    /// <summary>
    /// The same, for Program.cs before it starts the host, when a restore waits: the host's own start then skips the two steps once, since
    /// they are done (a restore that wrote the settings file asks for the next host at once instead, and this one is never started).
    /// </summary>
    public static async Task ApplyBeforeHostAsync(IServiceProvider services, CancellationToken ct)
    {
        await RestoreAndMigrateAsync(services, ct);
        var steps = services.GetServices<IHostedService>().ToList();
        steps.OfType<DatabaseMigrator>().Single().SkipNextStart();
        steps.OfType<RestoreStep>().Single().SkipNextStart();
    }
}
