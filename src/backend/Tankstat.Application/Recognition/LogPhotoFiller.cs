using System.Globalization;
using Microsoft.Extensions.Logging;
using Tankstat.Application.Expenses;
using Tankstat.Application.Notifications;
using Tankstat.Application.Photos;
using Tankstat.Application.Refuelings;
using Tankstat.Application.Vehicles;
using Tankstat.Domain;
using Tankstat.Domain.Notifications;
using Tankstat.Domain.Photos;
using Tankstat.Domain.Recognition;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.Recognition;

/// <summary>
/// Fills in logs that were saved while a photo of theirs was still being read (see <see cref="ReviewState"/>). It runs without a user:
/// right after such a log is saved (readings that finished before the save) and from the background worker whenever a reading of a log
/// photo finishes. Only values the provider was sure enough of are taken, only empty values are filled, and the person who logged it is
/// told through a notification, both when values were filled in (to check them) and when the reading left values missing.
/// </summary>
public sealed class LogPhotoFiller(
    ILogPhotoRepository photos, IPhotoReadingRepository readings, IRefuelingRepository refuelings, IExpenseRepository expenses,
    IVehicleRepository vehicles, RecognitionSetup setup, Notifier notifier, INotificationRepository notifications, Tyres.TyreService tyres, TimeProvider clock,
    ILogger<LogPhotoFiller> logger)
{
    /// <summary>
    /// How long after its upload a finished reading still lets a new log leave values empty: the dialog asks for readings every few
    /// seconds, so the reading may have finished just before the save (it is then taken right after it).
    /// </summary>
    public static readonly TimeSpan JustRead = TimeSpan.FromMinutes(2);

    /// <summary>Whether a new log may wait for these pictures (drafts about to be attached): one is still being read, or was just read.</summary>
    public async Task<bool> MayWaitForDraftsAsync(IReadOnlyCollection<Guid> imageIds, CancellationToken ct)
    {
        if (imageIds.Count == 0) return false;
        var recent = clock.GetUtcNow() - JustRead;
        return (await readings.FindManyAsync(imageIds, ct)).Any(r => IsPending(r) || r.CreatedAt >= recent);
    }

    /// <summary>
    /// Whether a saved log may leave values empty for its photos: one is still being read, or was just read (a photo added in the edit
    /// dialog; the reading may have finished just before the save, and is then taken right after it). The same rule as for new logs.
    /// </summary>
    public async Task<bool> MayWaitForLogPhotosAsync(LogType logType, Guid logId, CancellationToken ct)
    {
        var images = (await photos.ListForLogAsync(logType, logId, ct)).Select(p => p.ImageId).ToList();
        if (images.Count == 0) return false;
        var recent = clock.GetUtcNow() - JustRead;
        return (await readings.FindManyAsync(images, ct)).Any(r => IsPending(r) || r.CreatedAt >= recent);
    }

    /// <summary>The log that shows this picture, if the picture is a log photo (a draft has none yet).</summary>
    public async Task<(LogType Type, Guid Id)?> LogOfAsync(Guid imageId, CancellationToken ct) =>
        await photos.FindByImageAsync(imageId, ct) is { } photo ? (photo.LogType, photo.LogId) : null;

    /// <summary>
    /// Takes what the log's photos showed into the log while it waits for them, and moves it on once nothing is being read any more.
    /// Does nothing for a log that does not wait (anymore), is in the trash or is gone.
    /// </summary>
    public async Task FillAsync(LogType logType, Guid logId, CancellationToken ct)
    {
        // A log is only saved on the version it was read in: a person (or a device sending its changes) saving it at the same moment wins.
        // The fill reads it again once; if it was changed again, it is left as it is (the next round or the person's save sees to it).
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await FillOnceAsync(logType, logId, ct);
                return;
            }
            catch (KeyedException e) when (e.Key == "sync.versionMismatch")
            {
                if (attempt == 2)
                {
                    logger.LogDebug("{LogType} {LogId} was saved meanwhile while it was filled from its photos; left as it is", logType, logId);
                    return;
                }
            }
        }
    }

    private async Task FillOnceAsync(LogType logType, Guid logId, CancellationToken ct)
    {
        var images = (await photos.ListForLogAsync(logType, logId, ct)).Select(p => p.ImageId).ToList();
        var found = images.Count == 0 ? [] : await readings.FindManyAsync(images, ct);
        var values = ValuesOf(found);
        var pending = found.Any(IsPending);

        if (logType == LogType.Refueling)
        {
            if (await refuelings.FindAsync(logId, ct) is not { ReviewState: ReviewState.AwaitingPhotos } log) return;
            var before = log.FilledFromPhoto;
            var changes = log.FillFromPhoto(values);
            var moved = log.FinishReading(pending);
            if (log.FilledFromPhoto == before && !moved) return;
            await refuelings.UpdateAsync(log, changes, ct);
            await refuelings.SaveConsumptionsAsync(ConsumptionCalculator.Apply(await refuelings.ListAllForVehicleAsync(log.VehicleId, ct)), ct);
            // Which values, never what they are.
            logger.LogDebug("Refueling {RefuelingId} of vehicle {VehicleId} took {Values} from its photos and is {ReviewState}",
                logId, log.VehicleId, Names(log.FilledFromPhoto & ~before), log.ReviewState);
            if (moved) await NotifyAsync(NotificationRef.Refueling(logId), log.VehicleId, log.CreatedById, log.Date, log.ReviewState, log.FilledFromPhoto, log.Missing, ct);
        }
        else
        {
            if (await expenses.FindAsync(logId, ct) is not { ReviewState: ReviewState.AwaitingPhotos } log) return;
            var before = log.FilledFromPhoto;
            var changes = log.FillFromPhoto(values);
            var moved = log.FinishReading(pending);
            if (log.FilledFromPhoto == before && !moved) return;
            await expenses.UpdateAsync(log, changes, ct);
            // A tyre log line's expense (#288) holds the line's reading: one a photo filled in becomes the line's too.
            if (changes.CreatedReading is { } reading) await tyres.ExpenseReadingFilledAsync(logId, reading, ct);
            logger.LogDebug("Expense {ExpenseId} of vehicle {VehicleId} took {Values} from its photos and is {ReviewState}",
                logId, log.VehicleId, Names(log.FilledFromPhoto & ~before), log.ReviewState);
            if (moved) await NotifyAsync(NotificationRef.Expense(logId), log.VehicleId, log.CreatedById, log.Date, log.ReviewState, log.FilledFromPhoto, log.Missing, ct, log.Title);
        }
    }

    /// <summary>A person checked the log (saved it with every value): what they were told about its photos counts as read.</summary>
    public async Task ReviewedAsync(LogType logType, Guid logId, Guid createdById, CancellationToken ct)
    {
        var told = await notifications.ListForSubjectsAsync(createdById, NotificationTopic.LogReview, [logId], ct);
        var unread = told.Where(n => n.ReadAt is null && n.Subject.Type == EntityType(logType)).Select(n => n.Id).ToList();
        if (unread.Count > 0) await notifications.MarkReadAsync(createdById, unread, clock.GetUtcNow(), ct);
    }

    private async Task NotifyAsync(
        NotificationRef subject, Guid vehicleId, Guid recipientId, DateOnly date, ReviewState state, LogValues filled, LogValues missing, CancellationToken ct,
        string? title = null)
    {
        var kind = state switch
        {
            ReviewState.NeedsReview => NotificationKind.LogFilledFromPhoto,
            ReviewState.Incomplete => NotificationKind.LogNotFilled,
            _ => (NotificationKind?)null,
        };
        if (kind is null) return;
        var vehicle = await vehicles.FindAsync(vehicleId, ct);
        var draft = new NotificationDraft(
            recipientId, kind.Value, subject, NotificationRef.Vehicle(vehicleId),
            NotificationArgs.Of(
                ("vehicleName", vehicle?.Name),
                ("date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("title", title),
                ("values", Names(state == ReviewState.Incomplete ? missing : filled))),
            Occurrence: "photos");
        await notifier.NotifyAsync(null, [draft], ct);
    }

    /// <summary>The values as the API spells them (<c>ODOMETER,TOTAL</c>), so clients reuse their translations.</summary>
    private static string Names(LogValues values) =>
        string.Join(",", Enum.GetValues<LogValues>().Where(v => v != LogValues.None && values.HasFlag(v)).Select(v => v.ToString().ToUpperInvariant()));

    private static NotificationEntityType EntityType(LogType logType) =>
        logType == LogType.Refueling ? NotificationEntityType.Refueling : NotificationEntityType.Expense;

    private static bool IsPending(PhotoReading reading) => reading.Status is ReadingStatus.Queued or ReadingStatus.Reading;

    /// <summary>
    /// The best value per field across the log's photos, among those sure enough (<see cref="RecognitionSetup.IsSureEnough"/>: a blank
    /// beats a wrong value). The currency comes from a photo only, never from the reading's hint (the vehicle's usual one): a foreign receipt
    /// without a legible currency must not be stored as the home currency, so its total is left for the person (the log stays incomplete).
    /// </summary>
    private PhotoValues ValuesOf(IReadOnlyList<PhotoReading> found)
    {
        var read = found.Where(r => r.Status == ReadingStatus.Read).ToList();
        var sure = read.SelectMany(r => r.Values.Where(setup.IsSureEnough).Select(v => (Reading: r, Value: v))).ToList();
        var best = sure
            .GroupBy(x => x.Value.Name)
            .ToDictionary(g => g.Key, g => g.MaxBy(x => x.Value.Confidence)!.Value.Value);
        // The discount goes with the total it was taken off: from the photo that gave the total, none when that photo shows none (another
        // photo's discount was checked against another total).
        best.Remove(ReadingFieldName.Discount);
        var totals = sure.Where(x => x.Value.Name == ReadingFieldName.Total).ToList();
        if (totals.Count > 0
            && totals.MaxBy(x => x.Value.Confidence).Reading.Values.Where(setup.IsSureEnough).FirstOrDefault(v => v.Name == ReadingFieldName.Discount) is { } discount)
            best[ReadingFieldName.Discount] = discount.Value;

        decimal? Number(ReadingFieldName name) =>
            best.TryGetValue(name, out var text) && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : null;

        var odometer = Number(ReadingFieldName.Odometer) is { } o && o == decimal.Truncate(o) && o <= long.MaxValue ? (long?)o : null;
        var total = Number(ReadingFieldName.Total);
        var currency = best.GetValueOrDefault(ReadingFieldName.Currency) is { } c && IsCurrency(c) ? c : null;
        return new PhotoValues(odometer, Number(ReadingFieldName.Volume), total, currency?.ToUpperInvariant(), Number(ReadingFieldName.Discount));
    }

    private static bool IsCurrency(string code) => code.Length == 3 && code.All(char.IsAsciiLetter);
}
