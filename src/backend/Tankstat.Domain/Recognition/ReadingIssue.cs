namespace Tankstat.Domain.Recognition;

/// <summary>
/// Why a photo gave less than it might have: a value the photo may hold was not taken, or only taken with doubt. Kept with the reading
/// as codes (never the value that was read) so the log and the add dialog can say what happened instead of "nothing could be read".
/// </summary>
public enum ReadingIssueCode
{
    /// <summary>The model took the photo for something this entry has no use for, or for nothing at all (<c>unknown</c>).</summary>
    Unrecognised,

    /// <summary>The model knew what the photo shows but listed no value that it could read.</summary>
    NothingLegible,

    /// <summary>The value is not written the way the app takes it (an odometer that is not a plain number, a date that is none): dropped.</summary>
    NotUnderstood,

    /// <summary>A number no fill-up, price or total can be (litres of a lorry's tank times ten, a total of millions): dropped.</summary>
    OutOfRange,

    /// <summary>An odometer reading lower than the vehicle's latest one: it never goes back, so it was dropped.</summary>
    OdometerBelowLatest,

    /// <summary>An odometer reading more than 100 000 above the vehicle's latest one: not the same car between two logs, dropped.</summary>
    OdometerTooFarAbove,

    /// <summary>An odometer reading 50 000 to 100 000 above the vehicle's latest one: possible, but not to be believed on its own (doubted).</summary>
    OdometerFarAbove,

    /// <summary>A receipt date more than three years back: dropped.</summary>
    DateTooOld,

    /// <summary>A receipt date after tomorrow: dropped.</summary>
    DateInFuture,

    /// <summary>Litres times price per litre do not fit the total: the litres and the price are doubted, the total stays.</summary>
    AmountsDoNotAdd,

    /// <summary>
    /// Litres times price per litre fit the total without the discount read with it: either the total is before the discount or the
    /// discount was misread, which the numbers cannot tell apart, so the total and the discount are both doubted.
    /// </summary>
    DiscountNotTakenOff,

    /// <summary>The model gave the value no usable confidence, so it counts as doubtful.</summary>
    NoConfidence,

    /// <summary>The model rated the value under what the app fills in (<c>Recognition:MinConfidence</c>). Worked out when readings are listed, since it depends on that setting; never stored.</summary>
    Unsure,
}

/// <param name="Field">The value concerned (none when it is about the photo as a whole).</param>
public sealed record ReadingIssue(ReadingFieldName? Field, ReadingIssueCode Code);
