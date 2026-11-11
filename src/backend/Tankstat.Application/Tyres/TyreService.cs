using Microsoft.Extensions.Logging;
using Tankstat.Application.Access;
using Tankstat.Application.Auth;
using Tankstat.Application.Odometers;
using Tankstat.Application.Sync;
using Tankstat.Domain;
using Tankstat.Domain.Access;
using Tankstat.Domain.Tyres;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.Tyres;

/// <param name="RoughDistance">What a used set ran before it is tracked, roughly (0 for a new one).</param>
public sealed record TyreSetInput(string Name, TyreSeason Season, string? Size, DateOnly Since, int? MadeWeek, int? MadeYear, long RoughDistance = 0);

/// <param name="Fitted">The set is on the car from <see cref="Day"/> (a swap that day); else it is in storage.</param>
/// <param name="Day">The day tracking starts (the client's today); the rough figure stands for the time before it.</param>
/// <param name="Replace">A fitted set on a day that has a swap replaces it (as <see cref="TyreSwapInput.Replace"/>).</param>
public sealed record NewTyreSet(Guid VehicleId, TyreSetInput Set, bool Fitted, DateOnly Day, long? Odometer, bool? PairOneFront, bool Replace = false);

/// <param name="Replace">A swap logged on that day already is replaced (its installation goes to the trash) instead of refused.</param>
public sealed record TyreSwapInput(Guid VehicleId, Guid SetId, DateOnly Day, bool? PairOneFront, bool Replace, long? Odometer, string? Note);

public sealed record TyrePastSwap(DateOnly Day, Guid SetId, long? Odometer, Guid? InstallationId = null);

/// <param name="OdometerFrom">A reading for the first day, kept on the installation's line; null leaves the line's reading as it is.</param>
/// <param name="OdometerUntil">A reading for the day it came off, kept on the line of the installation that starts that day, else on a
/// <see cref="TyreEntryKind.Note"/> line of the set.</param>
public sealed record TyreInstallationInput(DateOnly From, DateOnly? Until, bool? PairOneFront, long? OdometerFrom, long? OdometerUntil);

/// <summary>A tyre log line as typed (#288): of any kind but the two of an installation, which come with the swap.</summary>
/// <param name="SetId">The set it concerns, or none.</param>
public sealed record TyreEntryInput(TyreEntryKind Kind, Guid? SetId, DateOnly Day, long? Odometer, string? Note);

/// <summary>An installation with its distance, worked out on read.</summary>
public sealed record TyreInstallationView(TyreInstallation Installation, InstallationDistance Distance);

/// <summary>A set with its figures and its live installations (none while it is in the trash).</summary>
public sealed record TyreSetView(TyreSet Set, TyreSetFigures Figures, IReadOnlyList<TyreInstallationView> Installations, bool OnTheCar);

/// <summary>
/// A vehicle's tyres as the Tyres tab shows them: the sets that are not in the trash and the tyre log's lines (those not in the trash, of
/// no set or of a set not in the trash), with the vehicle's readings
/// (date and value, by date), so a client works out the odometer of a day as <see cref="TyreCalculator.OdometerOn"/> does (a form's hint, a
/// log line), never saving it.
/// </summary>
public sealed record TyreOverview(Guid VehicleId, IReadOnlyList<TyreSetView> Sets, IReadOnlyList<TyreEntry> Entries, IReadOnlyList<ReadingPoint> Readings)
{
    public bool HasReadings => Readings.Count > 0;
}

