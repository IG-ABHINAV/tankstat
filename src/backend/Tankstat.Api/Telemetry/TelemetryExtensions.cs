using HotChocolate.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Tankstat.Application.Health;
using Tankstat.Application.Setup;
using Tankstat.Application.SystemSettings;
using Tankstat.Application.Telemetry;

namespace Tankstat.Api.Telemetry;

/// <summary>
/// OpenTelemetry over OTLP: traces, metrics and logs, registered only when <see cref="TelemetryGate"/> says so (an endpoint is configured and
/// every setting is usable), so an install without telemetry pays nothing. The SDK reads its own settings (<see cref="OtelSettings"/>) from
/// the configuration: the environment, the app's settings file and the Server settings page alike. What is exported keeps the log's promise:
/// ids, counts and reasons, never what someone typed or a client sent (docs/configuration.md, Telemetry).
/// </summary>
internal static class TelemetryExtensions
{
    /// <summary>The meters exported besides the instrumentations' own: the runtime's, EF Core's and the app's own (none carries a value anyone typed).</summary>
    internal static readonly string[] Meters = ["System.Runtime", "Microsoft.EntityFrameworkCore", AppMetrics.MeterName];

    /// <summary>
    /// The providers come alive as the host starts (OpenTelemetry's own hosted service, before the app's); a restore that waits is applied
    /// before the host starts at all (<c>Program.cs</c>, <c>StartupSteps.ApplyBeforeHostAsync</c>), so this brings them to life first, and the
    /// restore's span is exported too. Nothing without telemetry.
    /// </summary>
    public static void StartBeforeHost(this IServiceProvider services)
    {
        services.GetService<TracerProvider>();
        services.GetService<MeterProvider>();
    }

    /// <summary>The logging framework's two names for the exporter's provider: a configured rule may use either.</summary>
    private static readonly string[] ExporterProviderNames = [typeof(OpenTelemetryLoggerProvider).FullName!, "OpenTelemetry"];

    /// <summary>The categories of the one-time code's lines: never exported, named exactly so no rule can be more specific.</summary>
    internal static readonly string[] OneTimeCodeCategories =
        [OneTimeCode.LogCategory, typeof(OneTimeCode.SetupLine).FullName!.Replace('+', '.'), typeof(OneTimeCode.RestoreLine).FullName!.Replace('+', '.')];

