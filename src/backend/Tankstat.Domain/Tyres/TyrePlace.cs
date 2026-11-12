using Tankstat.Domain.Access;
using Tankstat.Domain.Sync;

namespace Tankstat.Domain.Tyres;

/// <summary>
/// A single tyre's time on the car (#289): from <see cref="From"/> to <see cref="Until"/> (the day it came off, null while on), at a
/// <see cref="Corner"/> (null: not recorded), always inside one installation of its set (<see cref="InstallationId"/>): a tyre is on the car
/// only while its set is. A place is live while it, its tyre, its installation and its set are; trashing the tyre or the installation hides it
/// through them. The tyre's key is the only cascading one (a key to the installation as well would give SQL Server two cascade paths).
/// </summary>
public sealed class TyrePlace : IOwned, ISoftDeletable, ISynced
{
    private TyrePlace() { } // EF Core

    public Guid Id { get; private set; }

    /// <summary>Always the owner of the vehicle, denormalized so access filtering works on this table directly.</summary>
    public Guid OwnerId { get; private set; }
    public Guid VehicleId { get; private set; }
    public Guid TyreId { get; private set; }
    public Guid InstallationId { get; private set; }
    public Guid CreatedById { get; private set; }

    public DateOnly From { get; private set; }
    public DateOnly? Until { get; private set; }
    public TyreCorner? Corner { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsDeleted => DeletedAt is not null;

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
    OfflineEntityType ISynced.SyncType => OfflineEntityType.TyrePlace;

    private void Bump(EntityChange what, Guid? by)
    {
        _loadedVersion ??= Version;
        Version++;
        (LastChange, ChangedById) = (what, by);
    }

    /// <param name="id">The id the client chose, or the one <see cref="TyreIds.PlaceOf"/> works out for a place a change makes by itself.</param>
    public static TyrePlace Create(
        Guid ownerId, Guid createdById, Guid vehicleId, Guid tyreId, Guid installationId, DateOnly from, DateOnly? until, TyreCorner? corner, Guid? id = null)
    {
        RequireDays(from, until);
        RequireCorner(corner);
        return new TyrePlace
        {
            Id = EntityId.OrNew(id), Version = 1, OwnerId = ownerId, CreatedById = createdById, VehicleId = vehicleId, TyreId = tyreId,
            InstallationId = installationId, From = from, Until = until, Corner = corner, LastChange = EntityChange.Created, ChangedById = createdById,
        };
    }

    /// <summary>Changes the days, the corner and the installation it lies in; true when anything changed.</summary>
    public bool Update(Guid installationId, DateOnly from, DateOnly? until, TyreCorner? corner, Guid? by = null)
    {
        RequireLive();
        RequireDays(from, until);
        RequireCorner(corner);
        if (InstallationId == installationId && From == from && Until == until && Corner == corner) return false;
        (InstallationId, From, Until, Corner) = (installationId, from, until, corner);
        Bump(EntityChange.Edited, by);
        return true;
    }

    public void MarkDeleted(DateTimeOffset now, Guid? by = null)
    {
        if (IsDeleted) throw new DomainException("tyres.placeAlreadyTrashed", "This time on the car is already in the trash.");
        DeletedAt = now;
        Bump(EntityChange.Trashed, by);
    }

    public void Restore(Guid? by = null)
    {
        if (!IsDeleted) throw new DomainException("tyres.placeNotTrashed", "This time on the car is not in the trash.");
        DeletedAt = null;
        Bump(EntityChange.Restored, by);
    }

    private void RequireLive()
    {
        if (IsDeleted) throw new DomainException("tyres.placeTrashedCannotEdit", "A time on the car in the trash cannot be changed; restore it first.");
    }

    private static void RequireDays(DateOnly from, DateOnly? until)
    {
        if (until is { } u && u <= from)
            throw new DomainException("tyres.untilBeforeFrom", "The day it came off must be after its first day on the car.",
                new { From = from.ToString("yyyy-MM-dd"), Until = u.ToString("yyyy-MM-dd") });
    }

    private static void RequireCorner(TyreCorner? corner)
    {
        if (corner is { } c && !Enum.IsDefined(c)) throw new DomainException("tyres.unknownCorner", $"Unknown corner {(int)c}.", new { Value = ((int)c).ToString() });
    }
}

/// <summary>
/// The ids of the places a change makes by itself (#289: a break-up's, a swap's, an added installation's), worked out from the tyre's and the
/// installation's ids by one rule the device follows too (<c>tyreIds.ts</c>, pinned by the contract): the two ids' hex digits exclusive-or'd
/// pairwise, so a change carries only the ids it chose and the device's answer names the same rows as the server's.
/// </summary>
public static class TyreIds
{
    public static Guid PlaceOf(Guid tyreId, Guid installationId)
    {
        var (a, b) = (tyreId.ToString("N"), installationId.ToString("N"));
        var digits = new char[32];
        for (var i = 0; i < 32; i++) digits[i] = "0123456789abcdef"[Convert.ToInt32(a[i].ToString(), 16) ^ Convert.ToInt32(b[i].ToString(), 16)];
        return Guid.ParseExact(new string(digits), "N");
    }
}
