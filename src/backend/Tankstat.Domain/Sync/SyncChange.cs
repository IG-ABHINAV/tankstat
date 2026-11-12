using Tankstat.Domain.Access;

namespace Tankstat.Domain.Sync;

/// <summary>What a change a device sends (<c>syncChanges</c>) does; one per mutation it stands for.</summary>
public enum SyncChangeKind
{
    LogRefueling,
    UpdateRefueling,
    DeleteRefueling,
    RestoreRefueling,
    AddExpense,
    UpdateExpense,
    DeleteExpense,
    RestoreExpense,
    AddRecurringExpense,
    UpdateRecurringExpense,
    DeleteRecurringExpense,
    MarkRecurringExpensesDone,
    AddVehicle,
    UpdateVehicle,
    DeleteVehicle,
    RestoreVehicle,
    AddRefuelingPhoto,
    RemoveRefuelingPhoto,
    AddExpensePhoto,
    RemoveExpensePhoto,
    SetVehiclePicture,
    RemoveVehiclePicture,

    // Tyres (#287).
    AddTyreSet,
    UpdateTyreSet,
    SetTyreRoughDistance,
    RetireTyreSet,
    UnretireTyreSet,
    DeleteTyreSet,
    RestoreTyreSet,
    SwapTyres,
    ChangeTyrePositions,
    AddPastTyreSwaps,
    AddTyreInstallation,
    UpdateTyreInstallation,
    DeleteTyreInstallation,
    RestoreTyreInstallation,

    // Tyre log entries (#288).
    AddTyreEntry,
    UpdateTyreEntry,
    DeleteTyreEntry,
    RestoreTyreEntry,

    // Single tyres and their times on the car (#289).
    BreakUpTyreSet,
    UpdateTyre,
    DeleteTyre,
    RestoreTyre,
    AddTyrePlace,
    UpdateTyrePlace,
    DeleteTyrePlace,
    RestoreTyrePlace,
}

/// <summary>Applied: the server has it. Parked: the server could not apply it and keeps it (with why) for a person to decide.</summary>
public enum SyncChangeStatus
{
    Applied,
    Parked,
    Discarded,
}

/// <summary>
/// One change a device sent, and what became of it: the ledger that makes sending a batch again harmless (a known change id is answered
/// with what was recorded, never applied twice), and the store of the changes the server could not apply (parked, with the reason and the
/// change itself, for a person to decide). Applied rows go after <c>Sync:RetentionDays</c>; parked ones stay.
/// </summary>
public sealed class SyncChange : IOwned
{
    public const int MaxBatch = 200;
    public const int MaxPayloadLength = 4000;

    /// <summary>The most parked changes a list (and the notifications made from it) holds, newest first.</summary>
    public const int MaxParkedListed = 200;
    public const int MaxReasonKeyLength = 80;
    public const int MaxReasonArgs = 10;

    private SyncChange() { } // EF Core

    /// <summary>The change's own id, chosen by the device.</summary>
    public Guid Id { get; private set; }

    /// <summary>The owner of the vehicle it concerns (the submitter when the vehicle is unknown).</summary>
    public Guid OwnerId { get; private set; }

    public Guid SubmittedById { get; private set; }
    public Guid? VehicleId { get; private set; }

    /// <summary>What it changed or added (an add's id); none for a service visit without an expense.</summary>
    public Guid? TargetId { get; private set; }

    public SyncChangeKind Kind { get; private set; }
    public int? ExpectedVersion { get; private set; }

    /// <summary>The change as the device sent it (JSON), kept so a parked change can be shown and applied later.</summary>
    public string Payload { get; private set; } = "";

    /// <summary>
    /// The values the change was made from, as the device sent them (JSON in the shape of its input; never read by the server), kept with a
    /// parked change so whoever decides can tell what the device changed from what was changed on the server meanwhile. Null when the
    /// device knew none, and on applied changes.
    /// </summary>
    public string? Base { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }
    public SyncChangeStatus Status { get; private set; }

    /// <summary>What applying it produced: the entity and its version.</summary>
    public Guid? ResultId { get; private set; }

    public int? ResultVersion { get; private set; }

    /// <summary>Why it was parked: the error key the server gave, and its arguments as text.</summary>
    public string? ReasonKey { get; private set; }

    public IReadOnlyDictionary<string, string> ReasonArgs { get; private set; } = new Dictionary<string, string>();

