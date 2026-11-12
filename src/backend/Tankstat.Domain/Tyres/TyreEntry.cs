using Tankstat.Domain.Access;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Sync;

namespace Tankstat.Domain.Tyres;

/// <summary>What one line of the tyre log says was done (#288). Stored by name.</summary>
public enum TyreEntryKind
{
    /// <summary>A set went on the car: the line of an installation, the swap itself.</summary>
    Swap,

    /// <summary>The set on the car changed its pairs from that day: the line of the installation the change of positions started.</summary>
    PositionsChanged,
    PunctureRepaired,
    ValveReplaced,
    WheelBalancing,
    StorageFee,
    Bought,
    Other,
    Note,
}

/// <summary>
/// One line of a vehicle's tyre log (#288): what was done (its <see cref="Kind"/>), on which day, to which set (or none), with an optional
/// odometer reading (one of the car's readings, owned by the line, so it counts wherever the others do and is checked against them) and a
/// note. Every installation has its line, a <see cref="TyreEntryKind.Swap"/> or <see cref="TyreEntryKind.PositionsChanged"/> one that
/// shares its id and its first day: the swap and its line are one thing to the user, trashed and restored together, and the line's day is
/// always its installation's <see cref="TyreInstallation.From"/>. The set and the installation are plain columns (the vehicle's is the only
/// cascading key, as for <c>RecurringCompletion</c>); their rows are cleaned up in code. A line in the trash takes its reading out of the
/// car's readings until it is restored.
/// </summary>
public sealed class TyreEntry : IOwned, ISoftDeletable, ISynced
{
    public const int MaxNoteLength = 500;

    private TyreEntry() { } // EF Core

    public Guid Id { get; private set; }

    /// <summary>Always the owner of the vehicle, denormalized so access filtering works on this table directly.</summary>
    public Guid OwnerId { get; private set; }
    public Guid VehicleId { get; private set; }
    public Guid CreatedById { get; private set; }
    public TyreEntryKind Kind { get; private set; }

    /// <summary>The set it concerns; null for a line tied to no set.</summary>
    public Guid? SetId { get; private set; }

    /// <summary>The installation whose line it is (its own id); null for every other kind.</summary>
    public Guid? InstallationId { get; private set; }

    /// <summary>The single tyre it is for (#289), of a set tracked tyre by tyre; null for a line of the whole set, or of no set.</summary>
    public Guid? TyreId { get; private set; }

    /// <summary>
    /// The lines made together (#289: one entry for several tyres, a line each, sharing one expense and one reading): shown as one row of
    /// the tyre log. Null for a line on its own; cleared when a line is changed alone.
    /// </summary>
    public Guid? GroupId { get; private set; }

    public DateOnly Day { get; private set; }
    public string? Note { get; private set; }

    public Guid? OdometerReadingId { get; private set; }

    /// <summary>The reading typed for the day, if any; load it (Include) before reading <see cref="Odometer"/>.</summary>
    public OdometerReading? OdometerReading { get; private set; }

    public long? Odometer => OdometerReading?.Value;

    /// <summary>
    /// The expense made from the line's amount (#288), an ordinary one on the Expenses tab: no foreign key, one per line. It holds the same
    /// reading and day as the line. Cleared when that expense is purged.
    /// </summary>
    public Guid? ExpenseId { get; private set; }

    /// <summary>The expense went to the trash together with the line (the remove dialog's box, or the history trashing a swap): restoring the line brings it back.</summary>
    public bool ExpenseTrashedWithEntry { get; private set; }

    /// <summary>A line that may carry an amount: a note never does.</summary>
    public bool TakesAmount => Kind != TyreEntryKind.Note;

    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsDeleted => DeletedAt is not null;

    /// <summary>The line of an installation: its kind and set are the installation's, its day the installation's first.</summary>
    public bool IsSwapLine => Kind is TyreEntryKind.Swap or TyreEntryKind.PositionsChanged;

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
    OfflineEntityType ISynced.SyncType => OfflineEntityType.TyreEntry;

    private void Bump(EntityChange what, Guid? by)
    {
        _loadedVersion ??= Version;
        Version++;
        (LastChange, ChangedById) = (what, by);
    }

    /// <summary>A line of any kind but the two of an installation (those come with the installation, <see cref="ForInstallation"/>).</summary>
    /// <param name="id">The id the client chose beforehand, if any (see <see cref="EntityId"/>).</param>
    /// <param name="tyreId">The single tyre it is for (#289), of <paramref name="setId"/>.</param>
    /// <param name="groupId">The lines made together with it, if any.</param>
    public static TyreEntry Create(
        Guid ownerId, Guid createdById, Guid vehicleId, TyreEntryKind kind, Guid? setId, DateOnly day, long? odometer, string? note, Guid? id = null,
        Guid? tyreId = null, Guid? groupId = null)
    {
        RequireOwnKind(kind);
        if (tyreId is not null && setId is null) throw new DomainException("tyres.tyreNeedsSet", "A line of a tyre is a line of its set.");
        var entry = New(ownerId, createdById, vehicleId, kind, setId, null, day, odometer, note, id);
        (entry.TyreId, entry.GroupId) = (tyreId, groupId);
        return entry;
    }

