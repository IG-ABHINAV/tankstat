using Tankstat.Domain.Access;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Sync;

namespace Tankstat.Domain.Vehicles;

/// <summary>
/// Money spent on a vehicle that is not a fill-up: service, insurance, parking, tolls, ... Like a refuelling it does not carry
/// an amount or odometer number of its own but owns a <see cref="Cost"/> and, when the odometer was noted, an
/// <see cref="OdometerReading"/> (they are trashed, restored and deleted with it). The category is free text, so whatever an
/// import brings along (or the user types) is kept as is. The amount may be empty only while a photo of the expense is still being
/// read (see <see cref="ReviewState"/>); the reading fills it in later, and the odometer too when it was not noted.
/// </summary>
public sealed class Expense : IOwned, ISoftDeletable, ISynced
{
    public const int MaxTitleLength = 120;
    public const int MaxCategoryLength = 60;
    public const int MaxNoteLength = 500;

    private Expense() { } // EF Core

    public Guid Id { get; private set; }

    /// <summary>Always the owner of the vehicle, denormalized so access filtering works on this table directly.</summary>
    public Guid OwnerId { get; private set; }
    public Guid VehicleId { get; private set; }

    /// <summary>Who logged it (the anonymous owner when authentication is off).</summary>
    public Guid CreatedById { get; private set; }
    public DateOnly Date { get; private set; }
    public string Title { get; private set; } = "";
    public string? Category { get; private set; }
    public string? Note { get; private set; }

    public Guid? CostId { get; private set; }

    /// <summary>The linked cost, null until known; load it (Include) before reading <see cref="Amount"/> or <see cref="Currency"/>.</summary>
    public Cost? Cost { get; private set; }
    public decimal? Amount => Cost?.Amount;
    public string? Currency => Cost?.Currency;

    /// <summary>What was taken off the amount (the amount is what was paid); null when none.</summary>
    public decimal? Discount => Cost?.Discount;

    public Guid? OdometerReadingId { get; private set; }

    /// <summary>The linked reading, if the odometer was noted; load it (Include) before reading <see cref="Odometer"/>.</summary>
    public OdometerReading? OdometerReading { get; private set; }

    /// <summary>The odometer value in the vehicle's distance unit, or null when it was not noted.</summary>
    public long? Odometer => OdometerReading?.Value;

    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsDeleted => DeletedAt is not null;

    public ReviewState ReviewState { get; private set; }

    /// <summary>The values its photos filled in that nobody has checked yet (odometer and total).</summary>
    public LogValues FilledFromPhoto { get; private set; }

    /// <summary>The values it needs but lacks: only the amount (the odometer is optional).</summary>
    public LogValues Missing => Cost is null ? LogValues.Total : LogValues.None;

    /// <summary>The empty values a photo could fill in: the amount and an odometer that was not noted.</summary>
    public LogValues Fillable => Missing | (OdometerReading is null ? LogValues.Odometer : LogValues.None);

    /// <summary>
    /// Counts the saves of what a client can edit (the values, the trash, what a photo filled in), from 1: a client that edits an old copy
    /// says which version it started from, so a change made meanwhile is noticed. The photos do not count.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>When it was last saved (set by the persistence layer, see <see cref="ISynced"/>): a device that keeps a copy downloads it again.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <inheritdoc cref="ISynced.ChangedAt"/>
    public DateTimeOffset? ChangedAt { get; private set; }

    /// <inheritdoc cref="ISynced.ChangedById"/>
    public Guid? ChangedById { get; private set; }

    /// <inheritdoc cref="ISynced.LastChange"/>
    public EntityChange? LastChange { get; private set; }

    /// <summary>The version it was loaded with, before this instance counted any save: the save is made only if nobody saved it meanwhile.</summary>
    public int SavedVersion => _loadedVersion ?? Version;

    private int? _loadedVersion;

    void ISynced.Saved() => _loadedVersion = null;

