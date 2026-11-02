namespace Tankstat.TestSupport;

/// <summary>
/// The settings that would make a test host export telemetry to a developer's or a runner's collector: the OpenTelemetry SDK's
/// <c>OTEL_*</c> and the app's <c>Telemetry:*</c>. <c>Program.cs</c> decides on telemetry before the host is built, where a test's in-memory
/// settings do not exist yet, so every host a test builds hands an empty value for each of them to the host as a command-line argument
/// (<c>UseSetting</c>), which outranks the environment.
/// </summary>
public static class TelemetryKeys
{
    public static bool Is(string key) =>
        key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("Telemetry:", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("Telemetry__", StringComparison.OrdinalIgnoreCase);

    /// <summary>The telemetry keys the process's environment sets, in the configuration's spelling (<c>Telemetry__X</c> is <c>Telemetry:X</c>).</summary>
    public static IEnumerable<string> InEnvironment() =>
        Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(Is).Select(key => key.Replace("__", ":", StringComparison.Ordinal));
}