    /// <summary>
    /// A line of the same group for another tyre (#289): the same kind, set, day and note, holding the same reading object (one row for the
    /// lines made together and their expense).
    /// </summary>
    public TyreEntry ForAnotherTyre(Guid tyreId, Guid by, Guid? id = null)
    {
        var other = New(OwnerId, by, VehicleId, Kind, SetId, null, Day, null, Note, id);
        (other.TyreId, other.GroupId, other.OdometerReading, other.OdometerReadingId) = (tyreId, GroupId, OdometerReading, OdometerReadingId);
        return other;
    }

    /// <summary>
    /// Leaves the lines made together (#289): changed alone, it is a row of the log of its own. With <paramref name="partFromExpense"/> it also
    /// lets go of their shared expense and reading (a day or odometer of its own, or the amount let go of): its reading, when it keeps one,
    /// is a row of its own. Returns that new reading for the repository. Bookkeeping of the change that does it: no version of its own.
    /// </summary>
    public OdometerReading? LeaveGroup(bool partFromExpense)
    {
        GroupId = null;
        if (!partFromExpense) return null;
        (ExpenseId, ExpenseTrashedWithEntry) = (null, false);
        if (OdometerReading is not { } shared) return null;
        var own = OdometerReading.Create(OwnerId, VehicleId, Day, shared.Value);
        (OdometerReading, OdometerReadingId) = (own, own.Id);
        return own;
    }

    /// <summary>
    /// A line in the trash that shares its reading with live lines about to change it (#289) takes a copy of its own as it is, in the trash
    /// with it, so it keeps its day and value; null when it holds none.
    /// </summary>
    public OdometerReading? KeepOwnReading(DateTimeOffset now)
    {
        if (OdometerReading is not { } shared) return null;
        var own = OdometerReading.Create(OwnerId, VehicleId, shared.Date, shared.Value);
        if (IsDeleted) own.MarkDeleted(now);
        (OdometerReading, OdometerReadingId) = (own, own.Id);
        return own;
    }

    /// <summary>Holds the reading another line of its group just made (#289: the lines made together hold one row), when it holds none.</summary>
    public void HoldReading(OdometerReading reading)
    {
        if (OdometerReadingId is null) (OdometerReading, OdometerReadingId) = (reading, reading.Id);
    }

    /// <summary>Shares another line's reading object (the lines made together hold one row), as the repository loads them.</summary>
    public void ShareReadingObject(OdometerReading reading)
    {
        if (OdometerReadingId == reading.Id) OdometerReading = reading;
    }

    /// <summary>The line of an installation: its id, set and first day, a <see cref="TyreEntryKind.Swap"/> or <see cref="TyreEntryKind.PositionsChanged"/>.</summary>
    public static TyreEntry ForInstallation(TyreInstallation installation, Guid createdById, bool positionsChanged = false) =>
        New(installation.OwnerId, createdById, installation.VehicleId, positionsChanged ? TyreEntryKind.PositionsChanged : TyreEntryKind.Swap,
            installation.SetId, installation.Id, installation.From, null, null, installation.Id);

    private static TyreEntry New(
        Guid ownerId, Guid createdById, Guid vehicleId, TyreEntryKind kind, Guid? setId, Guid? installationId, DateOnly day, long? odometer, string? note, Guid? id)
    {
        if (!Enum.IsDefined(kind)) throw new DomainException("tyres.entryKindUnknown", "This kind of tyre log line does not exist.", new { Kind = kind.ToString() });
        var entry = new TyreEntry
        {
            Id = EntityId.OrNew(id), Version = 1, OwnerId = ownerId, CreatedById = createdById, VehicleId = vehicleId, Kind = kind, SetId = setId,
            InstallationId = installationId, Day = day, Note = Trimmed(note), LastChange = EntityChange.Created, ChangedById = createdById,
        };
        if (odometer is { } value)
        {
            entry.OdometerReading = OdometerReading.Create(ownerId, vehicleId, day, value);
            entry.OdometerReadingId = entry.OdometerReading.Id;
        }
        return entry;
    }

