using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Tankstat.Application;
using Tankstat.Application.Access;
using Tankstat.Application.Auth;
using Tankstat.Application.Backups;
using Tankstat.Application.Expenses;
using Tankstat.Application.Images;
using Tankstat.Application.Notifications;
using Tankstat.Application.Odometers;
using Tankstat.Application.Recurring;
using Tankstat.Application.Refuelings;
using Tankstat.Application.Settings;
using Tankstat.Application.Setup;
using Tankstat.Application.Sync;
using Tankstat.Application.SystemSettings;
using Tankstat.Application.Stats;
using Tankstat.Application.Photos;
using Tankstat.Application.Recognition;
using Tankstat.Application.Users;
using Tankstat.Infrastructure.Auth;
using Tankstat.Infrastructure.Backups;
using Tankstat.Infrastructure.Recognition;
using Tankstat.Infrastructure.Settings;
using Tankstat.Infrastructure.Storage;
using Tankstat.Application.Vehicles;
using Tankstat.Infrastructure.Persistence;
using Tankstat.Infrastructure.Persistence.Repositories;

namespace Tankstat.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // The app's own metrics (#219), which the backups record: AddApplication registers them too, a host of these services alone needs them.
        services.TryAddSingleton(sp => new Tankstat.Application.Telemetry.AppMetrics(sp.GetService<System.Diagnostics.Metrics.IMeterFactory>()));
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .PostConfigure(o =>
            {
                if (string.IsNullOrWhiteSpace(o.ConnectionString) && o.Provider == DatabaseProvider.Sqlite)
                    o.ConnectionString = DatabaseOptions.DefaultSqliteConnectionString;
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                "Database:ConnectionString is required unless Database:Provider is Sqlite.")
            .ValidateOnStart();

        // Pooled factory: usable from scoped code and from GraphQL resolvers (which may run in parallel).
        services.AddPooledDbContextFactory<AppDbContext>((sp, builder) =>
        {
            var db = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            // Each provider keeps its migrations in its own assembly (Tankstat.Migrations.<Provider>).
            var migrations = $"Tankstat.Migrations.{db.Provider}";
            // Refuelings only ever load through their (query-filtered) vehicle, so the filter on the required end is intended.
            builder.ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
            // What devices download for offline use: UpdatedAt on every save, a tombstone for every removal (see SyncInterceptor).
            builder.AddInterceptors(new SyncInterceptor(sp.GetService<TimeProvider>() ?? TimeProvider.System));
            switch (db.Provider)
            {
                case DatabaseProvider.Sqlite:
                    builder.UseSqlite(db.ConnectionString!, o => o.MigrationsAssembly(migrations)).AddInterceptors(SqliteUnicodeLower.Instance);
                    break;
                case DatabaseProvider.PostgreSql: builder.UseNpgsql(db.ConnectionString!, o => o.MigrationsAssembly(migrations)); break;
                case DatabaseProvider.SqlServer: builder.UseSqlServer(db.ConnectionString!, o => o.MigrationsAssembly(migrations)); break;
                case DatabaseProvider.MySql: builder.UseMySQL(db.ConnectionString!, o => o.MigrationsAssembly(migrations)); break;
                default: throw new InvalidOperationException($"Unsupported database provider '{db.Provider}'.");
            }
        });

        services.AddSingleton<IDatabaseProbe, EfDatabaseProbe>();
        services.AddScoped<IVehicleRepository, VehicleRepository>();
        services.AddScoped<IRefuelingRepository, RefuelingRepository>();
        services.AddScoped<IExpenseRepository, ExpenseRepository>();
        services.AddScoped<IRecurringExpenseRepository, RecurringExpenseRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IStatsRepository, StatsRepository>();
        services.AddScoped<IVehicleChartRepository, VehicleChartRepository>();
        services.AddScoped<IUiSettingsRepository, UiSettingsRepository>();
        services.AddScoped<IVehicleOrderRepository, VehicleOrderRepository>();
        services.AddScoped<IOfflineSettingsRepository, OfflineSettingsRepository>();
        services.AddScoped<IOfflineFeedRepository, OfflineFeedRepository>();
        services.AddScoped<ISyncChangeRepository, SyncChangeRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserDataRepository, UserDataRepository>();
        services.AddScoped<IPasswordResetTokenRepository, PasswordResetTokenRepository>();
        services.AddScoped<IUserSessionRepository, UserSessionRepository>();
        services.AddScoped<IAccessGrantRepository, AccessGrantRepository>();
        services.AddScoped<IAccessSettingsRepository, AccessSettingsRepository>();
        services.AddScoped<IInstanceStateRepository, InstanceStateRepository>();
        services.AddScoped<ISetupStore, SetupStore>();
        services.AddScoped<IResourceGrantRepository, ResourceGrantRepository>();
        services.AddScoped<IOdometerReadingRepository, OdometerReadingRepository>();
        services.AddScoped<IImageRepository, ImageRepository>();
        services.AddScoped<ILogPhotoRepository, LogPhotoRepository>();
        services.AddScoped<IPhotoDraftRepository, PhotoDraftRepository>();
        services.AddScoped<IPhotoReadingRepository, PhotoReadingRepository>();
        services.AddSingleton<IImageStore, FileSystemImageStore>();
        services.AddSingleton<IBackupWriter, BackupWriter>();
        // The settings file and the restart loop are the Api's (Program.cs registers both first); a host of the backups alone has neither.
        services.TryAddSingleton<ISettingsBackup, NoSettingsBackup>();
        services.TryAddSingleton<IServerRestart, NoServerRestart>();
        services.AddSingleton<IBackupStore, FileBackupStore>();
        services.AddSingleton<BackupRunner>();
        services.AddSingleton<IBackupRunner>(sp => sp.GetRequiredService<BackupRunner>());
        services.AddSingleton<IBackupKeyMaker, BackupKeyMaker>();
        services.AddSingleton<IDatabaseFacts, EfDatabaseFacts>();
        services.AddSingleton<IStorageUsage, StorageUsage>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        // Self-service reset mails go out after the answer (PasswordResetMailer below), so the answer tells no accounts apart.
        services.AddSingleton(_ => new PasswordResetQueue(PasswordResetQueue.DefaultCapacity));
        services.AddSingleton<IPasswordResetQueue>(sp => sp.GetRequiredService<PasswordResetQueue>());

        // Photo reading (optional): the model provider the settings choose, or one that never reads (RecognitionSetup explains why).
        // No client-wide timeout (its default of 100 s would cut OpenAiCompatible:TimeoutSeconds short): every request brings its own.
        services.AddHttpClient(OpenAiCompatibleRecognitionProvider.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler(sp => new OpenAiTrafficHandler(sp.GetRequiredService<RecognitionSetup>().Options.OpenAiCompatible, sp.GetRequiredService<ILogger<OpenAiTrafficHandler>>()));
        services.AddSingleton<IRecognitionProvider>(sp => sp.GetRequiredService<RecognitionSetup>() is { Enabled: true, Kind: RecognitionProviderKind.OpenAiCompatible } setup
            ? new OpenAiCompatibleRecognitionProvider(
                sp.GetRequiredService<IHttpClientFactory>(), setup.Options.OpenAiCompatible, setup.SystemPrompt, sp.GetRequiredService<ILogger<OpenAiCompatibleRecognitionProvider>>())
            : NullRecognitionProvider.Instance);
        services.AddSingleton<RecognitionSignal>();
        services.AddSingleton<IRecognitionSignal>(sp => sp.GetRequiredService<RecognitionSignal>());

        // The discovery document of an OIDC authority an administrator typed: a short wait, a small answer, and no redirects. The handler's own
        // backchannel would follow one; the probe does not, on purpose: the address that is saved must be the one that answers. It asks with
        // the handler's User-Agent, so a firewall in front of the provider that turns away requests without one treats it as it will the handler.
        services.AddHttpClient<IOidcDiscovery, OidcDiscovery>(client =>
            {
                client.Timeout = OidcDiscovery.Timeout;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(OidcDiscovery.UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        // Order matters: the database first (DatabaseMigrator: a waiting restore, which sets the database up itself, else a backup when
        // migrations are pending, then the migrations), then the restore step's generation (the restored data's, stamped once the migrations
        // ran; before anything looks at the data, and the setup step finds the restored users and setup state), then move the photos kept with
        // the pictures once they have a folder of their own, then decide the setup (SetupStep; for Standalone without an administrator it
        // reopens the setup, or stops the start with the setup turned off: only the setup page makes the first administrator), then sign
        // everybody out when the authentication mode
        // changed since the last start (after what can still fail the start, so a start that never served a request records no mode, and after
        // the restore, so sessions a backup of another mode brought back end at this start; still before the server listens); the photo reading
        // worker needs the migrated database. The backup runner starts nothing; it comes first so that it stops last, waiting for a backup under
        // way once everything else stopped.
        services.AddHostedService(sp => sp.GetRequiredService<BackupRunner>());
        services.AddSingleton<RestoreStep>();
        services.AddSingleton<DatabaseMigrator>();
        services.AddHostedService(sp => sp.GetRequiredService<DatabaseMigrator>());
        services.AddHostedService(sp => sp.GetRequiredService<RestoreStep>());
        services.AddHostedService<PhotosMove>();
        services.AddHostedService<SetupStep>();
        services.AddHostedService<ModeChangeSignOut>();
        services.AddHostedService<RecognitionWorker>();
        services.AddHostedService<PasswordResetMailer>();
        services.AddHostedService<BackupWorker>();

        return services;
    }
}
