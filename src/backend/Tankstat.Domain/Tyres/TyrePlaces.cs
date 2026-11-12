namespace Tankstat.Domain.Tyres;

/// <summary>The tyres and places a change made or changed (the entities are changed in place; the caller saves them).</summary>
public sealed class TyrePlaceChanges
{
    public List<Tyre> CreatedTyres { get; } = [];
    public List<TyrePlace> Created { get; } = [];
    public List<TyrePlace> Changed { get; } = [];

    public void Change(TyrePlace place)
    {
        if (!Created.Contains(place) && !Changed.Contains(place)) Changed.Add(place);
    }
}

/// <summary>A tyre of a break-up, as the user gave it: a label and its corner (null: not sure).</summary>
public sealed record NewTyre(Guid? Id, string Label, TyreCorner? Corner);

/// <summary>
/// The rules of single tyres (#289) over a vehicle's tyre history: breaking a set up, the places that follow its installations, and the six
/// checks every change must pass. A place is live while it, its tyre, its installation and its set are. Pure: it changes the entities it was
/// given and says which; the device ports the same rules (<c>src/tyres/tyrePlaces.ts</c>, pinned by the contract in
/// <c>tests/backend/contracts/tyre-places</c>).
/// </summary>
public sealed class TyrePlaces
{
    private readonly TyreHistory _history;
    private readonly List<Tyre> _tyres;
    private readonly List<TyrePlace> _places;
    private readonly IReadOnlyDictionary<Guid, TyreSet> _allSets;
    private readonly Guid _ownerId;
    private readonly Guid _vehicleId;

    /// <param name="history">The vehicle's history; its live installations are read as they are when a rule runs.</param>
    /// <param name="sets">The vehicle's sets, the trash included (a tyre of a set in the trash is in the trash).</param>
    /// <param name="tyres">The vehicle's tyres, the trash included.</param>
    /// <param name="places">Their places, the trash included.</param>
    public TyrePlaces(Guid ownerId, Guid vehicleId, TyreHistory history, IEnumerable<TyreSet> sets, IEnumerable<Tyre> tyres, IEnumerable<TyrePlace> places)
    {
        (_ownerId, _vehicleId, _history) = (ownerId, vehicleId, history);
        _allSets = sets.ToDictionary(s => s.Id);
        _tyres = [.. tyres];
        _places = [.. places];
    }

    /// <summary>The days of every live installation before a change, so its places follow its ends (<see cref="Follow"/>).</summary>
    public static IReadOnlyDictionary<Guid, (DateOnly From, DateOnly? Until)> Ends(TyreHistory history) =>
        history.Live.ToDictionary(s => s.Id, s => (s.From, s.Until));

    /// <summary>A tyre not in the trash, its set not either.</summary>
    public bool IsLive(Tyre tyre) => !tyre.IsDeleted && _allSets.TryGetValue(tyre.SetId, out var set) && !set.IsDeleted;

    /// <summary>The set's tyres that are not in the trash, retired ones included.</summary>
    public IEnumerable<Tyre> TyresOf(Guid setId) => _tyres.Where(t => t.SetId == setId && IsLive(t));

    public IReadOnlyList<Tyre> Tyres => _tyres;

    /// <summary>A place counts: it, its tyre and its installation (so its set) are live.</summary>
    public bool IsLive(TyrePlace place) =>
        !place.IsDeleted && _tyres.FirstOrDefault(t => t.Id == place.TyreId) is { } tyre && IsLive(tyre) && _history.Live.Any(s => s.Id == place.InstallationId);

    public IEnumerable<TyrePlace> LivePlaces => _places.Where(IsLive);