    private void Bump(EntityChange what, Guid? by)
    {
        _loadedVersion ??= Version;
        Version++;
        (LastChange, ChangedById) = (what, by);
    }

    Guid ISynced.SyncVehicleId => VehicleId;
    OfflineEntityType ISynced.SyncType => OfflineEntityType.Expense;

    /// <param name="readingPhotos">A photo of the expense is still being read: only then may the amount be left empty.</param>
    /// <param name="id">The id the client chose beforehand, if any (see <see cref="EntityId"/>).</param>
    public static Expense Create(
        Guid ownerId, Guid createdById, Guid vehicleId, DateOnly date, string title, string? category, Cost? cost, OdometerReading? reading,
        string? note = null, bool readingPhotos = false, Guid? id = null)
    {
        if ((cost is not null && cost.VehicleId != vehicleId) || (reading is not null && reading.VehicleId != vehicleId))
            throw new DomainException("expense.wrongVehicle", "The odometer reading and the cost must belong to the same vehicle as the expense.");

        var expense = new Expense
        {
            Id = EntityId.OrNew(id), Version = 1, OwnerId = ownerId, CreatedById = createdById, LastChange = EntityChange.Created, ChangedById = createdById, VehicleId = vehicleId,
            CostId = cost?.Id, Cost = cost, OdometerReadingId = reading?.Id, OdometerReading = reading,
        };
        expense.Apply(date, title, category, note);
        cost?.Update(date, cost.Amount, cost.Currency, cost.Discount);
        reading?.Update(date, reading.Value);
        expense.ReviewState = LogReview.AfterSave(expense.Missing, expense.Fillable, readingPhotos);
        return expense;
    }

    /// <summary>
    /// Changes the expense. <paramref name="odometer"/> null means "not noted" and <paramref name="amount"/> null "not known yet" (only
    /// while a photo is being read): a reading or cost let go of, or one that had to be created, is returned for the repository. A person
    /// saved it, so values read from photos count as checked.
    /// </summary>
    /// <param name="by">Who changed it (see <see cref="ChangedById"/>).</param>
    /// <param name="currency">The currency of the cost; ignored without <paramref name="amount"/>.</param>
    /// <param name="discount">What was taken off <paramref name="amount"/>, which is what was paid; null is none (it needs an amount).</param>
    public LinkedChanges Update(
        DateOnly date, string title, string? category, decimal? amount, string? currency, long? odometer, string? note, bool readingPhotos = false, Guid? by = null,
        decimal? discount = null) =>
        Edit(date, title, category, amount, currency, note, readingPhotos, by, discount, () => SetReading(date, odometer));

    /// <summary>A person's edit: everything but the reading, which <paramref name="setReading"/> sets once the cost is (its own, or its line's).</summary>
    private LinkedChanges Edit(
        DateOnly date, string title, string? category, decimal? amount, string? currency, string? note, bool readingPhotos, Guid? by, decimal? discount,
        Func<(OdometerReading? Created, OdometerReading? Removed)> setReading)
    {
        if (IsDeleted) throw new DomainException("expense.trashedCannotEdit", "An expense in the trash cannot be edited; restore it first.");
        Cost.RequireAmountForDiscount(amount, discount);
        Apply(date, title, category, note);
        var (createdCost, removedCost) = SetCost(date, amount, currency, discount);
        var (createdReading, removedReading) = setReading();
        ReviewState = LogReview.AfterSave(Missing, Fillable, readingPhotos);
        FilledFromPhoto = LogValues.None;
        Bump(EntityChange.Edited, by);
        return new LinkedChanges(createdReading, removedReading, createdCost, removedCost);
    }