    /// <summary>Who applied or discarded a parked change, and when; while it is parked, who is deciding about it right now (the claim).</summary>
    public Guid? ResolvedById { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>A person edited the parked change before applying it: the change they apply instead (same kind, same target).</summary>
    public void Replace(string payload)
    {
        RequireParked();
        CheckLength(payload);
        Payload = payload;
    }

    /// <summary>Applying it was tried again and refused again: it stays parked, with the new reason.</summary>
    public void ParkAgain(string reasonKey, IReadOnlyDictionary<string, string> reasonArgs)
    {
        RequireParked();
        Settle(SyncChangeStatus.Parked, null, null, reasonKey, reasonArgs);
        (ResolvedById, ResolvedAt) = (null, null); // nobody decided: free for the next decision
    }

    public void MarkApplied(Guid resolvedById, DateTimeOffset now, Guid? resultId, int? resultVersion)
    {
        RequireParked();
        Settle(SyncChangeStatus.Applied, resultId, resultVersion, null, null);
        (ResolvedById, ResolvedAt) = (resolvedById, now);
    }

    public void MarkDiscarded(Guid resolvedById, DateTimeOffset now)
    {
        RequireParked();
        Status = SyncChangeStatus.Discarded;
        (ResolvedById, ResolvedAt) = (resolvedById, now);
    }

    private void RequireParked()
    {
        if (Status != SyncChangeStatus.Parked) throw new DomainException("sync.notParked", "This change is not parked: someone has decided about it already.");
    }

    public static SyncChange Applied(
        Guid id, Guid ownerId, Guid submittedById, Guid? vehicleId, Guid? targetId, SyncChangeKind kind, int? expectedVersion, string payload,
        DateTimeOffset now, Guid? resultId, int? resultVersion) =>
        Create(id, ownerId, submittedById, vehicleId, targetId, kind, expectedVersion, payload, now).Settle(SyncChangeStatus.Applied, resultId, resultVersion, null, null);

    public static SyncChange Parked(
        Guid id, Guid ownerId, Guid submittedById, Guid? vehicleId, Guid? targetId, SyncChangeKind kind, int? expectedVersion, string payload,
        DateTimeOffset now, string reasonKey, IReadOnlyDictionary<string, string> reasonArgs, string? baseValues = null)
    {
        CheckLength(baseValues);
        var change = Create(id, ownerId, submittedById, vehicleId, targetId, kind, expectedVersion, payload, now).Settle(SyncChangeStatus.Parked, null, null, reasonKey, reasonArgs);
        change.Base = baseValues;
        return change;
    }

    /// <summary>A payload, or the values a change was made from, fits the column.</summary>
    public static void CheckLength(string? json)
    {
        if (json?.Length > MaxPayloadLength)
            throw new DomainException("sync.payloadTooLarge", $"A change may hold at most {MaxPayloadLength} characters.", new { Max = MaxPayloadLength });
    }

    private SyncChange Settle(SyncChangeStatus status, Guid? resultId, int? resultVersion, string? reasonKey, IReadOnlyDictionary<string, string>? reasonArgs)
    {
        Status = status;
        ResultId = resultId;
        ResultVersion = resultVersion;
        ReasonKey = reasonKey is null ? null : reasonKey.Length <= MaxReasonKeyLength ? reasonKey : reasonKey[..MaxReasonKeyLength];
        ReasonArgs = (reasonArgs ?? new Dictionary<string, string>()).Take(MaxReasonArgs).ToDictionary(a => a.Key, a => a.Value.Length <= 200 ? a.Value : a.Value[..200]);
        return this;
    }

    private static SyncChange Create(
        Guid id, Guid ownerId, Guid submittedById, Guid? vehicleId, Guid? targetId, SyncChangeKind kind, int? expectedVersion, string payload, DateTimeOffset now)
    {
        if (id == Guid.Empty) throw new DomainException("id.empty", "An id may not be empty.");
        if (!Enum.IsDefined(kind)) throw new DomainException("sync.unknownKind", $"Unknown change kind '{kind}'.");
        CheckLength(payload);
        return new SyncChange
        {
            Id = id, OwnerId = ownerId, SubmittedById = submittedById, VehicleId = vehicleId, TargetId = targetId, Kind = kind, ExpectedVersion = expectedVersion,
            Payload = payload, ReceivedAt = now,
        };
    }
}
