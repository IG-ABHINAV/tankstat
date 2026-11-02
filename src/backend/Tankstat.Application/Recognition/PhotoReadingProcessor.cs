using Microsoft.Extensions.Logging;
using Tankstat.Application.Images;
using Tankstat.Application.Telemetry;
using Tankstat.Domain.Photos;
using Tankstat.Domain.Recognition;

namespace Tankstat.Application.Recognition;

public enum ReadingOutcome
{
    Read,
    /// <summary>The provider could not read it now; it is queued again for later.</summary>
    Retrying,
    Failed,
}

/// <summary>What happened to one reading in a round of the worker (for its log; never the values themselves).</summary>
/// <param name="Summary">For a reading that was read: what came of it.</param>
public sealed record ProcessedReading(Guid Id, ReadingOutcome Outcome, string? Reason = null, ReadingSummary? Summary = null);

/// <summary>What a finished reading came to, in counts: what the photo showed, how many values were kept and how many of them are sure enough to be filled in.</summary>
public sealed record ReadingSummary(DocumentKind Kind, int Values, int Sure);

/// <summary>
/// The background worker's job: read the photos that are due, a few at a time, through the recognition provider, and keep what was
/// found. Nothing is claimed while the provider is unavailable, so a stopped model server does not use up the attempts. A finished reading of
/// a photo that already belongs to a log (saved while it was being read) is then taken into that log (<see cref="LogPhotoFiller"/>).
/// </summary>
public sealed class PhotoReadingProcessor(
    IRecognitionProvider provider, RecognitionAvailability availability, RecognitionSetup setup, IPhotoReadingRepository readings,
    IImageRepository images, IImageStore store, LogPhotoFiller filler, TimeProvider clock, AppMetrics metrics, ILogger<PhotoReadingProcessor> logger)
{
    /// <summary>
    /// An attempt this old was cut short (the app stopped while reading): it is queued again. At least five minutes, and always longer than
    /// one read may take (<c>Recognition:OpenAiCompatible:TimeoutSeconds</c>), so another instance never takes over a read that is still going on.
    /// </summary>
    public static TimeSpan StaleAfter(RecognitionOptions options)
    {
        var longestRead = TimeSpan.FromSeconds(options.OpenAiCompatible.TimeoutSeconds) + TimeSpan.FromMinutes(1);
        return longestRead > MinimumStaleAfter ? longestRead : MinimumStaleAfter;
    }

    private static readonly TimeSpan MinimumStaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>Reads the due photos, up to <see cref="RecognitionOptions.MaxConcurrent"/> at a time; empty when nothing could be done.</summary>
    public async Task<IReadOnlyList<ProcessedReading>> ProcessDueAsync(CancellationToken ct)
    {
        if (!await availability.IsAvailableAsync(ct)) return [];

        var now = clock.GetUtcNow();
        var finished = new List<Guid>();
        foreach (var stale in await readings.ListStaleAsync(now - StaleAfter(setup.Options), ct))
        {
            stale.Abandon(now);
            await readings.SaveAsync(stale, ct);
            logger.LogInformation("The reading of photo {Id} was cut short and is {Outcome}", stale.Id, stale.Status == ReadingStatus.Failed ? "given up after its last attempt" : "queued again");
            if (stale.Status == ReadingStatus.Failed) finished.Add(stale.Id);
        }

        var due = await readings.ListDueAsync(now, setup.Options.MaxConcurrent, ct);
        var done = (await Task.WhenAll(due.Select(id => ProcessAsync(id, ct)))).OfType<ProcessedReading>().ToList();
        finished.AddRange(done.Where(d => d.Outcome != ReadingOutcome.Retrying).Select(d => d.Id));

        // One log at a time, after the reads: two photos of one log never fill it in at the same time.
        foreach (var log in await LogsOfAsync(finished, ct)) await filler.FillAsync(log.Type, log.Id, ct);
        return done;
    }

    private async Task<IReadOnlyList<(LogType Type, Guid Id)>> LogsOfAsync(IEnumerable<Guid> imageIds, CancellationToken ct)
    {
        var logs = new List<(LogType Type, Guid Id)>();
        foreach (var id in imageIds)
            if (await filler.LogOfAsync(id, ct) is { } log && !logs.Contains(log)) logs.Add(log);
        return logs;
    }

    private async Task<ProcessedReading?> ProcessAsync(Guid id, CancellationToken ct)
    {
        if (await readings.ClaimAsync(id, clock.GetUtcNow(), ct) is not { } reading) return null; // taken by another worker, or gone
        if (await images.FindAsync(id, ct) is not { } image) return null; // the picture went meanwhile, and its reading with it

        // A trace of its own per photo read (#219), the model server's call below it: ids, the outcome and counts, never a value or a message.
        using var activity = AppTraces.StartJob("tankstat.photo_reading.read");
        activity?.SetTag("tankstat.image.id", id);
        activity?.SetTag("tankstat.photo_reading.provider", provider.Name);
        var timestamp = clock.GetTimestamp();
        string? errorType = null;
        ProcessedReading outcome;
        var data = await LoadAsync(image, ct);
        if (data is null)
        {
            reading.Fail();
            outcome = new ProcessedReading(id, ReadingOutcome.Failed, "The picture's file is missing.");
            errorType = "photo_reading.fileMissing";
        }
        else
        {
            try
            {
                var result = await provider.ReadAsync(
                    new RecognitionRequest(data, image.ContentType, reading.AllowedKinds, reading.Locale, reading.LastOdometer, reading.Currency, reading.Today, id), ct);
                reading.Complete(provider.Name, result.ModelVersion, result.Kind, ReadingNormaliser.Normalise(result.Values), clock.GetUtcNow(), result.Issues);
                outcome = new ProcessedReading(id, ReadingOutcome.Read, Summary: new ReadingSummary(reading.Kind ?? DocumentKind.Unknown, reading.Values.Count, reading.Values.Count(setup.IsSureEnough)));
            }
            catch (RecognitionRejectedException e)
            {
                reading.Fail();
                outcome = new ProcessedReading(id, ReadingOutcome.Failed, e.Message);
                errorType = AppTraces.ErrorTypeOf(e);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Unavailable (or an unexpected answer): later, with the next attempt. Asking the provider again then confirms it is back.
                availability.Forget();
                reading.Retry(clock.GetUtcNow());
                outcome = new ProcessedReading(id, reading.Status == ReadingStatus.Failed ? ReadingOutcome.Failed : ReadingOutcome.Retrying, e.Message);
                errorType = AppTraces.ErrorTypeOf(e);
            }
        }
        var ended = outcome.Outcome.ToString().ToLowerInvariant();
        var kind = outcome.Summary?.Kind.ToString() ?? "none";
        activity?.SetTag("tankstat.photo_reading.outcome", ended);
        activity?.SetTag("tankstat.photo_reading.kind", kind);
        if (outcome.Summary is { } summary)
        {
            activity?.SetTag("tankstat.photo_reading.values", summary.Values);
            activity?.SetTag("tankstat.photo_reading.sure", summary.Sure);
        }
        if (errorType is not null) activity.Failed(errorType);
        metrics.PhotoReadingDuration.Record(clock.GetElapsedTime(timestamp).TotalSeconds, new("tankstat.photo_reading.outcome", ended), new("tankstat.photo_reading.kind", kind));
        await readings.SaveAsync(reading, ct);
        return outcome;
    }

    private async Task<byte[]?> LoadAsync(Domain.Images.StoredImage image, CancellationToken ct)
    {
        await using var stream = await store.OpenReadAsync(image, ct);
        if (stream is null) return null;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct);
        return copy.ToArray();
    }
}
