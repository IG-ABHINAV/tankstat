using System.Diagnostics;
using OpenTelemetry;

namespace Tankstat.Api.Telemetry;

/// <summary>
/// Takes off a span what a client sent or a person typed before it is exported, whatever the instrumentations' own switches say: the
/// browser's User-Agent header, which ASP.NET Core's instrumentation records by default; the values of database parameters (a plate, a
/// note, an amount, a password hash), which EF Core's instrumentation records when <c>OTEL_SEMCONV_STABILITY_OPT_IN=database</c> and
/// <c>OTEL_DOTNET_EXPERIMENTAL_EFCORE_ENABLE_TRACE_DB_QUERY_PARAMETERS=true</c> are both set; the values of a query string (the reset link's
/// token), which the ASP.NET Core and HttpClient instrumentations redact themselves unless their
/// <c>OTEL_DOTNET_EXPERIMENTAL_*_DISABLE_URL_QUERY_REDACTION</c> is set; and a GraphQL document's hash, which HotChocolate takes from the
/// request's own persisted-query id when it sends no document. A GraphQL span carrying an exception event is not exported at all: the
/// exception's text quotes the request (an operation name the document lacks), and an event cannot be taken off a span; the request's own
/// server span still counts it.
/// </summary>
internal sealed class ClientValueRedaction : BaseProcessor<Activity>
{
    internal const string UserAgent = "user_agent.original";
    internal const string QueryParameterPrefix = "db.query.parameter.";
    internal const string UrlQuery = "url.query";
    internal const string UrlFull = "url.full";
    internal const string DocumentHash = "graphql.document.hash";
    internal const string DocumentId = "graphql.document.id";
    internal const string Redacted = "Redacted";
    private const string GraphQLSource = "HotChocolate";
    private const string ExceptionEvent = "exception";

    public override void OnEnd(Activity activity)
    {
        if (activity.Source.Name.StartsWith(GraphQLSource, StringComparison.Ordinal) && activity.Events.Any(e => e.Name == ExceptionEvent))
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded; // the exporters leave a span that is not recorded alone
            return;
        }
        List<KeyValuePair<string, object?>>? changes = null;
        foreach (var tag in activity.TagObjects)
        {
            if (tag.Key == UserAgent || tag.Key == DocumentHash || tag.Key == DocumentId || tag.Key.StartsWith(QueryParameterPrefix, StringComparison.Ordinal)) (changes ??= []).Add(new(tag.Key, null));
            else if (tag.Key == UrlQuery && tag.Value is string query && RedactQuery(query) is { } redactedQuery && redactedQuery != query) (changes ??= []).Add(new(tag.Key, redactedQuery));
            else if (tag.Key == UrlFull && tag.Value is string url && RedactUrl(url) is { } redactedUrl && redactedUrl != url) (changes ??= []).Add(new(tag.Key, redactedUrl));
        }
        if (changes is null) return;
        foreach (var (key, value) in changes) activity.SetTag(key, value);
    }

    /// <summary>Every value of a query string replaced, its keys kept: <c>?resetToken=zzzz&amp;a=1</c> is <c>?resetToken=Redacted&amp;a=Redacted</c>.</summary>
    internal static string RedactQuery(string query)
    {
        var leading = query.StartsWith('?') ? "?" : "";
        var parts = query[leading.Length..].Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var at = parts[i].IndexOf('=');
            if (at >= 0) parts[i] = parts[i][..(at + 1)] + Redacted;
        }
        return leading + string.Join('&', parts);
    }

    /// <summary>The query of a full URL redacted the same way, the rest as it is.</summary>
    internal static string RedactUrl(string url)
    {
        var question = url.IndexOf('?');
        if (question < 0) return url;
        var hash = url.IndexOf('#', question);
        var end = hash < 0 ? url.Length : hash;
        return url[..question] + RedactQuery(url[question..end]) + url[end..];
    }
}