    /// <summary>
    /// Breaks <paramref name="set"/> up into four tyres on <paramref name="day"/>. Each starts with the set's history: a place per live
    /// installation of the set with its days, and the set's rough figure. On the installation the corners were given for (the one on the car
    /// now, else the last) the place has the tyre's corner; on the others <see cref="TyreCorner.Front"/> or <see cref="TyreCorner.Rear"/>
    /// where that installation recorded the pairs and the tyre's corner tells its pair, else none. The places' ids are worked out
    /// (<see cref="TyreIds.PlaceOf"/>).
    /// </summary>
    public TyrePlaceChanges BreakUp(TyreSet set, IReadOnlyList<NewTyre> given, DateOnly day, Guid by)
    {
        if (set.IsBrokenUp) throw new DomainException("tyres.alreadyBrokenUp", "This tyre set is tracked tyre by tyre already.", new { Set = set.Name });
        if (given.Count != 4) throw new DomainException("tyres.fourTyres", "A car's set is four tyres: give each a label.");
        var labels = given.Select(t => Tyre.CheckedLabel(t.Label)).ToList();
        if (labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != labels.Count)
            throw new DomainException("tyres.labelTwice", "Each tyre needs its own label.");
        foreach (var label in labels) RequireLabelFree(label, except: null);
        if (given.Where(t => t.Corner is { } c && c.IsExact()).GroupBy(t => t.Corner).Any(g => g.Count() > 1))
            throw new DomainException("tyres.cornerTwice", "Two tyres are at the same corner. Give each its own corner, or Not sure.");
        if (given.Any(t => t.Corner is { } c && !c.IsExact()))
            throw new DomainException("tyres.unknownCorner", "Give each tyre one of the four corners, or Not sure.");
        set.BreakUp(day, by);
        var changes = new TyrePlaceChanges();
        // A retired set's tyres are retired with it, so unretiring the set brings them back.
        void RetiredWithSet(Tyre tyre)
        {
            if (set.RetiredOn is { } retired) tyre.Retire(retired, TyreRetiredWhy.SetRetired, by);
        }
        var installations = _history.Live.Where(s => s.SetId == set.Id).OrderBy(s => s.From).ToList();
        var known = installations.FirstOrDefault(s => s.Until is null) ?? installations.LastOrDefault();
        for (var i = 0; i < given.Count; i++)
        {
            var tyre = Tyre.Create(_ownerId, by, _vehicleId, set.Id, labels[i], null, set.Since, set.MadeWeek, set.MadeYear, set.RoughDistance, set.RoughUntil, given[i].Id);
            RetiredWithSet(tyre);
            _tyres.Add(tyre);
            changes.CreatedTyres.Add(tyre);
            // The corner tells the pair: at the front where pair 1 was at the front, it is pair 1; at the rear there, pair 2 (and the other way round).
            bool? pairOne = given[i].Corner is { } c && known?.PairOneFront is { } p ? c.IsFront() == p : null;
            foreach (var installation in installations)
            {
                var corner = installation == known ? given[i].Corner
                    : pairOne is { } one && installation.PairOneFront is { } front ? (one == front ? TyreCorner.Front : TyreCorner.Rear) : (TyreCorner?)null;
                var place = TyrePlace.Create(_ownerId, by, _vehicleId, tyre.Id, installation.Id, installation.From, installation.Until, corner, TyreIds.PlaceOf(tyre.Id, installation.Id));
                _places.Add(place);
                changes.Created.Add(place);
            }
        }
        Check(changes.Created);
        return changes;
    }

    /// <summary>
    /// The corners a swap or a change of positions of <paramref name="set"/> on <paramref name="day"/> gives each live tyre: those given (a tyre
    /// left out has none), or, with none given (<c>null</c>: a swap from papers, an installation typed in), the corner each had the last time
    /// before that day (else none).
    /// </summary>
    public IReadOnlyDictionary<Guid, TyreCorner?> CornersFor(Guid setId, DateOnly day, IReadOnlyDictionary<Guid, TyreCorner?>? given) =>
        OnTheCarFrom(setId, day).ToDictionary(t => t.Id, t => given is null ? CornerBefore(t.Id, day) : given.GetValueOrDefault(t.Id));

