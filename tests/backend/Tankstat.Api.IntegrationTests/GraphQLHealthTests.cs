using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tankstat.Infrastructure.Persistence;

namespace Tankstat.Api.IntegrationTests;

/// <summary>
/// In-process host: real DI, HotChocolate pipeline, EF Core and migrations on a throwaway SQLite file.
/// The database is configured through environment variables, the same mechanism that overrides appsettings.json.
/// </summary>
public sealed class ApiFixture : IDisposable
{
    private const string ProviderVar = "Database__Provider";
    private const string ConnectionVar = "Database__ConnectionString";
    private const string ModeVar = "Auth__Mode";
    private const string StorageVar = "Storage__Path";
    private const string PhotosVar = "Storage__PhotosPath";
    private const string SettingsVar = "Settings__Path";
    private const string BackupsVar = "Backups__Path";

    /// <summary>A schedule or retention of the developer's would make a start write and prune backups: none here.</summary>
    private static readonly string[] ScheduleVars = ["Backups__Every", "Backups__Days", "Backups__Keep", "Backups__KeepDays"];

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tankstat-it-{Guid.NewGuid():N}.db");

    public string UploadsPath { get; } = Path.Combine(Path.GetTempPath(), $"tankstat-it-{Guid.NewGuid():N}-uploads");

    public string PhotosPath { get; } = Path.Combine(Path.GetTempPath(), $"tankstat-it-{Guid.NewGuid():N}-photos");

    /// <summary>A settings file of the run's own (none exists): never a developer's.</summary>
    public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), $"tankstat-it-{Guid.NewGuid():N}-settings.json");

    /// <summary>A backups folder of the run's own: never a developer's.</summary>
    public string BackupsPath { get; } = Path.Combine(Path.GetTempPath(), $"tankstat-it-{Guid.NewGuid():N}-backups");

    public WebApplicationFactory<Program> Factory { get; }

    public ApiFixture()
    {
        Environment.SetEnvironmentVariable(ProviderVar, "Sqlite");
        Environment.SetEnvironmentVariable(ConnectionVar, $"Data Source={_path}");
        Environment.SetEnvironmentVariable(ModeVar, "None"); // these tests are about no-auth; appsettings.json may say otherwise
        Environment.SetEnvironmentVariable(StorageVar, UploadsPath);
        Environment.SetEnvironmentVariable(PhotosVar, PhotosPath);
        Environment.SetEnvironmentVariable(SettingsVar, SettingsPath);
        Environment.SetEnvironmentVariable(BackupsVar, BackupsPath);
        foreach (var name in ScheduleVars) Environment.SetEnvironmentVariable(name, null);
        // Nor a telemetry endpoint of the developer's or the runner's: cleared, never set (the variables are the process's, and the telemetry
        // tests in it set theirs through their own host's settings).
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(n => n.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Telemetry__", StringComparison.OrdinalIgnoreCase)).ToList())
            Environment.SetEnvironmentVariable(name, null);
        Factory = new WebApplicationFactory<Program>();
    }

    public void Dispose()
    {
        Factory.Dispose();
        Environment.SetEnvironmentVariable(ProviderVar, null);
        Environment.SetEnvironmentVariable(ConnectionVar, null);
        Environment.SetEnvironmentVariable(ModeVar, null);
        Environment.SetEnvironmentVariable(StorageVar, null);
        Environment.SetEnvironmentVariable(PhotosVar, null);
        Environment.SetEnvironmentVariable(SettingsVar, null);
        Environment.SetEnvironmentVariable(BackupsVar, null);
        File.Delete(SettingsPath);
        foreach (var folder in new[] { UploadsPath, PhotosPath, BackupsPath })
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        // Only this database's pooled connections: ClearAllPools would also close the ones of tests running in parallel.
        using (var pooled = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}")) Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pooled);
        File.Delete(_path);
    }
}

/// <summary>One shared host/database for all API integration tests (env vars are process-global).</summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "Api";
}

[Collection(ApiCollection.Name)]
public class GraphQLHealthTests(ApiFixture api)
{
    [Fact]
    public void EnvironmentVariables_OverrideAppSettings()
    {
        var options = api.Factory.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;

        Assert.Equal(DatabaseProvider.Sqlite, options.Provider);
        Assert.StartsWith("Data Source=", options.ConnectionString);
        Assert.Contains("tankstat-it-", options.ConnectionString);
    }

    [Fact]
    public async Task HealthQuery_ReturnsOk()
    {
        var response = await api.Factory.CreateClient().PostAsJsonAsync("/graphql",
            new { query = "{ health { status databaseReachable } }" });

        response.EnsureSuccessStatusCode();
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("health");
        Assert.Equal("ok", data.GetProperty("status").GetString());
        Assert.True(data.GetProperty("databaseReachable").GetBoolean());
    }

    [Fact]
    public async Task UnknownField_ReturnsGraphQLError()
    {
        var response = await api.Factory.CreateClient().PostAsJsonAsync("/graphql", new { query = "{ nope }" });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0);
    }
}
