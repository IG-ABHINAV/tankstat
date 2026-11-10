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

/// <param name="Replace">A swap logged on that day already is replaced (its stretch goes to the trash) instead of refused.</param>
public sealed record TyreSwapInput(Guid VehicleId, Guid SetId, DateOnly Day, bool? PairOneFront, bool Replace, long? Odometer, string? Note);

public sealed record TyrePastSwap(DateOnly Day, Guid SetId, long? Odometer, Guid? StretchId = null);

/// <param name="OdometerFrom">A reading for the first day, kept on that day's tyre log line; null leaves the day as it is.</param>
public sealed record TyreStretchInput(DateOnly From, DateOnly? Until, bool? PairOneFront, long? OdometerFrom, long? OdometerUntil);

/// <summary>A stretch with its distance, worked out on read.</summary>
public sealed record TyreStretchView(TyreStretch Stretch, StretchDistance Distance);

/// <summary>A set with its figures and its live stretches (none while it is in the trash).</summary>
public sealed record TyreSetView(TyreSet Set, TyreSetFigures Figures, IReadOnlyList<TyreStretchView> Stretches, bool OnTheCar);

/// <summary>
/// A vehicle's tyres as the Tyres tab shows them: the sets that are not in the trash and the tyre log's days, with the vehicle's readings
/// (date and value, by date), so a client works out the odometer of a day as <see cref="TyreCalculator.OdometerOn"/> does (a form's hint, a
/// log line), never saving it.
/// </summary>
public sealed record TyreOverview(Guid VehicleId, IReadOnlyList<TyreSetView> Sets, IReadOnlyList<TyreLogDay> Days, IReadOnlyList<ReadingPoint> Readings)
{
    public bool HasReadings => Readings.Count > 0;
}