    /// <summary>
    /// Whether every live tyre of the installation's set has the corners asked for there: a swap joins an installation of the set only then
    /// (the owner's decision 47, as a whole set's pairs), and a change of positions with the same corners changes nothing.
    /// </summary>
    /// <param name="day">The day of the change: an installation starting later is compared on its own first day.</param>
    public bool SameCorners(TyreInstallation installation, DateOnly day, IReadOnlyDictionary<Guid, TyreCorner?> corners)
    {
        var anchor = installation.From > day ? installation.From : day;
        foreach (var (tyreId, corner) in corners)
        {
            var near = LivePlaces.Where(p => p.TyreId == tyreId && p.InstallationId == installation.Id)
                .FirstOrDefault(p => p.From <= anchor && (p.Until is null || p.Until > anchor))
                ?? LivePlaces.FirstOrDefault(p => p.TyreId == tyreId && p.InstallationId == installation.Id && p.Until == anchor);
            if (near?.Corner != corner) return false;
        }
        return true;
    }

    /// <summary>
    /// The places follow a change of the history, for a broken-up set's installations: each one created gets a place per live tyre of its set
    /// (the corners of <paramref name="corners"/>, else as before; a tyre retired before it has none, one retired during it comes off then),
    /// and the places at a changed installation's ends follow its new first or last day (the owner's decision 35). One that would fall outside
    /// it is refused by the checks; a trashed installation hides its places.
    /// </summary>
    /// <param name="before">The installations' days before the change (<see cref="Ends"/>).</param>
    public TyrePlaceChanges Follow(
        TyreChanges changes, IReadOnlyDictionary<Guid, (DateOnly From, DateOnly? Until)> before, Guid by,
        IReadOnlyDictionary<Guid, TyreCorner?>? corners = null, TyrePlaceChanges? into = null)
    {
        var result = into ?? new TyrePlaceChanges();
        foreach (var installation in changes.Changed)
        {
            if (!before.TryGetValue(installation.Id, out var was)) continue;
            foreach (var place in _places.Where(p => p.InstallationId == installation.Id && !p.IsDeleted).ToList())
            {
                var from = place.From == was.From ? installation.From : place.From;
                var until = place.Until == was.Until ? installation.Until : place.Until;
                if (from == place.From && until == place.Until) continue;
                if (until is { } u && u <= from) // the installation lost the place's days altogether
                    throw Outside(place, installation);
                place.Update(place.InstallationId, from, until, place.Corner, by);
                result.Change(place);
            }
        }
        foreach (var installation in changes.Created) PlaceTyres(installation, corners, by, result);
        Check([.. result.Created, .. result.Changed]);
        return result;
    }

    /// <summary>
    /// The corners change in place on the first day of the set's installation that starts on <paramref name="day"/> (a change of positions
    /// or a swap of the set onto itself on that day, as a whole set's pairs do): no new places.
    /// </summary>
    public TyrePlaceChanges SetCorners(TyreInstallation installation, IReadOnlyDictionary<Guid, TyreCorner?> corners, Guid by)
    {
        var result = new TyrePlaceChanges();
        foreach (var place in LivePlaces.Where(p => p.InstallationId == installation.Id && p.From == installation.From).ToList())
            if (corners.TryGetValue(place.TyreId, out var corner) && place.Update(place.InstallationId, place.From, place.Until, corner, by))
                result.Change(place);
        Check(result.Changed);
        return result;
    }

    /// <summary>
    /// A swap (<see cref="TyreHistory.Swap"/>) with its places: onto a broken-up set each tyre goes to the corner given (null: as before), the
    /// corners deciding a join and a retry; the set onto itself on its installation's first day changes the corners in place.
    /// </summary>
    public (TyreChanges History, TyrePlaceChanges Places) Swap(
        TyreSet set, DateOnly day, bool? pairOneFront, bool replace, IReadOnlyDictionary<Guid, TyreCorner?>? corners, Guid by, DateTimeOffset now,
        Guid? id = null, bool pairsGiven = true, bool repeatable = false)
    {
        var before = Ends(_history);
        var given = set.IsBrokenUp ? CornersFor(set.Id, day, corners) : null;
        var changes = _history.Swap(set, day, given is null ? pairOneFront : null, replace, by, now, id, pairsGiven, repeatable,
            given is null ? null : s => SameCorners(s, day, given));
        var placed = Follow(changes, before, by, given);
        if (given is not null && changes.Created.Count == 0 && _history.Live.FirstOrDefault(s => s.SetId == set.Id && s.From == day) is { } starting)
            foreach (var place in SetCorners(starting, given, by).Changed) placed.Change(place);
        return (changes, placed);
    }

