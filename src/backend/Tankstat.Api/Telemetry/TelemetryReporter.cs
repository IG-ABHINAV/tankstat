using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Tankstat.Application.Telemetry;

namespace Tankstat.Api.Telemetry;

/// <summary>
/// Says at start what became of telemetry, decided before the host existed (<see cref="TelemetryGate"/>), where there is no logger yet.
/// Settings that cannot be used are a Warning naming them, never their values (an endpoint or a header can hold a token); telemetry never
/// stops a start. Nothing set is the usual case: a Debug line only. While it is on, it also ends the export once the app has stopped: the
/// three providers shut down together, each given <see cref="ShutdownMs"/>, since the host's disposal would otherwise shut them down one
/// after the other, each waiting its own 5 s on a collector that accepts the connection and never answers (a stop or restart 15 s late,
/// which Docker's default grace kills).
/// </summary>
internal sealed class TelemetryReporter(TelemetryPlan plan, ILogger<TelemetryReporter> logger, IServiceProvider services, IHostApplicationLifetime lifetime) : IHostedService
{
    /// <summary>What the last export may take at a stop: enough for a collector that answers, little enough for one that does not.</summary>
    internal const int ShutdownMs = 2000;

    public Task StartAsync(CancellationToken ct)
    {
        if (plan.On) lifetime.ApplicationStopped.Register(EndTheExport);
        switch (plan.State)
        {
            case TelemetryState.On:
                logger.LogInformation("OpenTelemetry export is on: traces, metrics and logs over {Protocol}, as service {ServiceName}", plan.Protocol, plan.ServiceName);
                break;
            case TelemetryState.Misconfigured:
                logger.LogWarning("OpenTelemetry export is off because its settings cannot be used: {Problems}", string.Join(" ", plan.Problems));
                break;
            case TelemetryState.SignalEndpointWithoutBase:
                logger.LogWarning("OpenTelemetry export is off: a signal's own endpoint ({Settings}) only sends that signal elsewhere; set {Endpoint} to turn the export on",
                    string.Join(", ", plan.SignalEndpoints), OtelSettings.Endpoint);
                break;
            case TelemetryState.Disabled:
                logger.LogDebug("OpenTelemetry export is off: {Setting} is true", OtelSettings.SdkDisabled);
                break;
            default:
                logger.LogDebug("OpenTelemetry export is off: {Setting} is not set", OtelSettings.Endpoint);
                break;
        }
        if (plan.TraceDatabase)
            logger.LogWarning("{Setting} is on: the SQL text of every database command goes to the telemetry backend, traced by a beta instrumentation (the values are sent as parameters and never exported). Turn it off when you are done.",
                TelemetryOptions.TraceDatabaseKey);
        else if (plan.TraceDatabaseIgnored)
            logger.LogWarning("{Setting} is on, but nothing is traced: it takes effect only while telemetry is exported ({Endpoint} set and usable)",
                TelemetryOptions.TraceDatabaseKey, OtelSettings.Endpoint);
        if (plan.TraceDatabaseInvalid)
            logger.LogWarning("{Setting} must be true or false; database commands are not traced", TelemetryOptions.TraceDatabaseKey);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Shuts the providers down together; each runs its shutdown once, so the host's later disposal returns at once.</summary>
    private void EndTheExport()
    {
        var tracer = services.GetService<TracerProvider>();
        var meter = services.GetService<MeterProvider>();
        var exporter = services.GetService<LoggerProvider>();
        Task.WaitAll(
            Task.Run(() => tracer?.Shutdown(ShutdownMs)),
            Task.Run(() => meter?.Shutdown(ShutdownMs)),
            Task.Run(() => exporter?.Shutdown(ShutdownMs)));
    }
}
