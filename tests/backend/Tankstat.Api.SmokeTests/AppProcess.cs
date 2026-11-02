using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Tankstat.Api.SmokeTests;

/// <summary>
/// The real application as a separate process (<c>dotnet Tankstat.Api.dll</c>, listening on a free local port), exactly as
/// a self-hoster would run it, configured only through environment variables. This catches what in-process tests cannot:
/// dependency-injection and options errors, hosted services failing at startup, a missing runtime config, ...
/// </summary>
internal sealed class AppProcess : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);

    private readonly Process _process;
    private readonly StringBuilder _log;
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies;

    public string WorkDirectory { get; }
    public string DatabasePath => Path.Combine(WorkDirectory, "smoke.db");
    public string Log { get { lock (_log) return _log.ToString(); } }
    public int Port { get; }

    private AppProcess(Process process, StringBuilder log, int port, string workDirectory, HttpClient http, CookieContainer cookies)
    {
        _cookies = cookies;
        _process = process;
        _log = log;
        Port = port;
        WorkDirectory = workDirectory;
        _http = http;
    }

    /// <summary>Locates the built API next to the sources (the test project references it, so it is always built first).</summary>
    private static string ApiDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tankstat.slnx"))) dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("Tankstat.slnx not found above the test output.");
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var dll = Path.Combine(root, "src", "backend", "Tankstat.Api", "bin", configuration, "net10.0", "Tankstat.Api.dll");
        return File.Exists(dll) ? dll : throw new FileNotFoundException("The API is not built.", dll);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <param name="command">A command instead of the server (<c>backup</c>, say): the process runs it and exits.</param>
    private static (Process Process, StringBuilder Log, string WorkDirectory, int Port) Launch(
        IReadOnlyDictionary<string, string?> settings, string? workDirectory, bool useTempDatabase, string[]? command = null)
    {
        var dll = ApiDll();
        var work = workDirectory ?? Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"tankstat-smoke-{Guid.NewGuid():N}")).FullName;
        var port = FreePort();

        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = work,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(dll);
        if (command is null)
        {
            info.ArgumentList.Add("--urls");
            info.ArgumentList.Add($"http://127.0.0.1:{port}");
        }
        else
        {
            foreach (var arg in command) info.ArgumentList.Add(arg);
        }

        // A clean slate: nothing from the developer's own environment may leak into the run.
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("Auth__") || k.StartsWith("Database__") || k.StartsWith("Smtp__") || k.StartsWith("Recognition__") || k.StartsWith("Storage__") || k.StartsWith("Settings__") || k.StartsWith("Backups__") || k.StartsWith("Setup__") || k.StartsWith("ASPNETCORE_")
            || k.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Telemetry__", StringComparison.OrdinalIgnoreCase)).ToList()) // a developer's collector never sees a test run
            info.Environment.Remove(key);
        info.Environment["ASPNETCORE_CONTENTROOT"] = Path.GetDirectoryName(dll); // finds appsettings.json although the working directory is elsewhere
        info.Environment["DOTNET_NOLOGO"] = "1";
        if (useTempDatabase) info.Environment["Database__ConnectionString"] = $"Data Source={Path.Combine(work, "smoke.db")}";
        foreach (var (key, value) in settings)
        {
            if (value is null) info.Environment.Remove(key);
            else info.Environment[key] = value;
        }

        var log = new StringBuilder();
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        void Append(string? line) { if (line is not null) lock (log) log.AppendLine(line); }
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return (process, log, work, port);
    }

    /// <summary>Starts the app and waits until it answers GraphQL requests; throws (with the app's output) if it exits or never answers.</summary>
    public static async Task<AppProcess> StartAsync(IReadOnlyDictionary<string, string?> settings, string? workDirectory = null, bool useTempDatabase = true)
    {
        var (process, log, work, port) = Launch(settings, workDirectory, useTempDatabase);
        var cookies = new CookieContainer();
        var http = new HttpClient(new HttpClientHandler { UseCookies = true, CookieContainer = cookies }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var app = new AppProcess(process, log, port, work, http, cookies);

        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                var message = $"The app exited during startup with code {process.ExitCode}.\n{app.Log}";
                await app.DisposeAsync();
                throw new InvalidOperationException(message);
            }
            try
            {
                using var response = await http.PostAsJsonAsync("/graphql", new { query = "{ __typename }" });
                if (response.IsSuccessStatusCode) return app;
            }
            catch (HttpRequestException)
            {
                // not listening yet
            }
            await Task.Delay(150);
        }

        await app.DisposeAsync();
        throw new TimeoutException($"The app did not answer within {StartupTimeout.TotalSeconds:F0}s.\n{app.Log}");
    }

    /// <summary>For configurations that must be rejected: runs the app until it exits by itself (in <paramref name="workDirectory"/>, which it removes afterwards, when given).</summary>
    public static async Task<(int ExitCode, string Log)> RunUntilExitAsync(IReadOnlyDictionary<string, string?> settings, bool useTempDatabase = true, string? workDirectory = null)
    {
        var (process, log, work, _) = Launch(settings, workDirectory, useTempDatabase);
        try
        {
            using var cts = new CancellationTokenSource(StartupTimeout);
            await process.WaitForExitAsync(cts.Token);
            process.WaitForExit(); // flush the output readers
            lock (log) return (process.ExitCode, log.ToString());
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            lock (log) throw new TimeoutException($"The app was expected to exit but kept running.\n{log}");
        }
        finally
        {
            Cleanup(work);
        }
    }

    /// <summary>Runs a command of the executable (<c>backup</c>, <c>decrypt</c>) in a work directory, with <paramref name="stdin"/> on its standard input, until it exits.</summary>
    public static async Task<(int ExitCode, string Log)> RunCommandAsync(IReadOnlyDictionary<string, string?> settings, string workDirectory, string stdin, params string[] command)
    {
        var (process, log, _, _) = Launch(settings, workDirectory, useTempDatabase: true, command);
        try
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
            using var cts = new CancellationTokenSource(StartupTimeout);
            await process.WaitForExitAsync(cts.Token);
            process.WaitForExit(); // flush the output readers
            lock (log) return (process.ExitCode, log.ToString());
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            lock (log) throw new TimeoutException($"The command did not finish.\n{log}");
        }
    }

    public async Task<JsonElement> Gql(string query, object? variables = null, IDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql") { Content = JsonContent.Create(new { query, variables }) };
        foreach (var (name, value) in headers ?? new Dictionary<string, string>()) request.Headers.Add(name, value);
        using var response = await _http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}\n{Log}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public Task<HttpResponseMessage> GetAsync(string path) => _http.GetAsync(path);

    /// <summary>Any request, with this client's cookies (a signed-in user's upload, say).</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => _http.SendAsync(request);

    /// <summary>The cookies this client would send to <c>/graphql</c> (the access cookie), as a Cookie header for another process.</summary>
    public string CookieHeader => _cookies.GetCookieHeader(new Uri(_http.BaseAddress!, "/graphql"));

    /// <summary>The first administrator of the smoke tests' Standalone instances, made by <see cref="FinishSetupAsync"/>.</summary>
    public const string AdminEmail = "root@example.com";
    public const string AdminPassword = "initial-password-1";

    /// <summary>
    /// Finishes a waiting Standalone setup the unattended way (#149): the one-time code from the log and one <c>setupFinish</c>, as a script
    /// would, through a client of its own, so this one stays signed out. The setup page is the only way to the first administrator.
    /// </summary>
    public async Task FinishSetupAsync(string email = AdminEmail, string password = AdminPassword)
    {
        Assert.True(await LogsAsync("one-time setup code"), Log);
        var code = System.Text.RegularExpressions.Regex.Match(Log, "one-time setup code ([A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4})").Groups[1].Value;
        using var script = new HttpClient { BaseAddress = _http.BaseAddress };
        using var response = await script.PostAsJsonAsync("/graphql", new
        {
            query = "mutation($i: SetupFinishInput!) { setupFinish(input: $i) { restarting } }",
            variables = new { i = new { code, email, password, passwordConfirmation = password } },
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}\n{Log}");
        using var document = JsonDocument.Parse(body);
        var answer = document.RootElement;
        Assert.False(answer.TryGetProperty("errors", out var errors), $"{errors}\n{Log}");
        // Nothing waits for a restart (no settings file saved by a wizard): the instance runs on at once, as the scripts finishing a setup expect.
        Assert.False(answer.GetProperty("data").GetProperty("setupFinish").GetProperty("restarting").GetBoolean(), Log);
    }

    /// <summary>A Standalone instance with its first administrator (<see cref="AdminEmail"/>): started, and its setup finished.</summary>
    public static async Task<AppProcess> StartStandaloneAsync(IReadOnlyDictionary<string, string?> settings, string? workDirectory = null, bool useTempDatabase = true)
    {
        var app = await StartAsync(settings, workDirectory, useTempDatabase);
        try
        {
            await app.FinishSetupAsync();
        }
        catch
        {
            await app.DisposeAsync(); // as StartAsync does before every failure: no process or folder left behind
            throw;
        }
        return app;
    }

    /// <summary>Waits for a line in the app's output (background work logs a little after the app starts answering).</summary>
    public async Task<bool> LogsAsync(string text, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(10));
        while (!Log.Contains(text, StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(100);
        }
        return true;
    }

    public bool HasExited => _process.HasExited;

    /// <summary>Sends a POSIX signal to the app, as <c>docker stop</c> (TERM) or Ctrl+C (INT) would; not on Windows.</summary>
    public async Task SignalAsync(string signal)
    {
        using var kill = Process.Start("kill", [$"-{signal}", _process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)])!;
        await kill.WaitForExitAsync();
    }

    /// <summary>Whether the app exits by itself within <paramref name="within"/>.</summary>
    public async Task<bool> ExitsWithinAsync(TimeSpan within)
    {
        using var wait = new CancellationTokenSource(within);
        try
        {
            await _process.WaitForExitAsync(wait.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Stops the app (without removing its files, so it can be started again on the same database).</summary>
    public async Task StopAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
        _process.Dispose();
        Cleanup(WorkDirectory);
    }

    private static void Cleanup(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { /* a still-closing process may hold the database briefly; the temp folder is disposable */ }
    }
}

internal static class GraphQLJson
{
    public static string? ErrorCode(this JsonElement body) =>
        body.TryGetProperty("errors", out var errors) ? errors[0].GetProperty("extensions").GetProperty("code").GetString() : null;

    public static JsonElement Data(this JsonElement body)
    {
        Assert.False(body.TryGetProperty("errors", out var errors), errors.ToString());
        return body.GetProperty("data");
    }
}
