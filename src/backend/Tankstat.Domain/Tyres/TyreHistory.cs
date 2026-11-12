namespace Tankstat.Domain.Tyres;

/// <summary>The installations a change made, changed or trashed (the entities are changed in place; the caller saves them).</summary>
public sealed class TyreChanges
{
    public List<TyreInstallation> Created { get; } = [];
    public List<TyreInstallation> Changed { get; } = [];
    public List<TyreInstallation> Trashed { get; } = [];

    /// <summary>
    /// The installations a join took away from their first day (#288): trashed into the set's installation before them, or moved to an
    /// earlier day. What their lines held for that day (a reading, a note) stays on that day.
    /// </summary>
    public List<(TyreInstallation Installation, DateOnly Day)> JoinedAway { get; } = [];

    public bool Any => Created.Count + Changed.Count + Trashed.Count > 0;

    internal void Change(TyreInstallation s)
    {
        if (!Created.Contains(s) && !Changed.Contains(s)) Changed.Add(s);
    }
}

/// <summary>
/// The rules of a vehicle's tyre history (#287), over its live sets and their live installations: a swap or a change of positions on any day,
/// today or in the past, and the checks every change must pass (nothing on the car twice, never two whole sets at once, nothing after a set
/// was retired). Pure: it changes the installation entities it was given and says which; the device ports the same rules
/// (the swap contract in <c>tests/backend/contracts/tyre-swaps</c>).
/// </summary>
public sealed class TyreHistory
{
    private readonly Dictionary<Guid, TyreSet> _sets;
    private readonly List<TyreInstallation> _live;
    private readonly Guid _ownerId;
    private readonly Guid _vehicleId;

    /// <param name="sets">The vehicle's sets that are not in the trash.</param>
    /// <param name="installations">Their installations that are not in the trash.</param>
    public TyreHistory(Guid ownerId, Guid vehicleId, IEnumerable<TyreSet> sets, IEnumerable<TyreInstallation> installations)
    {
        (_ownerId, _vehicleId) = (ownerId, vehicleId);
        _sets = sets.Where(s => !s.IsDeleted).ToDictionary(s => s.Id);
        _live = [.. installations.Where(s => !s.IsDeleted && _sets.ContainsKey(s.SetId))];
    }

    public IReadOnlyList<TyreInstallation> Live => _live;

    /// <summary>The live sets the history was made over.</summary>
    public IReadOnlyCollection<TyreSet> Sets => _sets.Values;

    /// <summary>The live installation on the car on <paramref name="day"/>, if any.</summary>
    public TyreInstallation? Covering(DateOnly day) => _live.FirstOrDefault(s => s.From <= day && (s.Until is null || s.Until > day));

