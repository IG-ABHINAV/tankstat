namespace Tankstat.Application.Telemetry;

/// <summary>
/// The OpenTelemetry SDK's own settings, under the standard names every collector's documentation uses. The SDK reads them from the
/// configuration (so from the environment, the app's settings file and the Server settings page alike); the app reads them only to decide
/// whether to register telemetry at all (<see cref="TelemetryGate"/>) and to check their shape, since the SDK throws on some wrong values.
/// </summary>
public static class OtelSettings
{
    /// <summary>The OTLP endpoint of every signal: the one switch that turns the export on.</summary>
    public const string Endpoint = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary><c>grpc</c> (the SDK's default) or <c>http/protobuf</c>, written exactly so: the SDK takes nothing else.</summary>
    public const string Protocol = "OTEL_EXPORTER_OTLP_PROTOCOL";

    /// <summary>Headers sent with every export (a backend's API key: a secret), <c>key=value</c> pairs separated by commas.</summary>
    public const string Headers = "OTEL_EXPORTER_OTLP_HEADERS";

    /// <summary>The service the telemetry is filed under; <see cref="DefaultServiceName"/> when nothing sets it.</summary>
    public const string ServiceName = "OTEL_SERVICE_NAME";

    /// <summary><c>true</c> turns every signal off, whatever else is set.</summary>
    public const string SdkDisabled = "OTEL_SDK_DISABLED";

    public const string DefaultServiceName = "tankstat";
    public const string DefaultProtocol = "grpc";

    public static readonly IReadOnlyList<string> Protocols = ["grpc", "http/protobuf"];

    /// <summary>The signals, as the per-signal settings name them (<c>OTEL_EXPORTER_OTLP_TRACES_ENDPOINT</c>, ...).</summary>
    public static readonly IReadOnlyList<string> Signals = ["TRACES", "METRICS", "LOGS"];

    public static string SignalEndpoint(string signal) => $"OTEL_EXPORTER_OTLP_{signal}_ENDPOINT";
    public static string SignalProtocol(string signal) => $"OTEL_EXPORTER_OTLP_{signal}_PROTOCOL";
    public static string SignalHeaders(string signal) => $"OTEL_EXPORTER_OTLP_{signal}_HEADERS";

    public static IEnumerable<string> EndpointKeys => [Endpoint, .. Signals.Select(SignalEndpoint)];
    public static IEnumerable<string> ProtocolKeys => [Protocol, .. Signals.Select(SignalProtocol)];
    public static IEnumerable<string> HeadersKeys => [Headers, .. Signals.Select(SignalHeaders)];

    /// <summary>What the SDK runs with when nothing sets <paramref name="key"/> (null when it has no value of its own).</summary>
    public static string? DefaultOf(string key) =>
        key.Equals(Protocol, StringComparison.OrdinalIgnoreCase) ? DefaultProtocol
        : key.Equals(ServiceName, StringComparison.OrdinalIgnoreCase) ? DefaultServiceName
        : null;

    /// <summary>Whether <paramref name="key"/> is one of the SDK's settings rather than the app's own.</summary>
    public static bool IsOtelKey(string key) => key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase);

    public static bool IsEndpoint(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>The SDK compares the trimmed value as it is: <c>GRPC</c> is no protocol to it.</summary>
    public static bool IsProtocol(string value) => Protocols.Contains(value.Trim(), StringComparer.Ordinal);

    /// <summary>As the SDK reads headers (it throws otherwise): URL-decoded, then every part between commas holds a <c>=</c> after a key.</summary>
    public static bool AreHeaders(string value) =>
        Uri.UnescapeDataString(value).Split(',').All(pair => pair.IndexOf('=') is var at && at > 0 && pair[..at].Trim().Length > 0);
}
