using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tankstat.Application;
using Tankstat.Application.Images;
using Tankstat.Infrastructure;
using Tankstat.Infrastructure.Persistence;
using Tankstat.Infrastructure.Settings;
using Tankstat.Seeder.Pictures;

namespace Tankstat.Seeder;

public static class SeederApp
{
    public const int Success = 0, Refused = 1, Failed = 2;

    /// <param name="config">Environment-style settings (Database:*, Auth:Mode, Settings:Path); the app's own settings file sits below them, command-line options above.</param>
    public static async Task<int> RunAsync(
        string[] args, IConfiguration config, TextWriter output, TextReader input, TimeProvider clock, CancellationToken ct = default)
    {
        SeedOptions? options;
        try
        {
            options = SeedOptionsParser.Parse(args);
        }
        catch (SeedOptionsException e)
        {
            await output.WriteLineAsync($"Error: {e.Message}");
            return Refused;
        }
        if (options is null)
        {
            await output.WriteLineAsync(SeedOptionsParser.Usage);
            return Success;
        }

        // The app's own settings file counts as it does in the app: below the environment, so a mode set on the settings page is seen too.
        var settingsFile = Path.GetFullPath(new[] { options.SettingsPath, config["Settings:Path"] }.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "settings.json"); // blank = unset, as in the app
        var settings = new Dictionary<string, string?>();
        if (options.Provider is not null) settings["Database:Provider"] = options.Provider;
        if (options.ConnectionString is not null) settings["Database:ConnectionString"] = options.ConnectionString;
        if (options.UploadsPath is not null) settings["Storage:Path"] = options.UploadsPath; // the pictures folder: emptied, and where seeded pictures go
        IConfigurationRoot effective;
        try
        {
            effective = new ConfigurationBuilder()
                .Add(SettingsFileStore.SourceFor(settingsFile)) // as the app reads it, a name starting with a dot included
                .AddConfiguration(config)
                .AddInMemoryCollection(settings)
                .Build();
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"Error: the settings file {settingsFile} cannot be read, or is not valid JSON.");
            return Refused;
        }

        // This tool exists to fill a test database for the no-authentication setup; never for anything with real users.
        var mode = effective["Auth:Mode"];
        if (!string.IsNullOrWhiteSpace(mode) && !mode.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync($"Refusing to run: Auth:Mode is '{mode}'. The seeder is for authentication mode None only (it creates no users).");
            return Refused;
        }

        ServiceProvider services;
        try
        {
            services = new ServiceCollection().AddLogging().AddApplication(effective).AddInfrastructure(effective).BuildServiceProvider(validateScopes: true);
        }
        catch (Exception e)
        {
            await output.WriteLineAsync($"Error: invalid database settings: {e.Message}");
            return Refused;
        }

        await using var _ = services;
        var target = Describe(services);
        // With pictures, the folder they are written to is the one emptied, the app's default included, so a seed again leaves none behind.
        var uploads = options.UploadsPath ?? effective["Storage:Path"]
            ?? (options.Pictures != SeedPictures.None ? services.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value.Path : null);
        // The photos of logs, when they have a folder of their own; unset or blank, they are with the pictures, as in the app.
        var photos = new[] { options.PhotosPath, effective["Storage:PhotosPath"] }.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        await output.WriteLineAsync($"Target database: {target}");
        if (!string.IsNullOrWhiteSpace(uploads)) await output.WriteLineAsync($"Uploaded pictures folder (deleted too): {Path.GetFullPath(uploads)}");
        if (!string.IsNullOrWhiteSpace(photos)) await output.WriteLineAsync($"Photos of logs folder (deleted too): {Path.GetFullPath(photos)}");
        await output.WriteLineAsync(
            $"This will DELETE it and create it again with {options.Vehicles:N0} vehicles, {options.Trashed:N0} in the trash, {options.RefuelingsPerVehicle} refuelings, {options.RecurringPerVehicle} recurring expenses (with their past visits) and {options.ExpensesPerVehicle} one-off expenses each, and {options.Notifications:N0} notifications.");
        if (options.Pictures == SeedPictures.Photos)
            await output.WriteLineAsync($"Every vehicle gets a photo of its model from Wikimedia Commons (kept in {Path.GetFullPath(PicturesCache(options))}), else a drawn one.");
        else if (options.Pictures == SeedPictures.Drawn) await output.WriteLineAsync("Every vehicle gets a drawn picture.");

        if (!options.AssumeYes)
        {
            await output.WriteAsync("Type 'yes' to continue: ");
            var answer = await input.ReadLineAsync(ct);
            if (!string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                await output.WriteLineAsync("Cancelled; nothing was changed.");
                return Refused;
            }
        }

        try
        {
            var watch = Stopwatch.StartNew();
            // Pictures of the old data would be orphaned: nothing points to them once the database is recreated.
            foreach (var folder in new[] { uploads, photos })
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            var seeder = new DatabaseSeeder(services.GetRequiredService<IDbContextFactory<AppDbContext>>(), new DataGenerator(clock));
            var result = await seeder.RecreateAndSeedAsync(options, ct);
            var pictures = options.Pictures == SeedPictures.None ? null : await AddPicturesAsync(services, options, clock, ct);
            await output.WriteLineAsync(
                $"Done in {watch.Elapsed.TotalSeconds:F1}s: {result.Vehicles:N0} vehicles, {result.Trashed:N0} in the trash, {result.Refuelings:N0} refuelings, " +
                $"{result.Recurring:N0} recurring expenses, {result.Expenses:N0} expenses, {result.TyreSets:N0} tyre sets, {result.Notifications:N0} notifications (seed {options.RandomSeed})" +
                (pictures is null ? "." : $"; pictures: {pictures.Photos:N0} photos, {pictures.Drawn:N0} drawn."));
            return Success;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await output.WriteLineAsync($"Seeding failed: {e.Message}");
            return Failed;
        }
    }

    private static string PicturesCache(SeedOptions options) =>
        options.PicturesCache ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tankstat-seeder", "pictures");

    private static async Task<PicturesResult> AddPicturesAsync(IServiceProvider services, SeedOptions options, TimeProvider clock, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(CommonsPictures.UserAgent);
        var photos = options.Pictures == SeedPictures.Photos ? new CommonsPictures(http, PicturesCache(options)) : null;
        var pictures = new VehiclePictures(services.GetRequiredService<IDbContextFactory<AppDbContext>>(), services.GetRequiredService<IImageStore>(), clock, photos);
        return await pictures.AddAsync(options.RandomSeed, ct);
    }

    /// <summary>The provider and connection string with any password hidden.</summary>
    private static string Describe(IServiceProvider services)
    {
        var db = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>().Value;
        var parts = (db.ConnectionString ?? "").Split(';').Where(p => !p.TrimStart().StartsWith("Password", StringComparison.OrdinalIgnoreCase) && !p.TrimStart().StartsWith("Pwd", StringComparison.OrdinalIgnoreCase));
        return $"{db.Provider} ({string.Join(';', parts)})";
    }
}
