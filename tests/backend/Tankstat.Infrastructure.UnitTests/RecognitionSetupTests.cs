using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tankstat.Application;
using Tankstat.Application.Recognition;
using Tankstat.Infrastructure.Recognition;
using Tankstat.TestSupport;

namespace Tankstat.Infrastructure.UnitTests;

/// <summary>The Recognition settings as the app binds them: photo reading is optional, so no setting may ever stop the app.</summary>
public class RecognitionSetupTests
{
    private static (ServiceProvider Services, CapturedLog Log) Build(Dictionary<string, string?> settings)
    {
        var all = new Dictionary<string, string?> { ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = "Data Source=unused.db" };
        foreach (var (key, value) in settings) all[key] = value;
        var config = new ConfigurationBuilder().AddInMemoryCollection(all).Build();
        var log = new CapturedLog();
        var services = new ServiceCollection().AddLogging(b => b.AddProvider(log)).AddApplication(config).AddInfrastructure(config).BuildServiceProvider();
        return (services, log);
    }

    [Fact]
    public async Task WithoutSettings_PhotosAreNotRead()
    {
        var (services, _) = Build([]);
        await using var _s = services;

        var provider = services.GetRequiredService<IRecognitionProvider>();

        Assert.IsType<NullRecognitionProvider>(provider);
        Assert.Equal(("none", false), (provider.Name, provider.IsConfigured));
        Assert.False(await provider.IsHealthyAsync(default)); // so nothing is ever claimed for reading
        var request = new RecognitionRequest(new byte[] { 1 }, "image/jpeg", new HashSet<Tankstat.Domain.Recognition.DocumentKind> { Tankstat.Domain.Recognition.DocumentKind.Odometer }, "en", null, null, new DateOnly(2026, 10, 1));
        await Assert.ThrowsAsync<RecognitionUnavailableException>(() => provider.ReadAsync(request, default));
    }

