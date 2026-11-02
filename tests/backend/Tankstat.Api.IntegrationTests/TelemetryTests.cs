using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Tankstat.Api.Telemetry;
using Tankstat.Application.Backups;
using Tankstat.Application.Health;
using Tankstat.Application.Setup;
using Tankstat.Application.Telemetry;
using Tankstat.Infrastructure.Backups;
using Tankstat.TestSupport;

namespace Tankstat.Api.IntegrationTests;

/// <summary>
/// ActivitySource and logger listeners are the process's: an exporter of one host is handed what any host in the process does. These tests
/// run alone, after every collection that runs in parallel, so what they read is their own host's.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryCollection
{
    public const string Name = "Telemetry";
}

/// <summary>
/// OpenTelemetry over real GraphQL, REST and EF Core: registered only with an endpoint (one nothing answers on), and what its exporter is
/// handed (kept in memory beside it) keeps the log's promise: ids, counts and reasons, never what someone typed or a client sent.
/// </summary>
[Collection(TelemetryCollection.Name)]
public class TelemetryTests
{
    private const string NobodyListens = "http://127.0.0.1:1"; // refused at once: an export fails fast, nothing waits for it

    // What a person typed or a client sent; none may reach a span, an event, a resource or an exported log record.
    private const string VehicleName = "zzkarzz";
    private const string Plate = "ZZPLATEZZ";
    private const string Note = "zz'note\\zz"; // a quote and a backslash, to try the SQL sanitiser too
    private const long Odometer = 123457;
    private const string ScheduleTitle = "zzTitleQuxzz";
    private const string TokenVariable = "zzResetTokzz";
    private const string TokenInQuery = "zzQueryTokzz";
    private const string Alias = "zzAliaszz";
    private const string ExtensionValue = "zzExtValzz";
    private const string UserAgent = "zzAgentzz";
    private const string OperationNameSent = "zzOp Namezz"; // the request's own operationName, no operation of the document (and no GraphQL name: the log writes "(invalid name)" for it)
    private const string PersistedHash = "zzHashzz"; // a client's persisted-query hash, sent without a document
    private const string OnError = "zzOnErrzz"; // an unknown onError value: a parse error quoting it
    private const string Accept = "zzAcceptzz"; // an Accept header nothing can parse: an error quoting it
    private static readonly string[] Canaries =
        [VehicleName, Plate, "zz'note", Odometer.ToString(), ScheduleTitle, TokenVariable, TokenInQuery, Alias, ExtensionValue, UserAgent, OperationNameSent, PersistedHash, OnError, Accept];

    private sealed record Exported(List<Activity> Spans, List<LogRecord> Logs, List<Metric> Metrics);