    /// <summary>
    /// Fills in the empty values its photos showed (the total, and the odometer when it was not noted) while it waits for them; values a
    /// person entered are never replaced, and the reading is not checked against the vehicle's other readings.
    /// </summary>
    public LinkedChanges FillFromPhoto(PhotoValues values)
    {
        if (ReviewState != ReviewState.AwaitingPhotos || IsDeleted) return LinkedChanges.None;
        var before = FilledFromPhoto;
        OdometerReading? createdReading = null;
        Cost? createdCost = null;
        if (OdometerReading is null && LogReview.UsableOdometer(values.Odometer) is { } odometer)
        {
            createdReading = OdometerReading.Create(OwnerId, VehicleId, Date, odometer);
            (OdometerReading, OdometerReadingId) = (createdReading, createdReading.Id);
            FilledFromPhoto |= LogValues.Odometer;
        }
        if (Cost is null && LogReview.UsableAmount(values.Total) is { } total && values.Currency is { } currency)
        {
            // The discount only with the total it was taken off: a cost a person entered keeps what they said (an empty discount is none).
            createdCost = Cost.Create(OwnerId, VehicleId, Date, total, currency, LogReview.UsableAmount(values.Discount));
            (Cost, CostId) = (createdCost, createdCost.Id);
            FilledFromPhoto |= LogValues.Total | (createdCost.Discount is null ? LogValues.None : LogValues.Discount);
        }
        // Every value a photo fills in sets its FilledFromPhoto flag, so a change of the flags is a change of the values: an edit of the copy
        // from before is stale. A value added here later must set its flag too.
        if (FilledFromPhoto != before) Bump(EntityChange.FilledFromPhoto, null);
        return new LinkedChanges(createdReading, null, createdCost, null);
    }

    /// <summary>Moves on once its photos were read (or while some still are); true when the state changed.</summary>
    public bool FinishReading(bool readingPhotos)
    {
        var before = ReviewState;
        ReviewState = LogReview.AfterReading(ReviewState, Missing, Fillable, FilledFromPhoto, readingPhotos);
        return ReviewState != before;
    }

    public void MarkDeleted(DateTimeOffset now, Guid? by = null)
    {
        if (IsDeleted) throw new DomainException("expense.alreadyTrashed", "This expense is already in the trash.");
        DeletedAt = now;
        OdometerReading?.MarkDeleted(now);
        Cost?.MarkDeleted(now);
        Bump(EntityChange.Trashed, by);
    }

    public void Restore(Guid? by = null)
    {
        if (!IsDeleted) throw new DomainException("expense.notTrashed", "This expense is not in the trash.");
        DeletedAt = null;
        OdometerReading?.Restore();
        Cost?.Restore();
        Bump(EntityChange.Restored, by);
    }

    /// <summary>
    /// A person's edit of the expense of a tyre log line (#288), as <see cref="Update"/>, except that the reading is the line's (already set
    /// to the date and value asked for, or none), never one of its own.
    /// </summary>
    public LinkedChanges UpdateWithLineReading(
        DateOnly date, string title, string? category, decimal? amount, string? currency, OdometerReading? lineReading, string? note, Guid? by, decimal? discount,
        bool readingPhotos = false) =>
        Edit(date, title, category, amount, currency, note, readingPhotos, by, discount, () =>
        {
            (OdometerReading, OdometerReadingId) = (lineReading, lineReading?.Id);
            return (null, null);
        });

    /// <summary>Lets go of the reading it shares with a line in the trash (#288), keeping the row for the line: an edit then gives it its own.</summary>
    public void LeaveSharedReading() => (OdometerReading, OdometerReadingId) = (null, null);

    /// <summary>The same reading row loaded once with its tyre log line (#288): the expense holds the line's object, so the two are saved as one.</summary>
    public void ShareReadingObject(OdometerReading reading)
    {
        if (OdometerReadingId == reading.Id) OdometerReading = reading;
    }

