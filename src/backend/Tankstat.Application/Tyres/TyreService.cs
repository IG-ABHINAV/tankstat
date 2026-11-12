using Tankstat.Domain.Odometers;
using Tankstat.Domain.Measurements;
using Tankstat.Application.Expenses;
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
/// <param name="Price">What the set cost (#288): a <see cref="TyreEntryKind.Bought"/> line of the tyre log with its expense, on <paramref name="BoughtOn"/>.</param>
/// <param name="BoughtOn">The day the set was bought (a used set asks for it); none takes <paramref name="Day"/>.</param>
/// <param name="BoughtEntryId">The client's id of that line.</param>
public sealed record NewTyreSet(
    Guid VehicleId, TyreSetInput Set, bool Fitted, DateOnly Day, long? Odometer, bool? PairOneFront, bool Replace = false, TyreFee? Price = null,
    DateOnly? BoughtOn = null, Guid? BoughtEntryId = null);

/// <param name="Replace">A swap logged on that day already is replaced (its installation goes to the trash) instead of refused.</param>
/// <param name="Fee">What the swap (or the change of positions) cost (#288): on the line of the installation that starts that day.</param>
/// <param name="Corners">For a broken-up set (#289): each tyre's corner from that day (a tyre left out has none, "Not sure"); null gives each
/// the corner it had before. A whole set ignores it.</param>
public sealed record TyreSwapInput(
    Guid VehicleId, Guid SetId, DateOnly Day, bool? PairOneFront, bool Replace, long? Odometer, string? Note, TyreFee? Fee = null,
    IReadOnlyDictionary<Guid, TyreCorner?>? Corners = null);

/// <summary>Breaking a set up into four single tyres (#289) on <paramref name="Day"/> (the client's today): each tyre's id, label and corner.</summary>
public sealed record TyreBreakUpInput(Guid SetId, DateOnly Day, IReadOnlyList<NewTyre> Tyres);

/// <summary>A single tyre's time on the car, typed in its history (#289): the days and the corner (null: not recorded).</summary>
public sealed record TyrePlaceInput(DateOnly From, DateOnly? Until, TyreCorner? Corner);

/// <summary>A time on the car of a single tyre with its distance, worked out on read.</summary>
public sealed record TyrePlaceView(TyrePlace Place, InstallationDistance Distance);

/// <summary>A single tyre of a broken-up set (#289), with its figures and its live places, newest first.</summary>
/// <param name="OnTheCar">One of its places is open (its set on the car, the tyre on it).</param>
/// <param name="Corner">Its corner now when on the car, else the last one it had (null: not recorded).</param>
/// <param name="Spent">What it cost (#289): its lines' expenses, each by its even share among the lines holding it (the owner's decision 40).</param>
/// <param name="Repairs">Its punctures repaired and valves replaced.</param>
public sealed record TyreView(Tyre Tyre, TyreFigures Figures, IReadOnlyList<TyrePlaceView> Places, bool OnTheCar, TyreCorner? Corner,
    IReadOnlyList<TyreSpent>? Spent = null, int Repairs = 0);

/// <summary>
/// The amount a tyre log line carries (#288): an ordinary expense on the Expenses tab, with the line's day and reading. Null as a whole leaves
/// the line's as it is (an older client knows none); an <paramref name="Amount"/> of null takes it away (the expense goes to the trash).
/// </summary>
/// <param name="Title">The expense's title when it is made (the client words it, "Puncture repaired: Michelin Primacy 4"); kept after that.</param>
/// <param name="ExpenseId">The client's id for the expense it makes, so the change sent again makes one.</param>
public sealed record TyreFee(decimal? Amount, string? Currency, string? Category, string? Title, Guid? ExpenseId = null);

public sealed record TyrePastSwap(DateOnly Day, Guid SetId, long? Odometer, Guid? InstallationId = null);

/// <param name="OdometerFrom">A reading for the first day, kept on the installation's line; null leaves the line's reading as it is.</param>
/// <param name="OdometerUntil">A reading for the day it came off, kept on the line of the installation that starts that day, else on a
/// <see cref="TyreEntryKind.Note"/> line of the set.</param>
public sealed record TyreInstallationInput(DateOnly From, DateOnly? Until, bool? PairOneFront, long? OdometerFrom, long? OdometerUntil);

/// <summary>A line for a single tyre (#289) of an entry for several: the tyre, and the line's client id.</summary>
public sealed record TyreLine(Guid TyreId, Guid? Id = null);

/// <summary>What an emptied amount shared with other lines does (#289, the owner's decision 30): the person is asked on save.</summary>
public enum SharedExpenseChoice
{
    /// <summary>Only the lines changed let go of it; it stays with the others.</summary>
    LetGo,

    /// <summary>The expense goes to the trash: no line of it keeps an amount.</summary>
    Trash,
}

/// <summary>A tyre log line as typed (#288): of any kind but the two of an installation, which come with the swap.</summary>
/// <param name="SetId">The set it concerns, or none.</param>
/// <param name="Fee">Its amount (a <see cref="TyreEntryKind.Note"/> takes none: <c>tyres.noteTakesNoAmount</c>).</param>
/// <param name="Tyres">An add for single tyres of the set (#289): a line each, made together and sharing one expense and reading; none: the
/// whole set's line (or one of no set).</param>
/// <param name="BreakUp">An add for some tyres of a whole set breaks it up first (the owner's decision 19), its tyres' ids in it.</param>
/// <param name="Also">An edit: the other lines made together with this one that change too (*Also change it for*).</param>
/// <param name="SharedExpense">An edit emptying an amount other live lines share: what it does (required then: <c>tyres.sharedExpenseChoice</c>).</param>
public sealed record TyreEntryInput(
    TyreEntryKind Kind, Guid? SetId, DateOnly Day, long? Odometer, string? Note, TyreFee? Fee = null,
    IReadOnlyList<TyreLine>? Tyres = null, TyreBreakUpInput? BreakUp = null, IReadOnlyList<Guid>? Also = null, SharedExpenseChoice? SharedExpense = null);

/// <summary>An installation with its distance, worked out on read.</summary>
public sealed record TyreInstallationView(TyreInstallation Installation, InstallationDistance Distance);

/// <summary>A set with its figures and its live installations (none while it is in the trash).</summary>
/// <param name="Spent">What it cost (#288 step 4): its live lines' live expenses, per currency.</param>
/// <param name="Repairs">Its live punctures repaired and valves replaced.</param>
/// <param name="Tyres">A broken-up set's tyres not in the trash, retired ones included, by label (#289).</param>
public sealed record TyreSetView(TyreSet Set, TyreSetFigures Figures, IReadOnlyList<TyreInstallationView> Installations, bool OnTheCar,
    IReadOnlyList<TyreSpent>? Spent = null, int Repairs = 0, IReadOnlyList<TyreView>? Tyres = null);