    [Fact]
    public async Task WakingTheWorker_TwiceBeforeItLooks_WakesItOnce()
    {
        var signal = new RecognitionSignal();

        signal.Wake();
        signal.Wake(); // already woken: not an error

        await signal.WaitAsync(Timeout.InfiniteTimeSpan, default).WaitAsync(TimeSpan.FromSeconds(10)); // woken
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signal.WaitAsync(Timeout.InfiniteTimeSpan, stop.Token)); // the second wake queued no other round
    }

    [Fact]
    public void WithAModelServersSettings_TheModelReadsThem_WithThePromptTheOperatorGave()
    {
        var (services, _) = Build(new()
        {
            ["Recognition:Provider"] = "OpenAiCompatible",
            ["Recognition:OpenAiCompatible:BaseUrl"] = "http://localhost:1234/v1",
            ["Recognition:OpenAiCompatible:Model"] = "qwen2.5-vl",
            ["Recognition:OpenAiCompatible:SystemPrompt"] = "Read it your way.",
            ["Recognition:OpenAiCompatible:ResponseFormat"] = "JsonObject",
            ["Recognition:MinConfidence"] = "0.7",
        });
        using var _s = services;

        Assert.IsType<OpenAiCompatibleRecognitionProvider>(services.GetRequiredService<IRecognitionProvider>());
        var setup = services.GetRequiredService<RecognitionSetup>();
        Assert.Equal(("Read it your way.", OpenAiResponseFormat.JsonObject, 0.7), (setup.SystemPrompt, setup.Options.OpenAiCompatible.ResponseFormat, setup.Options.MinConfidence));
    }

    [Fact]
    public void ThePromptFile_IsReadThroughInfrastructure_AndAMissingOneTurnsReadingOffWithAWarning()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, " Read it from the file.\n");
            var settings = new Dictionary<string, string?>
            {
                ["Recognition:Provider"] = "OpenAiCompatible",
                ["Recognition:OpenAiCompatible:BaseUrl"] = "http://localhost:1234/v1",
                ["Recognition:OpenAiCompatible:Model"] = "qwen2.5-vl",
                ["Recognition:OpenAiCompatible:SystemPromptFile"] = file,
            };
            var (services, _) = Build(settings);
            using (services) Assert.Equal("Read it from the file.", services.GetRequiredService<RecognitionSetup>().SystemPrompt);

            settings["Recognition:OpenAiCompatible:SystemPromptFile"] = file + ".missing";
            var (without, _) = Build(settings);
            using var _w = without;
            var setup = without.GetRequiredService<RecognitionSetup>();
            Assert.False(setup.Enabled);
            Assert.Contains("SystemPromptFile does not exist", Assert.Single(setup.Problems));
            Assert.IsType<NullRecognitionProvider>(without.GetRequiredService<IRecognitionProvider>());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void TheModelServersClient_LeavesTheTimeoutToTheSettings()
    {
        var (services, _) = Build([]);
        using var _s = services;

        // TimeoutSeconds goes up to 600; the client's own default (100 s) must not cut it short.
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(OpenAiCompatibleRecognitionProvider.ClientName);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Theory]
    [InlineData("Recognition:OpenAiCompatible:Model", "", "Model")] // a missing model
    [InlineData("Recognition:OpenAiCompatible:BaseUrl", "localhost:1234/v1", "BaseUrl")] // not an address
    [InlineData("Recognition:MaxConcurrent", "many", "cannot be read")] // not even a number: binding fails
    [InlineData("Recognition:OpenAiCompatible:ResponseFormat", "Yaml", "cannot be read")]
    [InlineData("Recognition:OpenAiCompatible:LogTraffic", "maybe", "cannot be read")]
    public async Task SettingsThatCannotBeUsed_TurnReadingOff_WithAWarning_InsteadOfStoppingTheApp(string key, string value, string said)
    {
        var (services, log) = Build(new()
        {
            ["Recognition:Provider"] = "OpenAiCompatible",
            ["Recognition:OpenAiCompatible:BaseUrl"] = "http://localhost:1234/v1",
            ["Recognition:OpenAiCompatible:Model"] = "qwen2.5-vl",
            [key] = value,
        });
        await using var _s = services;
        var worker = services.GetServices<IHostedService>().OfType<RecognitionWorker>().Single();

        await worker.StartAsync(default);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)); // .NET runs it in the background: it must return at once on its own

        Assert.IsType<NullRecognitionProvider>(services.GetRequiredService<IRecognitionProvider>());
        Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(said, warning.Message);
    }

    private static Dictionary<string, string?> NothingListensThere(string? logTraffic) => new()
    {
        ["Recognition:Provider"] = "OpenAiCompatible",
        ["Recognition:OpenAiCompatible:BaseUrl"] = "http://127.0.0.1:9/v1",
        ["Recognition:OpenAiCompatible:Model"] = "qwen2.5-vl",
        ["Recognition:OpenAiCompatible:LogTraffic"] = logTraffic,
    };

    /// <summary>Starts the worker, which asks the model server whether it is there (nothing listens), and waits until that has been said, then stops it.</summary>
    private static async Task RunUntilTheModelServerWasAsked(ServiceProvider services, CapturedLog log)
    {
        var worker = services.GetServices<IHostedService>().OfType<RecognitionWorker>().Single();
        await worker.StartAsync(default);
        for (var i = 0; i < 100 && !log.Entries.Any(e => e.Message.Contains("cannot be used right now", StringComparison.Ordinal)); i++) await Task.Delay(100);
        await worker.StopAsync(default);
    }

    [Fact]
    public async Task LogTraffic_IsOffByDefault_SoNoRequestToTheModelServerIsWritten_AndNothingIsSaidAboutIt()
    {
        var (services, log) = Build(NothingListensThere(null));
        await using var _s = services;

        await RunUntilTheModelServerWasAsked(services, log);

        Assert.Empty(log.From<OpenAiTrafficHandler>());
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("LogTraffic", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LogTraffic_WarnsAtStart_AndThenTheCallsToTheModelServerAreInTheLog()
    {
        var (services, log) = Build(NothingListensThere("true"));
        await using var _s = services;

        await RunUntilTheModelServerWasAsked(services, log);

        var warning = Assert.Single(log.From<RecognitionWorker>(), e => e.Level == LogLevel.Warning);
        Assert.Contains("Recognition:OpenAiCompatible:LogTraffic is on", warning.Message);
        var traffic = log.From<OpenAiTrafficHandler>().ToList();
        Assert.Equal(["health check"], traffic.Select(t => t.Values.GetValueOrDefault("Purpose")?.ToString()).Where(p => p is not null)); // the request; its failure carries the same number instead
        var failure = Assert.Single(traffic, t => t.Message.Contains("failed after", StringComparison.Ordinal));
        Assert.Equal(traffic[0].Values["Seq"], failure.Values["Seq"]);
    }

    [Fact]
    public async Task WithoutSettings_TheWorkerStopsQuietly()
    {
        var (services, log) = Build([]);
        await using var _s = services;
        var worker = services.GetServices<IHostedService>().OfType<RecognitionWorker>().Single();

        await worker.StartAsync(default);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }
}
