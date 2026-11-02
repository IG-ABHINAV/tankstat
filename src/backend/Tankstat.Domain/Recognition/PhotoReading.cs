namespace Tankstat.Domain.Recognition;

/// <summary>What a photo was picked for, which decides what it may show: an odometer, or a receipt of that kind.</summary>
public enum ReadingPurpose
{
    Refueling,
    Expense,
}

public enum ReadingStatus
{
    /// <summary>Waiting for the provider (again, after a failed attempt, once <see cref="PhotoReading.DueAt"/> has come).</summary>
    Queued,
    Reading,
    Read,
    /// <summary>Given up: the photo cannot be read, or the provider stayed unavailable for every attempt.</summary>
    Failed,
}

/// <summary>What a photo turned out to show.</summary>
public enum DocumentKind
{
    Odometer,
    FuelReceipt,
    ExpenseReceipt,
    Unknown,
}

/// <summary>The values a photo can provide.</summary>
public enum ReadingFieldName
{
    Odometer,
    Total,
    Volume,
    UnitPrice,
    Currency,
    Date,
    Title,

    /// <summary>What a fuel receipt says was taken off the fuel (a positive amount; the total is what was paid after it).</summary>
    Discount,
}

/// <summary>Where a value came from: read on the photo, worked out from other values, or the hint it was given coming back (readings made by the former photo reader hold some; they are never filled in).</summary>
public enum ValueSource
{
    Read,
    Derived,
    Hint,
}

/// <summary>
/// One value read from a photo, normalised: invariant numbers (<c>38.52</c>), ISO dates, upper-case currency codes, trimmed titles.
/// <see cref="Confidence"/> (0 to 1) is how sure the provider was.
/// </summary>
public sealed record ReadingValue(ReadingFieldName Name, string Value, double Confidence, ValueSource Source)
{
    public double Confidence { get; } = Math.Clamp(Confidence, 0, 1);
}

/// <summary>
/// The reading of one uploaded photo by the recognition provider (an optional, separate service): queued when the photo arrives,
/// read in the background, then kept with what was found. Its id is the picture's (<c>StoredImage</c>) id, and it goes away with the
/// picture. The hints the reading is checked against are taken when the photo is uploaded, because the background worker has no user to ask.
/// </summary>
public sealed class PhotoReading
{
    /// <summary>Attempts before a reading counts as failed; a busy or unreachable provider is tried again later.</summary>
    public const int MaxAttempts = 5;

    /// <summary>What is kept of a model's name: a server can call one by a whole path, the column holds this much.</summary>
    public const int MaxModelVersionLength = 64;

    private PhotoReading() { } // EF Core

    /// <summary>The id of the picture that is read.</summary>
    public Guid Id { get; private set; }
    public ReadingPurpose Purpose { get; private set; }
    public ReadingStatus Status { get; private set; }

    /// <summary>How numbers and dates are written (<c>hu</c>, <c>en</c>, <c>de</c>): the language the user works in.</summary>
    public string Locale { get; private set; } = "en";
    /// <summary>The vehicle's latest odometer reading when the photo was uploaded (a lower number cannot be its odometer).</summary>
    public long? LastOdometer { get; private set; }
    /// <summary>The currency of the vehicle's latest log, for receipts that do not show one.</summary>
    public string? Currency { get; private set; }
    /// <summary>The day the photo was uploaded: receipt dates far from it are not believed.</summary>
    public DateOnly Today { get; private set; }

    public int Attempts { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>When a queued reading may be tried (again).</summary>
    public DateTimeOffset DueAt { get; private set; }
    /// <summary>When the current (or last) attempt started.</summary>
    public DateTimeOffset? ClaimedAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }

    public DocumentKind? Kind { get; private set; }
    /// <summary>Who read it (<c>openai-compatible</c>: a model behind such an API) and with which model, to tell readings apart later.</summary>
    public string? Provider { get; private set; }
    public string? ModelVersion { get; private set; }
    public IReadOnlyList<ReadingValue> Values { get; private set; } = [];

    /// <summary>Why the photo gave less than it might have: values dropped or doubted, or nothing to read (codes only, never what was read).</summary>
    public IReadOnlyList<ReadingIssue> Issues { get; private set; } = [];

