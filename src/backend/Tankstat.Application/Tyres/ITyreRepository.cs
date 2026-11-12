using Tankstat.Application.Access;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Tyres;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.Tyres;

/// <summary>
/// A vehicle's tyres as stored: every set, installation and tyre log line (the trash included; the lines with their readings), the
/// expenses the lines' amounts made (#288, the trash included, each holding its line's very reading object), the vehicle's live
/// odometer readings, and the single tyres of broken-up sets with their places (#289, the trash included).
/// </summary>
public sealed record TyreBook(
    IReadOnlyList<TyreSet> Sets, IReadOnlyList<TyreInstallation> Installations, IReadOnlyList<TyreEntry> Entries, IReadOnlyList<ReadingPoint> Readings,
    IReadOnlyList<Expense>? Expenses = null, IReadOnlyList<Tyre>? Tyres = null, IReadOnlyList<TyrePlace>? Places = null)
{
    public IReadOnlyList<Expense> LinkedExpenses => Expenses ?? [];
    public IReadOnlyList<Tyre> SingleTyres => Tyres ?? [];
    public IReadOnlyList<TyrePlace> TyrePlaces => Places ?? [];

    /// <summary>The expense a line's amount made, when it still exists (a purged one is gone, the line's link with it).</summary>
    public Expense? ExpenseOf(TyreEntry line) => line.ExpenseId is { } id ? LinkedExpenses.FirstOrDefault(e => e.Id == id) : null;
}

/// <summary>What one change saves, in one transaction (the entities are detached; new ones are added, the others updated).</summary>
public sealed class TyreSave
{
    /// <summary>The book the change was made on (the service's): its linked expenses follow their lines before the save (#288).</summary>
    internal TyreBook? Book { get; init; }

    internal Guid? By { get; init; }

    public List<TyreSet> AddedSets { get; } = [];
    public List<TyreSet> ChangedSets { get; } = [];
    public List<TyreInstallation> AddedInstallations { get; } = [];
    public List<TyreInstallation> ChangedInstallations { get; } = [];
    public List<TyreEntry> AddedEntries { get; } = [];
    public List<TyreEntry> ChangedEntries { get; } = [];
    public List<OdometerReading> AddedReadings { get; } = [];
    public List<Tyre> AddedTyres { get; } = [];
    public List<Tyre> ChangedTyres { get; } = [];
    public List<TyrePlace> AddedPlaces { get; } = [];
    public List<TyrePlace> ChangedPlaces { get; } = [];

    /// <summary>Expenses a line's amount made (#288), with their new <see cref="Expense.Cost"/>; a line and its expense are saved together.</summary>
    public List<Expense> AddedExpenses { get; } = [];

    /// <summary>Linked expenses changed with their line (their date, amount, reading, trash); <see cref="ExpenseChanges"/> holds what an expense's own edit let go of.</summary>
    public List<Expense> ChangedExpenses { get; } = [];

    /// <summary>The costs and readings an expense's own edit created or let go of (an expense changed from the Expenses tab).</summary>
    public List<LinkedChanges> ExpenseChanges { get; } = [];
    public List<OdometerReading> RemovedReadings { get; } = [];

    public void Change(Expense expense)
    {
        if (!AddedExpenses.Contains(expense) && !ChangedExpenses.Contains(expense)) ChangedExpenses.Add(expense);
    }

    public void Take(TyreChanges changes)
    {
        foreach (var s in changes.Created) AddedInstallations.Add(s);
        foreach (var s in changes.Changed.Concat(changes.Trashed))
            if (!AddedInstallations.Contains(s) && !ChangedInstallations.Contains(s)) ChangedInstallations.Add(s);
    }

    public void Take(TyreLogChanges changes)
    {
        foreach (var e in changes.Created)
            if (!AddedEntries.Contains(e)) AddedEntries.Add(e);
        foreach (var e in changes.Changed) Change(e);
    }

    public void Take(TyrePlaceChanges changes)
    {
        foreach (var t in changes.CreatedTyres)
            if (!AddedTyres.Contains(t)) AddedTyres.Add(t);
        foreach (var p in changes.Created)
            if (!AddedPlaces.Contains(p)) AddedPlaces.Add(p);
        foreach (var p in changes.Changed) Change(p);
    }

    public void Change(Tyre tyre)
    {
        if (!AddedTyres.Contains(tyre) && !ChangedTyres.Contains(tyre)) ChangedTyres.Add(tyre);
    }

    public void Change(TyrePlace place)
    {
        if (!AddedPlaces.Contains(place) && !ChangedPlaces.Contains(place)) ChangedPlaces.Add(place);
    }