    /// <summary>
    /// The expense of a tyre log line (#288) follows its line: its date (its cost's too), the reading the two share (the very object, or
    /// none), and, when the line's dialog sent them, the amount, the currency and the category (the discount kept). Not a review of its
    /// photos, so their state stays as it was. True when anything changed; a change bumps the version like an edit. An amount for an
    /// expense that has none yet (it waits for its photo's) makes its <see cref="Cost"/>, which the caller hands to the repository as new.
    /// </summary>
    /// <param name="category">Set when <paramref name="setCategory"/>, null clearing it.</param>
    public bool FollowLine(
        DateOnly date, OdometerReading? reading, decimal? amount = null, string? currency = null, string? category = null, bool setCategory = false, Guid? by = null)
    {
        var trimmedCategory = setCategory ? (string.IsNullOrWhiteSpace(category) ? null : category.Trim()) : Category;
        if (trimmedCategory is { Length: > MaxCategoryLength })
            throw new DomainException("expense.categoryTooLong", $"The category can be at most {MaxCategoryLength} characters.", new { Max = MaxCategoryLength });
        var newAmount = amount ?? Cost?.Amount;
        var newCurrency = amount is null ? Cost?.Currency : currency ?? Cost?.Currency;
        var changed = Date != date || OdometerReadingId != reading?.Id || trimmedCategory != Category
            || (Cost is { } cost ? cost.Amount != newAmount || cost.Currency != newCurrency || cost.Date != date : amount is not null);
        (OdometerReading, OdometerReadingId) = (reading, reading?.Id); // the same object as the line's (or the same row loaded once)
        if (!changed) return false;
        Date = date;
        Category = trimmedCategory;
        if (Cost is not null) Cost.Update(date, newAmount!.Value, newCurrency, Cost.Discount);
        else if (amount is { } typed) SetCost(date, typed, currency, discount: null);
        Bump(EntityChange.Edited, by);
        return true;
    }

    private (OdometerReading? Created, OdometerReading? Removed) SetReading(DateOnly date, long? odometer)
    {
        if (odometer is null)
        {
            var removed = OdometerReading;
            (OdometerReading, OdometerReadingId) = (null, null);
            return (null, removed);
        }
        if (OdometerReading is null)
        {
            var created = OdometerReading.Create(OwnerId, VehicleId, date, odometer.Value);
            (OdometerReading, OdometerReadingId) = (created, created.Id);
            return (created, null);
        }
        OdometerReading.Update(date, odometer.Value);
        return (null, null);
    }

    private (Cost? Created, Cost? Removed) SetCost(DateOnly date, decimal? amount, string? currency, decimal? discount)
    {
        if (amount is null)
        {
            var removed = Cost;
            (Cost, CostId) = (null, null);
            return (null, removed);
        }
        if (Cost is null)
        {
            var created = Cost.Create(OwnerId, VehicleId, date, amount.Value, currency, discount);
            (Cost, CostId) = (created, created.Id);
            return (created, null);
        }
        Cost.Update(date, amount.Value, currency, discount);
        return (null, null);
    }

    private void Apply(DateOnly date, string title, string? category, string? note)
    {
        var trimmedTitle = title?.Trim() ?? "";
        if (trimmedTitle.Length == 0) throw new DomainException("expense.titleRequired", "The expense needs a title.");
        if (trimmedTitle.Length > MaxTitleLength)
            throw new DomainException("expense.titleTooLong", $"The title can be at most {MaxTitleLength} characters.", new { Max = MaxTitleLength });
        var trimmedCategory = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        if (trimmedCategory is { Length: > MaxCategoryLength })
            throw new DomainException("expense.categoryTooLong", $"The category can be at most {MaxCategoryLength} characters.", new { Max = MaxCategoryLength });
        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote is { Length: > MaxNoteLength })
            throw new DomainException("expense.noteTooLong", $"The note can be at most {MaxNoteLength} characters.", new { Max = MaxNoteLength });

        Date = date;
        Title = trimmedTitle;
        Category = trimmedCategory;
        Note = trimmedNote;
    }
}