    /// <summary>
    /// Puts <paramref name="set"/> on the car on <paramref name="day"/>. What is on the car that day comes off; the set stays on until the
    /// next installation that starts after that day (or until the one it replaced would have come off). A second swap on a day that has one
    /// (another set's installation starts that day) is refused with <c>tyres.swapExists</c> unless <paramref name="replace"/>: then that installation
    /// goes to the trash first. The same set on the day it went on only sets its pairs. The set's installation that ends that day is carried on;
    /// its installation that starts where the one this swap cuts or replaces would have ended is joined to the new one (nothing else was on the
    /// car between them). Into a gap, a swap never joins the set's next installation: that one began with a swap of its own, which stays recorded.
    /// </summary>
    /// <param name="id">The id of the installation it creates, if it creates one (the client's, so a retry is idempotent).</param>
    /// <param name="pairsGiven">False for a swap from papers, which says nothing about the pairs: the set's own installation of that day keeps its own.</param>
    /// <param name="repeatable">Sent from a device's queue, so it may be a retry of a swap that joined the set's installation (the join kept no id to
    /// find): the set already on the car that day with the pairs asked for is the answer, not an error. With other pairs it is a swap of its
    /// own that came too late, refused as online.</param>
    /// <param name="sameCorners">For a set broken up into single tyres (#289): whether an installation of the set has every tyre at the corner
    /// this swap gives it, which decides a join and a retry as the pairs do for a whole set (the owner's decision 47).</param>
    public TyreChanges Swap(
        TyreSet set, DateOnly day, bool? pairOneFront, bool replace, Guid by, DateTimeOffset now, Guid? id = null, bool pairsGiven = true, bool repeatable = false,
        Func<TyreInstallation, bool>? sameCorners = null)
    {
        // A broken-up set goes by its tyres' corners alone: an installation from before the break-up keeps the pairs it recorded.
        bool Same(TyreInstallation s) => sameCorners?.Invoke(s) ?? s.PairOneFront == pairOneFront;
        bool? PairsOf(TyreInstallation s) => sameCorners is null ? pairOneFront : s.PairOneFront;
        var changes = new TyreChanges();
        if (set.IsRetired) throw new DomainException("tyres.setRetired", "A retired tyre set cannot go on the car.", new { Set = set.Name });
        var covering = Covering(day);
        if (covering?.SetId == set.Id)
        {
            if (covering.From != day)
            {
                if (repeatable && Same(covering)) return changes; // a retry of a swap that joined this installation to an earlier one
                throw new DomainException("tyres.alreadyOnCar", "This tyre set is already on the car that day.", new { Set = set.Name, From = Iso(covering.From) });
            }
            if (pairsGiven && covering.Update(covering.From, covering.Until, PairsOf(covering), by)) changes.Change(covering); // a broken-up set keeps the pairs it recorded
            return Checked(changes);
        }
        DateOnly? until;
        if (covering is not null && covering.From == day)
        {
            if (!replace)
                throw new DomainException("tyres.swapExists", "A swap is already logged on that day.", new { Date = Iso(day), Set = _sets[covering.SetId].Name });
            until = covering.Until;
            covering.MarkDeleted(now, by);
            _live.Remove(covering);
            changes.Trashed.Add(covering);
        }
        else if (covering is not null)
        {
            until = covering.Until;
            covering.Update(covering.From, day, covering.PairOneFront, by);
            changes.Change(covering);
        }
        else until = _live.Where(s => s.From > day).Select(s => (DateOnly?)s.From).Min();

        var before = _live.FirstOrDefault(s => s.SetId == set.Id && s.Until == day && Same(s));
        var after = covering is not null && until is { } u ? _live.FirstOrDefault(s => s.SetId == set.Id && s.From == u && Same(s)) : null;
        if (before is not null && after is not null)
        {
            before.Update(before.From, after.Until, PairsOf(before), by);
            changes.Change(before);
            changes.JoinedAway.Add((after, after.From));
            after.MarkDeleted(now, by);
            _live.Remove(after);
            changes.Trashed.Add(after);
        }
        else if (before is not null)
        {
            before.Update(before.From, until, PairsOf(before), by);
            changes.Change(before);
        }
        else if (after is not null)
        {
            changes.JoinedAway.Add((after, after.From));
            after.Update(day, after.Until, PairsOf(after), by);
            changes.Change(after);
        }
        else
        {
            var created = TyreInstallation.Create(_ownerId, by, _vehicleId, set.Id, day, until, pairOneFront, id);
            _live.Add(created);
            changes.Created.Add(created);
        }
        return Checked(changes);
    }

    /// <summary>
    /// The set on the car on <paramref name="day"/> changes its pairs from that day: on the day its installation began, in place; otherwise the
    /// installation ends that day and a new one begins with the new pairs.
    /// </summary>
    /// <param name="repeatable">Sent from a device's queue, so it may be a retry: one applied in place on the day its installation began leaves
    /// nothing with its id to find, and the pairs already as asked are its answer, not an error. Online they are <c>tyres.positionsUnchanged</c>.</param>
    /// <param name="sameCorners">For a broken-up set (#289): whether the installation has every tyre at the corner asked for, which then
    /// changes nothing, as the same pairs do. A broken-up set's change of positions splits the installation as a whole set's does, and on the
    /// installation's first day leaves it to the places (<see cref="TyrePlaces.SetCorners"/>).</param>
    public TyreChanges ChangePositions(
        TyreSet set, DateOnly day, bool? pairOneFront, Guid by, Guid? id = null, bool repeatable = false, Func<TyreInstallation, bool>? sameCorners = null)
    {
        var changes = new TyreChanges();
        var covering = _live.FirstOrDefault(s => s.SetId == set.Id && s.From <= day && (s.Until is null || s.Until > day))
            ?? throw new DomainException("tyres.notOnCar", "This tyre set is not on the car that day.", new { Set = set.Name, Date = Iso(day) });
        if (sameCorners?.Invoke(covering) ?? covering.PairOneFront == pairOneFront)
        {
            if (repeatable && covering.From == day) return changes;
            throw new DomainException("tyres.positionsUnchanged", "The positions are the same as they were.");
        }
        if (covering.From == day)
        {
            if (sameCorners is not null) return changes; // a broken-up set's corners change in place on the installation's first day: the places' to do
            covering.Update(day, covering.Until, pairOneFront, by);
            changes.Change(covering);
            return Checked(changes);
        }
        var until = covering.Until;
        covering.Update(covering.From, day, covering.PairOneFront, by);
        changes.Change(covering);
        var created = TyreInstallation.Create(_ownerId, by, _vehicleId, set.Id, day, until, pairOneFront, id);
        _live.Add(created);
        changes.Created.Add(created);
        return Checked(changes);
    }

