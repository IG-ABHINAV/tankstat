namespace Tankstat.Application.Telemetry;

/// <summary>
/// The app's own telemetry setting (the <c>Telemetry</c> section). Everything else about telemetry is the OpenTelemetry SDK's own settings,
/// read under their standard names (<see cref="OtelSettings"/>), which the Server settings page edits as they are, like the logging framework's.
/// </summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";
    public const string TraceDatabaseKey = "Telemetry:TraceDatabase";

    /// <summary>
    /// Also trace every database command: EF Core's instrumentation, a beta that sends the SQL text of each command to the telemetry backend.
    /// Off by default, and it counts only while telemetry is exported (<see cref="TelemetryGate"/>).
    /// </summary>
    public bool TraceDatabase { get; set; }
}
