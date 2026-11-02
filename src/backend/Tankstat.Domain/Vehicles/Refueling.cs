using Tankstat.Domain.Access;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Sync;

namespace Tankstat.Domain.Vehicles;

/// <summary>
/// A fill-up. The volume is in the vehicle's volume unit (<see cref="Vehicle.Units"/>). The odometer and the cost are not numbers of
/// their own but links to an <see cref="OdometerReading"/> and a <see cref="Cost"/> that this log owns (they are trashed, restored and
/// deleted with it). Odometer, volume and cost may be empty only while a photo of the log is still being read (see
/// <see cref="ReviewState"/>): the reading fills them in later.
/// </summary>
public sealed class Refueling : IOwned, ISoftDeletable, ISynced
{
    public const int MaxNoteLength = 500;

    private Refueling() { } // EF Core

    public Guid Id { get; private set; }

    /// <summary>Always the owner of the vehicle, denormalized so access filtering works on this table directly.</summary>
    public Guid OwnerId { get; private set; }
    public Guid VehicleId { get; private set; }

    /// <summary>Who logged it (the anonymous owner when authentication is off).</summary>
    public Guid CreatedById { get; private set; }
    public DateOnly Date { get; private set; }

    /// <summary>In the vehicle's volume unit (litres or gallons); null until a photo provides it.</summary>
    public decimal? Volume { get; private set; }

    public Guid? CostId { get; private set; }

    /// <summary>The linked cost (amount and currency), null until known; load it (Include) before reading <see cref="TotalCost"/> or <see cref="Currency"/>.</summary>
    public Cost? Cost { get; private set; }

    /// <summary>What the fill-up cost, in <see cref="Currency"/>: what was paid, after <see cref="Discount"/>.</summary>
    public decimal? TotalCost => Cost?.Amount;

    /// <summary>What was taken off at the till, in <see cref="Currency"/>; null when none (see <see cref="Measurements.Cost.Discount"/>).</summary>
    public decimal? Discount => Cost?.Discount;

    /// <summary>The currency this fill-up was paid in (it can differ from log to log, e.g. when travelling).</summary>
    public string? Currency => Cost?.Currency;
    public bool IsFullTank { get; private set; }

    /// <summary>
    /// A fill-up before this one was not logged: the fuel it added is unknown, so no consumption is worked out across the gap and the
    /// chain starts again here (see <see cref="ConsumptionCalculator"/>).
    /// </summary>
    public bool MissedPreviousFillUp { get; private set; }

    public string? Note { get; private set; }

    public Guid? OdometerReadingId { get; private set; }

    /// <summary>The linked odometer reading, null until known; load it (Include) before reading <see cref="Odometer"/>.</summary>
    public OdometerReading? OdometerReading { get; private set; }

    /// <summary>The odometer value, in the vehicle's distance unit.</summary>
    public long? Odometer => OdometerReading?.Value;

    /// <summary>
    /// Fuel used per 100 distance units, between the previous full fill-up and this one (litres per 100 km, gallons per 100 miles, ...),
    /// or null when it cannot be known: the log is not a full fill-up, no earlier full fill-up exists, a fill-up in between was not logged
    /// (<see cref="MissedPreviousFillUp"/>), or the odometer did not advance.
    /// It is stored so it is not recalculated on every read; <see cref="ConsumptionCalculator"/> refreshes it whenever the vehicle's logs change.
    /// </summary>
    public decimal? Consumption { get; private set; }

    /// <summary>Only <see cref="ConsumptionCalculator"/> should call this (consumption depends on the neighbouring logs, not on this log alone).</summary>
    public void SetConsumption(decimal? value) => Consumption = value is { } v ? Math.Round(v, 3) : null;

    public ReviewState ReviewState { get; private set; }

    /// <summary>The values its photos filled in that nobody has checked yet.</summary>
    public LogValues FilledFromPhoto { get; private set; }

    /// <summary>The values it lacks (all of them are needed).</summary>
    public LogValues Missing =>
        (OdometerReading is null ? LogValues.Odometer : LogValues.None)
        | (Volume is null ? LogValues.Volume : LogValues.None)
        | (Cost is null ? LogValues.Total : LogValues.None);

    /// <summary>Set while the log is in the trash.</summary>
    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsDeleted => DeletedAt is not null;

    /// <summary>Total cost divided by volume (derived, not stored); null while either is unknown.</summary>
    public decimal? PricePerUnit => Volume is { } volume && Cost is { } cost ? (volume == 0 ? 0 : Math.Round(cost.Amount / volume, 3)) : null;

    /// <summary>The pump's price per unit: what the fuel cost before the discount, per unit of volume (the same as <see cref="PricePerUnit"/> without one).</summary>
    public decimal? PricePerUnitBeforeDiscount => Volume is { } volume && Cost is { } cost ? (volume == 0 ? 0 : Math.Round(cost.AmountBeforeDiscount / volume, 3)) : null;

    /// <summary>
    /// Counts the saves of what a client can edit (the values, the trash, what a photo filled in), from 1: a client that edits an old copy
    /// says which version it started from, so a change made meanwhile is noticed.
    /// The consumption (derived from the neighbours) and the photos do not count.
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
    OfflineEntityType ISynced.SyncType => OfflineEntityType.Refueling;