    public static TelemetryPlan AddTelemetry(this WebApplicationBuilder builder)
    {
        var plan = TelemetryGate.Decide(key => builder.Configuration[key], File.Exists);
        builder.Services.AddSingleton(plan);
        builder.Services.AddHostedService<TelemetryReporter>(); // says at start what became of it, also when it stays off
        if (!plan.On) return plan;

        builder.Services.AddOpenTelemetry()
            // The app's name and version first, then the environment's OTEL_RESOURCE_ATTRIBUTES and OTEL_SERVICE_NAME, which win where they
            // say otherwise (a later resource wins the merge): an orchestrator's service.instance.id stays, and so does its service.name.
            .ConfigureResource(resource => resource.AddService(plan.ServiceName, serviceVersion: HealthReporter.AppVersion).AddEnvironmentVariableDetector())
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddHotChocolateInstrumentation();
                tracing.AddSource(AppTraces.SourceName); // the app's own spans (#219): backups, the start, photo reading, sync
                if (plan.TraceDatabase) tracing.AddEntityFrameworkCoreInstrumentation();
                // Before the exporter (registered after this by UseOtlpExporter), so what it removes is never exported: the User-Agent, every
                // parameter value and every query string's values, whatever the instrumentations' own redaction switches say.
                tracing.AddProcessor(new ClientValueRedaction());
            })
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddMeter(Meters))
            .WithLogging(configureBuilder: null, configureOptions: options =>
            {
                options.IncludeScopes = true; // the request's UserId, and ASP.NET Core's (the path without its query)
                options.IncludeFormattedMessage = true;
            })
            // One exporter for the three signals: it honours a signal's own endpoint (OTEL_EXPORTER_OTLP_TRACES_ENDPOINT, ...), which
            // AddOtlpExporter does not.
            .UseOtlpExporter();

        // The same (default) schema Program.cs's chain configures; AddGraphQL adds no server defaults of its own on the way to it.
        builder.Services.AddGraphQL().AddInstrumentation(options =>
        {
            // Never what a client sent: no variables (the reset token is one), no document, no extensions (the client's own JSON), and
            // nothing of the HTTP request's JSON either (its operationName and persisted-query hash are copied as typed): the operation
            // span still names the operation from the parsed document.
            options.RequestDetails = RequestDetails.None;
            options.IncludeDocument = false;
            options.IncludeDataLoaderKeys = false;
            options.IncludeOperationNameInSpanName = false;
            // An error event carries the error's message, and the app's quote what was typed (a schedule's title, an odometer reading):
            // the request span keeps only graphql.error.count.
            options.MaxErrorEvents = 0;
            // No field spans (a field's span carries the client's alias, and a grid page would make hundreds of them), and none of the HTTP
            // spans: their parse and accept-header errors are events with the client's text, which MaxErrorEvents does not cap.
            options.Scopes = (ActivityScopes.Default | ActivityScopes.ExecuteRequest) & ~(ActivityScopes.ResolveFieldValue | ActivityScopes.ParseHttpRequest | ActivityScopes.ExecuteHttpRequest);
            options.EnableResolveFieldValue = false;
        });

        // The exporter's log rules, kept whatever the configuration asks of its provider (a rule for a provider outranks every general one,
        // and a longer category outranks a shorter): ASP.NET Core's lines stay at Warning or above (its request lines hold the reset links'
        // tokens, the floor of docs/configuration.md), and the one-time code's lines never go (they are in the console log on purpose).
        builder.Services.PostConfigure<LoggerFilterOptions>(KeepTheExportersPromises);
        return plan;
    }

    /// <summary>
    /// Runs after the configuration's rules were read (and again on a reload): raises the exporter's rules under the floor's category to the
    /// floor, adds the floor itself, drops the exporter's rules that would match a one-time code category and adds those as None, last.
    /// </summary>
    internal static void KeepTheExportersPromises(LoggerFilterOptions options)
    {
        var rules = options.Rules;
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (!IsExporters(rule) || rule.CategoryName is null) continue;
            if (OneTimeCodeCategories.Any(category => Matches(rule.CategoryName, category)))
            {
                rules.RemoveAt(i--);
                continue;
            }
            if (rule.LogLevel < SettingsCatalog.LogLevelFloor && Matches(rule.CategoryName, SettingsCatalog.LogLevelFloorCategory, orBelow: true))
                rules[i] = new LoggerFilterRule(rule.ProviderName, rule.CategoryName, SettingsCatalog.LogLevelFloor, rule.Filter);
        }
        rules.Add(new LoggerFilterRule(ExporterProviderNames[0], SettingsCatalog.LogLevelFloorCategory, SettingsCatalog.LogLevelFloor, null));
        foreach (var category in OneTimeCodeCategories) rules.Add(new LoggerFilterRule(ExporterProviderNames[0], category, LogLevel.None, null));
    }

    private static bool IsExporters(LoggerFilterRule rule) => rule.ProviderName is { } name && ExporterProviderNames.Contains(name, StringComparer.Ordinal);

    /// <summary>
    /// Whether a rule's category (a prefix, or one with a <c>*</c> for anything) can select <paramref name="category"/>, as the logging framework
    /// matches them; with <paramref name="orBelow"/>, any category under it too (a rule for <c>Microsoft.AspNetCore.Hosting*</c> counts).
    /// </summary>
    private static bool Matches(string ruleCategory, string category, bool orBelow = false)
    {
        var star = ruleCategory.IndexOf('*');
        var prefix = star < 0 ? ruleCategory : ruleCategory[..star];
        var suffix = star < 0 ? "" : ruleCategory[(star + 1)..];
        var under = orBelow && prefix.StartsWith(category + ".", StringComparison.OrdinalIgnoreCase);
        return under || (category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (suffix.Length == 0 || orBelow || category.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
    }
}