    /// <summary>
    /// Changes what the line says: its kind and set (a line of an installation keeps both: <c>tyres.swapLineLocked</c>), its day (a line of
    /// an installation follows its installation's first day, <see cref="MoveTo"/>), its reading (null clears it) and its note. Returns the
    /// reading created or let go of, for the repository; nothing when nothing changed.
    /// </summary>
    public (OdometerReading? Created, OdometerReading? Removed, bool Changed) Update(TyreEntryKind kind, Guid? setId, DateOnly day, long? odometer, string? note, Guid? by = null)
    {
        RequireLive();
        if (IsSwapLine && (kind != Kind || setId != SetId))
            throw new DomainException("tyres.swapLineLocked", "A swap keeps its set. For another set, move the swap to the trash and log it again.");
        if (!IsSwapLine) RequireOwnKind(kind);
        var trimmed = Trimmed(note);
        OdometerReading? created = null, removed = null;
        var changed = kind != Kind || setId != SetId || day != Day || trimmed != Note;
        if (odometer is null && OdometerReading is not null)
        {
            removed = OdometerReading;
            (OdometerReading, OdometerReadingId) = (null, null);
            changed = true;
        }
        else if (odometer is { } value && OdometerReading is null)
        {
            created = OdometerReading.Create(OwnerId, VehicleId, day, value);
            (OdometerReading, OdometerReadingId) = (created, created.Id);
            changed = true;
        }
        else if (OdometerReading is { } reading && (reading.Value != odometer || reading.Date != day))
        {
            reading.Update(day, odometer!.Value);
            changed = true;
        }
        if (!changed) return (null, null, false);
        (Kind, SetId, Day, Note) = (kind, setId, day, trimmed);
        Bump(EntityChange.Edited, by);
        return (created, removed, true);
    }

    /// <summary>The line of an installation follows its first day; the reading typed for that day moves with it.</summary>
    public bool MoveTo(DateOnly day, Guid? by = null)
    {
        if (day == Day) return false;
        Day = day;
        OdometerReading?.Update(day, OdometerReading.Value);
        Bump(EntityChange.Edited, by);
        return true;
    }

    /// <summary>
    /// A <see cref="TyreEntryKind.Note"/> line of <paramref name="line"/>'s day and set that takes over its reading and note: a join took
    /// its installation away from that day, but what was typed for the day stays there (#288). The line keeps neither.
    /// </summary>
    public static TyreEntry Keeping(TyreEntry line, Guid by, Guid? id = null)
    {
        var kept = New(line.OwnerId, by, line.VehicleId, TyreEntryKind.Note, line.SetId, null, line.Day, null, line.Note, id);
        (kept.OdometerReading, kept.OdometerReadingId) = (line.OdometerReading, line.OdometerReadingId);
        (line.OdometerReading, line.OdometerReadingId, line.Note) = (null, null, null);
        line.Bump(EntityChange.Edited, by);
        return kept;
    }

    /// <summary>Links the expense its amount made. Part of the change that typed the amount, so it bumps nothing of its own.</summary>
    public void LinkExpense(Guid expenseId) => (ExpenseId, ExpenseTrashedWithEntry) = (expenseId, false);

    /// <summary>Takes the reading its expense's photo filled in (#288: the two hold one reading), when it has none.</summary>
    public bool TakeReading(OdometerReading reading, Guid? by = null)
    {
        if (OdometerReading is not null || reading.Date != Day) return false;
        (OdometerReading, OdometerReadingId) = (reading, reading.Id);
        Bump(EntityChange.Edited, by);
        return true;
    }

    /// <summary>The linked expense was purged: the line keeps everything else.</summary>
    public void UnlinkExpense() => (ExpenseId, ExpenseTrashedWithEntry) = (null, false);

    /// <summary>
    /// Whether the linked expense is in the trash because the line went there. Bookkeeping, not an edit: it moves no version, so a change of
    /// the line made meanwhile on a device is not refused for it.
    /// </summary>
    public void MarkExpenseTrashedWithEntry(bool trashedWith) => ExpenseTrashedWithEntry = trashedWith && ExpenseId is not null;

    /// <summary>Moves the line to the trash; its reading leaves the car's readings until the line is restored.</summary>
    public void MarkDeleted(DateTimeOffset now, Guid? by = null)
    {
        if (IsDeleted) throw new DomainException("tyres.entryAlreadyTrashed", "This line of the tyre log is already in the trash.");
        DeletedAt = now;
        OdometerReading?.MarkDeleted(now);
        Bump(EntityChange.Trashed, by);
    }

    public void Restore(Guid? by = null)
    {
        if (!IsDeleted) throw new DomainException("tyres.entryNotTrashed", "This line of the tyre log is not in the trash.");
        DeletedAt = null;
        OdometerReading?.Restore();
        Bump(EntityChange.Restored, by);
    }

    private void RequireLive()
    {
        if (IsDeleted) throw new DomainException("tyres.entryTrashedCannotEdit", "A line in the trash cannot be changed; restore it first.");
    }

    /// <summary>A swap or a change of positions is logged by itself, never as a line of its own.</summary>
    private static void RequireOwnKind(TyreEntryKind kind)
    {
        if (kind is TyreEntryKind.Swap or TyreEntryKind.PositionsChanged)
            throw new DomainException("tyres.entryKindNotAllowed", "A swap and a change of positions are logged as such, not as a line.");
    }

    private static string? Trimmed(string? note)
    {
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > MaxNoteLength })
            throw new DomainException("tyres.noteTooLong", $"The note can be at most {MaxNoteLength} characters.", new { Max = MaxNoteLength });
        return trimmed;
    }
}
