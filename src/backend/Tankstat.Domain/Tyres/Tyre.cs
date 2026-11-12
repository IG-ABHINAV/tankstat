using Tankstat.Domain.Access;
using Tankstat.Domain.Sync;

namespace Tankstat.Domain.Tyres;

/// <summary>Where a tyre is on the car (#289; cars only: four corners). Stored by name.</summary>
public enum TyreCorner
{
    FrontLeft,
    FrontRight,
    RearLeft,
    RearRight,

    /// <summary>At the front, the side not recorded (a set's past installation whose pairs were recorded).</summary>
    Front,

    /// <summary>At the rear, the side not recorded.</summary>
    Rear,
}

/// <summary>Why a tyre was retired (#289). Stored by name.</summary>
public enum TyreRetiredWhy
{
    Damaged,
    Worn,
    TooOld,

    /// <summary>Its set was retired: unretiring the set brings it back.</summary>
    SetRetired,
}

public static class TyreCorners
{
    public static bool IsFront(this TyreCorner corner) => corner is TyreCorner.FrontLeft or TyreCorner.FrontRight or TyreCorner.Front;
    public static bool IsRear(this TyreCorner corner) => corner is TyreCorner.RearLeft or TyreCorner.RearRight or TyreCorner.Rear;

    /// <summary>One of the four exact corners (not a side not recorded).</summary>
    public static bool IsExact(this TyreCorner corner) => corner is TyreCorner.FrontLeft or TyreCorner.FrontRight or TyreCorner.RearLeft or TyreCorner.RearRight;
}

/// <summary>
/// One tyre of a set tracked tyre by tyre (#289): a set broken up (<see cref="TyreSet.BrokenUpOn"/>) is four of these, each with a label
/// written on the tyre itself (DOT codes repeat within a batch), its own times on the car (<see cref="TyrePlace"/>, each inside one of the
/// set's installations: a tyre is on the car only while its set is), and its own rough figure, copied from the set when it was broken up.
/// Its distance is worked out on read like a set's. A tyre replaced early is retired with why and the tyre that took its place. The set's
/// key is the only cascading one; vehicle and owner are denormalized for access filtering and sync.
/// </summary>
public sealed class Tyre : IOwned, ISoftDeletable, ISynced
{
    public const int MaxLabelLength = 6;
    public const int MaxNameLength = TyreSet.MaxNameLength;

    private Tyre() { } // EF Core

    public Guid Id { get; private set; }

    /// <summary>Always the owner of the vehicle, denormalized so access filtering works on this table directly.</summary>
    public Guid OwnerId { get; private set; }
    public Guid VehicleId { get; private set; }
    public Guid SetId { get; private set; }
    public Guid CreatedById { get; private set; }

    /// <summary>Short, written on the tyre; unique among the vehicle's tyres not in the trash, without case (checked by the service).</summary>
    public string Label { get; private set; } = "";

    /// <summary>The tyre's own model name; null: the set's.</summary>
    public string? Name { get; private set; }

    public int? MadeWeek { get; private set; }
    public int? MadeYear { get; private set; }

    /// <summary>When it was first used: the set's for a tyre of a break-up, the day for a new one.</summary>
    public DateOnly Since { get; private set; }

    /// <summary>What it ran before its places measure it (the set's rough figure on a break-up, 0 for a new tyre).</summary>
    public long RoughDistance { get; private set; }

    /// <summary>The first day <see cref="RoughDistance"/> does not cover, as the set's.</summary>
    public DateOnly? RoughUntil { get; private set; }

    public DateOnly? RetiredOn { get; private set; }
    public TyreRetiredWhy? RetiredWhy { get; private set; }