/// <summary>
/// A vehicle's tyres as the Tyres tab shows them: the sets that are not in the trash and the tyre log's lines (those not in the trash, of
/// no set or of a set not in the trash), with the vehicle's readings
/// (date and value, by date), so a client works out the odometer of a day as <see cref="TyreCalculator.OdometerOn"/> does (a form's hint, a
/// log line), never saving it.
/// </summary>
/// <param name="ExpenseCategory">The category of the latest expense a line's amount made (#288), to offer for the next; none before the first.</param>
/// <param name="SpentAll">What the tyres cost altogether, per currency: every set's and every line of no set's (#288 step 4).</param>
/// <param name="SpentNoSet">What the lines of no set cost, per currency.</param>
public sealed record TyreOverview(
    Guid VehicleId, IReadOnlyList<TyreSetView> Sets, IReadOnlyList<TyreEntry> Entries, IReadOnlyList<ReadingPoint> Readings, string? ExpenseCategory = null,
    IReadOnlyList<TyreSpent>? SpentAll = null, IReadOnlyList<TyreSpent>? SpentNoSet = null)
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
    LogAccessGuard guard, ITyreRepository tyres, IExpenseRepository expenses, AccessService access, OdometerService odometer, TimeProvider clock,
    ILogger<TyreService> logger)
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
        var save = new TyreSave { Book = book, By = by };
        save.AddedSets.Add(set);
        if (input.Fitted)
        {
            var (history, places) = Rules(vehicle, book, extra: set);
            Follow(save, book, history, places.Swap(set, input.Day, input.PairOneFront, input.Replace, null, by, clock.GetUtcNow(), installationId), by);
            await TypedAsync(vehicle, book, save, history, set.Id, input.Day, input.Odometer, note: null, by, ct, installationId);
        }
        if (input.Price is { Amount: not null } price)
        {
            // What it cost: a Bought line of the tyre log on the day it was bought, its amount an expense (#288).
            var boughtOn = input.BoughtOn ?? input.Day;
            RequireNotFuture(boughtOn);
            // The line's id is the client's: one a line or an installation has is someone else's (this add's own was answered as a repeat above).
            if (input.BoughtEntryId is { } boughtId
                && (boughtId == installationId || await tyres.FindEntryAsync(boughtId, ct) is not null || await tyres.FindInstallationAsync(boughtId, ct) is not null))
                ClientIds.Repeat(boughtId, sameAdd: false, boughtId);
            var bought = TyreEntry.Create(vehicle.OwnerId, by, vehicle.Id, TyreEntryKind.Bought, set.Id, boughtOn, null, null, input.BoughtEntryId);
            save.AddedEntries.Add(bought);
            await ApplyFeeAsync(vehicle, book, save, bought, price, by, ct);
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
        var save = new TyreSave { Book = book, By = by };
        var (history, places) = Rules(vehicle, book);
        Follow(save, book, history,
            places.Swap(set, input.Day, input.PairOneFront, input.Replace, input.Corners, by, clock.GetUtcNow(), installationId, repeatable: repeat), by);
        await TypedAsync(vehicle, book, save, history, set.Id, input.Day, input.Odometer, input.Note, by, ct, installationId, input.Fee);
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
        var save = new TyreSave { Book = book, By = by };
        var (history, places) = Rules(vehicle, book);
        Follow(save, book, history, places.ChangePositions(set, input.Day, input.PairOneFront, input.Corners, by, installationId, repeatable: repeat), by);
        await TypedAsync(vehicle, book, save, history, set.Id, input.Day, input.Odometer, input.Note, by, ct, installationId, input.Fee);
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
        var (history, places) = Rules(vehicle, book);
        var save = new TyreSave { Book = book, By = by };
        foreach (var row in rows.OrderBy(r => r.Day))
        {
            RequireNotFuture(row.Day);
            // From papers: a broken-up set's tyres take the corners they had before (#289).
            Follow(save, book, history,
                places.Swap(LiveSet(book, row.SetId), row.Day, null, replace, null, by, clock.GetUtcNow(), row.InstallationId, pairsGiven: false, repeatable: repeat), by);
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
        var save = new TyreSave { Book = book, By = by };
        var (history, places) = Rules(vehicle, book);
        var (created, placed) = places.AddInstallation(set, input.From, input.Until, input.PairOneFront, by, id); // a broken-up set's tyres at their corners before (#289)
        save.AddedInstallations.Add(created);
        save.Take(placed);
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
        var save = new TyreSave { Book = book, By = by };
        var (history, places) = Rules(vehicle, book);
        var (edited, placed) = places.EditInstallation(installation, input.From, input.Until, input.PairOneFront, by); // the places at its ends follow (#289)
        save.Take(placed);
        if (edited)
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
        await TrashSwapAsync(book, installation, LineOf(book, id), withExpense: true, ct);
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

    /// <summary>
    /// Adds a line to the tyre log (#288); for single tyres of a set (#289) a line each, made together (their group's id the first line's)
    /// and holding one reading and one expense, breaking a whole set up first when asked.
    /// </summary>
    /// <param name="id">The line's id chosen by the client (the first tyre's line's when it names none): an add sent again is answered with what it created.</param>
    public async Task<TyreOverview> AddEntryAsync(Guid vehicleId, TyreEntryInput input, CancellationToken ct, Guid? id = null)
    {
        var vehicle = await EditableVehicleAsync(vehicleId, ct);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        var firstId = input.Tyres is [{ Id: { } given }, ..] ? given : id;
        Task<bool> Repeat() => RepeatedEntryAsync(firstId, vehicle.Id, by, ct);
        if (await Repeat()) return Overview(vehicle.Id, book);
        RequireNotFuture(input.Day);
        if (input.SetId is { } setId) LiveSet(book, setId);
        if (input.Odometer is { } value) await odometer.ValidateAsync(vehicle.Id, input.Day, value, exceptReadingId: null, ct);
        var save = new TyreSave { Book = book, By = by };
        var (_, places) = Rules(vehicle, book);
        if (input.BreakUp is { } breakUp)
        {
            // A repair of some tyres of a whole set breaks it up on save (the owner's decision 19).
            var set = LiveSet(book, breakUp.SetId);
            if (set.Id != input.SetId) throw new DomainException("tyres.tyreNotOfSet", "A line of a tyre is a line of its set.");
            save.Take(places.BreakUp(set, breakUp.Tyres, breakUp.Day, by));
            save.ChangedSets.Add(set);
        }
        var lines = new List<TyreEntry>();
        if (input.Tyres is { Count: > 0 } picked)
        {
            if (input.SetId is not { } of) throw new DomainException("tyres.tyreNeedsSet", "A line of a tyre is a line of its set.");
            if (picked.Select(p => p.TyreId).Distinct().Count() != picked.Count) throw new DomainException("tyres.tyreTwice", "Each tyre once.");
            foreach (var p in picked)
                if (places.TyresOf(of).FirstOrDefault(t => t.Id == p.TyreId) is not { IsRetired: false })
                    throw new NotFoundException("tyres.tyreNotFound", $"Tyre {p.TyreId} does not exist.", new { Id = p.TyreId });
            var first = TyreEntry.Create(vehicle.OwnerId, by, vehicle.Id, input.Kind, of, input.Day, input.Odometer, input.Note, firstId, picked[0].TyreId,
                groupId: picked.Count > 1 ? firstId ?? Guid.NewGuid() : null);
            if (picked.Count > 1 && first.GroupId != first.Id) first = Regroup(first); // no id given: the group takes the first line's
            lines.Add(first);
            lines.AddRange(picked.Skip(1).Select(p => first.ForAnotherTyre(p.TyreId, by, p.Id)));
        }
        else lines.Add(TyreEntry.Create(vehicle.OwnerId, by, vehicle.Id, input.Kind, input.SetId, input.Day, input.Odometer, input.Note, id));
        foreach (var line in lines) save.AddedEntries.Add(line);
        if (lines[0].OdometerReading is { } reading) save.AddedReadings.Add(reading);
        await ApplyFeeAsync(vehicle, book, save, lines[0], input.Fee, by, ct);
        if (lines[0].ExpenseId is { } expenseId) foreach (var other in lines.Skip(1)) other.LinkExpense(expenseId); // one expense, counted once
        if (!await SaveOnceAsync(save, Repeat, ct)) return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
        logger.LogDebug("User {UserId} added {Count} tyre log lines from {EntryId} to vehicle {VehicleId}", by, lines.Count, lines[0].Id, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));

        TyreEntry Regroup(TyreEntry line) =>
            TyreEntry.Create(vehicle.OwnerId, by, vehicle.Id, input.Kind, line.SetId, input.Day, input.Odometer, input.Note, line.Id, line.TyreId, groupId: line.Id);
    }

    /// <summary>
    /// Changes a line, with the other lines made together with it that <see cref="TyreEntryInput.Also"/> names (#289). A swap's line keeps its
    /// kind and set (<c>tyres.swapLineLocked</c>); a new day moves the swap with it, by the history's rules (<see cref="TyreHistory.MoveSwap"/>).
    /// The reading moves with the line's day; null clears it. Lines changed without the others of their group leave it (a row of the log of
    /// their own); with a day or odometer of their own they also part from the expense they shared with the others (the owner's decision 29).
    /// An amount emptied while other live lines share it does what <see cref="TyreEntryInput.SharedExpense"/> says (decision 30).
    /// </summary>
    public async Task<TyreOverview> UpdateEntryAsync(Guid id, TyreEntryInput input, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, entry, book) = await EditableEntryAsync(id, ct);
        VersionCheck.Require(expectedVersion, entry.Version);
        RequireNotFuture(input.Day);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        var save = new TyreSave { Book = book, By = by };
        List<TyreEntry> lines = [entry];
        foreach (var other in input.Also ?? [])
            lines.Add(book.Entries.FirstOrDefault(e => e.Id == other && e.GroupId is not null && e.GroupId == entry.GroupId && IsLiveLine(book, e))
                ?? throw new NotFoundException("tyres.entryNotFound", $"Tyre log line {other} does not exist.", new { Id = other }));
        lines = [.. lines.Distinct()];
        if (entry.IsSwapLine)
        {
            if (input.Kind != entry.Kind || input.SetId != entry.SetId)
                throw new DomainException("tyres.swapLineLocked", "A swap keeps its set. For another set, move the swap to the trash and log it again.");
            var installation = book.Installations.First(s => s.Id == entry.InstallationId);
            var (history, places) = Rules(vehicle, book);
            Follow(save, book, history, places.MoveSwap(installation, input.Day, by), by);
        }
        else if (input.SetId is { } setId && setId != entry.SetId)
        {
            if (entry.TyreId is not null) throw new DomainException("tyres.tyreNotOfSet", "A line of a tyre is a line of its set.");
            LiveSet(book, setId);
        }
        // Changed without the others of its group: a row of its own; a day or odometer of its own parts it from their expense too.
        var holders = entry.ExpenseId is { } shared ? Holders(save, book, shared) : [];
        var allHolders = holders.All(lines.Contains);
        var group = entry.GroupId is { } g ? book.Entries.Where(e => e.GroupId == g && IsLiveLine(book, e)).ToList() : [];
        var ownDays = input.Day != entry.Day || input.Odometer != entry.Odometer;
        var parted = !allHolders && ownDays && entry.ExpenseId is not null;
        var changesAny = ownDays || input.Kind != entry.Kind || Trimmed(input.Note) != entry.Note || input.SetId != entry.SetId;
        foreach (var line in lines)
        {
            if (!allHolders && ownDays && line.ExpenseId is not null)
            {
                if (line.LeaveGroup(partFromExpense: true) is { } own) save.AddedReadings.Add(own);
                save.Change(line);
            }
            else if (changesAny && group.Count > 0 && !group.All(lines.Contains) && line.GroupId is not null)
            {
                line.LeaveGroup(partFromExpense: false);
                save.Change(line);
            }
        }
        if (input.Odometer is { } value) await odometer.ValidateAsync(vehicle.Id, input.Day, value, exceptReadingId: entry.OdometerReadingId, ct);
        KeepReadingsOfOthers(save, book, lines);
        OdometerReading? made = null;
        foreach (var line in lines)
        {
            // The lines still holding one reading share it: the first changes it, the others take what it made.
            if (made is not null) line.HoldReading(made);
            var (added, removed, changed) = line.Update(input.Kind, line.TyreId is not null ? line.SetId : input.SetId, input.Day, input.Odometer, input.Note, by);
            if (added is not null)
            {
                save.AddedReadings.Add(added);
                made ??= added;
            }
            if (removed is not null) save.RemovedReadings.Add(removed);
            if (changed) save.Change(line);
        }
        // A line that parted here keeps its day without an amount (decision 29), whatever amount the dialog still showed.
        if (!parted) await ApplySharedFeeAsync(vehicle, book, save, entry, lines, input, by, ct);
        // An amount typed here makes an expense with the client's id: the same change sent twice at once is answered as the repeat it is.
        if (save.AddedExpenses.Count == 0) await SaveAsync(save, ct);
        else await SaveOnceAsync(save, async () => input.Fee?.ExpenseId is { } given && (await tyres.FindEntryAsync(id, ct))?.ExpenseId == given, ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>
    /// The amount of an edit (#289): typed, it changes the one expense for every line holding it (one made now is linked to every line
    /// changed); emptied while other live lines share it, they keep it and the lines changed let go, or it goes to the trash, as the person chose.
    /// </summary>
    private async Task ApplySharedFeeAsync(Vehicle vehicle, TyreBook book, TyreSave save, TyreEntry entry, IReadOnlyList<TyreEntry> lines, TyreEntryInput input, Guid by, CancellationToken ct)
    {
        if (input.Fee is not { } fee) return;
        var expense = ExpenseOf(save, book, entry);
        var others = entry.ExpenseId is { } shared ? Holders(save, book, shared).Where(h => !lines.Contains(h)).ToList() : [];
        if (fee.Amount is null && expense is { IsDeleted: false } && others.Count > 0)
        {
            switch (input.SharedExpense)
            {
                case SharedExpenseChoice.LetGo:
                    foreach (var line in lines)
                    {
                        if (line.LeaveGroup(partFromExpense: true) is { } own) save.AddedReadings.Add(own);
                        save.Change(line);
                    }
                    return;
                case SharedExpenseChoice.Trash:
                    expense.MarkDeleted(clock.GetUtcNow(), by);
                    save.Change(expense);
                    return;
                default:
                    throw new DomainException("tyres.sharedExpenseChoice", "This amount is shared with other tyres: say whether only these let go of it or it goes to the trash.");
            }
        }
        await ApplyFeeAsync(vehicle, book, save, entry, fee, by, ct);
        if (entry.ExpenseId is { } made) foreach (var line in lines.Skip(1).Where(l => l.ExpenseId is null)) line.LinkExpense(made);
    }

    private static bool IsLiveLine(TyreBook book, TyreEntry line) => book.IsLive(line);

    /// <summary>
    /// Before live lines change the reading they share (#289): every other line holding it that is not live takes a copy of its own, so a
    /// line in the trash keeps its day and value (and parts from the expense when restored, as #288 has it).
    /// </summary>
    private void KeepReadingsOfOthers(TyreSave save, TyreBook book, IReadOnlyCollection<TyreEntry> changing)
    {
        foreach (var reading in changing.Select(l => l.OdometerReading).OfType<OdometerReading>().Distinct().ToList())
            foreach (var other in book.Entries.Where(e => !changing.Contains(e) && e.OdometerReadingId == reading.Id && !IsLiveLine(book, e)).ToList())
                if (other.KeepOwnReading(clock.GetUtcNow()) is { } own)
                {
                    save.AddedReadings.Add(own);
                    save.Change(other);
                }
    }

    /// <summary>The live lines holding an expense (#289: the lines made together for several tyres share one).</summary>
    private static List<TyreEntry> Holders(TyreSave save, TyreBook book, Guid expenseId) =>
        [.. book.Entries.Concat(save.AddedEntries).Distinct().Where(e => e.ExpenseId == expenseId && IsLiveLine(book, e))];

    private static string? Trimmed(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    /// <summary>
    /// Moves a line to the trash; a swap's line takes its installation along, so the swap's time on the car no longer counts. Its expense goes
    /// along when <paramref name="withExpense"/> (the remove dialog's box), and comes back with it then; else it stays, with the reading.
    /// </summary>
    public async Task<TyreOverview> TrashEntryAsync(Guid id, CancellationToken ct, int? expectedVersion = null, bool withExpense = true)
    {
        var (vehicle, entry, book) = await EditableEntryAsync(id, ct);
        VersionCheck.Require(expectedVersion, entry.Version);
        await TrashSwapAsync(book, InstallationOf(book, entry, trashed: false), entry, withExpense, ct);
        logger.LogDebug("Tyre log line {EntryId} of vehicle {VehicleId} moved to the trash", id, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>Brings a line back; a swap's line brings its installation back, or is refused with the history's reason.</summary>
    public async Task<TyreOverview> RestoreEntryAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, entry, book) = await EditableEntryAsync(id, ct, trashed: true);
        VersionCheck.Require(expectedVersion, entry.Version);
        // The line's reading is checked where it comes back (RestoreSwapAsync), a swap's with its installation.
        await RestoreSwapAsync(vehicle, book, InstallationOf(book, entry, trashed: true), entry, ct);
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
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        set.Retire(day, by);
        var save = new TyreSave();
        save.ChangedSets.Add(set);
        // Its tyres retire with it (#289), so unretiring it brings back these and not one replaced before.
        foreach (var tyre in book.SingleTyres.Where(t => t.SetId == id && !t.IsDeleted && !t.IsRetired))
        {
            tyre.Retire(day, TyreRetiredWhy.SetRetired, by);
            save.Change(tyre);
        }
        await SaveAsync(save, ct);
        logger.LogDebug("Tyre set {SetId} of vehicle {VehicleId} retired", id, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    public async Task<TyreOverview> UnretireAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, set, book) = await EditableSetAsync(id, ct);
        VersionCheck.Require(expectedVersion, set.Version);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        set.Unretire(by);
        var save = new TyreSave();
        save.ChangedSets.Add(set);
        foreach (var tyre in book.SingleTyres.Where(t => t.SetId == id && !t.IsDeleted && t.RetiredWhy == TyreRetiredWhy.SetRetired))
        {
            tyre.Unretire(by);
            save.Change(tyre);
        }
        await SaveAsync(save, ct);
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
        var (history, places) = Rules(vehicle, book);
        history.CheckRestored([.. history.Live.Where(s => s.SetId == id)]); // the set's own live installations now count again
        // Its tyres come back too (#289): a label another tyre took meanwhile is refused, as are places that no longer fit.
        foreach (var tyre in places.TyresOf(id)) places.RequireLabelFree(tyre.Label, except: tyre);
        places.CheckRestored([.. places.LivePlaces.Where(p => places.TyresOf(id).Any(t => t.Id == p.TyreId))]);
        await SaveSetAsync(set, ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>
    /// Breaks a set up into four single tyres (#289), never undone: each starts with the set's history and rough figure (<see cref="TyrePlaces.BreakUp"/>).
    /// The tyres' ids are the client's: the same break-up sent again is answered with what it made.
    /// </summary>
    public async Task<TyreOverview> BreakUpAsync(TyreBreakUpInput input, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, set, book) = await EditableSetAsync(input.SetId, ct);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        Task<bool> Repeat() => RepeatedTyresAsync(input.Tyres.Select(t => t.Id), vehicle.Id, by, ct);
        if (await Repeat()) return Overview(vehicle.Id, book);
        VersionCheck.Require(expectedVersion, set.Version);
        RequireNotFuture(input.Day);
        var (_, places) = Rules(vehicle, book);
        var save = new TyreSave { Book = book, By = by };
        save.Take(places.BreakUp(set, input.Tyres, input.Day, by));
        save.ChangedSets.Add(set);
        if (!await SaveOnceAsync(save, Repeat, ct)) return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
        logger.LogDebug("Tyre set {SetId} of vehicle {VehicleId} is tracked tyre by tyre from now on", set.Id, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>A single tyre's new label (#289): unique among the vehicle's tyres not in the trash, without case.</summary>
    public async Task<TyreOverview> UpdateTyreAsync(Guid id, string label, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, tyre, book) = await EditableTyreAsync(id, ct);
        VersionCheck.Require(expectedVersion, tyre.Version);
        var (_, places) = Rules(vehicle, book);
        places.RequireLabelFree(Tyre.CheckedLabel(label), except: tyre);
        if (tyre.Relabel(label, (await access.RequirePrincipalAsync(ct)).Id))
        {
            var save = new TyreSave();
            save.Change(tyre);
            await SaveAsync(save, ct);
        }
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>Moves a single tyre to the trash (#289): its places hide through it, so its corner is empty for that time; they come back with it.</summary>
    public async Task<TyreOverview> TrashTyreAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, tyre, _) = await EditableTyreAsync(id, ct);
        VersionCheck.Require(expectedVersion, tyre.Version);
        tyre.MarkDeleted(clock.GetUtcNow(), (await access.RequirePrincipalAsync(ct)).Id);
        var save = new TyreSave();
        save.Change(tyre);
        await SaveAsync(save, ct);
        logger.LogDebug("Tyre {TyreId} of vehicle {VehicleId} moved to the trash", id, vehicle.Id);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>Brings a single tyre back with its places: refused when another tyre took its label meanwhile, or another tyre is at its corner then.</summary>
    public async Task<TyreOverview> RestoreTyreAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, tyre, book) = await EditableTyreAsync(id, ct, trashed: true);
        VersionCheck.Require(expectedVersion, tyre.Version);
        tyre.Restore((await access.RequirePrincipalAsync(ct)).Id);
        var (_, places) = Rules(vehicle, book);
        places.RequireLabelFree(tyre.Label, except: tyre);
        places.CheckRestored([.. places.LivePlaces.Where(p => p.TyreId == id)]);
        var save = new TyreSave();
        save.Change(tyre);
        await SaveAsync(save, ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>A time on the car typed in a single tyre's history (#289), inside one of its set's installations.</summary>
    /// <param name="id">The client's id: the same add sent again is answered with what it made.</param>
    public async Task<TyreOverview> AddPlaceAsync(Guid tyreId, TyrePlaceInput input, CancellationToken ct, Guid? id = null)
    {
        var (vehicle, tyre, book) = await EditableTyreAsync(tyreId, ct);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        Task<bool> Repeat() => RepeatedPlaceAsync(id, vehicle.Id, by, ct);
        if (await Repeat()) return Overview(vehicle.Id, book);
        RequireNotFuture(input.From);
        if (input.Until is { } until) RequireNotFuture(until);
        var (_, places) = Rules(vehicle, book);
        var save = new TyreSave { Book = book, By = by };
        save.AddedPlaces.Add(places.Add(tyre, input.From, input.Until, input.Corner, by, id));
        if (!await SaveOnceAsync(save, Repeat, ct)) return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>Changes a single tyre's time on the car (#289): that tyre's alone.</summary>
    public async Task<TyreOverview> UpdatePlaceAsync(Guid id, TyrePlaceInput input, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, place, book) = await EditablePlaceAsync(id, ct);
        VersionCheck.Require(expectedVersion, place.Version);
        RequireNotFuture(input.From);
        if (input.Until is { } until) RequireNotFuture(until);
        var (_, places) = Rules(vehicle, book);
        if (places.Edit(place, input.From, input.Until, input.Corner, (await access.RequirePrincipalAsync(ct)).Id))
        {
            var save = new TyreSave();
            save.Change(place);
            await SaveAsync(save, ct);
        }
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>Moves a single tyre's time on the car to the trash (#289): its corner is empty for that time.</summary>
    public async Task<TyreOverview> TrashPlaceAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, place, _) = await EditablePlaceAsync(id, ct);
        VersionCheck.Require(expectedVersion, place.Version);
        place.MarkDeleted(clock.GetUtcNow(), (await access.RequirePrincipalAsync(ct)).Id);
        var save = new TyreSave();
        save.Change(place);
        await SaveAsync(save, ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>Brings a time on the car back: refused, with the reason, when another tyre is at its corner then (or the checks no longer pass).</summary>
    public async Task<TyreOverview> RestorePlaceAsync(Guid id, CancellationToken ct, int? expectedVersion = null)
    {
        var (vehicle, place, book) = await EditablePlaceAsync(id, ct, trashed: true);
        VersionCheck.Require(expectedVersion, place.Version);
        place.Restore((await access.RequirePrincipalAsync(ct)).Id);
        var (_, places) = Rules(vehicle, book);
        places.CheckRestored([place]);
        var save = new TyreSave();
        save.Change(place);
        await SaveAsync(save, ct);
        return Overview(vehicle.Id, await tyres.LoadAsync(vehicle.Id, ct));
    }

    /// <summary>The single tyre when it is in the trash (on its own, or with its set) and the user may restore it, else null.</summary>
    public async Task<Tyre?> TyreInTrashAsync(Guid id, CancellationToken ct) =>
        (await tyres.ListTyresAsync([id], ct)).FirstOrDefault() is { } tyre
        && (tyre.IsDeleted || await tyres.FindSetAsync(tyre.SetId, ct) is { IsDeleted: true })
        && await guard.ForVehicleAsync(tyre.VehicleId, ct) is { Level: >= AccessLevel.Edit }
            ? tyre
            : null;

    /// <summary>The time on the car when it is in the trash (on its own, or with its tyre or set) and the user may restore it, else null.</summary>
    public async Task<TyrePlace?> PlaceInTrashAsync(Guid id, CancellationToken ct)
    {
        if ((await tyres.ListPlacesAsync([id], ct)).FirstOrDefault() is not { } place) return null;
        if (await guard.ForVehicleAsync(place.VehicleId, ct) is not { Level: >= AccessLevel.Edit }) return null;
        if (place.IsDeleted) return place;
        return (await tyres.ListTyresAsync([place.TyreId], ct)).FirstOrDefault() is { } tyre
            && (tyre.IsDeleted || await tyres.FindSetAsync(tyre.SetId, ct) is { IsDeleted: true }) ? place : null;
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
        && (entry.IsDeleted || (entry.SetId is { } setId && await tyres.FindSetAsync(setId, ct) is { IsDeleted: true })
            || (entry.TyreId is { } tyreId && (await tyres.ListTyresAsync([tyreId], ct)).FirstOrDefault() is { IsDeleted: true }))
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
        // The single tyres (#289): each live tyre's live places, as the rules count them (its tyre, installation and set live).
        var places = new TyrePlaces(Guid.Empty, vehicleId, new TyreHistory(Guid.Empty, vehicleId, book.Sets, book.Installations), book.Sets, book.SingleTyres, book.TyrePlaces);
        var livePlaces = places.LivePlaces.ToList();
        IReadOnlyList<TyreView> TyresOf(TyreSet set) =>
        [
            .. places.TyresOf(set.Id).OrderBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Id).Select(t =>
            {
                var own = livePlaces.Where(p => p.TyreId == t.Id).OrderByDescending(p => p.From).ToList();
                return new TyreView(t, TyreCalculator.TyreFigures(book.Readings, t, own),
                    [.. own.Select(p => new TyrePlaceView(p, TyreCalculator.Distance(book.Readings, p.From, p.Until)))], own.Any(p => p.Until is null), own.FirstOrDefault()?.Corner);
            }),
        ];
        var liveSets = book.Sets.Where(s => !s.IsDeleted).Select(s => s.Id).ToHashSet();
        var entries = book.Entries.Where(e => IsLiveLine(book, e) && (e.SetId is null || liveSets.Contains(e.SetId.Value))) // a trashed tyre's lines hide through it (#289)
            .OrderByDescending(e => e.Day).ThenBy(e => e.IsSwapLine ? 0 : 1).ThenBy(e => e.Id).ToList();
        // What they cost (#288 step 4): the shown lines, each with its expense's amount while that is not in the trash.
        var costs = TyreCostCalculator.Costs(entries.Select(e =>
            book.ExpenseOf(e) is { IsDeleted: false } x ? new TyreLineCost(e.SetId, e.Kind, x.Amount, x.Currency, x.Id, e.TyreId) : new TyreLineCost(e.SetId, e.Kind, null, null, null, e.TyreId)));
        var sets = book.Sets.Where(s => !s.IsDeleted).OrderBy(s => s.IsRetired).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Id)
            .Select(set =>
            {
                var own = live.Where(s => s.SetId == set.Id).OrderByDescending(s => s.From).ToList();
                return new TyreSetView(set, TyreCalculator.SetFigures(book.Readings, set, own),
                    [.. own.Select(s => new TyreInstallationView(s, TyreCalculator.Distance(book.Readings, s.From, s.Until)))], own.Any(s => s.IsOpen),
                    costs.SpentOn(set.Id), costs.RepairsOf(set.Id),
                    set.IsBrokenUp ? [.. TyresOf(set).Select(v => v with { Spent = costs.SpentOnTyre(v.Tyre.Id), Repairs = costs.RepairsOfTyre(v.Tyre.Id) })] : []);
            })
            .ToList();
        var category = book.Entries.Where(e => !e.IsDeleted && e.ExpenseId is not null).OrderByDescending(e => e.Day).ThenByDescending(e => e.UpdatedAt)
            .Select(book.ExpenseOf).FirstOrDefault(x => x is { IsDeleted: false })?.Category;
        return new TyreOverview(vehicleId, sets, entries, book.Readings, category, costs.All, costs.NoSet);
    }

    private static TyreHistory History(Vehicle vehicle, TyreBook book, TyreSet? extra = null) =>
        new(vehicle.OwnerId, vehicle.Id, extra is null ? book.Sets : [.. book.Sets, extra], book.Installations);

    /// <summary>The history and the single tyres' rules over it (#289): every change of the history goes through the places.</summary>
    private static (TyreHistory History, TyrePlaces Places) Rules(Vehicle vehicle, TyreBook book, TyreSet? extra = null)
    {
        var history = History(vehicle, book, extra);
        return (history, new TyrePlaces(vehicle.OwnerId, vehicle.Id, history, extra is null ? book.Sets : [.. book.Sets, extra], book.SingleTyres, book.TyrePlaces));
    }

    /// <summary>
    /// A change that creates an installation with this id came again: it was applied already (same creator and vehicle), else the id is taken
    /// (<c>sync.idTaken</c>), looked up beyond the vehicle too.
    /// </summary>
    private async Task<bool> RepeatedAsync(TyreBook book, Guid? id, Guid vehicleId, Guid by, CancellationToken ct)
    {
        if (id is not { } given) return false;
        if ((book.Installations.FirstOrDefault(s => s.Id == given) ?? await tyres.FindInstallationAsync(given, ct)) is not { } existing)
        {
            // The installation's line takes its id. Without one, a line of its own with it is what the same change typed where a join made
            // no installation (TypedAsync: a Note line, or an Other one for a fee, whatever kind it was given since); a swap's line with it
            // means the id is someone else's.
            if (await tyres.FindEntryAsync(given, ct) is not { } line) return false;
            ClientIds.Repeat(line, line is { IsSwapLine: false, InstallationId: null } && line.VehicleId == vehicleId && line.CreatedById == by, given);
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

    /// <summary>The same for the tyres of a break-up (#289): any of them there means the break-up was made (or the id is someone else's).</summary>
    private async Task<bool> RepeatedTyresAsync(IEnumerable<Guid?> ids, Guid vehicleId, Guid by, CancellationToken ct)
    {
        var given = ids.OfType<Guid>().ToList();
        if (given.Count == 0 || (await tyres.ListTyresAsync(given, ct)).FirstOrDefault() is not { } existing) return false;
        ClientIds.Repeat(existing, existing.VehicleId == vehicleId && existing.CreatedById == by, existing.Id);
        return true;
    }

    /// <summary>The same for a time on the car typed in a tyre's history.</summary>
    private async Task<bool> RepeatedPlaceAsync(Guid? id, Guid vehicleId, Guid by, CancellationToken ct)
    {
        if (id is not { } given || (await tyres.ListPlacesAsync([given], ct)).FirstOrDefault() is not { } existing) return false;
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

    /// <summary>A change of the history with its places (#289): the places are saved, the rest as below.</summary>
    private void Follow(TyreSave save, TyreBook book, TyreHistory history, (TyreChanges History, TyrePlaceChanges Places) changes, Guid by)
    {
        save.Take(changes.Places);
        Follow(save, book, history, changes.History, by);
    }

    /// <summary>The lines a change of the history makes, moves or trashes (<see cref="TyreLog.Follow"/>), with the installations.</summary>
    private void Follow(TyreSave save, TyreBook book, TyreHistory history, TyreChanges changes, Guid by)
    {
        save.Take(changes);
        save.Take(TyreLog.Follow(changes, history, [.. book.Entries, .. save.AddedEntries], by, clock.GetUtcNow()));
        // A swap the history trashes (a replace, a join) takes its expense along, as the remove dialog's ticked box (the owner's choice, #288).
        foreach (var installation in changes.Trashed)
            if (save.AddedEntries.Concat(book.Entries).FirstOrDefault(e => e.Id == installation.Id) is { IsDeleted: true } line
                && ExpenseOf(save, book, line) is { IsDeleted: false } expense)
            {
                // A join's kept line took the reading of the day: the expense lets go of it first, or its trashing would take the live note's reading along.
                expense.FollowLine(line.Day, line.OdometerReading, by: by);
                expense.MarkDeleted(clock.GetUtcNow(), by);
                line.MarkExpenseTrashedWithEntry(true);
                save.Change(expense);
            }
    }

    private static Expense? ExpenseOf(TyreSave save, TyreBook book, TyreEntry line) =>
        line.ExpenseId is { } id ? save.AddedExpenses.FirstOrDefault(e => e.Id == id) ?? book.ExpenseOf(line) : null;

    /// <summary>
    /// The amount of a line (#288): none given leaves it; an empty one moves its expense to the trash; one typed makes the expense (an
    /// ordinary one, with the line's day and very reading), or changes it, bringing it back from the trash when the amount was emptied before.
    /// </summary>
    private async Task ApplyFeeAsync(Vehicle vehicle, TyreBook book, TyreSave save, TyreEntry line, TyreFee? fee, Guid by, CancellationToken ct)
    {
        if (fee is null) return;
        var expense = ExpenseOf(save, book, line);
        if (fee.Amount is not { } amount)
        {
            if (expense is not { IsDeleted: false }) return;
            expense.MarkDeleted(clock.GetUtcNow(), by);
            line.MarkExpenseTrashedWithEntry(false);
            save.Change(expense);
            save.Change(line);
            return;
        }
        if (!line.TakesAmount) throw new DomainException("tyres.noteTakesNoAmount", "A note takes no amount; choose what was done.");
        if (expense is null)
        {
            if (fee.ExpenseId is { } given && await expenses.FindIncludingDeletedAsync(given, ct) is { } taken)
                ClientIds.Repeat(taken, false, given); // someone else's id: the line's own would have been found as its expense
            var cost = Cost.Create(vehicle.OwnerId, vehicle.Id, line.Day, amount, fee.Currency);
            expense = Expense.Create(vehicle.OwnerId, by, vehicle.Id, line.Day, fee.Title ?? "", fee.Category, cost, line.OdometerReading, id: fee.ExpenseId);
            line.LinkExpense(expense.Id);
            save.AddedExpenses.Add(expense);
            save.Change(line);
            return;
        }
        if (expense.IsDeleted) expense.Restore(by); // the amount was emptied before, or the expense trashed on its own: typed again, it is back
        var waitedForAmount = expense.Cost is null; // left for its photo's amount (the Expenses tab): the one typed here makes its cost
        expense.FollowLine(line.Day, line.OdometerReading, amount, fee.Currency, fee.Category, setCategory: true, by);
        if (waitedForAmount && expense.Cost is { } made) save.ExpenseChanges.Add(new LinkedChanges(null, null, made, null));
        line.MarkExpenseTrashedWithEntry(false);
        save.Change(expense);
        save.Change(line);
    }

    /// <summary>Before a save: every linked expense of a line the change touched follows its line's day and reading, which is live while a live holder has it.</summary>
    private void Linked(TyreSave save)
    {
        // A reading made and let go of in one change (#289: a parting line's own copy, its odometer then emptied) is never written.
        foreach (var both in save.AddedReadings.Intersect(save.RemovedReadings).ToList())
        {
            save.AddedReadings.Remove(both);
            save.RemovedReadings.Remove(both);
        }
        if (save.Book is { } book)
            foreach (var line in save.AddedEntries.Concat(save.ChangedEntries).Where(l => l.ExpenseId is not null).ToList())
            {
                if (ExpenseOf(save, book, line) is not { } expense) continue;
                // A line in the trash pulls nothing: its expense goes on as it is (alone, or in the trash with it); a live line's is on its day, with its reading.
                if (!line.IsDeleted && expense.FollowLine(line.Day, line.OdometerReading, by: save.By)) save.Change(expense);
            }
        SettleReadings(save);
    }

    /// <summary>
    /// Every reading the change touched is live while a live line or expense holds it (#288; #289: the lines made together and their expense
    /// hold one row), else in the trash with the last of them.
    /// </summary>
    private void SettleReadings(TyreSave save)
    {
        var lines = (save.Book?.Entries ?? []).Concat(save.AddedEntries).Concat(save.ChangedEntries).Distinct().ToList();
        var expenses = (save.Book?.LinkedExpenses ?? []).Concat(save.AddedExpenses).Concat(save.ChangedExpenses).Distinct().ToList();
        var readings = lines.Where(l => save.AddedEntries.Contains(l) || save.ChangedEntries.Contains(l)).Select(l => l.OdometerReading)
            .Concat(save.AddedExpenses.Concat(save.ChangedExpenses).Select(x => x.OdometerReading)).OfType<OdometerReading>().Distinct().ToList();
        foreach (var reading in readings)
            TyreExpenses.Settle(reading,
                lines.Any(l => l.OdometerReadingId == reading.Id && !l.IsDeleted) || expenses.Any(x => x.OdometerReadingId == reading.Id && !x.IsDeleted), clock.GetUtcNow());
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
    /// <param name="fee">What the change cost (#288): on the same line; a line made for it is an Other line, since a note takes no amount.</param>
    private async Task TypedAsync(
        Vehicle vehicle, TyreBook book, TyreSave save, TyreHistory history, Guid setId, DateOnly day, long? value, string? note, Guid by, CancellationToken ct,
        Guid? id = null, TyreFee? fee = null)
    {
        if (string.IsNullOrWhiteSpace(note)) note = null; // only spaces is no note: it leaves the line's, as one left out
        if (value is null && note is null && fee?.Amount is null) return;
        var line = history.Live.FirstOrDefault(s => s.From == day) is { } starting
            ? save.AddedEntries.FirstOrDefault(e => e.Id == starting.Id) ?? LineOf(book, starting.Id)
            : null;
        if (value is { } v) await odometer.ValidateAsync(vehicle.Id, day, v, exceptReadingId: line?.OdometerReadingId, ct);
        if (line is null)
        {
            var created = TyreEntry.Create(vehicle.OwnerId, by, vehicle.Id, fee?.Amount is null ? TyreEntryKind.Note : TyreEntryKind.Other, setId, day, value, note,
                id is { } given && save.AddedEntries.All(e => e.Id != given) && book.Entries.All(e => e.Id != given) ? given : null);
            save.AddedEntries.Add(created);
            if (created.OdometerReading is { } reading) save.AddedReadings.Add(reading);
            await ApplyFeeAsync(vehicle, book, save, created, fee, by, ct);
            return;
        }
        var (added, removed, changed) = line.Update(line.Kind, line.SetId, line.Day, value ?? line.Odometer, note ?? line.Note, by);
        if (added is not null) save.AddedReadings.Add(added);
        if (removed is not null) save.RemovedReadings.Add(removed);
        if (changed) save.Change(line);
        await ApplyFeeAsync(vehicle, book, save, line, fee, by, ct); // an installation's line: a swap or a change of positions, which takes an amount
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

    /// <summary>
    /// A line goes to the trash, a swap's with its installation. Its expense goes along when <paramref name="withExpense"/> (and comes back with
    /// it); else it stays, holding the reading, which then stays among the car's readings.
    /// </summary>
    private async Task TrashSwapAsync(TyreBook book, TyreInstallation? installation, TyreEntry? line, bool withExpense, CancellationToken ct)
    {
        var (now, by) = (clock.GetUtcNow(), (await access.RequirePrincipalAsync(ct)).Id);
        var save = new TyreSave { Book = book, By = by };
        if (installation is not null)
        {
            installation.MarkDeleted(now, by);
            save.Change(installation);
        }
        if (line is { IsDeleted: false })
        {
            line.MarkDeleted(now, by);
            save.Change(line);
            // Its expense goes along only with the last live line holding it (#289, the owner's decision 33): the others keep it.
            if (withExpense && book.ExpenseOf(line) is { IsDeleted: false } expense && Holders(save, book, expense.Id).Count == 0)
            {
                expense.MarkDeleted(now, by);
                line.MarkExpenseTrashedWithEntry(true);
                save.Change(expense);
            }
        }
        await SaveAsync(save, ct);
    }

    /// <summary>
    /// A line comes back, a swap's with its installation (or not at all: the history's reason), and its expense with it when the two went to
    /// the trash together.
    /// </summary>
    private async Task RestoreSwapAsync(Vehicle vehicle, TyreBook book, TyreInstallation? installation, TyreEntry? line, CancellationToken ct)
    {
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        var save = new TyreSave { Book = book, By = by };
        if (installation is not null)
        {
            installation.Restore(by);
            var (history, places) = Rules(vehicle, book);
            history.CheckRestored([installation]);
            places.CheckRestored([.. places.LivePlaces.Where(p => p.InstallationId == installation.Id)]); // its tyres' places come back with it (#289)
            save.Change(installation);
        }
        if (line is { IsDeleted: true })
        {
            await RequireReadingFitsAsync(vehicle, line, ct);
            line.Restore(by);
            save.Change(line);
            if (book.ExpenseOf(line) is { } expense)
            {
                if (expense.Date != line.Day || expense.OdometerReadingId != line.OdometerReadingId)
                    line.UnlinkExpense(); // edited on its own while the line was in the trash: the two part (the owner's choice)
                else if (line.ExpenseTrashedWithEntry && expense.IsDeleted)
                {
                    expense.Restore(by);
                    save.Change(expense);
                }
            }
            line.MarkExpenseTrashedWithEntry(false);
        }
        await SaveAsync(save, ct);
    }

    /// <summary>The line an expense was made from (#288), the trash included; null for an ordinary expense.</summary>
    public async Task<TyreEntry?> LineOfExpenseAsync(Guid expenseId, CancellationToken ct) => (await tyres.ListEntriesByExpenseAsync([expenseId], ct)).FirstOrDefault();

    /// <summary>
    /// A person's edit of an expense a tyre log line made (#288), from the Expenses tab: its date and odometer are its line's, so they change
    /// both (a swap's line moving its swap by the history's rules). While the line is in the trash the expense is edited on its own: a new
    /// day or odometer is then its own, and restoring the line parts the two (the owner's choice).
    /// </summary>
    /// <param name="readingPhotos">One of its photos is still being read (as <see cref="ExpenseService.UpdateAsync"/> finds): it may wait for it.</param>
    public async Task<Expense> UpdateExpenseAsync(TyreEntry found, Guid expenseId, ExpenseInput input, CancellationToken ct, int? expectedVersion = null, bool readingPhotos = false)
    {
        var vehicle = await EditableVehicleAsync(found.VehicleId, ct);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        // Every live line holding it follows (#289, the owner's decision 34); with none live, it is edited alone (#288).
        var save = new TyreSave { Book = book, By = by };
        var live = Holders(save, book, expenseId);
        var line = live.FirstOrDefault() ?? book.Entries.First(e => e.Id == found.Id);
        var expense = book.ExpenseOf(line) ?? throw new NotFoundException("expense.notFound", $"Expense {expenseId} does not exist.", new { Id = expenseId });
        VersionCheck.Require(expectedVersion, expense.Version);
        RequireNotFuture(input.Date);
        var (currency, discount) = (input.Currency ?? expense.Currency, input.Discount ?? expense.Discount);
        var sameDayAndReading = input.Date == line.Day && input.Odometer == line.Odometer && expense.OdometerReadingId == line.OdometerReadingId;
        if (live.Count == 0 && sameDayAndReading)
        {
            // Its amount, title, category or note: the day and the reading are still the line's, held for it.
            save.ExpenseChanges.Add(expense.UpdateWithLineReading(input.Date, input.Title, input.Category, input.Amount, currency, line.OdometerReading, input.Note, by, discount, readingPhotos));
            save.Change(expense);
        }
        else if (live.Count == 0)
        {
            if (line.OdometerReadingId is { } shared && expense.OdometerReadingId == shared) expense.LeaveSharedReading();
            if (input.Odometer is { } own) await odometer.ValidateAsync(vehicle.Id, input.Date, own, exceptReadingId: expense.OdometerReadingId, ct);
            save.ExpenseChanges.Add(expense.Update(input.Date, input.Title, input.Category, input.Amount, currency, input.Odometer, input.Note, readingPhotos, by, discount));
            save.Change(expense);
            save.Change(line); // its reading leaves the car's readings once nothing live holds it (settled on save)
        }
        else
        {
            if (line.IsSwapLine && input.Date != line.Day)
            {
                var (history, places) = Rules(vehicle, book);
                Follow(save, book, history, places.MoveSwap(book.Installations.First(s => s.Id == line.InstallationId), input.Date, by), by);
            }
            if (input.Odometer is { } value) await odometer.ValidateAsync(vehicle.Id, input.Date, value, exceptReadingId: line.OdometerReadingId, ct);
            KeepReadingsOfOthers(save, book, live);
            OdometerReading? made = null;
            foreach (var holder in live)
            {
                if (made is not null) holder.HoldReading(made); // the lines made together keep holding one row
                var (added, removed, _) = holder.Update(holder.Kind, holder.SetId, input.Date, input.Odometer, holder.Note, by);
                if (added is not null)
                {
                    save.AddedReadings.Add(added);
                    made ??= added;
                }
                if (removed is not null) save.RemovedReadings.Add(removed);
                save.Change(holder);
            }
            save.ExpenseChanges.Add(expense.UpdateWithLineReading(input.Date, input.Title, input.Category, input.Amount, currency, line.OdometerReading, input.Note, by, discount, readingPhotos));
            save.Change(expense);
        }
        await SaveAsync(save, ct);
        logger.LogDebug("Expense {ExpenseId} of tyre log line {EntryId} updated", expense.Id, line.Id);
        return await expenses.FindAsync(expense.Id, ct) ?? expense;
    }

    /// <summary>An expense a line made, moved to the trash on its own (the Expenses tab): the line stays, with its reading.</summary>
    public async Task<Expense> TrashExpenseAsync(TyreEntry found, Guid expenseId, CancellationToken ct, int? expectedVersion = null) =>
        await ChangeExpenseAsync(found, expenseId, expectedVersion, (expense, by) => expense.MarkDeleted(clock.GetUtcNow(), by), ct);

    /// <summary>An expense a line made, restored on its own: only it comes back (the owner's choice), the line stays where it is.</summary>
    public async Task<Expense> RestoreExpenseAsync(TyreEntry found, Guid expenseId, CancellationToken ct, int? expectedVersion = null) =>
        await ChangeExpenseAsync(found, expenseId, expectedVersion, (expense, by) => expense.Restore(by), ct);

    private async Task<Expense> ChangeExpenseAsync(TyreEntry found, Guid expenseId, int? expectedVersion, Action<Expense, Guid> change, CancellationToken ct)
    {
        var vehicle = await EditableVehicleAsync(found.VehicleId, ct);
        var by = (await access.RequirePrincipalAsync(ct)).Id;
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        var line = book.Entries.First(e => e.Id == found.Id);
        var expense = book.ExpenseOf(line) ?? throw new NotFoundException("expense.notFound", $"Expense {expenseId} does not exist.", new { Id = expenseId });
        VersionCheck.Require(expectedVersion, expense.Version);
        change(expense, by);
        var save = new TyreSave { Book = book, By = by };
        save.Change(expense);
        // Every line of it (#289): their reading, and the bookkeeping of the trash (settled on save).
        foreach (var holder in book.Entries.Where(e => e.ExpenseId == expense.Id))
        {
            holder.MarkExpenseTrashedWithEntry(false);
            save.Change(holder);
        }
        await SaveAsync(save, ct);
        return await expenses.FindIncludingDeletedAsync(expense.Id, ct) ?? expense;
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
        Linked(save);
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
        Linked(save);
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

    /// <summary>A single tyre the user may change: one hidden through its set in the trash does not exist (restore the set).</summary>
    private async Task<(Vehicle, Tyre, TyreBook)> EditableTyreAsync(Guid id, CancellationToken ct, bool trashed = false)
    {
        var tyre = (await tyres.ListTyresAsync([id], ct)).FirstOrDefault();
        NotFoundException Missing() => new("tyres.tyreNotFound", $"Tyre {id} does not exist.", new { Id = id });
        var vehicle = LogAccessGuard.RequireEdit(await guard.ForVehicleAsync(tyre?.VehicleId, ct), Missing).Vehicle;
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        var loaded = book.SingleTyres.First(t => t.Id == id);
        if (book.Sets.First(s => s.Id == loaded.SetId).IsDeleted) throw Missing();
        if (loaded.IsDeleted != trashed) throw trashed ? new DomainException("tyres.tyreNotTrashed", "This tyre is not in the trash.") : Missing();
        return (vehicle, loaded, book);
    }

    /// <summary>A time on the car the user may change: one hidden through its tyre, installation or set does not exist (restore that first).</summary>
    private async Task<(Vehicle, TyrePlace, TyreBook)> EditablePlaceAsync(Guid id, CancellationToken ct, bool trashed = false)
    {
        var place = (await tyres.ListPlacesAsync([id], ct)).FirstOrDefault();
        NotFoundException Missing() => new("tyres.placeNotFound", $"Time on the car {id} does not exist.", new { Id = id });
        var vehicle = LogAccessGuard.RequireEdit(await guard.ForVehicleAsync(place?.VehicleId, ct), Missing).Vehicle;
        var book = await tyres.LoadAsync(vehicle.Id, ct);
        var loaded = book.TyrePlaces.First(p => p.Id == id);
        var tyre = book.SingleTyres.First(t => t.Id == loaded.TyreId);
        if (tyre.IsDeleted || book.Sets.First(s => s.Id == tyre.SetId).IsDeleted || book.Installations.FirstOrDefault(s => s.Id == loaded.InstallationId) is not { IsDeleted: false })
            throw Missing();
        if (loaded.IsDeleted != trashed) throw trashed ? new DomainException("tyres.placeNotTrashed", "This time on the car is not in the trash.") : Missing();
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