    /// <summary>
    /// A change of positions (<see cref="TyreHistory.ChangePositions"/>) with its places: a broken-up set's installation splits on that day,
    /// each tyre at its new corner, or on the installation's first day its corners change in place.
    /// </summary>
    public (TyreChanges History, TyrePlaceChanges Places) ChangePositions(
        TyreSet set, DateOnly day, bool? pairOneFront, IReadOnlyDictionary<Guid, TyreCorner?>? corners, Guid by, Guid? id = null, bool repeatable = false)
    {
        if (!set.IsBrokenUp) return (_history.ChangePositions(set, day, pairOneFront, by, id, repeatable), new TyrePlaceChanges());
        var before = Ends(_history);
        var given = CornersFor(set.Id, day, corners);
        var changes = _history.ChangePositions(set, day, null, by, id, repeatable, s => SameCorners(s, day, given));
        if (changes.Any) return (changes, Follow(changes, before, by, given));
        var starting = _history.Live.FirstOrDefault(s => s.SetId == set.Id && s.From == day);
        return (changes, starting is null ? new TyrePlaceChanges() : SetCorners(starting, given, by));
    }

    /// <summary>A swap moved by its line's day (<see cref="TyreHistory.MoveSwap"/>), the places at the installations' ends with it.</summary>
    public (TyreChanges History, TyrePlaceChanges Places) MoveSwap(TyreInstallation installation, DateOnly day, Guid by)
    {
        var before = Ends(_history);
        var changes = _history.MoveSwap(installation, day, by);
        return (changes, Follow(changes, before, by));
    }

    /// <summary>An installation edited in History (<see cref="TyreHistory.Edit"/>), the places at its ends with it (the owner's decision 35).</summary>
    public (bool Changed, TyrePlaceChanges Places) EditInstallation(TyreInstallation installation, DateOnly from, DateOnly? until, bool? pairOneFront, Guid by)
    {
        var before = Ends(_history);
        if (!_history.Edit(installation, from, until, pairOneFront, by)) return (false, new TyrePlaceChanges());
        var changes = new TyreChanges();
        changes.Changed.Add(installation);
        return (true, Follow(changes, before, by));
    }

    /// <summary>An installation typed in History (<see cref="TyreHistory.Add"/>): a broken-up set's tyres each get a place, at the corner they had before (decision 48).</summary>
    public (TyreInstallation Installation, TyrePlaceChanges Places) AddInstallation(TyreSet set, DateOnly from, DateOnly? until, bool? pairOneFront, Guid by, Guid? id = null)
    {
        var before = Ends(_history);
        var created = _history.Add(set, from, until, set.IsBrokenUp ? null : pairOneFront, by, id);
        var changes = new TyreChanges();
        changes.Created.Add(created);
        return (created, Follow(changes, before, by));
    }

    /// <summary>A place typed in a tyre's history: it lies in the set's live installation that holds its days, else it is refused.</summary>
    public TyrePlace Add(Tyre tyre, DateOnly from, DateOnly? until, TyreCorner? corner, Guid by, Guid? id = null)
    {
        var installation = InstallationHolding(tyre, from, until);
        var place = TyrePlace.Create(_ownerId, by, _vehicleId, tyre.Id, installation.Id, from, until, corner, id);
        _places.Add(place);
        Check([place]);
        return place;
    }

