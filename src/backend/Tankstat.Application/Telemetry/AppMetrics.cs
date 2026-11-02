using System.Diagnostics.Metrics;

namespace Tankstat.Application.Telemetry;

/// <summary>
/// The app's own metrics (#219), the meter <see cref="MeterName"/>: exported while telemetry is on (<c>TelemetryExtensions</c>), recorded
/// for nobody while it is off. One instance per host, made through the host's <see cref="IMeterFactory"/> (disposed with it, so an in-process
/// restart starts afresh); without one (a test's own services) a meter of its own, which also keeps one test's measurements apart from
/// another's. Attributes are outcomes and kinds, a small set each, never an id: every distinct value is a series of its own, and an id
/// would also say who did what.
/// </summary>
public sealed class AppMetrics : IDisposable
{
    public const string MeterName = "Tankstat";

    private readonly bool _ownsMeter;

    public AppMetrics(IMeterFactory? factory = null)
    {
        _ownsMeter = factory is null;
        Meter = factory?.Create(MeterName) ?? new Meter(MeterName);
        BackupDuration = Meter.CreateHistogram("tankstat.backup.duration", unit: "s",
            description: "How long a backup run took, by its outcome (ok, or the error's key) and what started it.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [1, 5, 15, 30, 60, 120, 300, 600, 1200, 1800, 3600] });
        BackupSize = Meter.CreateHistogram<long>("tankstat.backup.size", unit: "By",
            description: "The size of a backup written, by whether it is encrypted and holds the photos.",
            advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = [1L << 20, 10L << 20, 100L << 20, 500L << 20, 1L << 30, 5L << 30, 20L << 30] });
        PhotoReadingDuration = Meter.CreateHistogram("tankstat.photo_reading.duration", unit: "s",
            description: "How long the model took to read a photo, by the reading's outcome and what the photo showed.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.5, 1, 2, 5, 10, 20, 30, 60, 120, 300] });
        SyncChanges = Meter.CreateCounter<long>("tankstat.sync.changes", unit: "{change}",
            description: "Changes devices sent, by what they change and their result (applied, parked, or answered as sent before).");
    }

    /// <summary>The meter the instruments belong to (tests listen to it alone).</summary>
    public Meter Meter { get; }

    /// <summary><c>tankstat.backup.outcome</c> (<c>ok</c>, <c>stopped</c> or the error's key) and <c>tankstat.backup.trigger</c>.</summary>
    public Histogram<double> BackupDuration { get; }

    /// <summary><c>tankstat.backup.encrypted</c> and <c>tankstat.backup.photos</c>.</summary>
    public Histogram<long> BackupSize { get; }

    /// <summary><c>tankstat.photo_reading.outcome</c> and <c>tankstat.photo_reading.kind</c>.</summary>
    public Histogram<double> PhotoReadingDuration { get; }

    /// <summary><c>tankstat.sync.change.kind</c> and <c>tankstat.sync.change.result</c>.</summary>
    public Counter<long> SyncChanges { get; }

    public void Dispose()
    {
        if (_ownsMeter) Meter.Dispose();
    }
}
