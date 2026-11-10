using HotChocolate.Execution;
using Tankstat.Api;
using Tankstat.Api.Auth;
using Tankstat.Api.Backups;
using Tankstat.Api.Media;
using Tankstat.Api.GraphQL;
using Tankstat.Api.Settings;
using Tankstat.Api.Telemetry;
using Tankstat.Application;
using Tankstat.Application.Backups;
using Tankstat.Application.SystemSettings;
using Tankstat.Infrastructure;
using Tankstat.Infrastructure.Backups;
using Tankstat.Infrastructure.Settings;

// Build and run, and again when an administrator restarts the server (ServerRestart): in this process, never by exiting, since Docker's
// default restart policy leaves a container that exited cleanly stopped. Under WebApplicationFactory the first Build() throws
// HostAbortedException out of here, so nothing may catch around it (the using below lets go of the signals on the way out).
var restarts = new RestartRequests();
using var stopSignals = restarts.EndOnStopSignals(); // docker stop during a restart still ends the process
do
{
    restarts.Reset();
    // A command's flags are no configuration (its parser refuses anything else): read as such, `backup --environment merge` would set the
    // hosting environment and leave appsettings.{Environment}.json out of that run.
    var builder = WebApplication.CreateBuilder(BackupCommands.Handles(args) ? [] : args);
    // The app's own settings file joins the configuration below the environment variables, unless the app could not start with it (see SettingsFile).
    var settingsFile = SettingsFile.Prepare(builder);
    builder.Services.AddSingleton(settingsFile);
    builder.Services.AddSingleton(settingsFile.Status);
    builder.Services.AddSingleton<ISettingsFileStore>(new SettingsFileStore(settingsFile.Status.Path));
    builder.Services.AddSingleton<ISystemConfiguration, SystemConfiguration>();
    builder.Services.AddSingleton<ISettingsBackup, SettingsBackup>(); // before AddInfrastructure, whose default (no file) steps aside then
    builder.Services.AddSingleton<IServerRestart>(sp => new ServerRestart(restarts, sp.GetRequiredService<IHostApplicationLifetime>(), sp.GetRequiredService<ILogger<ServerRestart>>())); // plain Add: a test host replaces it
    builder.Services.AddSingleton(restarts.Code); // the one-time code outlives a restart in the process
    builder.Services.AddHostedService<SettingsFileReporter>();
    builder.Services.AddApplication(builder.Configuration);
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddAuthModes();
    builder.Services.AddScoped<SyncChangeServices>();
    builder.Services.AddGraphQLServer()
        .AddQueryType<Query>()
        .AddMutationType<Mutation>()
        .AddTypeExtension<SessionQueries>()
        .AddTypeExtension<AuthMutations>()
        .AddTypeExtension<BackupMutations>()
        .AddTypeExtension<BackupQueries>()
        .AddTypeExtension<BackupOverviewInfoExtensions>()
        .AddTypeExtension<SystemSettingsInfoExtensions>()
        .AddTypeExtension<DeviceSessionQueries>()
        .AddTypeExtension<DeviceSessionMutations>()
        .AddTypeExtension<SetupQueries>()
        .AddTypeExtension<SetupMutations>()
        .AddType<VehicleType>()
        .AddType<RefuelingType>()
        .AddTypeExtension<VehicleExtensions>()
        .AddTypeExtension<RefuelingExtensions>()
        .AddTypeExtension<RefuelingQueries>()
        .AddTypeExtension<RefuelingMutations>()
        .AddType<ExpenseType>()
        .AddTypeExtension<ExpenseExtensions>()
        .AddTypeExtension<VehicleExpenseExtensions>()
        .AddDataLoader<ExpensePhotosLoader>()
        .AddDataLoader<LogLevelByVehicleLoader>()
        .AddDataLoader<UserRefLoader>()
        .AddDataLoader<VehicleIncludingDeletedLoader>()
        .AddDataLoader<PhotoReadingsLoader>()
        .AddTypeExtension<LogPhotoReadingExtensions>()
        .AddDataLoader<RefuelingPhotosLoader>()
        .AddTypeExtension<ExpensePhotoExtensions>()
        .AddTypeExtension<RefuelingPhotoExtensions>()
        .AddTypeExtension<ExpenseQueries>()
        .AddTypeExtension<ExpenseMutations>()
        .AddDataLoader<RecurringByVehicleLoader>()
        .AddDataLoader<ExpenseSchedulesLoader>()
        .AddTypeExtension<ExpenseRecurringExtensions>()
        .AddTypeExtension<VehicleRecurringExtensions>()
        .AddTypeExtension<RecurringMutations>()
        .AddTypeExtension<RecurringExpenseInfoExtensions>()
        .AddType<TyreSetType>()
        .AddType<TyreStretchType>()
        .AddType<TyreLogDayType>()
        .AddType<VehicleTyresType>()
        .AddType<TyreSetViewType>()
        .AddType<TyreStretchViewType>()
        .AddTypeExtension<TyreSetExtensions>()
        .AddTypeExtension<TyreTrashRowExtensions>()
        .AddTypeExtension<TyreQueries>()
        .AddTypeExtension<TyreMutations>()
        .AddTypeExtension<NotificationQueries>()
        .AddTypeExtension<RecognitionQueries>()
        .AddTypeExtension<NotificationMutations>()
        .AddTypeExtension<ImportQueries>()
        .AddTypeExtension<ImportMutations>()
        .AddType<VehicleChartType>()
        .AddType<TrendPeriodType>()
        .AddType<ChartConfigType>()
        .AddType<FigureRecipeType>()
        .AddType<TileRecipeType>()
        .AddTypeExtension<SavedWidgetExtensions>()
        .AddTypeExtension<VehicleChartExtensions>()
        .AddTypeExtension<VehicleSummaryExtensions>()
        .AddTypeExtension<DashboardQueries>()
        .AddTypeExtension<DashboardMutations>()
        .AddTypeExtension<UiSettingsQueries>()
        .AddTypeExtension<UiSettingsInfoExtensions>()
        .AddTypeExtension<UiSettingsMutations>()
        .AddTypeExtension<OfflineQueries>()
        .AddTypeExtension<OfflineMutations>()
        .AddTypeExtension<SyncMutations>()
        .AddTypeExtension<SyncQueries>()
        .AddTypeExtension<SyncChangeInfoExtensions>()
        .AddTypeExtension<SyncCurrentExtensions>()
        .AddTypeExtension<VehicleSyncExtensions>()
        .AddTypeExtension<SystemSettingsQueries>()
        .AddTypeExtension<SystemSettingsMutations>()
        .AddDataLoader<ParkedChangeCountLoader>()
        .AddDataLoader<SyncCurrentLoader>()
        .AddTypeExtension<VehicleOfflineExtensions>()
        .AddDataLoader<LogCountSinceLoader>()
        .AddErrorFilter<BusinessErrorFilter>()
        // HotChocolate does not log what goes wrong in a request. The listener is built from the schema's own services, so what it needs from
        // the application is handed over.
        .AddApplicationService<ILoggerFactory>()
        .AddApplicationService<IHttpContextAccessor>()
        .AddDiagnosticEventListener<GraphQLLoggingListener>();
    // OpenTelemetry over OTLP, registered only with an endpoint configured (docs/configuration.md, Telemetry); never for the commands that
    // build the app without serving it.
    if (!BackupCommands.Handles(args) && args is not ["schema", "export", ..]) builder.AddTelemetry();

    var app = builder.Build();

    app.UseAuthentication();
    app.UseUserLogScope();
    app.MapAuthEndpoints();
    app.MapMediaEndpoints();
    app.MapImportEndpoints();
    app.MapGraphQL(); // POST /graphql (Banana Cake Pop UI on GET in Development)

    // Self-hosting: the built SPA (dist/) is copied into wwwroot and served by Kestrel. Hashed assets may be kept for good, everything with
    // a fixed name (index.html, the service worker, the manifest) is revalidated, so a release reaches every browser on its next start.
    var staticFiles = new StaticFileOptions
    {
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = StaticCaching.HeaderFor(ctx.Context.Request.Path),
    };
    app.UseDefaultFiles();
    app.UseStaticFiles(staticFiles);
    app.MapFallbackToFile("index.html", staticFiles);

    // `dotnet run -- schema export --output schema.graphql` writes the GraphQL schema for client code generation
    // (see `mise run schema:export`) without starting the server.
    if (args is ["schema", "export", ..])
    {
        var output = args is [_, _, "--output", var path, ..] ? path : "schema.graphql";
        var executor = await app.Services.GetRequiredService<IRequestExecutorProvider>().GetExecutorAsync();
        await File.WriteAllTextAsync(output, executor.Schema.ToString() + Environment.NewLine);
        return;
    }

    // `dotnet Tankstat.Api.dll backup ...` and `decrypt ...` write and open backups the same way (see BackupCommands and docs/backup-and-restore.md).
    if (BackupCommands.Handles(args))
    {
        // A passphrase typed at a terminal shows nothing; piped in (a script, `docker exec -i`), it is read a line at a time.
        var prompt = Console.IsInputRedirected ? null : new TerminalPrompt(Console.Error);
        Environment.ExitCode = await BackupCommands.RunAsync(args, app.Services, Console.In, Console.Out, Console.Error, prompt);
        return;
    }

    // A restore that waits is applied before the host starts, as `restore --now` applies it (the migrator, which restores first, then the
    // restore step): when it writes the backup's settings file, the next pass of the loop builds the host that reads it, so no start step
    // and no request ever ran under the file it replaced. The host's own start then finds both steps done. (Under WebApplicationFactory
    // nothing after Build() runs: the host's start applies the restore, see RestoreStep.)
    if (app.Services.GetRequiredService<IBackupStore>().RestorePending)
    {
        // The options the host validates before any start step, validated first here too: a wrong folder stops the start before the restore
        // replaces anything, as it did when the host's start applied it.
        app.Services.GetService<Microsoft.Extensions.Options.IStartupValidator>()?.Validate();
        app.Services.StartBeforeHost(); // telemetry's providers, so the restore's own span is exported (#219)
        await StartupSteps.ApplyBeforeHostAsync(app.Services, CancellationToken.None);
        if (restarts.Requested)
        {
            await app.DisposeAsync();
            continue;
        }
    }

    try
    {
        app.Run();
    }
    catch (ServerRestartException) when (restarts.Requested)
    {
        // A start step asked for the next host at once (a restore that wrote the settings file): nothing after it started, the loop builds
        // the next one, which reads the file.
    }
} while (restarts.Requested);

// Exposed for WebApplicationFactory<Program> in the integration tests.
public partial class Program;