    /// <summary>Changes a live place; false when nothing changed.</summary>
    public bool Edit(TyrePlace place, DateOnly from, DateOnly? until, TyreCorner? corner, Guid by)
    {
        if (!IsLive(place)) throw new DomainException("tyres.placeTrashedCannotEdit", "A time on the car in the trash cannot be changed; restore it first.");
        var tyre = _tyres.First(t => t.Id == place.TyreId);
        var changed = place.Update(InstallationHolding(tyre, from, until).Id, from, until, corner, by);
        Check([place]);
        return changed;
    }

    /// <summary>A label no other tyre of the vehicle not in the trash has, without case (the owner's decision 27).</summary>
    public void RequireLabelFree(string label, Tyre? except)
    {
        if (_tyres.Any(t => t != except && IsLive(t) && string.Equals(t.Label, label, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("tyres.labelTaken", "Another tyre is already called that.", new { Label = label });
    }

    /// <summary>Brings back places (a tyre's, an installation's, a set's, or one alone): refused, with the reason, when they no longer fit.</summary>
    public void CheckRestored(IReadOnlyCollection<TyrePlace> restored) => Check(restored, restoring: true);

    /// <summary>
    /// The six checks over the live places (the owner's decision 32): a place inside its installation (<c>tyres.placeOutsideSet</c>), not after
    /// its tyre was retired (<c>tyres.tyreRetired</c>), a tyre on the car once at a time (<c>tyres.tyreOnTwice</c>), one tyre per exact corner
    /// (<c>tyres.cornerTaken</c>), at most two tyres at the front and two at the rear, a side not recorded counting toward its end
    /// (<c>tyres.tooManyFront</c> / <c>tooManyRear</c>), at most four on the car (<c>tyres.fifthTyre</c>). While restoring, an overlap is
    /// <c>tyres.tyreRestoreConflict</c>, naming the other tyre and its days.
    /// </summary>
    /// <param name="moving">The places the change adds, edits or restores: an overlap names the other one, already there.</param>
    public void Check(IReadOnlyCollection<TyrePlace>? moving = null, bool restoring = false)
    {
        // By first day, then label: a refusal names the same tyre on the server and on the device, whose ids differ in the contract.
        var live = LivePlaces.OrderBy(p => p.From).ThenBy(p => TyreOf(p).Label, StringComparer.Ordinal).ThenBy(p => p.Id).ToList();
        foreach (var place in live)
        {
            var installation = _history.Live.First(s => s.Id == place.InstallationId);
            if (place.From < installation.From || installation.Until is { } end && (place.Until is null || place.Until > end)) throw Outside(place, installation);
            var tyre = TyreOf(place);
            if (tyre.RetiredOn is { } retired && (place.Until is null || place.Until > retired))
                throw new DomainException("tyres.tyreRetired", "A retired tyre cannot be on the car then.", new { Label = tyre.Label, Date = Iso(retired) });
        }
        TyrePlace Other(TyrePlace a, TyrePlace b) => moving?.Contains(b) == true && moving?.Contains(a) != true ? a : b;
        DomainException Conflict(string key, string message, TyrePlace other, object args) => restoring
            ? new DomainException("tyres.tyreRestoreConflict", "It cannot be restored as it is: another tyre is on the car then.",
                new { Label = TyreOf(other).Label, From = Iso(other.From), Until = other.Until is { } u ? Iso(u) : null })
            : new DomainException(key, message, args);
        for (var i = 0; i < live.Count; i++)
            for (var j = i + 1; j < live.Count; j++)
            {
                var (a, b) = (live[i], live[j]);
                if (b.From >= (a.Until ?? DateOnly.MaxValue)) continue;
                var other = Other(a, b);
                var args = new { Label = TyreOf(other).Label, From = Iso(other.From), Until = other.Until is { } u ? Iso(u) : null };
                if (a.TyreId == b.TyreId) throw Conflict("tyres.tyreOnTwice", "This tyre is on the car then already.", other, args);
                if (a.Corner is { } ca && ca.IsExact() && ca == b.Corner) throw Conflict("tyres.cornerTaken", "Another tyre is at that corner then.", other, args);
            }
        // What is on the car on each day a place starts: the most at once.
        foreach (var start in live)
        {
            var on = live.Where(p => p.From <= start.From && (p.Until is null || p.Until > start.From)).ToList();
            var other = on.FirstOrDefault(p => moving?.Contains(p) != true) ?? start; // the first tyre there already, by label
            var args = new { Label = TyreOf(other).Label, From = Iso(start.From) };
            if (on.Count(p => p.Corner is { } c && c.IsFront()) > 2) throw Conflict("tyres.tooManyFront", "More than two tyres would be at the front then.", other, args);
            if (on.Count(p => p.Corner is { } c && c.IsRear()) > 2) throw Conflict("tyres.tooManyRear", "More than two tyres would be at the rear then.", other, args);
            if (on.Count > 4) throw Conflict("tyres.fifthTyre", "Four tyres are on the car then already.", other, args);
        }
    }

    private Tyre TyreOf(TyrePlace place) => _tyres.First(t => t.Id == place.TyreId);

    /// <summary>The set's live tyres that could be on the car from <paramref name="day"/>: not retired by then.</summary>
    private IEnumerable<Tyre> OnTheCarFrom(Guid setId, DateOnly day) => TyresOf(setId).Where(t => t.RetiredOn is not { } r || r > day);

    private TyreCorner? CornerBefore(Guid tyreId, DateOnly day) =>
        LivePlaces.Where(p => p.TyreId == tyreId && p.From < day).OrderByDescending(p => p.From).FirstOrDefault()?.Corner;

    private void PlaceTyres(TyreInstallation installation, IReadOnlyDictionary<Guid, TyreCorner?>? corners, Guid by, TyrePlaceChanges result)
    {
        if (!_allSets.TryGetValue(installation.SetId, out var set) || !set.IsBrokenUp) return;
        var given = corners ?? CornersFor(set.Id, installation.From, null);
        foreach (var tyre in OnTheCarFrom(set.Id, installation.From).ToList())
        {
            var until = tyre.RetiredOn is { } r && (installation.Until is null || installation.Until > r) ? r : installation.Until;
            var place = TyrePlace.Create(_ownerId, by, _vehicleId, tyre.Id, installation.Id, installation.From, until, given.GetValueOrDefault(tyre.Id),
                TyreIds.PlaceOf(tyre.Id, installation.Id));
            _places.Add(place);
            result.Created.Add(place);
        }
    }

    /// <summary>The set's live installation that holds the days; else the refusal (decision 31: the set's History adds it first).</summary>
    private TyreInstallation InstallationHolding(Tyre tyre, DateOnly from, DateOnly? until) =>
        _history.Live.FirstOrDefault(s => s.SetId == tyre.SetId && s.From <= from && (s.Until is null || until is { } u && u <= s.Until))
        ?? throw OutsideDays(tyre, from, until);

    private DomainException Outside(TyrePlace place, TyreInstallation installation) => OutsideDays(TyreOf(place), place.From, place.Until);

    private DomainException OutsideDays(Tyre tyre, DateOnly from, DateOnly? until)
    {
        // Names the set that was on the car then instead, when one was: "… was on from … to …".
        var other = _history.Covering(from) is { } covering && covering.SetId != tyre.SetId ? covering : null;
        if (other is null)
            return new DomainException("tyres.placeOutsideSet", "A tyre is on the car only while its set is.", new
            {
                Set = _allSets[tyre.SetId].Name, Label = tyre.Label, From = Iso(from), Until = until is { } u ? Iso(u) : null,
            });
        return new DomainException("tyres.placeWhileOtherSet", "A tyre is on the car only while its set is: another set was on then.", new
        {
            Set = _allSets[tyre.SetId].Name, Label = tyre.Label, Other = _allSets[other.SetId].Name, OtherFrom = Iso(other.From),
            OtherUntil = other.Until is { } ou ? Iso(ou) : null,
        });
    }

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd");
}
