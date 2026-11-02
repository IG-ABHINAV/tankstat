using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Tankstat.TestSupport;

/// <summary>
/// The spans of one <see cref="ActivitySource"/> as they end. A listener hears every activity of that source in the process, so tests that
/// run side by side see each other's: find a test's own by a tag only it can have (the id of something it made), never by count.
/// </summary>
public sealed class CapturedSpans : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<Activity> _ended = new();

    public CapturedSpans(string sourceName)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _ended.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Activity> Ended => [.. _ended];

    /// <summary>The ended spans called <paramref name="name"/> whose <paramref name="tag"/> is <paramref name="value"/>.</summary>
    public IReadOnlyList<Activity> Named(string name, string tag, object value) =>
        [.. Ended.Where(a => a.OperationName == name && Equals(a.GetTagItem(tag), value))];

    public void Dispose() => _listener.Dispose();
}

/// <summary>
/// What the instruments of one <see cref="Meter"/> instance recorded: one meter per test (each <c>World</c> has its own), so no other
/// test's measurements mix in, unlike a listener by the meter's name.
/// </summary>
public sealed class CapturedMeasurements : IDisposable
{
    public sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<Measurement> _recorded = new();

    public CapturedMeasurements(Meter meter)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
    }

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>();
        foreach (var tag in tags) copy[tag.Key] = tag.Value;
        _recorded.Enqueue(new Measurement(instrument.Name, value, copy));
    }

    public IReadOnlyList<Measurement> Of(string instrument) => [.. _recorded.Where(m => m.Instrument == instrument)];

    public void Dispose() => _listener.Dispose();
}
