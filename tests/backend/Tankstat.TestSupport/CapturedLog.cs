using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Tankstat.TestSupport;

/// <summary>One captured log line: its level, category, rendered message, the exception, and the scopes that were open when it was written.</summary>
/// <param name="Values">
/// What the placeholders of the message template were given (<c>{UserId}</c> is <c>Values["UserId"]</c>), for tests that check what a line
/// says without depending on how it is worded. Empty for a line without placeholders.
/// </param>
public sealed record LogEntry(LogLevel Level, string Category, string Message, Exception? Exception, IReadOnlyList<object?> Scopes, IReadOnlyDictionary<string, object?> Values)
{
    /// <summary>The message and, when there is an exception, its type, message and stack trace (everything a log line could leak).</summary>
    public string Text => Exception is null ? Message : $"{Message}\n{Exception}";

    /// <summary>The scopes as text: key/value scopes (the shape the JSON formatter renders) as <c>key=value</c>, anything else as it prints.</summary>
    public string ScopeText => string.Join(" ", Scopes.Select(scope => scope is IEnumerable<KeyValuePair<string, object>> pairs
        ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"))
        : scope?.ToString()));
}

/// <summary>
/// Keeps what is logged, for tests: a provider for hosts (<c>AddLogging(b =&gt; b.AddProvider(log))</c>) and, through <see cref="For{T}"/>,
/// loggers for services that tests build by hand. Thread-safe, because the app logs from background work too.
/// </summary>
public sealed partial class CapturedLog : ILoggerProvider, ISupportExternalScope
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = [];
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    /// <summary>Everything logged so far, oldest first (a snapshot).</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate) return _entries.ToList();
        }
    }

    /// <summary>The entries at <paramref name="level"/> or more severe.</summary>
    public IEnumerable<LogEntry> AtLeast(LogLevel level) => Entries.Where(e => e.Level >= level);

    /// <summary>What one class logged (its category is its full name, as the framework names it).</summary>
    public IEnumerable<LogEntry> From<T>() => Entries.Where(e => e.Category == CategoryOf(typeof(T)));

    /// <summary>What was logged under a category or below it: <c>From("Tankstat")</c> is every line of the app, not the framework's.</summary>
    public IEnumerable<LogEntry> From(string category) =>
        Entries.Where(e => e.Category == category || e.Category.StartsWith(category + ".", StringComparison.Ordinal));

    /// <summary>
    /// Whether any line (message, exception or scope) contains <paramref name="text"/>, ignoring case. This is what the privacy tests ask:
    /// nothing a user typed, and no secret, may ever show up in a log line. A find strictly inside a token shaped like one of the random ids a
    /// line may hold (<see cref="RandomIds"/>) does not count, because an id can spell a short needle such as <c>24687</c> by chance; the whole
    /// token, and a find that runs past one, still count. The rule goes by shape, not by who wrote the token, so a canary searches a whole
    /// value (or a piece no id shape can hold), never a piece of one that could be logged in such a shape.
    /// </summary>
    public bool Mentions(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return Entries.Any(e => Holds(e.Text, text) || Holds(e.ScopeText, text));
    }

    /// <summary>
    /// Whether <paramref name="line"/> contains <paramref name="text"/> as <see cref="Mentions"/> counts it (ignoring case, not strictly inside a
    /// random id): for what else a privacy test reads, such as the spans and log records the telemetry exporter is handed.
    /// </summary>
    public static bool Holds(string line, string text)
    {
        MatchCollection? ids = null;
        for (var at = 0; (at = line.IndexOf(text, at, StringComparison.OrdinalIgnoreCase)) >= 0; at++)
            if (!(ids ??= RandomIds().Matches(line)).Any(id => id.Index <= at && at + text.Length <= id.Index + id.Length && text.Length < id.Length)) return true;
        return false;
    }

    /// <summary>
    /// The random ids a line may hold, each as a whole token: a GUID (also in braces), 32 hex characters (a GUID without hyphens, a trace id),
    /// 16 (a span or parent id), and the request id ASP.NET Core makes from the clock's ticks (13 characters of 0-9A-V, 0H... until about 2056).
    /// </summary>
    [GeneratedRegex("(?<![0-9A-Za-z])(?:[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}|[0-9A-Fa-f]{32}|[0-9A-Fa-f]{16}|0H[0-9A-V]{11})(?![0-9A-Za-z])")]
    private static partial Regex RandomIds();

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    /// <summary>A logger for a service built by hand, named like the framework would name it (the type's full name).</summary>
    public ILogger<T> For<T>() => new CapturingLogger<T>(this);

    // The host hands over the scope provider that all its loggers share, so scopes opened anywhere show up in the entries.
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private void Add<TState>(string category, LogLevel level, TState state, Exception? exception, Func<TState, Exception?, string> format)
    {
        var scopes = new List<object?>();
        _scopes.ForEachScope((scope, list) => list.Add(scope), scopes);
        var values = new Dictionary<string, object?>();
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            foreach (var (key, value) in pairs)
                if (key != "{OriginalFormat}") values[key] = value;
        var entry = new LogEntry(level, category, format(state, exception), exception, scopes, values);
        lock (_gate) _entries.Add(entry);
    }

    private class CapturingLogger(CapturedLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Add(category, logLevel, state, exception, formatter);
    }

    private sealed class CapturingLogger<T>(CapturedLog owner) : CapturingLogger(owner, CategoryOf(typeof(T))), ILogger<T>;

    private static string CategoryOf(Type type) => type.FullName!.Replace('+', '.');
}