    /// <param name="reading">The reading of the odometer at this fill-up; created for this log, belonging to the same vehicle.</param>
    /// <param name="readingPhotos">A photo of the log is still being read: only then may values be left empty.</param>
    /// <param name="id">The id the client chose beforehand, if any (see <see cref="EntityId"/>).</param>
    public static Refueling Create(
        Guid ownerId, Guid createdById, Guid vehicleId, DateOnly date, decimal? volume, Cost? cost, OdometerReading? reading, bool isFullTank,
        bool missedPreviousFillUp, string? note = null, bool readingPhotos = false, Guid? id = null)
    {
        if ((reading is not null && reading.VehicleId != vehicleId) || (cost is not null && cost.VehicleId != vehicleId))
            throw new DomainException("refueling.wrongVehicle", "The odometer reading and the cost must belong to the same vehicle as the log.");

        var refueling = new Refueling
        {
            Id = EntityId.OrNew(id), Version = 1, OwnerId = ownerId, CreatedById = createdById, LastChange = EntityChange.Created, ChangedById = createdById, VehicleId = vehicleId,
            OdometerReadingId = reading?.Id, OdometerReading = reading, CostId = cost?.Id, Cost = cost,
        };
        refueling.Apply(date, volume, isFullTank, missedPreviousFillUp, note);
        reading?.Update(date, reading.Value);
        cost?.Update(date, cost.Amount, cost.Currency, cost.Discount);
        refueling.ReviewState = LogReview.AfterSave(refueling.Missing, refueling.Missing, readingPhotos);
        return refueling;
    }

    /// <summary>
    /// Changes the log and keeps its odometer reading and cost (date and value) in step. A person saved it, so values read from photos
    /// count as checked. Readings and costs that come or go are returned for the repository.
    /// </summary>
    /// <param name="currency">The currency of the cost; ignored without <paramref name="totalCost"/>.</param>
    /// <param name="by">Who changed it (see <see cref="ChangedById"/>).</param>
    /// <param name="readingPhotos">A photo of the log is still being read: only then may values be left empty.</param>
    /// <param name="discount">What was taken off <paramref name="totalCost"/> at the till; null, the usual case, means none (<c>cost.discountWithoutAmount</c> without a total).</param>
    public LinkedChanges Update(
        DateOnly date, decimal? volume, decimal? totalCost, string? currency, long? odometer, bool isFullTank, bool missedPreviousFillUp, string? note,
        bool readingPhotos = false, Guid? by = null, decimal? discount = null)
    {
        if (IsDeleted) throw new DomainException("refueling.trashedCannotEdit", "A log in the trash cannot be edited; restore it first.");
        Measurements.Cost.RequireAmountForDiscount(totalCost, discount);
        Apply(date, volume, isFullTank, missedPreviousFillUp, note);
        var (createdReading, removedReading) = SetReading(date, odometer);
        var (createdCost, removedCost) = SetCost(date, totalCost, currency, discount);
        ReviewState = LogReview.AfterSave(Missing, Missing, readingPhotos);
        FilledFromPhoto = LogValues.None;
        Bump(EntityChange.Edited, by);
        return new LinkedChanges(createdReading, removedReading, createdCost, removedCost);
    }

    /// <summary>
    /// Fills in the empty values its photos showed, while it waits for them; values a person entered are never replaced. The reading is
    /// not checked against the vehicle's other readings: the person who checks the log decides.
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
        if (Volume is null && LogReview.UsableVolume(values.Volume) is { } volume)
        {
            Volume = volume;
            FilledFromPhoto |= LogValues.Volume;
        }
        if (Cost is null && LogReview.UsableAmount(values.Total) is { } total && values.Currency is { } currency)
        {
            // The discount only with the total it was taken off: a cost a person entered keeps what they said (an empty discount is none).
            var discount = LogReview.UsableAmount(values.Discount);
            createdCost = Cost.Create(OwnerId, VehicleId, Date, total, currency, discount);
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
        ReviewState = LogReview.AfterReading(ReviewState, Missing, Missing, FilledFromPhoto, readingPhotos);
        return ReviewState != before;
    }

    public void MarkDeleted(DateTimeOffset now, Guid? by = null)
    {
        if (IsDeleted) throw new DomainException("refueling.alreadyTrashed", "This log is already in the trash.");
        DeletedAt = now;
        OdometerReading?.MarkDeleted(now);
        Cost?.MarkDeleted(now);
        Bump(EntityChange.Trashed, by);
    }

    public void Restore(Guid? by = null)
    {
        if (!IsDeleted) throw new DomainException("refueling.notTrashed", "This log is not in the trash.");
        DeletedAt = null;
        OdometerReading?.Restore();
        Cost?.Restore();
        Bump(EntityChange.Restored, by);
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

    private void Apply(DateOnly date, decimal? volume, bool isFullTank, bool missedPreviousFillUp, string? note)
    {
        if (volume is <= 0) throw new DomainException("refueling.volumePositive", "The volume must be greater than zero.");
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > MaxNoteLength })
            throw new DomainException("refueling.noteTooLong", $"The note can be at most {MaxNoteLength} characters.", new { Max = MaxNoteLength });

        Date = date;
        Volume = volume;
        IsFullTank = isFullTank;
        MissedPreviousFillUp = missedPreviousFillUp;
        Note = trimmed;
    }
}
