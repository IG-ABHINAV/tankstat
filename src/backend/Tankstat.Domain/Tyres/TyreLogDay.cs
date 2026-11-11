using Tankstat.Domain.Access;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Sync;

namespace Tankstat.Domain.Tyres;

/// <summary>
/// The tyre log's line for a day (#287): it owns the odometer reading typed with a swap, a past swap or a stretch on that day (one of the
/// car's readings, so it counts wherever the others do and is checked against them), and a note. One per vehicle and day, created only when
/// a reading or a note is given. It is never trashed: clearing both deletes it, and clearing the reading deletes the reading for good, as
/// clearing an expense's odometer does.
/// </summary>
public sealed class TyreLogDay : IOwned, ISynced
{
    public const int MaxNoteLength = 500;

    private TyreLogDay() { } // EF Core

    public Guid Id { get; private set; }

    /// <summary>Always the owner of the vehicle, denormalized so access filtering works on this table directly.</summary>
    public Guid OwnerId { get; private set; }
    public Guid VehicleId { get; private set; }
    public Guid CreatedById { get; private set; }
    public DateOnly Day { get; private set; }
    public string? Note { get; private set; }

    public Guid? OdometerReadingId { get; private set; }

    /// <summary>The reading typed that day, if any; load it (Include) before reading <see cref="Odometer"/>.</summary>
    public OdometerReading? OdometerReading { get; private set; }

    public long? Odometer => OdometerReading?.Value;

    /// <summary>Neither a reading nor a note: the day is deleted.</summary>
    public bool IsEmpty => OdometerReading is null && Note is null;

    /// <summary>Counts the saves of what a client can edit, from 1 (see <see cref="VersionCheck"/>).</summary>
    public int Version { get; private set; }

    /// <inheritdoc cref="ISynced.UpdatedAt"/>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <inheritdoc cref="ISynced.ChangedAt"/>
    public DateTimeOffset? ChangedAt { get; private set; }

    /// <inheritdoc cref="ISynced.ChangedById"/>
    public Guid? ChangedById { get; private set; }

    /// <inheritdoc cref="ISynced.LastChange"/>
    public EntityChange? LastChange { get; private set; }

    /// <inheritdoc cref="ISynced.SavedVersion"/>
    public int SavedVersion => _loadedVersion ?? Version;

    private int? _loadedVersion;

    void ISynced.Saved() => _loadedVersion = null;

    Guid ISynced.SyncVehicleId => VehicleId;
    OfflineEntityType ISynced.SyncType => OfflineEntityType.TyreLogDay;

    /// <param name="id">The id the client chose beforehand, if any (see <see cref="EntityId"/>).</param>
    public static TyreLogDay Create(Guid ownerId, Guid createdById, Guid vehicleId, DateOnly day, long? odometer, string? note, Guid? id = null)
    {
        var logDay = new TyreLogDay
        {
            Id = EntityId.OrNew(id), Version = 1, OwnerId = ownerId, CreatedById = createdById, VehicleId = vehicleId, Day = day,
            LastChange = EntityChange.Created, ChangedById = createdById,
        };
        logDay.Note = Trimmed(note);
        if (odometer is { } value)
        {
            logDay.OdometerReading = OdometerReading.Create(ownerId, vehicleId, day, value);
            logDay.OdometerReadingId = logDay.OdometerReading.Id;
        }
        return logDay;
    }

    /// <summary>Sets the reading (null clears it) and the note; returns the reading created or let go of, for the repository.</summary>
    public (OdometerReading? Created, OdometerReading? Removed) Update(long? odometer, string? note, Guid? by = null)
    {
        var trimmed = Trimmed(note);
        OdometerReading? created = null, removed = null;
        if (odometer is null && OdometerReading is not null)
        {
            removed = OdometerReading;
            (OdometerReading, OdometerReadingId) = (null, null);
        }
        else if (odometer is { } value && OdometerReading is null)
        {
            created = OdometerReading.Create(OwnerId, VehicleId, Day, value);
            (OdometerReading, OdometerReadingId) = (created, created.Id);
        }
        else if (odometer is { } changed && OdometerReading is { } reading && reading.Value != changed) reading.Update(Day, changed);
        else if (trimmed == Note) return (null, null);
        Note = trimmed;
        _loadedVersion ??= Version;
        Version++;
        (LastChange, ChangedById) = (EntityChange.Edited, by);
        return (created, removed);
    }

    private static string? Trimmed(string? note)
    {
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > MaxNoteLength })
            throw new DomainException("tyres.noteTooLong", $"The note can be at most {MaxNoteLength} characters.", new { Max = MaxNoteLength });
        return trimmed;
    }
}