    /// <summary>
    /// The swap that began <paramref name="installation"/> moves to <paramref name="day"/> (#288, its line's day changed): the installation
    /// starts then, and the one that came off on its old first day (the set before it on the car) comes off then instead.
    /// </summary>
    public TyreChanges MoveSwap(TyreInstallation installation, DateOnly day, Guid by)
    {
        if (!_live.Contains(installation)) throw new DomainException("tyres.installationTrashedCannotEdit", "An installation in the trash cannot be changed; restore it first.");
        var changes = new TyreChanges();
        if (day == installation.From) return changes;
        var before = _live.FirstOrDefault(s => s != installation && s.Until == installation.From);
        if (before is not null)
        {
            if (day <= before.From)
                throw new DomainException("tyres.swapBeforePrevious", "The swap cannot move to or before the day the set before it went on the car.",
                    new { Set = _sets[before.SetId].Name, From = Iso(before.From) });
            before.Update(before.From, day, before.PairOneFront, by);
            changes.Change(before);
        }
        installation.Update(day, installation.Until, installation.PairOneFront, by);
        changes.Change(installation);
        return Checked(changes);
    }

    /// <summary>Adds an installation typed in a set's history.</summary>
    public TyreInstallation Add(TyreSet set, DateOnly from, DateOnly? until, bool? pairOneFront, Guid by, Guid? id = null)
    {
        var created = TyreInstallation.Create(_ownerId, by, _vehicleId, set.Id, from, until, pairOneFront, id);
        _live.Add(created);
        Check([created]);
        return created;
    }

    /// <summary>Changes a live installation of the history; false when nothing changed.</summary>
    public bool Edit(TyreInstallation installation, DateOnly from, DateOnly? until, bool? pairOneFront, Guid by)
    {
        if (!_live.Contains(installation)) throw new DomainException("tyres.installationTrashedCannotEdit", "An installation in the trash cannot be changed; restore it first.");
        var changed = installation.Update(from, until, pairOneFront, by);
        Check([installation]);
        return changed;
    }

    /// <summary>Brings back an installation (alone, or the live installations of a set coming back from the trash): refused, with the reason, when it no longer fits.</summary>
    public void CheckRestored(IReadOnlyCollection<TyreInstallation> restored)
    {
        foreach (var s in restored)
            if (!_live.Contains(s)) _live.Add(s);
        Check(restored, restoring: true);
    }

    /// <summary>
    /// Every live installation fits: it does not overlap another of its set (<c>tyres.setOnTwice</c>) or of another set (<c>tyres.twoSetsAtOnce</c>;
    /// <c>tyres.restoreConflict</c> while restoring), and a retired set's ends by the day it was retired (<c>tyres.setRetired</c>). "In use since"
    /// is month-level, so an installation may start before it.
    /// </summary>
    /// <param name="moving">The installations the change adds, edits or restores: an overlap names the other one, the one already on the car then.</param>
    public void Check(IReadOnlyCollection<TyreInstallation>? moving = null, bool restoring = false)
    {
        var ordered = _live.OrderBy(s => s.From).ToList();
        foreach (var s in ordered)
        {
            var set = _sets[s.SetId];
            if (set.RetiredOn is { } retired && (s.Until is null || s.Until > retired))
                throw new DomainException("tyres.setRetired", "A retired tyre set cannot go on the car.", new { Set = set.Name });
        }
        // Ordered by start, an installation that overlaps any later one overlaps the next one, so neighbours are enough.
        for (var i = 0; i + 1 < ordered.Count; i++)
        {
            var (a, b) = (ordered[i], ordered[i + 1]);
            if (b.From >= (a.Until ?? DateOnly.MaxValue)) continue;
            var other = moving?.Contains(b) == true && moving?.Contains(a) != true ? a : b;
            var args = new { Set = _sets[other.SetId].Name, From = Iso(other.From), Until = other.Until is { } u ? Iso(u) : null };
            if (restoring) throw new DomainException("tyres.restoreConflict", "It cannot be restored as it is: another installation is on the car then.", args);
            if (a.SetId == b.SetId) throw new DomainException("tyres.setOnTwice", "This tyre set is on the car then already.", args);
            throw new DomainException("tyres.twoSetsAtOnce", "Another tyre set is on the car then.", args);
        }
    }

    private TyreChanges Checked(TyreChanges changes)
    {
        Check([.. changes.Created, .. changes.Changed]);
        return changes;
    }

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd");
}