    public void Change(TyreEntry entry)
    {
        if (!AddedEntries.Contains(entry) && !ChangedEntries.Contains(entry)) ChangedEntries.Add(entry);
    }

    public void Change(TyreInstallation installation)
    {
        if (!AddedInstallations.Contains(installation) && !ChangedInstallations.Contains(installation)) ChangedInstallations.Add(installation);
    }
}

/// <summary>
/// A row of the Trash page's Tyres tab: a trashed set, a trashed tyre log line of no set or of a set that is not in the trash, a trashed
/// single tyre of a set not in the trash, or a trashed time on the car of a tyre not in the trash (#289).
/// </summary>
/// <param name="SetName">The set's name; null for a line of no set.</param>
/// <param name="EntryKind">For a line: what it says was done.</param>
/// <param name="Day">For a line: its day.</param>
/// <param name="Until">For a swap's line: the day its installation came off (null while it would still be on the car).</param>
/// <param name="InstallationCount">For a set: its installations that come back with it.</param>
/// <param name="WithExpense">For a line: its expense went to the trash with it and comes back with it (#288).</param>
/// <param name="Label">For a tyre or a time on the car: the tyre's label (#289).</param>
/// <param name="Corner">For a time on the car: its corner, if recorded (its days are <paramref name="Day"/> and <paramref name="Until"/>).</param>
public sealed record TyreTrashRow(
    TyreTrashKind Kind, Guid Id, Guid VehicleId, string? SetName, TyreEntryKind? EntryKind, DateOnly? Day, DateOnly? Until, int InstallationCount,
    DateTimeOffset DeletedAt, int Version, bool WithExpense = false, string? Label = null, TyreCorner? Corner = null);

public enum TyreTrashKind
{
    Set,
    Entry,
    Tyre,
    Place,
}

/// <summary>What the Tyres tab of the Trash page sorts by: when a row was moved to the trash (then the id).</summary>
public enum TyreTrashSortField
{
    DeletedAt,
}

public interface ITyreRepository
{
    Task<TyreBook> LoadAsync(Guid vehicleId, CancellationToken ct);

    Task<TyreSet?> FindSetAsync(Guid id, CancellationToken ct);

    Task<TyreInstallation?> FindInstallationAsync(Guid id, CancellationToken ct);

    /// <summary>The sets with these ids, the trash included (what a parked change concerns).</summary>
    Task<IReadOnlyList<TyreSet>> ListSetsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>The installations with these ids, the trash included.</summary>
    Task<IReadOnlyList<TyreInstallation>> ListInstallationsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>A tyre log line, the trash included, with its reading.</summary>
    Task<TyreEntry?> FindEntryAsync(Guid id, CancellationToken ct);

    /// <summary>The lines that made these expenses (#288), the trash included.</summary>
    Task<IReadOnlyList<TyreEntry>> ListEntriesByExpenseAsync(IReadOnlyCollection<Guid> expenseIds, CancellationToken ct);

    /// <summary>The single tyres with these ids, the trash included (#289).</summary>
    Task<IReadOnlyList<Tyre>> ListTyresAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>The times on the car with these ids, the trash included (#289).</summary>
    Task<IReadOnlyList<TyrePlace>> ListPlacesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>The tyre log lines with these ids, the trash included.</summary>
    Task<IReadOnlyList<TyreEntry>> ListEntriesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>
    /// Saves one change in one transaction. False when a set, installation or line it adds could not be inserted because a row with its id is
    /// there by now (the same add, saved by another request at once): nothing is saved, as with <c>AddOnceAsync</c>.
    /// </summary>
    Task<bool> SaveAsync(TyreSave save, CancellationToken ct);

    /// <summary>The vehicle has a tyre set with a rough figure: its units are locked, like with logs.</summary>
    Task<bool> HasRoughFiguresAsync(Guid vehicleId, CancellationToken ct);

    /// <summary>By when it was trashed (<paramref name="descending"/>: newest first), then by id; <paramref name="skip"/>/<paramref name="take"/> over both kinds.</summary>
    Task<IReadOnlyList<TyreTrashRow>> ListTrashAsync(OwnerScope scope, int skip, int take, CancellationToken ct, bool descending = true);

    Task<int> CountTrashAsync(OwnerScope scope, CancellationToken ct);

    /// <summary>
    /// Removes the trashed sets (with their installations and lines) and the trashed lines (a swap's with its installation) in scope for
    /// good, with the readings of the lines; returns how many rows of the list went.
    /// </summary>
    Task<int> PurgeAsync(OwnerScope scope, CancellationToken ct);
}
