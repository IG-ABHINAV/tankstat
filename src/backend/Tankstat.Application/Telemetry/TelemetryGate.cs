namespace Tankstat.Application.Telemetry;

/// <summary>What became of telemetry at start.</summary>
public enum TelemetryState
{
    /// <summary>No endpoint is set: nothing is registered (the usual case).</summary>
    Off,
    /// <summary><c>OTEL_SDK_DISABLED</c> is <c>true</c>.</summary>
    Disabled,
    /// <summary>A setting has a shape the SDK cannot use (it would throw, or quietly use another): nothing is registered.</summary>
    Misconfigured,
    /// <summary>Only a signal's own endpoint is set: alone it would send the other signals to the SDK's default, <c>localhost:4317</c>.</summary>
    SignalEndpointWithoutBase,
    /// <summary>Traces, metrics and logs are exported.</summary>
    On,
}

/// <summary>The gate's decision; <paramref name="Problems"/> name settings and rules, never a value (an endpoint or a header can hold a token).</summary>
/// <param name="TraceDatabase">Database commands are traced: the switch is on and telemetry is exported.</param>
/// <param name="TraceDatabaseIgnored">The switch is on, but nothing is exported, so it does nothing.</param>
/// <param name="TraceDatabaseInvalid">The switch is set to something other than true or false (read as off).</param>
public sealed record TelemetryPlan(
    TelemetryState State,
    string Protocol,
    string ServiceName,
    bool TraceDatabase,
    bool TraceDatabaseIgnored,
    bool TraceDatabaseInvalid,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> SignalEndpoints)
{
    public bool On => State == TelemetryState.On;
}

/// <summary>
/// Whether the app registers telemetry, decided from the configuration before the host is built. Pure, and it never throws: telemetry never
/// stops a start. The base endpoint (<see cref="OtelSettings.Endpoint"/>) is the only switch; a signal's own endpoint only sends that signal
/// elsewhere. Without an endpoint nothing is registered at all, so no install tries the exporter's default address.
/// </summary>
public static class TelemetryGate
{
    public static TelemetryPlan Decide(Func<string, string?> valueOf)
    {
        string? Value(string key) => valueOf(key) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        var problems = new List<string>();
        foreach (var key in OtelSettings.EndpointKeys)
            if (Value(key) is { } value && !OtelSettings.IsEndpoint(value)) problems.Add($"{key} must be an absolute http or https address.");
        foreach (var key in OtelSettings.ProtocolKeys)
            if (Value(key) is { } value && !OtelSettings.IsProtocol(value)) problems.Add($"{key} must be {string.Join(" or ", OtelSettings.Protocols)}, written so.");
        foreach (var key in OtelSettings.HeadersKeys)
            if (Value(key) is { } value && !OtelSettings.AreHeaders(value)) problems.Add($"{key} must be key=value pairs separated by commas.");

        var signalEndpoints = OtelSettings.Signals.Select(OtelSettings.SignalEndpoint).Where(k => Value(k) is not null).ToList();
        var anyEndpoint = Value(OtelSettings.Endpoint) is not null || signalEndpoints.Count > 0;
        var state =
            !anyEndpoint ? TelemetryState.Off
            : Value(OtelSettings.SdkDisabled) is { } disabled && bool.TryParse(disabled, out var off) && off ? TelemetryState.Disabled
            : problems.Count > 0 ? TelemetryState.Misconfigured
            : Value(OtelSettings.Endpoint) is null ? TelemetryState.SignalEndpointWithoutBase
            : TelemetryState.On;

        var databaseSetting = Value(TelemetryOptions.TraceDatabaseKey);
        var databaseInvalid = databaseSetting is not null && !bool.TryParse(databaseSetting, out _);
        var databaseRequested = databaseSetting is not null && bool.TryParse(databaseSetting, out var on) && on;

        return new TelemetryPlan(
            state,
            Value(OtelSettings.Protocol) ?? OtelSettings.DefaultProtocol,
            Value(OtelSettings.ServiceName) ?? OtelSettings.DefaultServiceName,
            TraceDatabase: databaseRequested && state == TelemetryState.On,
            TraceDatabaseIgnored: databaseRequested && state != TelemetryState.On,
            TraceDatabaseInvalid: databaseInvalid,
            state == TelemetryState.Misconfigured ? problems : [],
            signalEndpoints);
    }
}