/// <summary>
/// A vehicle's tyre sets and their time on the car (#287). They follow the access rules of the vehicle's logs (<see cref="LogAccessGuard"/>):
/// View sees, Edit changes, trashes and restores, Delete purges. Every change is checked by <see cref="TyreHistory"/> and saved in one
/// transaction; a reading typed with it is one of the car's readings, owned by that day's <see cref="TyreLogDay"/> and checked against the
/// others. Distances are worked out on every read (<see cref="TyreCalculator"/>).
/// </summary>
public sealed class TyreService(
    LogAccessGuard guard, ITyreRepository tyres, AccessService access, OdometerService odometer, TimeProvider clock, ILogger<TyreService> logger)
{
    /// <summary>A day ahead covers every time zone, like the logs.</summary>
    private DateOnly LatestAllowedDate => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(1);

    public async Task<TyreOverview?> OverviewAsync(Guid vehicleId, CancellationToken ct) =>
        await guard.ForVehicleAsync(vehicleId, ct) is null ? null : Overview(vehicleId, await tyres.LoadAsync(vehicleId, ct));

    /// <param name="id">The set's id chosen by the client: an add sent again is answered with what it created.</param>
    /// <param name="stretchId">The id of the stretch a fitted set starts with.</param>
    public async Task<TyreOverview> AddSetAsync(NewTyreSet input, CancellationToken ct, Guid? id = null, Guid? stretchId = null)
    {
        var vehicle = await EditableVehicleAsync(input.VehicleId, ct);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        async Task<bool> Repeat() => await RepeatedSetAsync(id, vehicle.Id, by, ct) | await RepeatedAsync(book, stretchId, vehicle.Id, by, ct);
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
            save.Take(history.Swap(set, input.Day, input.PairOneFront, input.Replace, by, clock.GetUtcNow(), stretchId));
            await SetDayAsync(vehicle, book, save, input.Day, input.Odometer, note: null, keepNote: true, by, ct);
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
        History(vehicle, book).Check(); // a later "in use since" may no longer fit its stretches
        var save = new TyreSave();
        save.ChangedSets.Add(set);
        await SaveAsync(save, ct);
        logger.LogDebug("Tyre set {SetId} of vehicle {VehicleId} updated", id, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>
    /// Sets the rough figure; <paramref name="until"/> (accepting a suggestion) moves the first day it does not cover. Without one it keeps
    /// the day it had, else it covers the time before the set's first stretch (today when it has none), never time already measured.
    /// </summary>
    public async Task<TyreOverview> SetRoughAsync(Guid id, long distance, DateOnly? until, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, set, book) = await EditableSetAsync(id, ct);
        VersionCheck.Require(expectedVersion, set.Version);
        if (until is { } given) RequireNotFuture(given);
        var first = book.Stretches.Where(s => s.SetId == id && !s.IsDeleted).Select(s => (DateOnly?)s.From).Min();
        set.SetRough(distance, until ?? set.RoughUntil ?? first ?? Today, (await access.RequirePrincipalAsync(ct)).Id);
        var save = new TyreSave();
        save.ChangedSets.Add(set);
        await SaveAsync(save, ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <param name="stretchId">The id of the stretch the swap creates, if it creates one: a swap sent again finds it and changes nothing.</param>
    /// <param name="repeat">Sent from a device's queue (the sync): the set already on the car that day is a retry's answer, not an error.</param>
    public async Task<TyreOverview> SwapAsync(TyreSwapInput input, CancellationToken ct, Guid? stretchId = null, bool repeat = false)
    {
        var vehicle = await EditableVehicleAsync(input.VehicleId, ct);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        Task<bool> Repeat() => RepeatedAsync(book, stretchId, vehicle.Id, by, ct);
        if (await Repeat()) return Overview(vehicle.Id, book);
        RequireNotFuture(input.Day);
        var set = LiveSet(book, input.SetId);
        var save = new TyreSave();
        save.Take(History(vehicle, book).Swap(set, input.Day, input.PairOneFront, input.Replace, by, clock.GetUtcNow(), stretchId, repeatable: repeat));
        await SetDayAsync(vehicle, book, save, input.Day, input.Odometer, input.Note, keepNote: input.Note is null, by, ct);
        if (!await SaveOnceAsync(save, Repeat, ct)) return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
        logger.LogDebug("Tyre set {SetId} of vehicle {VehicleId} went on the car", set.Id, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    public async Task<TyreOverview> ChangePositionsAsync(TyreSwapInput input, CancellationToken ct, Guid? stretchId = null)
    {
        var vehicle = await EditableVehicleAsync(input.VehicleId, ct);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        Task<bool> Repeat() => RepeatedAsync(book, stretchId, vehicle.Id, by, ct);
        if (await Repeat()) return Overview(vehicle.Id, book);
        RequireNotFuture(input.Day);
        var set = LiveSet(book, input.SetId);
        var save = new TyreSave();
        save.Take(History(vehicle, book).ChangePositions(set, input.Day, input.PairOneFront, by, stretchId, repeatable: stretchId is not null));
        await SetDayAsync(vehicle, book, save, input.Day, input.Odometer, input.Note, keepNote: input.Note is null, by, ct);
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
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        async Task<bool> Repeat()
        {
            var repeated = false;
            foreach (var row in rows) repeated |= await RepeatedAsync(book, row.StretchId, vehicle.Id, by, ct);
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
            save.Take(history.Swap(LiveSet(book, row.SetId), row.Day, null, replace, by, clock.GetUtcNow(), row.StretchId, pairsGiven: false, repeatable: repeat));
            if (row.Odometer is not null) await SetDayAsync(vehicle, book, save, row.Day, row.Odometer, note: null, keepNote: true, by, ct);
        }
        if (!await SaveOnceAsync(save, Repeat, ct)) return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
        logger.LogDebug("User {UserId} added {Count} past tyre swaps to vehicle {VehicleId}", by, rows.Count, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    public async Task<TyreOverview> AddStretchAsync(Guid setId, TyreStretchInput input, CancellationToken ct, Guid? id = null)
    {
        var (vehicle, set, book) = await EditableSetAsync(setId, ct);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        Task<bool> Repeat() => RepeatedAsync(book, id, vehicle.Id, by, ct);
        if (await Repeat()) return Overview(vehicle.Id, book);
        RequireDays(input);
        var save = new TyreSave();
        save.AddedStretches.Add(History(vehicle, book).Add(set, input.From, input.Until, input.PairOneFront, by, id));
        await StretchReadingsAsync(vehicle, book, save, input, by, ct);
        await SaveOnceAsync(save, Repeat, ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    public async Task<TyreOverview> UpdateStretchAsync(Guid id, TyreStretchInput input, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, stretch, book) = await EditableStretchAsync(id, ct);
        VersionCheck.Require(expectedVersion, stretch.Version);
        RequireDays(input);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        var save = new TyreSave();
        if (History(vehicle, book).Edit(stretch, input.From, input.Until, input.PairOneFront, by)) save.ChangedStretches.Add(stretch);
        await StretchReadingsAsync(vehicle, book, save, input, by, ct);
        await SaveAsync(save, ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>Moves a stretch to the trash; the reading of its day stays (it is one of the car's readings, on the tyre log's line).</summary>
    public async Task<TyreOverview> TrashStretchAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, stretch, _) = await EditableStretchAsync(id, ct);
        VersionCheck.Require(expectedVersion, stretch.Version);
        stretch.MarkDeleted(clock.GetUtcNow(), (await access.RequirePrincipalAsync(ct)).Id);
        var save = new TyreSave();
        save.ChangedStretches.Add(stretch);
        await SaveAsync(save, ct);
        logger.LogDebug("Tyre stretch {StretchId} of vehicle {VehicleId} moved to the trash", id, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    public async Task<TyreOverview> RestoreStretchAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, stretch, book) = await EditableStretchAsync(id, ct, trashed: true);
        VersionCheck.Require(expectedVersion, stretch.Version);
        if (book.Sets.FirstOrDefault(s => s.Id == stretch.SetId) is not { IsDeleted: false })
            throw new NotFoundException("tyres.stretchNotFound", $"Tyre stretch {id} does not exist.", new { Id = id }); // its set is in the trash: restore that
        stretch.Restore((await access.RequirePrincipalAsync(ct)).Id);
        History(vehicle, book).CheckRestored([stretch]);
        var save = new TyreSave();
        save.ChangedStretches.Add(stretch);
        await SaveAsync(save, ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>Sets or clears the tyre log's reading and note of a day; a day left with neither is deleted (the reading too, for good).</summary>
    /// <param name="id">The id of the day's line when this creates it (the client's); the id of another day's line is refused.</param>
    /// <param name="expectedVersion">The version of the day's line the change was made from, when it has one (see <see cref="VersionCheck"/>).</param>
    public async Task<TyreOverview> SetLogDayAsync(
        Guid vehicleId, DateOnly day, long? odometerValue, string? note, CancellationToken ct, Guid? id = null, int? expectedVersion = null)
    {
        var vehicle = await EditableVehicleAsync(vehicleId, ct);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        RequireNotFuture(day);
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        // A line with that id is this day's (the change edits it, or the same add got there first), else it is someone else's id.
        async Task<bool> Repeat()
        {
            if (id is not { } given || await tyres.FindDayAsync(given, ct) is not { } found) return false;
            ClientIds.Repeat(found, found.VehicleId == vehicle.Id && found.Day == day, given);
            return true;
        }
        await Repeat(); // found here, it is simply the line this change edits
        if (book.Days.FirstOrDefault(d => d.Day == day) is { } existing) VersionCheck.Require(expectedVersion, existing.Version);
        var save = new TyreSave();
        await SetDayAsync(vehicle, book, save, day, odometerValue, note, keepNote: false, by, ct, clear: true, id: id);
        // Only an add of the line can be the same change twice; an edit that lost a race is refused as one.
        await SaveOnceAsync(save, save.AddedDays.Count > 0 ? Repeat : () => Task.FromResult(false), ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>Retires a set in storage on <paramref name="day"/> (the client's today): it keeps everything and takes no swap.</summary>
    public async Task<TyreOverview> RetireAsync(Guid id, DateOnly day, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, set, book) = await EditableSetAsync(id, ct);
        VersionCheck.Require(expectedVersion, set.Version);
        RequireNotFuture(day);
        if (book.Stretches.Any(s => s.SetId == id && !s.IsDeleted && (s.Until is null || s.Until > day)))
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

    /// <summary>Moves a set to the trash; its stretches hide through it and come back with it (one trashed on its own before stays there).</summary>
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
        var history = new TyreHistory(vehicle.OwnerId, vehicle.Id, book.Sets, book.Stretches);
        history.CheckRestored([.. history.Live.Where(s => s.SetId == id)]); // the set's own live stretches now count again
        await SaveSetAsync(set, ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>The set when it is in the trash and the user may restore it, else null (a trash sent again finds it there).</summary>
    public async Task<TyreSet?> SetInTrashAsync(Guid id, CancellationToken ct) =>
        await tyres.FindSetAsync(id, ct) is { IsDeleted: true } set && await guard.ForVehicleAsync(set.VehicleId, ct) is { Level: >= AccessLevel.Edit } ? set : null;

    /// <summary>The stretch when it is in the trash and the user may restore it, else null.</summary>
    public async Task<TyreStretch?> StretchInTrashAsync(Guid id, CancellationToken ct) =>
        await tyres.FindStretchAsync(id, ct) is { IsDeleted: true } stretch && await guard.ForVehicleAsync(stretch.VehicleId, ct) is { Level: >= AccessLevel.Edit } ? stretch : null;

    public async Task<IReadOnlyList<TyreTrashRow>> ListTrashAsync(int skip, int take, CancellationToken ct, bool descending = true) =>
        await tyres.ListTrashAsync(await access.LogScopeAsync(AccessLevel.Edit, ct), Math.Max(0, skip), Math.Clamp(take, 1, 100), ct, descending);

    public async Task<int> CountTrashAsync(CancellationToken ct) => await tyres.CountTrashAsync(await access.LogScopeAsync(AccessLevel.Edit, ct), ct);

    public async Task<int> CountDeletableTrashAsync(CancellationToken ct) => await tyres.CountTrashAsync(await access.LogScopeAsync(AccessLevel.Delete, ct), ct);

    /// <summary>Permanently removes the trashed sets and stretches the user has Delete access to.</summary>
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
        var live = book.Stretches.Where(s => !s.IsDeleted).ToList();
        var sets = book.Sets.Where(s => !s.IsDeleted).OrderBy(s => s.IsRetired).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Id)
            .Select(set =>
            {
                var own = live.Where(s => s.SetId == set.Id).OrderByDescending(s => s.From).ToList();
                return new TyreSetView(set, TyreCalculator.SetFigures(book.Readings, set, own),
                    [.. own.Select(s => new TyreStretchView(s, TyreCalculator.Distance(book.Readings, s.From, s.Until)))], own.Any(s => s.IsOpen));
            })
            .ToList();
        return new TyreOverview(vehicleId, sets, [.. book.Days.OrderByDescending(d => d.Day)], book.Readings);
    }

    private static TyreHistory History(Vehicle vehicle, TyreBook book, TyreSet? extra = null) =>
        new(vehicle.OwnerId, vehicle.Id, extra is null ? book.Sets : [.. book.Sets, extra], book.Stretches);

    /// <summary>
    /// A change that creates a stretch with this id came again: it was applied already (same creator and vehicle), else the id is taken
    /// (<c>sync.idTaken</c>), looked up beyond the vehicle too.
    /// </summary>
    private async Task<bool> RepeatedAsync(TyreBook book, Guid? id, Guid vehicleId, Guid by, CancellationToken ct)
    {
        if (id is not { } given) return false;
        if ((book.Stretches.FirstOrDefault(s => s.Id == given) ?? await tyres.FindStretchAsync(given, ct)) is not { } existing) return false;
        ClientIds.Repeat(existing, existing.VehicleId == vehicleId && existing.CreatedById == by, given);
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

    /// <summary>
    /// Gives the day's tyre log line the reading (and note): created when there is none, else edited; validated against the car's other
    /// readings, the day's own excluded. <paramref name="keepNote"/> leaves a note as it is; <paramref name="clear"/> lets a null reading
    /// clear the one there (else null leaves it).
    /// </summary>
    private async Task SetDayAsync(
        Vehicle vehicle, TyreBook book, TyreSave save, DateOnly day, long? value, string? note, bool keepNote, Guid by, CancellationToken ct, bool clear = false, Guid? id = null)
    {
        var existing = save.AddedDays.FirstOrDefault(d => d.Day == day) ?? book.Days.FirstOrDefault(d => d.Day == day);
        if (value is { } v) await odometer.ValidateAsync(vehicle.Id, day, v, exceptReadingId: existing?.OdometerReadingId, ct);
        if (existing is null)
        {
            var created = TyreLogDay.Create(vehicle.OwnerId, by, vehicle.Id, day, value, note, id);
            if (created.IsEmpty) return; // created only for a reading or a note (a blank one is none)
            save.AddedDays.Add(created);
            if (created.OdometerReading is { } reading) save.AddedReadings.Add(reading);
            return;
        }
        var version = existing.Version;
        var (added, removed) = existing.Update(value ?? (clear ? null : existing.Odometer), keepNote ? existing.Note : note, by);
        if (added is not null) save.AddedReadings.Add(added);
        if (removed is not null) save.RemovedReadings.Add(removed);
        if (save.AddedDays.Contains(existing) || existing.Version == version) return; // nothing changed: the line is not written again
        if (existing.IsEmpty) save.RemovedDays.Add(existing);
        else if (!save.ChangedDays.Contains(existing)) save.ChangedDays.Add(existing);
    }

    private async Task StretchReadingsAsync(Vehicle vehicle, TyreBook book, TyreSave save, TyreStretchInput input, Guid by, CancellationToken ct)
    {
        if (input.OdometerFrom is not null) await SetDayAsync(vehicle, book, save, input.From, input.OdometerFrom, null, keepNote: true, by, ct);
        if (input.OdometerUntil is not null && input.Until is { } until) await SetDayAsync(vehicle, book, save, until, input.OdometerUntil, null, keepNote: true, by, ct);
    }

    /// <summary>
    /// The readings one change saves are each checked against the stored ones (<see cref="OdometerService.ValidateAsync"/>), but not
    /// against each other: in day order they must not fall either (readings of one day are not compared, as everywhere).
    /// </summary>
    private static void RequireRising(TyreSave save)
    {
        var planned = save.AddedDays.Concat(save.ChangedDays).Where(d => d.OdometerReading is not null)
            .Select(d => (d.Day, Value: d.OdometerReading!.Value)).OrderBy(r => r.Day).ToList();
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

    private void RequireDays(TyreStretchInput input)
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

    private async Task<(Vehicle, TyreStretch, TyreBook)> EditableStretchAsync(Guid id, CancellationToken ct, bool trashed = false)
    {
        var stretch = await tyres.FindStretchAsync(id, ct);
        NotFoundException Missing() => new("tyres.stretchNotFound", $"Tyre stretch {id} does not exist.", new { Id = id });
        var vehicle = LogAccessGuard.RequireEdit(await guard.ForVehicleAsync(stretch?.VehicleId, ct), Missing).Vehicle;
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        var loaded = book.Stretches.First(s => s.Id == id);
        var setTrashed = book.Sets.First(s => s.Id == loaded.SetId).IsDeleted;
        if (setTrashed && !trashed) throw Missing(); // hidden through its set
        if (loaded.IsDeleted != trashed) throw trashed ? new DomainException("tyres.stretchNotTrashed", "This stretch is not in the trash.") : Missing();
        return (vehicle, loaded, book);
    }
}
