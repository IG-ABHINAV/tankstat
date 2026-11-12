using System.Globalization;

namespace Tankstat.Application.Telemetry;

/// <summary>
/// The OpenTelemetry SDK's own settings, under the standard names every collector's documentation uses. The SDK reads them from the
/// configuration (so from the environment, the app's settings file and the Server settings page alike); the app reads them only to decide
/// whether to register telemetry at all (<see cref="TelemetryGate"/>) and to check their shape (<see cref="ProblemOf"/>), since the SDK
/// throws on some wrong values while the host is built, which would stop the start.
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

    /// <summary>How long an export may take, in milliseconds; the SDK sets it on its client unchecked, which throws on 0.</summary>
    public const string Timeout = "OTEL_EXPORTER_OTLP_TIMEOUT";

    /// <summary>How often metrics are exported, in milliseconds; the SDK's reader throws on 0.</summary>
    public const string MetricExportInterval = "OTEL_METRIC_EXPORT_INTERVAL";

    public const string DefaultServiceName = "tankstat";
    public const string DefaultProtocol = "grpc";

    public static readonly IReadOnlyList<string> Protocols = ["grpc", "http/protobuf"];

    /// <summary>The header the exporter adds itself: one configured under that name makes it throw.</summary>
    private const string ReservedHeader = "User-Agent";

    /// <summary>The signals, as the per-signal settings name them (<c>OTEL_EXPORTER_OTLP_TRACES_ENDPOINT</c>, ...).</summary>
    public static readonly IReadOnlyList<string> Signals = ["TRACES", "METRICS", "LOGS"];

    /// <summary>The certificate files the exporter reads at start (the collector's, and the client's certificate and key for mTLS): a missing one throws.</summary>
    private static readonly string[] CertificateSuffixes = ["CERTIFICATE", "CLIENT_CERTIFICATE", "CLIENT_KEY"];

    public static string SignalEndpoint(string signal) => $"OTEL_EXPORTER_OTLP_{signal}_ENDPOINT";

    public static IEnumerable<string> EndpointKeys => [Endpoint, .. Signals.Select(SignalEndpoint)];

    /// <summary>Every setting whose shape is checked before the host is built: the base one and each signal's own.</summary>
    public static IEnumerable<string> CheckedKeys
    {
        get
        {
            string[] suffixes = ["ENDPOINT", "PROTOCOL", "HEADERS", "TIMEOUT", .. CertificateSuffixes];
            return [.. suffixes.SelectMany(suffix => Signals.Select(s => $"OTEL_EXPORTER_OTLP_{s}_{suffix}").Prepend($"OTEL_EXPORTER_OTLP_{suffix}")), MetricExportInterval];
        }
    }

    /// <summary>What the SDK runs with when nothing sets <paramref name="key"/> (null when it has no value of its own).</summary>
    public static string? DefaultOf(string key) =>
        key.Equals(Protocol, StringComparison.OrdinalIgnoreCase) ? DefaultProtocol
        : key.Equals(ServiceName, StringComparison.OrdinalIgnoreCase) ? DefaultServiceName
        : null;

    /// <summary>Whether <paramref name="key"/> is one of the SDK's settings rather than the app's own.</summary>
    public static bool IsOtelKey(string key) => key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why the SDK could not run with <paramref name="value"/> for <paramref name="key"/>, or null when it can: the one rule for the start
    /// (<see cref="TelemetryGate"/>) and the Server settings page, worded for both, naming the setting and never the value (an endpoint or a
    /// header can hold a token). A key the SDK takes any value for, or quietly ignores a wrong one of, has no problem.
    /// </summary>
    /// <param name="fileExists">Whether a file the app can read is at a path (#312: Application reads no file; the caller asks the file system).</param>
    public static string? ProblemOf(string key, string value, Func<string, bool> fileExists)
    {
        if (!IsOtelKey(key)) return null;
        var trimmed = value.Trim();
        if (key.EndsWith("_ENDPOINT", StringComparison.OrdinalIgnoreCase)) return IsEndpoint(trimmed) ? null : $"{key} must be an absolute http or https address.";
        if (key.EndsWith("_PROTOCOL", StringComparison.OrdinalIgnoreCase)) return IsProtocol(trimmed) ? null : $"{key} must be one of {string.Join(", ", Protocols)}, written so.";
        if (key.EndsWith("_HEADERS", StringComparison.OrdinalIgnoreCase)) return AreHeaders(value) ? null : $"{key} must be key=value pairs separated by commas, no key twice and none {ReservedHeader}.";
        if (key.EndsWith("_TIMEOUT", StringComparison.OrdinalIgnoreCase) || key.Equals(MetricExportInterval, StringComparison.OrdinalIgnoreCase))
            return IsMilliseconds(trimmed) ? null : $"{key} must be a whole number of milliseconds, at least 1.";
        if (CertificateSuffixes.Any(suffix => key.EndsWith($"_{suffix}", StringComparison.OrdinalIgnoreCase)))
            return fileExists(trimmed) ? null : $"{key} must name a file the app can read.";
        return null;
    }

    public static bool IsEndpoint(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>The SDK compares the trimmed value as it is: <c>GRPC</c> is no protocol to it.</summary>
    public static bool IsProtocol(string value) => Protocols.Contains(value.Trim(), StringComparer.Ordinal);

    /// <summary>
    /// As the SDK reads headers (it throws otherwise): URL-decoded, then every part between commas holds a <c>=</c> after a key, each key
    /// once (the SDK adds them to a dictionary) and none the exporter's own <c>User-Agent</c>, which it adds after them.
    /// </summary>
    public static bool AreHeaders(string value)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { ReservedHeader };
        foreach (var pair in Uri.UnescapeDataString(value).Split(','))
        {
            var at = pair.IndexOf('=');
            if (at <= 0) return false;
            var key = pair[..at].Trim();
            if (key.Length == 0 || !keys.Add(key)) return false;
        }
        return true;
    }

    /// <summary>
    /// As the SDK reads a duration: digits only (anything else it ignores and runs with its default, so it is no problem here), and the
    /// client or reader it is set on throws on 0.
    /// </summary>
    private static bool IsMilliseconds(string value) =>
        !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds >= 1;
}
