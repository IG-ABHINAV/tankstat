using HotChocolate.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Tankstat.Application.Health;
using Tankstat.Application.Setup;
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
    /// <summary>The meters exported besides the instrumentations' own: the runtime's and EF Core's (neither carries a value anyone typed).</summary>
    internal static readonly string[] Meters = ["System.Runtime", "Microsoft.EntityFrameworkCore"];

    public static TelemetryPlan AddTelemetry(this WebApplicationBuilder builder)
    {
        var plan = TelemetryGate.Decide(key => builder.Configuration[key]);
        builder.Services.AddSingleton(plan);
        builder.Services.AddHostedService<TelemetryReporter>(); // says at start what became of it, also when it stays off
        if (!plan.On) return plan;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(plan.ServiceName, serviceVersion: HealthReporter.AppVersion))
            .WithTracing(tracing =>
            {
                // The query string's values are redacted by both instrumentations by default (the reset link's token is one): their
                // OTEL_DOTNET_EXPERIMENTAL_*_DISABLE_URL_QUERY_REDACTION switches must never be set.
                tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddHotChocolateInstrumentation();
                if (plan.TraceDatabase) tracing.AddEntityFrameworkCoreInstrumentation();
                // Before the exporter (registered after this by UseOtlpExporter), so what it removes is never exported.
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

        // A second AddGraphQLServer() configures the same (default) schema as Program.cs's chain.
        builder.Services.AddGraphQLServer().AddInstrumentation(options =>
        {
            // Never what a client sent: no variables (the reset token is one), no document, no extensions (the client's own JSON).
            options.RequestDetails = RequestDetails.Hash | RequestDetails.OperationName;
            options.IncludeDocument = false;
            options.IncludeDataLoaderKeys = false;
            options.IncludeOperationNameInSpanName = false;
            // An error event carries the error's message, and the app's quote what was typed (a schedule's title, an odometer reading):
            // the root span keeps only graphql.error.count.
            options.MaxErrorEvents = 0;
            // A field's span carries the client's alias, and a grid page would make hundreds of them.
            options.Scopes = ActivityScopes.Default & ~ActivityScopes.ResolveFieldValue;
            options.EnableResolveFieldValue = false;
        });

        // The one-time code is in the console log on purpose, never in a telemetry backend: its two lines (and every category below the
        // code's) are filtered out of the exporter, named exactly too, so no configured rule for them can be more specific than these.
        foreach (var category in new[] { OneTimeCode.LogCategory, typeof(OneTimeCode.SetupLine).FullName!.Replace('+', '.'), typeof(OneTimeCode.RestoreLine).FullName!.Replace('+', '.') })
            builder.Logging.AddFilter<OpenTelemetryLoggerProvider>(category, LogLevel.None);
        return plan;
    }
}
