using Tankstat.Application.Access;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Tyres;

namespace Tankstat.Application.Tyres;

/// <summary>A vehicle's tyres as stored: every set and stretch (the trash included), the tyre log's days with their readings, and the vehicle's live odometer readings.</summary>
public sealed record TyreBook(IReadOnlyList<TyreSet> Sets, IReadOnlyList<TyreStretch> Stretches, IReadOnlyList<TyreLogDay> Days, IReadOnlyList<ReadingPoint> Readings);

/// <summary>What one change saves, in one transaction (the entities are detached; new ones are added, the others updated).</summary>
public sealed class TyreSave
{
    public List<TyreSet> AddedSets { get; } = [];
    public List<TyreSet> ChangedSets { get; } = [];
    public List<TyreStretch> AddedStretches { get; } = [];
    public List<TyreStretch> ChangedStretches { get; } = [];
    public List<TyreLogDay> AddedDays { get; } = [];
    public List<TyreLogDay> ChangedDays { get; } = [];
    public List<TyreLogDay> RemovedDays { get; } = [];
    public List<OdometerReading> AddedReadings { get; } = [];
    public List<OdometerReading> RemovedReadings { get; } = [];

    public void Take(TyreChanges changes)
    {
        foreach (var s in changes.Created) AddedStretches.Add(s);
        foreach (var s in changes.Changed.Concat(changes.Trashed))
            if (!AddedStretches.Contains(s) && !ChangedStretches.Contains(s)) ChangedStretches.Add(s);
    }
}

/// <summary>A row of the Trash page's Tyres tab: a trashed set, or a trashed stretch of a set that is not in the trash.</summary>
/// <param name="StretchCount">For a set: its stretches that come back with it.</param>
public sealed record TyreTrashRow(
    TyreTrashKind Kind, Guid Id, Guid VehicleId, string SetName, DateOnly? From, DateOnly? Until, int StretchCount, DateTimeOffset DeletedAt, int Version);

public enum TyreTrashKind
{
    Set,
    Stretch,
}

public interface ITyreRepository
{
    Task<TyreBook> LoadAsync(Guid vehicleId, CancellationToken ct);

    Task<TyreSet?> FindSetAsync(Guid id, CancellationToken ct);

    Task<TyreStretch?> FindStretchAsync(Guid id, CancellationToken ct);

    Task<TyreLogDay?> FindDayAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Saves one change in one transaction. False when a set, stretch or day it adds could not be inserted because a row with its id is
    /// there by now (the same add, saved by another request at once): nothing is saved, as with <c>AddOnceAsync</c>.
    /// </summary>
    Task<bool> SaveAsync(TyreSave save, CancellationToken ct);

    /// <summary>The vehicle has a tyre set with a rough figure: its units are locked, like with logs.</summary>
    Task<bool> HasRoughFiguresAsync(Guid vehicleId, CancellationToken ct);

    /// <summary>Newest first (then by id), <paramref name="skip"/>/<paramref name="take"/> over both kinds.</summary>
    Task<IReadOnlyList<TyreTrashRow>> ListTrashAsync(OwnerScope scope, int skip, int take, CancellationToken ct);

    Task<int> CountTrashAsync(OwnerScope scope, CancellationToken ct);

    /// <summary>Removes the trashed sets (with their stretches) and stretches in scope for good; returns how many rows of the list went.</summary>
    Task<int> PurgeAsync(OwnerScope scope, CancellationToken ct);
}
