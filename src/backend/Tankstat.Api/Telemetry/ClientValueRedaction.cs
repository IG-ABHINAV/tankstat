using System.Diagnostics;
using OpenTelemetry;

namespace Tankstat.Api.Telemetry;

/// <summary>
/// Takes off a span what a client sent or a person typed before it is exported: the browser's User-Agent header, which ASP.NET Core's
/// instrumentation records by default, and the values of database parameters (a plate, a note, an amount, a password hash), which EF Core's
/// instrumentation records when <c>OTEL_SEMCONV_STABILITY_OPT_IN=database</c> and
/// <c>OTEL_DOTNET_EXPERIMENTAL_EFCORE_ENABLE_TRACE_DB_QUERY_PARAMETERS=true</c> are both set, whatever those say.
/// </summary>
internal sealed class ClientValueRedaction : BaseProcessor<Activity>
{
    internal const string UserAgent = "user_agent.original";
    internal const string QueryParameterPrefix = "db.query.parameter.";

    public override void OnEnd(Activity activity)
    {
        List<string>? removed = null;
        foreach (var tag in activity.TagObjects)
            if (tag.Key == UserAgent || tag.Key.StartsWith(QueryParameterPrefix, StringComparison.Ordinal)) (removed ??= []).Add(tag.Key);
        if (removed is null) return;
        foreach (var key in removed) activity.SetTag(key, null);
    }
}