    private static (TestApp App, Exported Exported) Start(Dictionary<string, string?> settings, bool migrated = true)
    {
        var exported = new Exported([], [], []);
        var app = new TestApp(new Dictionary<string, string?>(settings) { ["Auth:Mode"] = settings.GetValueOrDefault("Auth:Mode") ?? "None" }, services: s =>
        {
            // Configures only providers the app registered: without telemetry there are none, and these add nothing.
            s.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(exported.Spans));
            s.ConfigureOpenTelemetryLoggerProvider(l => l.AddInMemoryExporter(exported.Logs));
            s.ConfigureOpenTelemetryMeterProvider(m => m.AddInMemoryExporter(exported.Metrics));
        }, migrated: migrated);
        return (app, exported);
    }

    private static Dictionary<string, string?> On(params (string Key, string? Value)[] more)
    {
        var settings = new Dictionary<string, string?> { [OtelSettings.Endpoint] = NobodyListens };
        foreach (var (key, value) in more) settings[key] = value;
        return settings;
    }

    /// <summary>A server span ends just after its response is sent: waits until the host's requests have all ended theirs.</summary>
    private static async Task Settled(TestApp app, Exported exported, int requests)
    {
        for (var tries = 0; tries < 100 && exported.Spans.Count(s => s.Source.Name == "Microsoft.AspNetCore") < requests; tries++) await Task.Delay(20);
        app.Factory.Services.GetRequiredService<TracerProvider>().ForceFlush();
        app.Factory.Services.GetService<LoggerProvider>()?.ForceFlush();
    }

    private static IEnumerable<(string Where, string Text)> Texts(Exported exported)
    {
        foreach (var span in exported.Spans.ToList())
        {
            var where = $"[{span.Source.Name}] '{span.DisplayName}'";
            yield return ($"{where} name", span.DisplayName);
            if (span.StatusDescription is { } status) yield return ($"{where} status", status);
            foreach (var tag in span.TagObjects) yield return ($"{where} {tag.Key}", tag.Value?.ToString() ?? "");
            foreach (var e in span.Events)
                foreach (var tag in e.Tags) yield return ($"{where} event {e.Name} {tag.Key}", tag.Value?.ToString() ?? "");
        }
        foreach (var log in exported.Logs.ToList())
        {
            var where = $"log [{log.CategoryName}]";
            if (log.FormattedMessage is { } message) yield return ($"{where} message", message);
            if (log.Body is { } body) yield return ($"{where} body", body);
            foreach (var attribute in log.Attributes ?? []) yield return ($"{where} {attribute.Key}", attribute.Value?.ToString() ?? "");
            var scopes = new List<string>();
            log.ForEachScope((scope, list) => { foreach (var item in scope) list.Add($"{item.Key}={item.Value}"); }, scopes);
            foreach (var scope in scopes) yield return ($"{where} scope", scope);
        }
    }

    private static void NoCanary(Exported exported)
    {
        var found = Texts(exported).SelectMany(t => Canaries.Where(c => CapturedLog.Holds(t.Text, c)).Select(c => $"{c} in {t.Where}")).Distinct().ToList();
        Assert.True(found.Count == 0, "Exported what someone typed or a client sent:\n" + string.Join("\n", found));
    }

    /// <summary>
    /// The probe's flow (#214): what a person types, two refusals whose messages quote it, a reset token, an alias, extensions, and four
    /// requests whose own JSON or headers HotChocolate would put into a span's tags or events (an operationName no operation has, a
    /// persisted-query hash without a document, an onError nothing knows, an Accept header nothing can parse).
    /// </summary>
    private static async Task<int> Flow(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        var vehicleId = (await client.Gql("mutation($i: AddVehicleInput!) { addVehicle(input: $i) { id } }",
            new { i = new { name = VehicleName, licensePlate = Plate, fuelType = "PETROL" } })).Data().GetProperty("addVehicle").GetProperty("id").GetString()!;
        (await client.Gql("mutation LogIt($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }",
            new { i = new { vehicleId, date = "2026-10-01", volume = 40, totalCost = 20000, currency = "HUF", odometer = Odometer, isFullTank = true, note = Note } })).Data();
        Assert.Equal("odometer.aboveNext", (await client.Gql("mutation LogEarlier($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }",
            new { i = new { vehicleId, date = "2026-09-01", volume = 40, totalCost = 20000, currency = "HUF", odometer = 200000, isFullTank = true } })).ErrorKey());
        var scheduleId = (await client.Gql("mutation AddSchedule($i: AddRecurringExpenseInput!) { addRecurringExpense(input: $i) { id } }",
            new { i = new { vehicleId, title = ScheduleTitle, kind = "TIME", intervalMonths = 12, lastDoneDate = "2026-06-01" } })).Data().GetProperty("addRecurringExpense").GetProperty("id").GetString()!;
        Assert.Equal("recurring.doneBeforeLast", (await client.Gql("mutation Done($i: MarkRecurringExpensesDoneInput!) { markRecurringExpensesDone(input: $i) { __typename } }",
            new { i = new { ids = new[] { scheduleId }, date = "2026-01-01" } })).ErrorKey());
        Assert.NotNull((await client.Gql("mutation Reset($i: ResetPasswordInput!) { resetPassword(input: $i) }",
            new { i = new { token = TokenVariable, newPassword = "whatever-password-1", newPasswordConfirmation = "whatever-password-1" } })).ErrorKey());
        Assert.Equal(VehicleName, (await client.Gql($"query Aliased($id: UUID!) {{ {Alias}: vehicle(id: $id) {{ name }} }}", new { id = vehicleId }, new { zzExtKey = ExtensionValue }))
            .Data().GetProperty(Alias).GetProperty("name").GetString());
        await client.GetAsync($"/?resetToken={TokenInQuery}");
        // A batch a device kept offline (#219: a span of the request's, one per change): what it holds stays out of them.
        var (changeId, logId) = (Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(1, (await client.Gql("mutation Sync($i: SyncChangesInput!) { syncChanges(input: $i) { applied } }",
            new { i = new { changes = new[] { new { id = changeId, logRefueling = new { vehicleId, id = logId, date = "2026-11-01", volume = 30, totalCost = 15000, currency = "HUF", odometer = Odometer + 1000, isFullTank = true, note = Note } } } } }))
            .GetProperty("data").GetProperty("syncChanges").GetProperty("applied").GetInt32());
        // The four whose answer is an error of the request's own, read for nothing: only what reaches the exporter matters.
        await client.PostAsJsonAsync("/graphql", new { query = "{ __typename }", operationName = OperationNameSent });
        await client.PostAsJsonAsync("/graphql", new { extensions = new { persistedQuery = new { version = 1, md5Hash = PersistedHash } } });
        await client.PostAsJsonAsync("/graphql", new { query = "{ __typename }", onError = OnError });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql") { Content = JsonContent.Create(new { query = "{ __typename }" }) };
        request.Headers.TryAddWithoutValidation("Accept", Accept);
        await client.SendAsync(request);
        return 13; // the requests sent
    }

    [Fact]
    public void WithoutAnEndpoint_NothingIsRegistered_EvenWithTheDevelopersOwnInTheEnvironment()
    {
        Environment.SetEnvironmentVariable(OtelSettings.Endpoint, NobodyListens); // a developer's collector: TestApp leaves it out
        try
        {
            var (app, _) = Start([]);
            using (app)
            {
                app.NewClient();

                Assert.Null(app.Factory.Services.GetService<TracerProvider>());
                Assert.Null(app.Factory.Services.GetService<MeterProvider>());
                Assert.DoesNotContain(app.Factory.Services.GetServices<ILoggerProvider>(), p => p is OpenTelemetryLoggerProvider);
                Assert.Equal(TelemetryState.Off, app.Factory.Services.GetRequiredService<TelemetryPlan>().State);
                Assert.Empty(app.Log.From<TelemetryReporter>().Where(e => e.Level >= LogLevel.Information));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(OtelSettings.Endpoint, null);
        }
    }

    [Fact]
    public async Task WhatIsExported_NeverHoldsWhatSomeoneTypedOrAClientSent()
    {
        var (app, exported) = Start(On());
        using var _ = app;

        var requests = await Flow(app.NewClient());
        await Settled(app, exported, requests);

        NoCanary(exported);
        // The settings that keep it so (#214's probe found each of these with the defaults).
        var graphQL = exported.Spans.Where(s => s.Source.Name.StartsWith("HotChocolate", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(graphQL);
        Assert.DoesNotContain(graphQL.SelectMany(s => s.Events), e => e.Name == "graphql.error");
        Assert.DoesNotContain(graphQL.SelectMany(s => s.TagObjects),
            t => t.Key is "graphql.http.request.extensions" or "graphql.http.request.operation" or "graphql.http.request.query.hash" or "graphql.field.alias" or "graphql.field.path" or "graphql.document.body");
        Assert.Contains(graphQL, s => s.GetTagItem("graphql.error.count") is not null); // the refusals are still counted
        Assert.Contains(exported.Spans, s => (s.GetTagItem("url.query") as string) == "?resetToken=Redacted");
        Assert.DoesNotContain(exported.Spans.SelectMany(s => s.TagObjects), t => t.Key == ClientValueRedaction.UserAgent);
        Assert.DoesNotContain(exported.Spans, s => s.Source.Name.Contains("EntityFramework", StringComparison.Ordinal)); // off by default
    }

    [Fact]
    public async Task WithDatabaseSpans_TheSqlTextHoldsNoValue_AndParameterValuesAreDropped_WhateverTheSdkIsAskedFor()
    {
        // Both together make EF Core's instrumentation record every parameter's value (#214); the app drops them before the exporter.
        (string Key, string Value)[] optIns = [("OTEL_SEMCONV_STABILITY_OPT_IN", "database"), ("OTEL_DOTNET_EXPERIMENTAL_EFCORE_ENABLE_TRACE_DB_QUERY_PARAMETERS", "true")];
        foreach (var (key, value) in optIns) Environment.SetEnvironmentVariable(key, value);
        try
        {
            var (app, exported) = Start(On([(TelemetryOptions.TraceDatabaseKey, "true"), .. optIns.Select(o => (o.Key, (string?)o.Value))]));
            using var _ = app;

            var requests = await Flow(app.NewClient());
            await Settled(app, exported, requests);

            NoCanary(exported);
            var database = exported.Spans.Where(s => s.Source.Name.Contains("EntityFramework", StringComparison.Ordinal)).ToList();
            Assert.Contains(database, s => (s.GetTagItem("db.query.text") as string)?.Contains("INSERT INTO", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(database.SelectMany(s => s.TagObjects), t => t.Key.StartsWith(ClientValueRedaction.QueryParameterPrefix, StringComparison.Ordinal));
            Assert.Contains(app.Log.From<TelemetryReporter>(), e => e.Level == LogLevel.Warning && (string?)e.Values["Setting"] == TelemetryOptions.TraceDatabaseKey);
        }
        finally
        {
            foreach (var (key, _) in optIns) Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public async Task ARequest_IsOneTrace_FromTheServerSpanThroughGraphQL_ToItsLogLines()
    {
        var (app, exported) = Start(On());
        using var _ = app;
        var client = app.NewClient();

        (await client.Gql("mutation AddCar { addVehicle(input: { name: \"Car\", fuelType: PETROL }) { id } }")).Data();
        await client.GetAsync($"/media/{Guid.NewGuid()}");
        await Settled(app, exported, 2);

        var server = exported.Spans.Single(s => s.Source.Name == "Microsoft.AspNetCore" && (s.GetTagItem("url.path") as string) == "/graphql");
        Assert.Equal("/graphql/{**slug}", server.GetTagItem("http.route"));
        var operation = exported.Spans.Where(s => s.Source.Name.StartsWith("HotChocolate", StringComparison.Ordinal) && (s.GetTagItem("graphql.operation.name") as string) == "AddCar").ToList();
        Assert.NotEmpty(operation); // the operation's span, and its planning's
        Assert.All(operation, span => Assert.Equal(server.TraceId, span.TraceId));
        Assert.Contains(exported.Logs, l => l.TraceId == server.TraceId && l.CategoryName!.StartsWith("Tankstat", StringComparison.Ordinal)); // the app's own lines of that request
        var media = exported.Spans.Single(s => s.Source.Name == "Microsoft.AspNetCore" && ((s.GetTagItem("url.path") as string)?.StartsWith("/media/", StringComparison.Ordinal) ?? false));
        Assert.StartsWith("/media/", (string?)media.GetTagItem("http.route")); // the route's pattern, the span's name with it
        Assert.DoesNotContain((string)media.GetTagItem("url.path")!, media.DisplayName); // never the id that was asked for

        app.Factory.Services.GetRequiredService<MeterProvider>().ForceFlush();
        Assert.Contains(exported.Metrics, m => m.MeterName == "Microsoft.AspNetCore.Hosting");
        Assert.Contains(exported.Metrics, m => m.MeterName == "Microsoft.EntityFrameworkCore");
        Assert.Contains(app.Log.From<TelemetryReporter>(), e => e.Level == LogLevel.Information && (string?)e.Values["Protocol"] == "grpc" && (string?)e.Values["ServiceName"] == "tankstat");
    }

    private static List<Activity> Own(Exported exported, string name) =>
        [.. exported.Spans.ToList().Where(s => s.Source.Name == AppTraces.SourceName && s.OperationName == name)];

    [Fact]
    public async Task ASyncBatch_IsPartOfItsRequestsTrace_WithASpanPerChange()
    {
        var (app, exported) = Start(On());
        using var _ = app;

        var requests = await Flow(app.NewClient());
        await Settled(app, exported, requests);

        var change = Assert.Single(Own(exported, "tankstat.sync.change"));
        var batch = Assert.Single(Own(exported, "tankstat.sync.batch"));
        Assert.Equal(batch.SpanId, change.ParentSpanId);
        Assert.Contains(exported.Spans, s => s.SpanId == batch.ParentSpanId && s.Source.Name.StartsWith("HotChocolate", StringComparison.Ordinal)); // inside the GraphQL request
        Assert.Equal(("applied", "LogRefueling"), ((string?)change.GetTagItem("tankstat.sync.change.result"), (string?)change.GetTagItem("tankstat.sync.change.kind")));
    }

    [Fact]
    public async Task ABackUpNowRun_IsATraceOfItsOwn_LinkedToTheRequestThatAskedForIt_AndMeasured()
    {
        var (app, exported) = Start(On(("Auth:Mode", "Standalone")));
        using var _ = app;
        var admin = app.NewClient();
        await admin.LoginAs(TestApp.AdminEmail, TestApp.AdminPassword);

        Assert.Null((await admin.Gql("mutation BackUpNow { runBackup }")).ErrorKey());
        await app.Factory.Services.GetRequiredService<BackupRunner>().WhenIdleAsync();
        await Settled(app, exported, 2);
        app.Factory.Services.GetRequiredService<MeterProvider>().ForceFlush();

        var run = Assert.Single(Own(exported, "tankstat.backup.run"));
        Assert.Equal(default, run.ParentSpanId); // it outlives the request: a trace of its own...
        var asked = exported.Spans.First(s => (s.GetTagItem("graphql.operation.name") as string) == "BackUpNow");
        Assert.Equal(asked.TraceId, Assert.Single(run.Links).Context.TraceId); // ...linked to the request that asked for it
        Assert.Equal(("manual", "ok", ActivityStatusCode.Unset), ((string?)run.GetTagItem("tankstat.backup.trigger"), (string?)run.GetTagItem("tankstat.backup.outcome"), run.Status));
        var write = Assert.Single(Own(exported, "tankstat.backup.write"));
        Assert.Equal(run.SpanId, write.ParentSpanId);
        Assert.Equal((false, false), ((bool?)write.GetTagItem("tankstat.backup.encrypted"), (bool?)write.GetTagItem("tankstat.backup.safety_net")));
        Assert.True((long)write.GetTagItem("tankstat.backup.bytes")! > 0);
        Assert.Contains(exported.Metrics, m => m.MeterName == AppMetrics.MeterName && m.Name == "tankstat.backup.duration");
        Assert.Contains(exported.Metrics, m => m.MeterName == AppMetrics.MeterName && m.Name == "tankstat.backup.size");
        NoCanary(exported);
    }

    [Fact]
    public void ARestoreThatWaitsAtStart_IsATraceOfItsOwn_WithItsOutcome()
    {
        var (app, exported) = Start(On());
        using var _ = app;
        Directory.CreateDirectory(app.BackupsPath);
        new RestoreMarker("tankstat-backup-20260101T000000Z.zip", Guid.Empty, DateTimeOffset.UtcNow, null).Write(app.BackupsPath); // a backup that is not there
        app.NewClient(); // the start: refused, and it goes on
        app.Factory.Services.GetRequiredService<TracerProvider>().ForceFlush();

        var restore = Assert.Single(Own(exported, "tankstat.restore.apply"));
        Assert.Equal(default, restore.ParentSpanId);
        Assert.Equal(("refused", ActivityStatusCode.Error, "restore.refused"),
            ((string?)restore.GetTagItem("tankstat.restore.outcome"), restore.Status, (string?)restore.GetTagItem("error.type")));
    }

    [Fact]
    public void AStartThatMigratesTheDatabase_IsATraceOfItsOwn()
    {
        var (app, exported) = Start(On(), migrated: false); // a new database: every migration is pending
        using var _ = app;
        app.NewClient();
        app.Factory.Services.GetRequiredService<TracerProvider>().ForceFlush();

        var migrate = Assert.Single(Own(exported, "tankstat.database.migrate"));
        Assert.Equal(default, migrate.ParentSpanId); // no request carries the start
        Assert.True((int)migrate.GetTagItem("tankstat.database.migrations")! > 1);
        Assert.Equal(ActivityStatusCode.Unset, migrate.Status);
    }

    [Fact]
    public void TheOneTimeCode_IsInTheLog_ButNeverExported_WhateverTheConfigurationAsks()
    {
        // Sign-in off: the start names the restore code. A rule for the exporter's own provider and exactly that category cannot let it out.
        var (app, exported) = Start(On((
            $"Logging:OpenTelemetry:LogLevel:{typeof(OneTimeCode.RestoreLine).FullName!.Replace('+', '.')}", "Information")));
        using var _ = app;
        app.NewClient();
        app.Factory.Services.GetService<LoggerProvider>()?.ForceFlush();

        var code = (string)Assert.Single(app.Log.From<OneTimeCode.RestoreLine>(), e => e.Values.ContainsKey("Code")).Values["Code"]!;
        Assert.NotEmpty(exported.Logs); // the start's other lines are exported
        Assert.DoesNotContain(exported.Logs, l => l.CategoryName!.StartsWith(OneTimeCode.LogCategory, StringComparison.Ordinal));
        Assert.DoesNotContain(Texts(exported), t => t.Text.Contains(code, StringComparison.Ordinal));
    }

    [Fact]
    public void WhileTheSetupWaits_ItsCodeIsNotExportedEither()
    {
        var (app, exported) = Start(On(("Auth:Mode", "Standalone"), ("Setup:Enabled", "true"),
            ($"Logging:OpenTelemetry:LogLevel:{typeof(OneTimeCode.SetupLine).FullName!.Replace('+', '.')}*", "Information"))); // a rule more specific than the exact name
        using var _ = app;
        app.NewClient();
        app.Factory.Services.GetService<LoggerProvider>()?.ForceFlush();

        var code = (string)Assert.Single(app.Log.From<OneTimeCode.SetupLine>(), e => e.Values.ContainsKey("Code")).Values["Code"]!;
        Assert.NotEmpty(exported.Logs); // the start's other lines are there, so the code's absence means something
        Assert.DoesNotContain(exported.Logs, l => l.CategoryName!.StartsWith(OneTimeCode.LogCategory, StringComparison.Ordinal));
        Assert.DoesNotContain(Texts(exported), t => t.Text.Contains(code, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(OtelSettings.Endpoint, "zzzz")]
    [InlineData(OtelSettings.Headers, "zzzz")] // the SDK would throw at start
    [InlineData(OtelSettings.Protocol, "GRPC")]
    public void SettingsThatCannotBeUsed_LeaveItOff_AndTheStartSaysWhichNeverTheirValue(string key, string value)
    {
        var (app, _) = Start(On((key, value)));
        using var __ = app;
        app.NewClient(); // starts: telemetry never stops a start

        Assert.Null(app.Factory.Services.GetService<TracerProvider>());
        var warning = Assert.Single(app.Log.From<TelemetryReporter>(), e => e.Level == LogLevel.Warning);
        Assert.Contains(key, warning.Message);
        if (value != "GRPC") Assert.False(app.Log.Mentions(value), "the value was logged"); // (the protocol's rule names grpc itself)
    }

    [Fact]
    public void TheDatabaseSwitch_WithoutAnEndpoint_DoesNothing_AndTheStartSaysSo()
    {
        var (app, _) = Start(new() { [TelemetryOptions.TraceDatabaseKey] = "true" });
        using var __ = app;
        app.NewClient();

        Assert.Null(app.Factory.Services.GetService<TracerProvider>());
        Assert.Contains(app.Log.From<TelemetryReporter>(), e => e.Level == LogLevel.Warning && (string?)e.Values["Setting"] == TelemetryOptions.TraceDatabaseKey);
    }

    [Fact]
    public void TheRedaction_DropsTheUserAgentAndEveryParameterValue_AndNothingElse()
    {
        using var activity = new Activity("probe").Start();
        activity.SetTag(ClientValueRedaction.UserAgent, UserAgent);
        activity.SetTag("db.query.parameter.@p0", Plate);
        activity.SetTag("db.query.parameter.@name", VehicleName);
        activity.SetTag("url.path", "/graphql");
        activity.SetTag("db.query.text", "SELECT 1");
        activity.SetTag(ClientValueRedaction.UrlQuery, $"?resetToken={TokenInQuery}&lang=hu"); // as the instrumentation records it with its own redaction switched off
        activity.SetTag(ClientValueRedaction.UrlFull, $"https://idp.example/token?code={TokenInQuery}#frag");
        activity.SetTag(ClientValueRedaction.DocumentHash, PersistedHash);

        new ClientValueRedaction().OnEnd(activity);

        Assert.Equal(["url.path", "db.query.text", ClientValueRedaction.UrlQuery, ClientValueRedaction.UrlFull], activity.TagObjects.Select(t => t.Key));
        Assert.Equal("?resetToken=Redacted&lang=Redacted", activity.GetTagItem(ClientValueRedaction.UrlQuery));
        Assert.Equal("https://idp.example/token?code=Redacted#frag", activity.GetTagItem(ClientValueRedaction.UrlFull));
    }

    [Fact]
    public void TheRedaction_LeavesAGraphQLSpanWithAnExceptionEventUnexported_SinceTheExceptionQuotesTheRequest()
    {
        using var source = new ActivitySource("HotChocolate.Diagnostics");
        using var listener = new ActivityListener { ShouldListenTo = s => s.Name == source.Name, Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded };
        ActivitySource.AddActivityListener(listener);
        using var failed = source.StartActivity("GraphQL Operation")!;
        failed.AddException(new InvalidOperationException($"Operation '{OperationNameSent}' not found"));
        using var fine = source.StartActivity("GraphQL Request")!;

        new ClientValueRedaction().OnEnd(failed);
        new ClientValueRedaction().OnEnd(fine);

        Assert.False(failed.Recorded);
        Assert.True(fine.Recorded);
    }

    [Fact]
    public async Task TheQueryStringsValues_StayRedacted_WhateverTheInstrumentationsOwnSwitchesSay()
    {
        var (app, exported) = Start(On(("OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION", "true"), ("OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION", "true")));
        using var _ = app;

        await app.NewClient().GetAsync($"/?resetToken={TokenInQuery}");
        await Settled(app, exported, 1);

        Assert.Contains(exported.Spans, s => (s.GetTagItem(ClientValueRedaction.UrlQuery) as string) == "?resetToken=Redacted");
        Assert.DoesNotContain(Texts(exported), t => t.Text.Contains(TokenInQuery, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheExportersOwnLogRules_CannotLowerTheFloor_NorLetTheOneTimeCodeOut()
    {
        // A provider rule outranks every general one, and a longer category a shorter: these two would otherwise export ASP.NET Core's
        // request lines (with the reset link's token) and the restore code.
        var (app, exported) = Start(On(
            ("Logging:OpenTelemetry:LogLevel:Default", "Information"),
            ("Logging:OpenTelemetry:LogLevel:Microsoft.AspNetCore.Hosting*", "Information"),
            ($"Logging:OpenTelemetry:LogLevel:{typeof(OneTimeCode.RestoreLine).FullName!.Replace('+', '.')}*", "Information")));
        using var _ = app;

        await app.NewClient().GetAsync($"/?resetToken={TokenInQuery}");
        await Settled(app, exported, 1);

        var code = (string)Assert.Single(app.Log.From<OneTimeCode.RestoreLine>(), e => e.Values.ContainsKey("Code")).Values["Code"]!;
        Assert.NotEmpty(exported.Logs); // the start's other lines are exported at Information, as the rule asks
        Assert.DoesNotContain(exported.Logs, l => l.CategoryName!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) && l.LogLevel < LogLevel.Warning);
        Assert.DoesNotContain(exported.Logs, l => l.CategoryName!.StartsWith(OneTimeCode.LogCategory, StringComparison.Ordinal));
        Assert.DoesNotContain(Texts(exported), t => t.Text.Contains(TokenInQuery, StringComparison.Ordinal) || t.Text.Contains(code, StringComparison.Ordinal));
    }

    [Fact]
    public void TheEnvironmentsResourceAttributes_Win_OverTheAppsDefaults()
    {
        Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", "service.name=zz-prod,service.instance.id=zz-pod-7");
        try
        {
            var (app, _) = Start(On(("OTEL_RESOURCE_ATTRIBUTES", "service.name=zz-prod,service.instance.id=zz-pod-7")));
            using var __ = app;
            app.NewClient();

            var resource = app.Factory.Services.GetRequiredService<TracerProvider>().GetResource().Attributes.ToDictionary(a => a.Key, a => a.Value);
            Assert.Equal(("zz-prod", "zz-pod-7"), (resource["service.name"], resource["service.instance.id"]));
            Assert.Equal(HealthReporter.AppVersion, resource["service.version"]); // the app's own stays where the environment says nothing
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", null);
        }
    }
}
