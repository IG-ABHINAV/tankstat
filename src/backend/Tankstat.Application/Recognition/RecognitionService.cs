using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tankstat.Application.Access;
using Tankstat.Application.Expenses;
using Tankstat.Application.Odometers;
using Tankstat.Application.Photos;
using Tankstat.Application.Refuelings;
using Tankstat.Application.Vehicles;
using Tankstat.Domain.Photos;
using Tankstat.Domain.Recognition;

namespace Tankstat.Application.Recognition;

/// <summary>A draft photo of the current user with its reading (none when photo reading is off, or the photo was not queued).</summary>
public sealed record DraftReading(PhotoDraft Draft, PhotoReading? Reading);

/// <summary>
/// Photo reading for the user: photos picked in the add dialogs, and photos added to a saved log in its edit dialog, are queued for the
/// recognition provider as soon as they are uploaded, and the dialog asks for their readings until they are done. Everything is skipped
/// when no provider is set up.
/// </summary>
public sealed class RecognitionService(
    RecognitionAvailability availability, RecognitionSetup setup, IPhotoReadingRepository readings, ILogPhotoRepository logPhotos, IRefuelingRepository refuelingLogs, IExpenseRepository expenseLogs, OdometerService odometer,
    RefuelingService refuelings, IOptionsSnapshot<VehicleDefaultsOptions> defaults, IRecognitionSignal signal, AccessService access, PhotoDraftAccess own,
    TimeProvider clock, ILogger<RecognitionService> logger)
{
    private static readonly string[] Locales = ["hu", "en", "de"];

    /// <summary>A provider is set up and answers: the UI then waits for readings.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        await access.RequirePrincipalAsync(ct);
        return await availability.IsAvailableAsync(ct);
    }

    /// <summary>
    /// Queues the reading of a draft the current user just uploaded, with the hints the checks need (the background worker that reads it
    /// has no user to ask). Nothing happens when no provider is set up, or when the draft is not the user's.
    /// </summary>
    /// <param name="locale">The language the user works in (how numbers and dates are written); unknown ones count as English.</param>
    public async Task QueueForDraftAsync(Guid draftId, ReadingPurpose purpose, string? locale, CancellationToken ct)
    {
        if (!availability.IsConfigured) return;
        if (await own.MineAsync(draftId, ct) is not { } draft) return;

        var known = await refuelings.DefaultsAsync(draft.VehicleId, ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        await readings.AddAsync(
            PhotoReading.Queue(draft.Id, purpose, Language(locale), known?.LastOdometer, known?.LastCurrency ?? defaults.Value.Currency, today, clock.GetUtcNow()), ct);
        signal.Wake();
        logger.LogDebug("Queued photo {ImageId} to be read ({Purpose})", draft.Id, purpose);
    }

    /// <summary>
    /// Queues the reading of a photo the current user just added to a saved log (its edit dialog). The hints describe that log, not the
    /// vehicle today: the odometer before the log's day (a back-dated log would otherwise have its correct odometer dropped as lower than
    /// the latest) and the log's own currency. Nothing happens when no provider is set up, or when the photo is not the user's on that log.
    /// </summary>
    public async Task QueueForLogPhotoAsync(LogType logType, Guid logId, Guid imageId, string? locale, CancellationToken ct)
    {
        if (!availability.IsConfigured) return;
        await access.RequirePrincipalAsync(ct); // signed out is refused first, never answered as a missing photo
        if (await logPhotos.FindByImageAsync(imageId, ct) is not { } photo || photo.LogType != logType || photo.LogId != logId || !await own.IsUploaderAsync(photo, ct)) return;

        var (date, readingId, currency) = logType == LogType.Refueling
            ? await refuelingLogs.FindAsync(logId, ct) is { } r ? (r.Date, r.OdometerReadingId, r.Currency) : default
            : await expenseLogs.FindAsync(logId, ct) is { } e ? (e.Date, e.OdometerReadingId, e.Currency) : default;
        if (date == default) return; // gone or in the trash meanwhile
        var previous = await odometer.PreviousAsync(photo.VehicleId, date, readingId, ct);
        var known = currency is null ? await refuelings.DefaultsAsync(photo.VehicleId, ct) : null;
        var purpose = logType == LogType.Refueling ? ReadingPurpose.Refueling : ReadingPurpose.Expense;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        await readings.AddAsync(
            PhotoReading.Queue(imageId, purpose, Language(locale), previous?.Value, currency ?? known?.LastCurrency ?? defaults.Value.Currency, today, clock.GetUtcNow()), ct);
        signal.Wake();
        logger.LogDebug("Queued photo {ImageId} of {LogType} {LogId} to be read", imageId, logType, logId);
    }

    /// <summary>The readings of the given pictures (already authorised by the log that shows them), by picture id.</summary>
    public async Task<IReadOnlyDictionary<Guid, PhotoReading>> ReadingsOfAsync(IReadOnlyCollection<Guid> imageIds, CancellationToken ct) =>
        imageIds.Count == 0 ? new Dictionary<Guid, PhotoReading>() : (await readings.FindManyAsync(imageIds, ct)).ToDictionary(r => r.Id);

    /// <summary>The current user's drafts among <paramref name="ids"/>, in that order, with their readings; anyone else's look missing.</summary>
    public async Task<IReadOnlyList<DraftReading>> ListDraftsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().Take(PhotoDraft.MaxPerUserAndVehicle).ToList();
        var mine = (await own.MineAsync(wanted, ct)).ToDictionary(d => d.Id);
        var found = (await readings.FindManyAsync(mine.Keys, ct)).ToDictionary(r => r.Id);
        return wanted.Where(mine.ContainsKey).Select(id => new DraftReading(mine[id], found.GetValueOrDefault(id))).ToList();
    }

    /// <summary>The values worth filling in: ones the provider is sure enough of, and read rather than the hint it was given coming back.</summary>
    public IReadOnlyList<ReadingValue> UsableValues(PhotoReading reading) => reading.Values.Where(setup.IsSureEnough).ToList();

    /// <summary>
    /// Why the photo gave less than it might have: the reasons kept with the reading, and for each value the model was too unsure of to fill
    /// in (unless a reason already says why) that. Codes only: the dialog words them, and knows the vehicle's latest reading itself.
    /// </summary>
    public IReadOnlyList<ReadingIssue> IssuesOf(PhotoReading reading) =>
    [
        .. reading.Issues,
        .. reading.Values
            .Where(v => v.Source != ValueSource.Hint && !setup.IsSureEnough(v) && reading.Issues.All(i => i.Field != v.Name))
            .Select(v => new ReadingIssue(v.Name, ReadingIssueCode.Unsure)),
    ];

    private static string Language(string? locale)
    {
        var language = (locale ?? "").Split('-', '_')[0].Trim().ToLowerInvariant();
        return Locales.Contains(language) ? language : "en";
    }
}
