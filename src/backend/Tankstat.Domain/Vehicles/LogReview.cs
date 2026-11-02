using Tankstat.Domain.Measurements;
using Tankstat.Domain.Odometers;

namespace Tankstat.Domain.Vehicles;

/// <summary>
/// Where a log stands with values that come from its photos. A log may be saved while one of its photos is still being read, with the
/// values the photo will give left empty; the reading fills them in later and the log then waits for a person to check them.
/// </summary>
public enum ReviewState
{
    /// <summary>Nothing to do: every value was entered (or checked) by a person.</summary>
    None,

    /// <summary>Saved with empty values while a photo of it is still being read.</summary>
    AwaitingPhotos,

    /// <summary>Values were read from its photos and filled in; a person should check them.</summary>
    NeedsReview,

    /// <summary>Its photos were read, but values it needs are still missing; a person has to enter them.</summary>
    Incomplete,
}

/// <summary>The values of a log that a photo can provide.</summary>
[Flags]
public enum LogValues
{
    None = 0,
    Odometer = 1,
    Volume = 2,
    Total = 4,

    /// <summary>Taken with the total it was taken off: a log never waits for it, it is never missing.</summary>
    Discount = 8,
}

/// <summary>What the photos of one log showed, normalised (only values the provider was sure enough of).</summary>
public sealed record PhotoValues(long? Odometer, decimal? Volume, decimal? Total, string? Currency, decimal? Discount = null);

/// <summary>
/// Readings and costs a log created or let go of while it changed. They are rows of their own (with ids made by the domain), so the
/// repository adds or deletes them explicitly.
/// </summary>
public sealed record LinkedChanges(OdometerReading? CreatedReading, OdometerReading? RemovedReading, Cost? CreatedCost, Cost? RemovedCost)
{
    public static LinkedChanges None { get; } = new(null, null, null, null);
}

/// <summary>The rules shared by refuellings and expenses for values that come from photos.</summary>
public static class LogReview
{
    public static DomainException ValuesRequired() =>
        new("log.valuesRequired", "Fill in every value; only a photo that is still being read may leave them empty.");

    /// <summary>The state a log starts in, or after a person saved it.</summary>
    /// <param name="required">Values the log needs that are empty.</param>
    /// <param name="fillable">Empty values a photo could still fill in (the required ones and optional ones).</param>
    /// <param name="readingPhotos">A photo of the log is still being read.</param>
    public static ReviewState AfterSave(LogValues required, LogValues fillable, bool readingPhotos)
    {
        if (required != LogValues.None && !readingPhotos) throw ValuesRequired();
        return readingPhotos && fillable != LogValues.None ? ReviewState.AwaitingPhotos : ReviewState.None;
    }

    /// <summary>
    /// The state after (some of) its photos were read: still waiting while a photo is being read and something is left to fill,
    /// otherwise incomplete when a needed value is missing, to be checked when something was filled in, else done.
    /// </summary>
    public static ReviewState AfterReading(ReviewState state, LogValues required, LogValues fillable, LogValues filled, bool readingPhotos)
    {
        if (state != ReviewState.AwaitingPhotos) return state;
        if (readingPhotos && fillable != LogValues.None) return state;
        if (required != LogValues.None) return ReviewState.Incomplete;
        return filled != LogValues.None ? ReviewState.NeedsReview : ReviewState.None;
    }

    /// <summary>A value read from a photo that the log could not take anyway (a misread) is left out rather than failing.</summary>
    public static long? UsableOdometer(long? value) => value is >= 0 ? value : null;

    public static decimal? UsableVolume(decimal? value) => value is > 0 ? value : null;

    public static decimal? UsableAmount(decimal? value) => value is >= 0 ? value : null;
}
