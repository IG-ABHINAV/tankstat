using Tankstat.Domain.Access;
using Tankstat.Domain.Sync;

namespace Tankstat.Domain.Tyres;

/// <summary>
/// A tyre set's time on the car (#287): from <see cref="From"/> to <see cref="Until"/>, the day it came off (which is the day the next set
/// went on; null while it is on the car). Both stretches of that day use the same odometer value for it, so no distance is lost or counted
/// twice. A stretch lasts at least one day. A whole set records its pairs: <see cref="PairOneFront"/> true when pair 1 (the two tyres at
/// the front when the pairs were first recorded) is at the front, false when it is at the rear, null when not recorded. The vehicle and
/// owner are denormalized for access filtering and sync; the only cascading key is the set's.
/// </summary>
public sealed class TyreStretch : IOwned, ISoftDeletable, ISynced
{
    private TyreStretch() { } // EF Core

    public Guid Id { get; private set; }

    /// <summary>Always the owner of the vehicle, denormalized so access filtering works on this table directly.</summary>
    public Guid OwnerId { get; private set; }
    public Guid VehicleId { get; private set; }
    public Guid SetId { get; private set; }
    public Guid CreatedById { get; private set; }

    /// <summary>The first day on the car.</summary>
    public DateOnly From { get; private set; }

    /// <summary>The day it came off; null while it is on the car.</summary>
    public DateOnly? Until { get; private set; }

    /// <summary>True: pair 1 at the front; false: pair 2 at the front; null: not recorded.</summary>
    public bool? PairOneFront { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsDeleted => DeletedAt is not null;
    public bool IsOpen => Until is null;

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
    OfflineEntityType ISynced.SyncType => OfflineEntityType.TyreStretch;

    private void Bump(EntityChange what, Guid? by)
    {
        _loadedVersion ??= Version;
        Version++;
        (LastChange, ChangedById) = (what, by);
    }

    /// <param name="id">The id the client chose beforehand, if any (see <see cref="EntityId"/>).</param>
    public static TyreStretch Create(Guid ownerId, Guid createdById, Guid vehicleId, Guid setId, DateOnly from, DateOnly? until, bool? pairOneFront, Guid? id = null)
    {
        RequireDays(from, until);
        return new TyreStretch
        {
            Id = EntityId.OrNew(id), Version = 1, OwnerId = ownerId, CreatedById = createdById, VehicleId = vehicleId, SetId = setId,
            From = from, Until = until, PairOneFront = pairOneFront, LastChange = EntityChange.Created, ChangedById = createdById,
        };
    }

    /// <summary>Changes the days and the pairs; true when anything changed (nothing is saved otherwise).</summary>
    public bool Update(DateOnly from, DateOnly? until, bool? pairOneFront, Guid? by = null)
    {
        RequireLive();
        RequireDays(from, until);
        if (From == from && Until == until && PairOneFront == pairOneFront) return false;
        (From, Until, PairOneFront) = (from, until, pairOneFront);
        Bump(EntityChange.Edited, by);
        return true;
    }

    public void MarkDeleted(DateTimeOffset now, Guid? by = null)
    {
        if (IsDeleted) throw new DomainException("tyres.stretchAlreadyTrashed", "This stretch is already in the trash.");
        DeletedAt = now;
        Bump(EntityChange.Trashed, by);
    }

    public void Restore(Guid? by = null)
    {
        if (!IsDeleted) throw new DomainException("tyres.stretchNotTrashed", "This stretch is not in the trash.");
        DeletedAt = null;
        Bump(EntityChange.Restored, by);
    }

    private void RequireLive()
    {
        if (IsDeleted) throw new DomainException("tyres.stretchTrashedCannotEdit", "A stretch in the trash cannot be changed; restore it first.");
    }

    private static void RequireDays(DateOnly from, DateOnly? until)
    {
        if (until is { } u && u <= from)
            throw new DomainException("tyres.untilBeforeFrom", "The day it came off must be after its first day on the car.",
                new { From = from.ToString("yyyy-MM-dd"), Until = u.ToString("yyyy-MM-dd") });
    }
}