/// <summary>
/// A vehicle's tyre sets and their time on the car (#287). They follow the access rules of the vehicle's logs (<see cref="LogAccessGuard"/>):
/// View sees, Edit changes, trashes and restores, Delete purges. Every change is checked by <see cref="TyreHistory"/> and saved in one
/// transaction, with the tyre log's lines it makes, moves or trashes (<see cref="TyreLog"/>, #288); a reading typed with it is one of the
/// car's readings, owned by a line (<see cref="TyreEntry"/>) and checked against the others. Distances are worked out on every read
/// (<see cref="TyreCalculator"/>).
/// </summary>
public sealed class TyreService(
    LogAccessGuard guard, ITyreRepository tyres, AccessService access, OdometerService odometer, TimeProvider clock, ILogger<TyreService> logger)
{
    /// <summary>A day ahead covers every time zone, like the logs.</summary>
    private DateOnly LatestAllowedDate => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(1);

    public async Task<TyreOverview?> OverviewAsync(Guid vehicleId, CancellationToken ct) =>
        await guard.ForVehicleAsync(vehicleId, ct) is null ? null : Overview(vehicleId, await tyres.LoadAsync(vehicleId, ct));

    /// <param name="id">The set's id chosen by the client: an add sent again is answered with what it created.</param>
    /// <param name="installationId">The id of the installation a fitted set starts with.</param>
    public async Task<TyreOverview> AddSetAsync(NewTyreSet input, CancellationToken ct, Guid? id = null, Guid? installationId = null)
    {
        var vehicle = await EditableVehicleAsync(input.VehicleId, ct);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        async Task<bool> Repeat() => await RepeatedSetAsync(id, vehicle.Id, by, ct) | await RepeatedAsync(book, installationId, vehicle.Id, by, ct);
        if (await Repeat()) return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
        RequireNotFuture(input.Day);
        RequireNotFuture(input.Set.Since);
        var set = TyreSet.Create(vehicle.OwnerId, by, vehicle.Id, input.Set.Name, input.Set.Season, input.Set.Size, input.Set.Since, input.Set.MadeWeek, input.Set.MadeYear,
            input.Set.RoughDistance, input.Set.RoughDistance > 0 ? input.Day : null, id);
        var save = new TyreSave();
        save.AddedSets.Add(set);
        if (input.Fitted)
        {
            var history = History(vehicle, book, extra: set);
            Follow(save, book, history, history.Swap(set, input.Day, input.PairOneFront, input.Replace, by, clock.GetUtcNow(), installationId), by);
            await TypedAsync(vehicle, book, save, history, set.Id, input.Day, input.Odometer, note: null, by, ct, installationId);
        }
        if (!await SaveOnceAsync(save, Repeat, ct)) return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
        logger.LogDebug("User {UserId} added tyre set {SetId} to vehicle {VehicleId}", by, set.Id, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    public async Task<TyreOverview> UpdateSetAsync(Guid id, TyreSetInput input, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, set, book) = await EditableSetAsync(id, ct);
        VersionCheck.Require(expectedVersion, set.Version);
        RequireNotFuture(input.Since);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        set.Update(input.Name, input.Season, input.Size, input.Since, input.MadeWeek, input.MadeYear, by);
        History(vehicle, book).Check(); // a later "in use since" may no longer fit its installations
        var save = new TyreSave();
        save.ChangedSets.Add(set);
        await SaveAsync(save, ct);
        logger.LogDebug("Tyre set {SetId} of vehicle {VehicleId} updated", id, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>
    /// Sets the rough figure; <paramref name="until"/> (accepting a suggestion) moves the first day it does not cover. Without one it keeps
    /// the day it had, else it covers the time before the set's first installation (today when it has none), never time already measured.
    /// </summary>
    public async Task<TyreOverview> SetRoughAsync(Guid id, long distance, DateOnly? until, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, set, book) = await EditableSetAsync(id, ct);
        VersionCheck.Require(expectedVersion, set.Version);
        if (until is { } given) RequireNotFuture(given);
        var first = book.Installations.Where(s => s.SetId == id && !s.IsDeleted).Select(s => (DateOnly?)s.From).Min();
        set.SetRough(distance, until ?? set.RoughUntil ?? first ?? Today, (await access.RequirePrincipalAsync(ct)).Id);
        var save = new TyreSave();
        save.ChangedSets.Add(set);
        await SaveAsync(save, ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <param name="installationId">The id of the installation the swap creates, if it creates one: a swap sent again finds it and changes nothing.</param>
    /// <param name="repeat">Sent from a device's queue (the sync): the set already on the car that day with the pairs asked for is a retry's answer, not an error.</param>
    public async Task<TyreOverview> SwapAsync(TyreSwapInput input, CancellationToken ct, Guid? installationId = null, bool repeat = false)
    {
        var vehicle = await EditableVehicleAsync(input.VehicleId, ct);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        Task<bool> Repeat() => RepeatedAsync(book, installationId, vehicle.Id, by, ct);
        if (await Repeat()) return Overview(vehicle.Id, book);
        RequireNotFuture(input.Day);
        var set = LiveSet(book, input.SetId);
        var save = new TyreSave();
        var history = History(vehicle, book);
        Follow(save, book, history, history.Swap(set, input.Day, input.PairOneFront, input.Replace, by, clock.GetUtcNow(), installationId, repeatable: repeat), by);
        await TypedAsync(vehicle, book, save, history, set.Id, input.Day, input.Odometer, input.Note, by, ct, installationId);
        if (!await SaveOnceAsync(save, Repeat, ct)) return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
        logger.LogDebug("Tyre set {SetId} of vehicle {VehicleId} went on the car", set.Id, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <param name="repeat">Sent from a device's queue (the sync): pairs already as asked on the day their installation began are a retry's answer.
    /// Online, they are <c>tyres.positionsUnchanged</c>.</param>
    public async Task<TyreOverview> ChangePositionsAsync(TyreSwapInput input, CancellationToken ct, Guid? installationId = null, bool repeat = false)
    {
        var vehicle = await EditableVehicleAsync(input.VehicleId, ct);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        Task<bool> Repeat() => RepeatedAsync(book, installationId, vehicle.Id, by, ct);
        if (await Repeat()) return Overview(vehicle.Id, book);
        RequireNotFuture(input.Day);
        var set = LiveSet(book, input.SetId);
        var save = new TyreSave();
        var history = History(vehicle, book);
        Follow(save, book, history, history.ChangePositions(set, input.Day, input.PairOneFront, by, installationId, repeatable: repeat), by);
        await TypedAsync(vehicle, book, save, history, set.Id, input.Day, input.Odometer, input.Note, by, ct, installationId);
        await SaveOnceAsync(save, Repeat, ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>Swaps from papers, applied in day order, all or nothing. Two on one day are refused; one on a day that has a swap follows <paramref name="replace"/>.</summary>
    public const int MaxPastSwaps = 25; // one change of a device's queue must fit the ledger's payload

    public async Task<TyreOverview> AddPastSwapsAsync(Guid vehicleId, IReadOnlyList<TyrePastSwap> rows, bool replace, CancellationToken ct, bool repeat = false)
    {
        var vehicle = await EditableVehicleAsync(vehicleId, ct);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        if (rows.Count == 0) throw new DomainException("tyres.pastSwapsRequired", "Enter at least one swap.");
        if (rows.Count > MaxPastSwaps)
            throw new DomainException("tyres.pastSwapsTooMany", $"Enter at most {MaxPastSwaps} swaps at once.", new { Max = MaxPastSwaps });
        // Each row creates an installation of its own: two with one id could never both be saved (refused, so a device's change is parked, not stuck).
        if (rows.Where(r => r.InstallationId is not null).GroupBy(r => r.InstallationId).Any(g => g.Count() > 1))
            throw new DomainException("tyres.installationIdTwice", "Two of these swaps carry the same id.");
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        async Task<bool> Repeat()
        {
            var repeated = false;
            foreach (var row in rows) repeated |= await RepeatedAsync(book, row.InstallationId, vehicle.Id, by, ct);
            return repeated;
        }
        if (await Repeat()) return Overview(vehicle.Id, book);
        var day = rows.GroupBy(r => r.Day).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (day is { } twice)
            throw new DomainException("tyres.pastSwapsSameDay", "Two swaps on one day: one day holds one swap.", new { Date = twice.ToString("yyyy-MM-dd") });
        var history = History(vehicle, book);
        var save = new TyreSave();
        foreach (var row in rows.OrderBy(r => r.Day))
        {
            RequireNotFuture(row.Day);
            Follow(save, book, history, history.Swap(LiveSet(book, row.SetId), row.Day, null, replace, by, clock.GetUtcNow(), row.InstallationId, pairsGiven: false, repeatable: repeat), by);
            await TypedAsync(vehicle, book, save, history, row.SetId, row.Day, row.Odometer, note: null, by, ct, row.InstallationId);
        }
        if (!await SaveOnceAsync(save, Repeat, ct)) return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
        logger.LogDebug("User {UserId} added {Count} past tyre swaps to vehicle {VehicleId}", by, rows.Count, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    public async Task<TyreOverview> AddInstallationAsync(Guid setId, TyreInstallationInput input, CancellationToken ct, Guid? id = null)
    {
        var (vehicle, set, book) = await EditableSetAsync(setId, ct);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        Task<bool> Repeat() => RepeatedAsync(book, id, vehicle.Id, by, ct);
        if (await Repeat()) return Overview(vehicle.Id, book);
        RequireDays(input);
        var save = new TyreSave();
        var history = History(vehicle, book);
        var created = history.Add(set, input.From, input.Until, input.PairOneFront, by, id);
        save.AddedInstallations.Add(created);
        save.AddedEntries.Add(TyreLog.LineFor(created, history, by));
        await InstallationReadingsAsync(vehicle, book, save, history, set.Id, input, by, ct);
        await SaveOnceAsync(save, Repeat, ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    public async Task<TyreOverview> UpdateInstallationAsync(Guid id, TyreInstallationInput input, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, installation, book) = await EditableInstallationAsync(id, ct);
        VersionCheck.Require(expectedVersion, installation.Version);
        RequireDays(input);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        var save = new TyreSave();
        var history = History(vehicle, book);
        if (history.Edit(installation, input.From, input.Until, input.PairOneFront, by))
        {
            save.ChangedInstallations.Add(installation);
            if (LineOf(book, installation.Id) is { IsDeleted: false } line && line.MoveTo(installation.From, by))
            {
                save.Change(line);
                // Its reading moves with it, and must fit the readings around its new day (one typed now is checked below).
                if (input.OdometerFrom is null && line.OdometerReading is { } moved)
                    await odometer.ValidateAsync(vehicle.Id, moved.Date, moved.Value, exceptReadingId: moved.Id, ct);
            }
        }
        await InstallationReadingsAsync(vehicle, book, save, history, installation.SetId, input, by, ct);
        await SaveAsync(save, ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>Moves an installation to the trash with its line (#288): the line's reading leaves the car's readings until it comes back.</summary>
    public async Task<TyreOverview> TrashInstallationAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, installation, book) = await EditableInstallationAsync(id, ct);
        VersionCheck.Require(expectedVersion, installation.Version);
        await TrashSwapAsync(installation, LineOf(book, id), ct);
        logger.LogDebug("Tyre installation {InstallationId} of vehicle {VehicleId} moved to the trash", id, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    public async Task<TyreOverview> RestoreInstallationAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, installation, book) = await EditableInstallationAsync(id, ct, trashed: true);
        VersionCheck.Require(expectedVersion, installation.Version);
        if (book.Sets.FirstOrDefault(s => s.Id == installation.SetId) is not { IsDeleted: false })
            throw new NotFoundException("tyres.installationNotFound", $"Tyre installation {id} does not exist.", new { Id = id }); // its set is in the trash: restore that
        await RestoreSwapAsync(vehicle, book, installation, LineOf(book, id), ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>Adds a line to the tyre log (#288).</summary>
    /// <param name="id">The line's id chosen by the client: an add sent again is answered with what it created.</param>
    public async Task<TyreOverview> AddEntryAsync(Guid vehicleId, TyreEntryInput input, CancellationToken ct, Guid? id = null)
    {
        var vehicle = await EditableVehicleAsync(vehicleId, ct);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        Task<bool> Repeat() => RepeatedEntryAsync(id, vehicle.Id, by, ct);
        if (await Repeat()) return Overview(vehicle.Id, book);
        RequireNotFuture(input.Day);
        if (input.SetId is { } setId) LiveSet(book, setId);
        if (input.Odometer is { } value) await odometer.ValidateAsync(vehicle.Id, input.Day, value, exceptReadingId: null, ct);
        var entry = TyreEntry.Create(vehicle.OwnerId, by, vehicle.Id, input.Kind, input.SetId, input.Day, input.Odometer, input.Note, id);
        var save = new TyreSave();
        save.AddedEntries.Add(entry);
        if (entry.OdometerReading is { } reading) save.AddedReadings.Add(reading);
        if (!await SaveOnceAsync(save, Repeat, ct)) return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
        logger.LogDebug("User {UserId} added tyre log line {EntryId} to vehicle {VehicleId}", by, entry.Id, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>
    /// Changes a line. A swap's line keeps its kind and set (<c>tyres.swapLineLocked</c>); a new day moves the swap with it, by the history's
    /// rules (<see cref="TyreHistory.MoveSwap"/>). The reading moves with the line's day; null clears it.
    /// </summary>
    public async Task<TyreOverview> UpdateEntryAsync(Guid id, TyreEntryInput input, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, entry, book) = await EditableEntryAsync(id, ct);
        VersionCheck.Require(expectedVersion, entry.Version);
        RequireNotFuture(input.Day);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        var save = new TyreSave();
        if (entry.IsSwapLine)
        {
            if (input.Kind != entry.Kind || input.SetId != entry.SetId)
                throw new DomainException("tyres.swapLineLocked", "A swap keeps its set. For another set, move the swap to the trash and log it again.");
            var installation = book.Installations.First(s => s.Id == entry.InstallationId);
            var history = History(vehicle, book);
            Follow(save, book, history, history.MoveSwap(installation, input.Day, by), by);
        }
        else if (input.SetId is { } setId && setId != entry.SetId) LiveSet(book, setId);
        if (input.Odometer is { } value) await odometer.ValidateAsync(vehicle.Id, input.Day, value, exceptReadingId: entry.OdometerReadingId, ct);
        var (added, removed, changed) = entry.Update(input.Kind, input.SetId, input.Day, input.Odometer, input.Note, by);
        if (added is not null) save.AddedReadings.Add(added);
        if (removed is not null) save.RemovedReadings.Add(removed);
        if (changed) save.Change(entry);
        await SaveAsync(save, ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>Moves a line to the trash; a swap's line takes its installation along, so the swap's time on the car no longer counts.</summary>
    public async Task<TyreOverview> TrashEntryAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, entry, book) = await EditableEntryAsync(id, ct);
        VersionCheck.Require(expectedVersion, entry.Version);
        if (InstallationOf(book, entry, trashed: false) is { } installation) await TrashSwapAsync(installation, entry, ct);
        else
        {
            entry.MarkDeleted(clock.GetUtcNow(), (await access.RequirePrincipalAsync(ct)).Id);
            var save = new TyreSave();
            save.Change(entry);
            await SaveAsync(save, ct);
        }
        logger.LogDebug("Tyre log line {EntryId} of vehicle {VehicleId} moved to the trash", id, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>Brings a line back; a swap's line brings its installation back, or is refused with the history's reason.</summary>
    public async Task<TyreOverview> RestoreEntryAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, entry, book) = await EditableEntryAsync(id, ct, trashed: true);
        VersionCheck.Require(expectedVersion, entry.Version);
        await RequireReadingFitsAsync(vehicle, entry, ct);
        if (InstallationOf(book, entry, trashed: true) is { } installation) await RestoreSwapAsync(vehicle, book, installation, entry, ct);
        else
        {
            entry.Restore((await access.RequirePrincipalAsync(ct)).Id);
            var save = new TyreSave();
            save.Change(entry);
            await SaveAsync(save, ct);
        }
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>Retires a set in storage on <paramref name="day"/> (the client's today): it keeps everything and takes no swap.</summary>
    public async Task<TyreOverview> RetireAsync(Guid id, DateOnly day, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, set, book) = await EditableSetAsync(id, ct);
        VersionCheck.Require(expectedVersion, set.Version);
        RequireNotFuture(day);
        if (book.Installations.Any(s => s.SetId == id && !s.IsDeleted && (s.Until is null || s.Until > day)))
            throw new DomainException("tyres.retireOnCar", "A tyre set on the car cannot be retired; swap it off first.", new { Set = set.Name });
        set.Retire(day, (await access.RequirePrincipalAsync(ct)).Id);
        await SaveSetAsync(set, ct);
        logger.LogDebug("Tyre set {SetId} of vehicle {VehicleId} retired", id, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    public async Task<TyreOverview> UnretireAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, set, _) = await EditableSetAsync(id, ct);
        VersionCheck.Require(expectedVersion, set.Version);
        set.Unretire((await access.RequirePrincipalAsync(ct)).Id);
        await SaveSetAsync(set, ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>Moves a set to the trash; its installations hide through it and come back with it (one trashed on its own before stays there).</summary>
    public async Task<TyreOverview> TrashSetAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, set, _) = await EditableSetAsync(id, ct);
        VersionCheck.Require(expectedVersion, set.Version);
        set.MarkDeleted(clock.GetUtcNow(), (await access.RequirePrincipalAsync(ct)).Id);
        await SaveSetAsync(set, ct);
        logger.LogDebug("Tyre set {SetId} of vehicle {VehicleId} moved to the trash", id, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    public async Task<TyreOverview> RestoreSetAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, set, book) = await EditableSetAsync(id, ct, trashed: true);
        VersionCheck.Require(expectedVersion, set.Version);
        set.Restore((await access.RequirePrincipalAsync(ct)).Id);
        var history = new TyreHistory(vehicle.OwnerId, vehicle.Id, book.Sets, book.Installations);
        history.CheckRestored([.. history.Live.Where(s => s.SetId == id)]); // the set's own live installations now count again
        await SaveSetAsync(set, ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>The set when it is in the trash and the user may restore it, else null (a trash sent again finds it there).</summary>
    public async Task<TyreSet?> SetInTrashAsync(Guid id, CancellationToken ct) =>
        await tyres.FindSetAsync(id, ct) is { IsDeleted: true } set && await guard.ForVehicleAsync(set.VehicleId, ct) is { Level: >= AccessLevel.Edit } ? set : null;

    /// <summary>The installation when it is in the trash (on its own, or with its set) and the user may restore it, else null.</summary>
    public async Task<TyreInstallation?> InstallationInTrashAsync(Guid id, CancellationToken ct) =>
        await tyres.FindInstallationAsync(id, ct) is { } installation
        && (installation.IsDeleted || await tyres.FindSetAsync(installation.SetId, ct) is { IsDeleted: true })
        && await guard.ForVehicleAsync(installation.VehicleId, ct) is { Level: >= AccessLevel.Edit }
            ? installation
            : null;

    /// <summary>The line when it is in the trash (on its own, or with its set) and the user may restore it, else null.</summary>
    public async Task<TyreEntry?> EntryInTrashAsync(Guid id, CancellationToken ct) =>
        await tyres.FindEntryAsync(id, ct) is { } entry
        && (entry.IsDeleted || (entry.SetId is { } setId && await tyres.FindSetAsync(setId, ct) is { IsDeleted: true }))
        && await guard.ForVehicleAsync(entry.VehicleId, ct) is { Level: >= AccessLevel.Edit }
            ? entry
            : null;

    public async Task<IReadOnlyList<TyreTrashRow>> ListTrashAsync(int skip, int take, CancellationToken ct, bool descending = true) =>
        await tyres.ListTrashAsync(await access.LogScopeAsync(AccessLevel.Edit, ct), Math.Max(0, skip), Math.Clamp(take, 1, 100), ct, descending);

    public async Task<int> CountTrashAsync(CancellationToken ct) => await tyres.CountTrashAsync(await access.LogScopeAsync(AccessLevel.Edit, ct), ct);

    public async Task<int> CountDeletableTrashAsync(CancellationToken ct) => await tyres.CountTrashAsync(await access.LogScopeAsync(AccessLevel.Delete, ct), ct);

    /// <summary>Permanently removes the trashed sets and tyre log lines (a swap's with its installation) the user has Delete access to.</summary>
    public async Task<int> EmptyTrashAsync(CancellationToken ct)
    {
        var user = await access.RequirePrincipalAsync(ct);
        var purged = await tyres.PurgeAsync(await access.LogScopeAsync(AccessLevel.Delete, ct), ct);
        if (purged > 0) logger.LogInformation("User {UserId} emptied the tyre trash: {Count} items deleted for good", user.Id, purged);
        return purged;
    }

    /// <summary>Whether the vehicle's units are locked by its tyres (a rough figure is a number in its distance unit).</summary>
    public Task<bool> LocksUnitsAsync(Guid vehicleId, CancellationToken ct) => tyres.HasRoughFiguresAsync(vehicleId, ct);

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private static TyreOverview Overview(Guid vehicleId, TyreBook book)
    {
        var live = book.Installations.Where(s => !s.IsDeleted).ToList();
        var sets = book.Sets.Where(s => !s.IsDeleted).OrderBy(s => s.IsRetired).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Id)
            .Select(set =>
            {
                var own = live.Where(s => s.SetId == set.Id).OrderByDescending(s => s.From).ToList();
                return new TyreSetView(set, TyreCalculator.SetFigures(book.Readings, set, own),
                    [.. own.Select(s => new TyreInstallationView(s, TyreCalculator.Distance(book.Readings, s.From, s.Until)))], own.Any(s => s.IsOpen));
            })
            .ToList();
        var liveSets = book.Sets.Where(s => !s.IsDeleted).Select(s => s.Id).ToHashSet();
        var entries = book.Entries.Where(e => !e.IsDeleted && (e.SetId is null || liveSets.Contains(e.SetId.Value)))
            .OrderByDescending(e => e.Day).ThenBy(e => e.IsSwapLine ? 0 : 1).ThenBy(e => e.Id).ToList();
        return new TyreOverview(vehicleId, sets, entries, book.Readings);
    }

    private static TyreHistory History(Vehicle vehicle, TyreBook book, TyreSet? extra = null) =>
        new(vehicle.OwnerId, vehicle.Id, extra is null ? book.Sets : [.. book.Sets, extra], book.Installations);

    /// <summary>
    /// A change that creates an installation with this id came again: it was applied already (same creator and vehicle), else the id is taken
    /// (<c>sync.idTaken</c>), looked up beyond the vehicle too.
    /// </summary>
    private async Task<bool> RepeatedAsync(TyreBook book, Guid? id, Guid vehicleId, Guid by, CancellationToken ct)
    {
        if (id is not { } given) return false;
        if ((book.Installations.FirstOrDefault(s => s.Id == given) ?? await tyres.FindInstallationAsync(given, ct)) is not { } existing)
        {
            // The installation's line takes its id. Without one, a Note line with it is what the same change typed where a join made no
            // installation (TypedAsync); any other line with it means the id is someone else's.
            if (await tyres.FindEntryAsync(given, ct) is not { } line) return false;
            ClientIds.Repeat(line, line is { Kind: TyreEntryKind.Note, InstallationId: null } && line.VehicleId == vehicleId && line.CreatedById == by, given);
            return true;
        }
        ClientIds.Repeat(existing, existing.VehicleId == vehicleId && existing.CreatedById == by, given);
        return true;
    }

    /// <summary>The same for an added line (a swap's line has its installation's id, never an added line's).</summary>
    private async Task<bool> RepeatedEntryAsync(Guid? id, Guid vehicleId, Guid by, CancellationToken ct)
    {
        if (id is not { } given) return false;
        if (await tyres.FindEntryAsync(given, ct) is not { } existing)
        {
            if (await tyres.FindInstallationAsync(given, ct) is { } installation) ClientIds.Repeat(installation, false, given);
            return false;
        }
        ClientIds.Repeat(existing, existing.VehicleId == vehicleId && existing.CreatedById == by && !existing.IsSwapLine, given);
        return true;
    }

    /// <summary>The same for an added set.</summary>
    private async Task<bool> RepeatedSetAsync(Guid? id, Guid vehicleId, Guid by, CancellationToken ct)
    {
        if (id is not { } given || await tyres.FindSetAsync(given, ct) is not { } existing) return false;
        ClientIds.Repeat(existing, existing.VehicleId == vehicleId && existing.CreatedById == by, given);
        return true;
    }

    private static TyreSet LiveSet(TyreBook book, Guid id) =>
        book.Sets.FirstOrDefault(s => s.Id == id && !s.IsDeleted)
        ?? throw new NotFoundException("tyres.setNotFound", $"Tyre set {id} does not exist.", new { Id = id });

    /// <summary>The lines a change of the history makes, moves or trashes (<see cref="TyreLog.Follow"/>), with the installations.</summary>
    private void Follow(TyreSave save, TyreBook book, TyreHistory history, TyreChanges changes, Guid by)
    {
        save.Take(changes);
        save.Take(TyreLog.Follow(changes, history, [.. book.Entries, .. save.AddedEntries], by, clock.GetUtcNow()));
    }

    private static TyreEntry? LineOf(TyreBook book, Guid installationId) => book.Entries.FirstOrDefault(e => e.Id == installationId);

    /// <summary>The installation of a swap's line, when it is <paramref name="trashed"/> as asked (else none: the line goes on its own).</summary>
    private static TyreInstallation? InstallationOf(TyreBook book, TyreEntry entry, bool trashed) =>
        entry.InstallationId is { } id ? book.Installations.FirstOrDefault(s => s.Id == id && s.IsDeleted == trashed) : null;

    /// <summary>
    /// Puts what was typed with a change (a reading, a note) on the line of the installation that starts that day, else (a join made no
    /// new installation that day) on a new <see cref="TyreEntryKind.Note"/> line of the set. A reading is set, a note set when given; null
    /// leaves what the line has.
    /// </summary>
    /// <param name="id">The change's own client id (the installation's it would have made): a Note line made in its place takes it, so the
    /// change sent again finds it (<see cref="RepeatedAsync"/>) instead of writing the line twice.</param>
    private async Task TypedAsync(
        Vehicle vehicle, TyreBook book, TyreSave save, TyreHistory history, Guid setId, DateOnly day, long? value, string? note, Guid by, CancellationToken ct,
        Guid? id = null)
    {
        if (value is null && string.IsNullOrWhiteSpace(note)) return;
        var line = history.Live.FirstOrDefault(s => s.From == day) is { } starting
            ? save.AddedEntries.FirstOrDefault(e => e.Id == starting.Id) ?? LineOf(book, starting.Id)
            : null;
        if (value is { } v) await odometer.ValidateAsync(vehicle.Id, day, v, exceptReadingId: line?.OdometerReadingId, ct);
        if (line is null)
        {
            var created = TyreEntry.Create(vehicle.OwnerId, by, vehicle.Id, TyreEntryKind.Note, setId, day, value, note,
                id is { } given && save.AddedEntries.All(e => e.Id != given) && book.Entries.All(e => e.Id != given) ? given : null);
            save.AddedEntries.Add(created);
            if (created.OdometerReading is { } reading) save.AddedReadings.Add(reading);
            return;
        }
        var (added, removed, changed) = line.Update(line.Kind, line.SetId, line.Day, value ?? line.Odometer, note ?? line.Note, by);
        if (added is not null) save.AddedReadings.Add(added);
        if (removed is not null) save.RemovedReadings.Add(removed);
        if (changed) save.Change(line);
    }

    private async Task InstallationReadingsAsync(
        Vehicle vehicle, TyreBook book, TyreSave save, TyreHistory history, Guid setId, TyreInstallationInput input, Guid by, CancellationToken ct)
    {
        await TypedAsync(vehicle, book, save, history, setId, input.From, input.OdometerFrom, null, by, ct);
        if (input.Until is { } until) await TypedAsync(vehicle, book, save, history, setId, until, input.OdometerUntil, null, by, ct);
    }

    /// <summary>A line's reading coming back from the trash must still fit the car's readings, as an expense's does when it is restored.</summary>
    private async Task RequireReadingFitsAsync(Vehicle vehicle, TyreEntry line, CancellationToken ct)
    {
        if (line.IsDeleted && line.OdometerReading is { } reading) await odometer.ValidateAsync(vehicle.Id, line.Day, reading.Value, exceptReadingId: null, ct);
    }

    /// <summary>A swap's line and its installation go to the trash together.</summary>
    private async Task TrashSwapAsync(TyreInstallation installation, TyreEntry? line, CancellationToken ct)
    {
        var (now, by) = (clock.GetUtcNow(), (await access.RequirePrincipalAsync(ct)).Id);
        var save = new TyreSave();
        installation.MarkDeleted(now, by);
        save.Change(installation);
        if (line is { IsDeleted: false })
        {
            line.MarkDeleted(now, by);
            save.Change(line);
        }
        await SaveAsync(save, ct);
    }

    /// <summary>A swap's line and its installation come back together, or not at all (the history's reason).</summary>
    private async Task RestoreSwapAsync(Vehicle vehicle, TyreBook book, TyreInstallation installation, TyreEntry? line, CancellationToken ct)
    {
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        installation.Restore(by);
        History(vehicle, book).CheckRestored([installation]);
        var save = new TyreSave();
        save.Change(installation);
        if (line is { IsDeleted: true })
        {
            await RequireReadingFitsAsync(vehicle, line, ct);
            line.Restore(by);
            save.Change(line);
        }
        await SaveAsync(save, ct);
    }

    /// <summary>
    /// The readings one change saves are each checked against the stored ones (<see cref="OdometerService.ValidateAsync"/>), but not
    /// against each other: in day order they must not fall either (readings of one day are not compared, as everywhere).
    /// </summary>
    private static void RequireRising(TyreSave save)
    {
        var planned = save.AddedEntries.Concat(save.ChangedEntries).Where(e => !e.IsDeleted && e.OdometerReading is not null)
            .Select(e => (e.Day, Value: e.OdometerReading!.Value)).OrderBy(r => r.Day).ToList();
        for (var i = 1; i < planned.Count; i++)
        {
            var (before, after) = (planned[i - 1], planned[i]);
            if (after.Day > before.Day && after.Value < before.Value)
                throw new DomainException("odometer.belowPrevious", $"The odometer cannot be lower than {before.Value} on {before.Day:yyyy-MM-dd}.",
                    new { Previous = before.Value, Date = before.Day.ToString("yyyy-MM-dd") });
        }
    }

    private async Task SaveAsync(TyreSave save, CancellationToken ct)
    {
        RequireRising(save);
        if (!await tyres.SaveAsync(save, ct))
            throw new InvalidOperationException("A tyre change without client ids clashed on an id."); // not reached: only a client's id is chosen twice
    }

    /// <summary>
    /// Saves a change that may create rows with the client's ids. The same change sent twice at once passes the id lookups twice; the one
    /// whose insert then loses (the repository says so) or whose conditional update loses to the other's runs <paramref name="repeat"/>
    /// again, which answers it as the repeat it is (or refuses an id that is someone else's). False: answered as a repeat, nothing saved.
    /// </summary>
    private async Task<bool> SaveOnceAsync(TyreSave save, Func<Task<bool>> repeat, CancellationToken ct)
    {
        RequireRising(save);
        try
        {
            if (await tyres.SaveAsync(save, ct)) return true;
        }
        catch (KeyedException e) when (e.Key == "sync.versionMismatch")
        {
            if (await repeat()) return false;
            throw;
        }
        if (await repeat()) return false;
        throw new InvalidOperationException("A tyre row's id was taken meanwhile, and not by this change."); // the repository found the id there
    }

    private async Task SaveSetAsync(TyreSet set, CancellationToken ct)
    {
        var save = new TyreSave();
        save.ChangedSets.Add(set);
        await SaveAsync(save, ct);
    }

    private void RequireNotFuture(DateOnly day)
    {
        if (day > LatestAllowedDate) throw new DomainException("refueling.dateInFuture", "The date cannot be in the future.");
    }

    private void RequireDays(TyreInstallationInput input)
    {
        RequireNotFuture(input.From);
        if (input.Until is { } until) RequireNotFuture(until);
    }

    private async Task<Vehicle> EditableVehicleAsync(Guid vehicleId, CancellationToken ct) =>
        LogAccessGuard.RequireEdit(await guard.ForVehicleAsync(vehicleId, ct),
            () => new NotFoundException("vehicle.notFound", $"Vehicle {vehicleId} does not exist.", new { Id = vehicleId })).Vehicle;

    private async Task<(Vehicle, TyreSet, TyreBook)> EditableSetAsync(Guid id, CancellationToken ct, bool trashed = false)
    {
        var set = await tyres.FindSetAsync(id, ct);
        NotFoundException Missing() => new("tyres.setNotFound", $"Tyre set {id} does not exist.", new { Id = id });
        var vehicle = LogAccessGuard.RequireEdit(await guard.ForVehicleAsync(set?.VehicleId, ct), Missing).Vehicle;
        if (set!.IsDeleted != trashed) throw trashed ? new DomainException("tyres.notTrashed", "This tyre set is not in the trash.") : Missing();
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        return (vehicle, book.Sets.First(s => s.Id == id), book);
    }

    /// <summary>A line the user may change: one hidden through its set in the trash does not exist (restore the set).</summary>
    private async Task<(Vehicle, TyreEntry, TyreBook)> EditableEntryAsync(Guid id, CancellationToken ct, bool trashed = false)
    {
        var entry = await tyres.FindEntryAsync(id, ct);
        NotFoundException Missing() => new("tyres.entryNotFound", $"Tyre log line {id} does not exist.", new { Id = id });
        var vehicle = LogAccessGuard.RequireEdit(await guard.ForVehicleAsync(entry?.VehicleId, ct), Missing).Vehicle;
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        var loaded = book.Entries.First(e => e.Id == id);
        if (loaded.SetId is { } setId && book.Sets.First(s => s.Id == setId).IsDeleted) throw Missing();
        if (loaded.IsDeleted != trashed) throw trashed ? new DomainException("tyres.entryNotTrashed", "This line of the tyre log is not in the trash.") : Missing();
        return (vehicle, loaded, book);
    }

    private async Task<(Vehicle, TyreInstallation, TyreBook)> EditableInstallationAsync(Guid id, CancellationToken ct, bool trashed = false)
    {
        var installation = await tyres.FindInstallationAsync(id, ct);
        NotFoundException Missing() => new("tyres.installationNotFound", $"Tyre installation {id} does not exist.", new { Id = id });
        var vehicle = LogAccessGuard.RequireEdit(await guard.ForVehicleAsync(installation?.VehicleId, ct), Missing).Vehicle;
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        var loaded = book.Installations.First(s => s.Id == id);
        var setTrashed = book.Sets.First(s => s.Id == loaded.SetId).IsDeleted;
        if (setTrashed && !trashed) throw Missing(); // hidden through its set
        if (loaded.IsDeleted != trashed) throw trashed ? new DomainException("tyres.installationNotTrashed", "This installation is not in the trash.") : Missing();
        return (vehicle, loaded, book);
    }
}
