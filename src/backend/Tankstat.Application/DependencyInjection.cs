using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Tankstat.Application.Access;
using Tankstat.Application.Auth;
using Tankstat.Application.Backups;
using Tankstat.Application.Expenses;
using Tankstat.Application.Health;
using Tankstat.Application.Images;
using Tankstat.Application.Photos;
using Tankstat.Application.Imports;
using Tankstat.Application.Notifications;
using Tankstat.Application.Odometers;
using Tankstat.Application.Recognition;
using Tankstat.Application.Recurring;
using Tankstat.Application.Refuelings;
using Tankstat.Application.Settings;
using Tankstat.Application.Setup;
using Tankstat.Application.Sync;
using Tankstat.Application.Telemetry;
using Tankstat.Application.Sharing;
using Tankstat.Application.SystemSettings;
using Tankstat.Application.Stats;
using Tankstat.Application.Telemetry;
using Tankstat.Application.Users;
using Tankstat.Application.Vehicles;
using Tankstat.Application.Tyres;

namespace Tankstat.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<VehicleDefaultsOptions>().Bind(configuration.GetSection(VehicleDefaultsOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<VehicleDefaultsOptions>, VehicleDefaultsOptionsValidator>());
        services.AddOptions<NotificationOptions>().Bind(configuration.GetSection(NotificationOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<NotificationOptions>, NotificationOptionsValidator>());
        services.AddOptions<SyncOptions>().Bind(configuration.GetSection(SyncOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SyncOptions>, SyncOptionsValidator>());
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>());
        services.AddOptions<BackupOptions>().Bind(configuration.GetSection(BackupOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BackupOptions>, BackupOptionsValidator>());
        services.AddOptions<SetupOptions>().Bind(configuration.GetSection(SetupOptions.SectionName)).ValidateOnStart();
        services.AddOptions<KeyRingOptions>().Bind(configuration.GetSection(KeyRingOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.KeysPath), "DataProtection:KeysPath must not be empty.").ValidateOnStart();
        services.AddOptions<SmtpOptions>().Bind(configuration.GetSection(SmtpOptions.SectionName));
        // The password requirements apply at once: bound with a change token, apart from the sign-in below, which waits for a restart.
        services.AddOptions<PasswordOptions>().Bind(configuration.GetSection(PasswordOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PasswordOptions>, PasswordOptionsValidator>());
        services.AddSingleton<PasswordRequirements>();
        // Bound without a change token: the sign-in is read once, at start. A save on the settings page reloads the configuration, and the
        // options monitor ValidateOnStart leaves behind would validate the saved sign-in again then, against the Smtp its validator read at
        // start (Auth:PublicUrl is required while Smtp is configured), and throw out of the save.
        services.AddOptions<AuthOptions>().Configure(o => configuration.GetSection(AuthOptions.SectionName).Bind(o)).ValidateOnStart();
        // Photo reading is optional: its settings are never validated on start (they would stop the app); RecognitionSetup turns
        // reading off instead when they cannot be used.
        services.AddOptions<RecognitionOptions>().Bind(configuration.GetSection(RecognitionOptions.SectionName));
        // Telemetry is optional too, never validated on start: TelemetryGate reads its key before the host is built and only says what it ignores.
        services.AddOptions<TelemetryOptions>().Bind(configuration.GetSection(TelemetryOptions.SectionName));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>());

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<HealthReporter>();
        services.AddSingleton<NoticeService>();
        services.AddSingleton<InstanceGeneration>();
        services.AddAppMetrics();
        services.TryAddSingleton<OneTimeCode>(); // the host's own when it keeps one over a restart in the process
        services.AddSingleton<SetupStatus>();

        services.AddScoped<AccessService>();
        services.AddScoped<SetupGate>();
        services.AddScoped<SetupService>();
        services.AddScoped<RestoreService>();
        services.AddScoped<BackupAdminService>();
        services.AddSingleton<BackupCommandService>(); // the command line runs from the root services, without a scope
        services.AddScoped<BackupFailureNotifier>();
        services.AddScoped<AccessAdminService>();
        services.AddScoped<SessionService>();
        services.AddScoped<PasswordResetService>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserSessionService>();
        services.AddScoped<UserService>();
        services.AddScoped<VehicleService>();
        services.AddScoped<ImageService>();
        services.AddScoped<LogAccessGuard>();
        services.AddScoped<LogPhotoAccess>();
        services.AddScoped<PhotoDraftAccess>();
        services.AddScoped<LogPhotoService>();
        services.AddScoped<PhotoDraftService>();
        services.AddScoped<RefuelingService>();
        services.AddScoped<ExpenseService>();
        services.AddScoped<RecurringExpenseService>();
        services.AddScoped<TyreService>();
        services.AddScoped<TyreExpenseReadings>();
        services.AddScoped<StatsService>();
        services.AddScoped<ChartService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<UiSettingsService>();
        services.AddScoped<VehicleOrderService>();
        services.AddScoped<OfflineSettingsService>();
        services.AddScoped<OfflineFeedService>();
        services.AddScoped<SyncService>();
        services.AddScoped<SyncTargetService>();
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<InstanceStartService>();
        services.AddSingleton<ImportSessionStore>();
        services.AddSingleton<IImportParser, FuelioCsvParser>();
        services.AddScoped<ImportService>();
        services.AddScoped<OdometerService>();
        services.AddScoped<ResourceSharingService>();
        services.AddScoped<Notifier>();
        services.AddScoped<RecurringNotificationSync>();
        services.AddScoped<SyncNotificationSync>();
        services.AddScoped<NotificationService>();
        services.AddSingleton<RecognitionSetup>();
        services.AddSingleton<RecognitionAvailability>();
        services.AddScoped<RecognitionService>();
        services.AddScoped<LogPhotoFiller>();
        services.AddScoped<PhotoReadingProcessor>();
        return services;
    }

    /// <summary>
    /// The app's own metrics (#219), once: through the host's meter factory when there is one (a test's services have none). Called by
    /// <see cref="AddApplication"/> and by <c>AddInfrastructure</c> (the backups record them), so a host of either layer's services alone has them.
    /// </summary>
    public static IServiceCollection AddAppMetrics(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new AppMetrics(sp.GetService<System.Diagnostics.Metrics.IMeterFactory>()));
        return services;
    }
}