    /// <summary>The tyre that took its place, when it was replaced early.</summary>
    public Guid? ReplacedById { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsDeleted => DeletedAt is not null;
    public bool IsRetired => RetiredOn is not null;

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
    OfflineEntityType ISynced.SyncType => OfflineEntityType.Tyre;

    private void Bump(EntityChange what, Guid? by)
    {
        _loadedVersion ??= Version;
        Version++;
        (LastChange, ChangedById) = (what, by);
    }

    /// <param name="id">The id the client chose beforehand, if any (see <see cref="EntityId"/>).</param>
    public static Tyre Create(
        Guid ownerId, Guid createdById, Guid vehicleId, Guid setId, string label, string? name, DateOnly since, int? madeWeek, int? madeYear,
        long roughDistance, DateOnly? roughUntil, Guid? id = null)
    {
        if ((madeWeek is null) != (madeYear is null) || madeWeek is < 1 or > 53 || madeYear is < 2000 or > 2099)
            throw new DomainException("tyres.madeInvalid", "The made date is a week (1 to 53) and a year.");
        var trimmedName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (trimmedName is { Length: > MaxNameLength })
            throw new DomainException("tyres.nameTooLong", $"The name can be at most {MaxNameLength} characters.", new { Max = MaxNameLength });
        if (roughDistance < 0) throw new DomainException("tyres.roughNegative", "The distance cannot be negative.");
        return new Tyre
        {
            Id = EntityId.OrNew(id), Version = 1, OwnerId = ownerId, CreatedById = createdById, VehicleId = vehicleId, SetId = setId,
            Label = CheckedLabel(label), Name = trimmedName, Since = since, MadeWeek = madeWeek, MadeYear = madeYear,
            RoughDistance = roughDistance, RoughUntil = roughDistance > 0 ? roughUntil : null,
            LastChange = EntityChange.Created, ChangedById = createdById,
        };
    }

    /// <summary>The label as stored: trimmed, required, at most <see cref="MaxLabelLength"/> characters.</summary>
    public static string CheckedLabel(string? label)
    {
        var trimmed = label?.Trim() ?? "";
        if (trimmed.Length == 0) throw new DomainException("tyres.labelRequired", "Every tyre needs a label.");
        if (trimmed.Length > MaxLabelLength)
            throw new DomainException("tyres.labelTooLong", $"A label can be at most {MaxLabelLength} characters.", new { Max = MaxLabelLength });
        return trimmed;
    }

    /// <summary>A new label; false when it is the same.</summary>
    public bool Relabel(string label, Guid? by = null)
    {
        RequireLive();
        var checkedLabel = CheckedLabel(label);
        if (checkedLabel == Label) return false;
        Label = checkedLabel;
        Bump(EntityChange.Edited, by);
        return true;
    }

    public void Retire(DateOnly day, TyreRetiredWhy why, Guid? by = null, Guid? replacedById = null)
    {
        RequireLive();
        if (IsRetired) throw new DomainException("tyres.tyreAlreadyRetired", "This tyre is retired already.");
        (RetiredOn, RetiredWhy, ReplacedById) = (day, why, replacedById);
        Bump(EntityChange.Edited, by);
    }

    public void Unretire(Guid? by = null)
    {
        RequireLive();
        if (!IsRetired) throw new DomainException("tyres.tyreNotRetired", "This tyre is not retired.");
        (RetiredOn, RetiredWhy, ReplacedById) = (null, null, null);
        Bump(EntityChange.Edited, by);
    }

    public void MarkDeleted(DateTimeOffset now, Guid? by = null)
    {
        if (IsDeleted) throw new DomainException("tyres.tyreAlreadyTrashed", "This tyre is already in the trash.");
        DeletedAt = now;
        Bump(EntityChange.Trashed, by);
    }

    public void Restore(Guid? by = null)
    {
        if (!IsDeleted) throw new DomainException("tyres.tyreNotTrashed", "This tyre is not in the trash.");
        DeletedAt = null;
        Bump(EntityChange.Restored, by);
    }

    private void RequireLive()
    {
        if (IsDeleted) throw new DomainException("tyres.tyreTrashedCannotEdit", "A tyre in the trash cannot be changed; restore it first.");
    }
}