    public static PhotoReading Queue(
        Guid imageId, ReadingPurpose purpose, string locale, long? lastOdometer, string? currency, DateOnly today, DateTimeOffset now)
    {
        if (!Enum.IsDefined(purpose)) throw new DomainException("reading.unknownPurpose", $"Unknown reading purpose '{purpose}'.", new { Purpose = purpose.ToString() });
        return new PhotoReading
        {
            Id = imageId,
            Purpose = purpose,
            Status = ReadingStatus.Queued,
            Locale = locale,
            LastOdometer = lastOdometer,
            Currency = currency,
            Today = today,
            CreatedAt = now,
            DueAt = now,
        };
    }

    /// <summary>What the photo may show, given what it was picked for.</summary>
    public IReadOnlySet<DocumentKind> AllowedKinds => Purpose == ReadingPurpose.Refueling
        ? new HashSet<DocumentKind> { DocumentKind.Odometer, DocumentKind.FuelReceipt }
        : new HashSet<DocumentKind> { DocumentKind.Odometer, DocumentKind.ExpenseReceipt };

    public bool IsDue(DateTimeOffset now) => Status == ReadingStatus.Queued && DueAt <= now;

    /// <summary>An attempt starts (the repository does the same in one atomic update, so only one worker gets it).</summary>
    public void Claim(DateTimeOffset now)
    {
        if (Status != ReadingStatus.Queued) throw new InvalidOperationException($"Reading {Id} is {Status}, not queued.");
        Status = ReadingStatus.Reading;
        Attempts++;
        ClaimedAt = now;
    }

    /// <summary>
    /// The provider answered. A kind the photo may not show (a receipt in the odometer-only case) counts as unknown, without values, and
    /// is said to be so among the <paramref name="issues"/>.
    /// </summary>
    public void Complete(string provider, string modelVersion, DocumentKind kind, IEnumerable<ReadingValue> values, DateTimeOffset now, IEnumerable<ReadingIssue>? issues = null)
    {
        RequireReading();
        var allowed = kind != DocumentKind.Unknown && AllowedKinds.Contains(kind);
        Status = ReadingStatus.Read;
        Provider = provider;
        ModelVersion = modelVersion.Length <= MaxModelVersionLength ? modelVersion : modelVersion[..MaxModelVersionLength];
        Kind = allowed ? kind : DocumentKind.Unknown;
        Values = allowed ? values.GroupBy(v => v.Name).Select(g => g.MaxBy(v => v.Confidence)!).ToList() : [];
        var why = (issues ?? []).Distinct().ToList();
        if (!allowed && !why.Any(i => i.Code == ReadingIssueCode.Unrecognised)) why.Add(new ReadingIssue(null, ReadingIssueCode.Unrecognised));
        Issues = why;
        ReadAt = now;
    }

    /// <summary>The provider could not read it now (unreachable, busy, timed out): queued again a while later, or failed after the last attempt.</summary>
    public void Retry(DateTimeOffset now)
    {
        RequireReading();
        if (Attempts >= MaxAttempts)
        {
            Status = ReadingStatus.Failed;
            return;
        }
        Status = ReadingStatus.Queued;
        DueAt = now + Backoff(Attempts);
    }

    /// <summary>The photo cannot be read at all (the provider refused it): no further attempts.</summary>
    public void Fail()
    {
        RequireReading();
        Status = ReadingStatus.Failed;
    }

    /// <summary>The attempt was cut short (the app stopped while reading): queued again right away, unless it was the last attempt.</summary>
    public void Abandon(DateTimeOffset now)
    {
        RequireReading();
        Status = Attempts >= MaxAttempts ? ReadingStatus.Failed : ReadingStatus.Queued;
        DueAt = now;
    }

    /// <summary>10 seconds after the first attempt, then three times longer each time (30 s, 1.5 min, 4.5 min).</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(10 * Math.Pow(3, Math.Max(0, attempts - 1)));

    private void RequireReading()
    {
        if (Status != ReadingStatus.Reading) throw new InvalidOperationException($"Reading {Id} is {Status}, not being read.");
    }
}
