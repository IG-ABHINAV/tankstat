namespace Tankstat.Domain.Tyres;

/// <summary>The stretches a change made, changed or trashed (the entities are changed in place; the caller saves them).</summary>
public sealed class TyreChanges
{
    public List<TyreStretch> Created { get; } = [];
    public List<TyreStretch> Changed { get; } = [];
    public List<TyreStretch> Trashed { get; } = [];

    public bool Any => Created.Count + Changed.Count + Trashed.Count > 0;

    internal void Change(TyreStretch s)
    {
        if (!Created.Contains(s) && !Changed.Contains(s)) Changed.Add(s);
    }
}

/// <summary>
/// The rules of a vehicle's tyre history (#287), over its live sets and their live stretches: a swap or a change of positions on any day,
/// today or in the past, and the checks every change must pass (nothing on the car twice, never two whole sets at once, nothing after a set
/// was retired or before it was in use). Pure: it changes the stretch entities it was given and says which; the device ports the same rules
/// (the swap contract in <c>tests/backend/contracts/tyre-swaps</c>).
/// </summary>
public sealed class TyreHistory
{
    private readonly Dictionary<Guid, TyreSet> _sets;
    private readonly List<TyreStretch> _live;
    private readonly Guid _ownerId;
    private readonly Guid _vehicleId;

    /// <param name="sets">The vehicle's sets that are not in the trash.</param>
    /// <param name="stretches">Their stretches that are not in the trash.</param>
    public TyreHistory(Guid ownerId, Guid vehicleId, IEnumerable<TyreSet> sets, IEnumerable<TyreStretch> stretches)
    {
        (_ownerId, _vehicleId) = (ownerId, vehicleId);
        _sets = sets.Where(s => !s.IsDeleted).ToDictionary(s => s.Id);
        _live = [.. stretches.Where(s => !s.IsDeleted && _sets.ContainsKey(s.SetId))];
    }

    public IReadOnlyList<TyreStretch> Live => _live;

    /// <summary>The live stretch on the car on <paramref name="day"/>, if any.</summary>
    public TyreStretch? Covering(DateOnly day) => _live.FirstOrDefault(s => s.From <= day && (s.Until is null || s.Until > day));

    /// <summary>
    /// Puts <paramref name="set"/> on the car on <paramref name="day"/>. What is on the car that day comes off; the set stays on until the
    /// next stretch that starts after that day (or until the one it replaced would have come off). A second swap on a day that has one
    /// (another set's stretch starts that day) is refused with <c>tyres.swapExists</c> unless <paramref name="replace"/>: then that stretch
    /// goes to the trash first. The same set on the day it went on only sets its pairs. Stretches of the set that meet are joined.
    /// </summary>
    /// <param name="id">The id of the stretch it creates, if it creates one (the client's, so a retry is idempotent).</param>
    public TyreChanges Swap(TyreSet set, DateOnly day, bool? pairOneFront, bool replace, Guid by, DateTimeOffset now, Guid? id = null)
    {
        var changes = new TyreChanges();
        if (set.IsRetired) throw new DomainException("tyres.setRetired", "A retired tyre set cannot go on the car.", new { Set = set.Name });
        var covering = Covering(day);
        if (covering?.SetId == set.Id)
        {
            if (covering.From != day)
                throw new DomainException("tyres.alreadyOnCar", "This tyre set is already on the car that day.", new { Set = set.Name, From = Iso(covering.From) });
            if (covering.Update(covering.From, covering.Until, pairOneFront, by)) changes.Change(covering);
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

        var before = _live.FirstOrDefault(s => s.SetId == set.Id && s.Until == day && s.PairOneFront == pairOneFront);
        var after = until is { } u ? _live.FirstOrDefault(s => s.SetId == set.Id && s.From == u && s.PairOneFront == pairOneFront) : null;
        if (before is not null && after is not null)
        {
            before.Update(before.From, after.Until, pairOneFront, by);
            changes.Change(before);
            after.MarkDeleted(now, by);
            _live.Remove(after);
            changes.Trashed.Add(after);
        }
        else if (before is not null)
        {
            before.Update(before.From, until, pairOneFront, by);
            changes.Change(before);
        }
        else if (after is not null)
        {
            after.Update(day, after.Until, pairOneFront, by);
            changes.Change(after);
        }
        else
        {
            var created = TyreStretch.Create(_ownerId, by, _vehicleId, set.Id, day, until, pairOneFront, id);
            _live.Add(created);
            changes.Created.Add(created);
        }
        return Checked(changes);
    }

    /// <summary>
    /// The set on the car on <paramref name="day"/> changes its pairs from that day: on the day its stretch began, in place; otherwise the
    /// stretch ends that day and a new one begins with the new pairs.
    /// </summary>
    public TyreChanges ChangePositions(TyreSet set, DateOnly day, bool? pairOneFront, Guid by, Guid? id = null)
    {
        var changes = new TyreChanges();
        var covering = _live.FirstOrDefault(s => s.SetId == set.Id && s.From <= day && (s.Until is null || s.Until > day))
            ?? throw new DomainException("tyres.notOnCar", "This tyre set is not on the car that day.", new { Set = set.Name, Date = Iso(day) });
        if (covering.PairOneFront == pairOneFront) throw new DomainException("tyres.positionsUnchanged", "The positions are the same as they were.");
        if (covering.From == day)
        {
            covering.Update(day, covering.Until, pairOneFront, by);
            changes.Change(covering);
            return Checked(changes);
        }
        var until = covering.Until;
        covering.Update(covering.From, day, covering.PairOneFront, by);
        changes.Change(covering);
        var created = TyreStretch.Create(_ownerId, by, _vehicleId, set.Id, day, until, pairOneFront, id);
        _live.Add(created);
        changes.Created.Add(created);
        return Checked(changes);
    }

    /// <summary>Adds a stretch typed in a set's history.</summary>
    public TyreStretch Add(TyreSet set, DateOnly from, DateOnly? until, bool? pairOneFront, Guid by, Guid? id = null)
    {
        var created = TyreStretch.Create(_ownerId, by, _vehicleId, set.Id, from, until, pairOneFront, id);
        _live.Add(created);
        Check();
        return created;
    }

    /// <summary>Changes a live stretch of the history; false when nothing changed.</summary>
    public bool Edit(TyreStretch stretch, DateOnly from, DateOnly? until, bool? pairOneFront, Guid by)
    {
        if (!_live.Contains(stretch)) throw new DomainException("tyres.stretchTrashedCannotEdit", "A stretch in the trash cannot be changed; restore it first.");
        var changed = stretch.Update(from, until, pairOneFront, by);
        Check();
        return changed;
    }

    /// <summary>Brings back a stretch (alone, or the live stretches of a set coming back from the trash): refused, with the reason, when it no longer fits.</summary>
    public void CheckRestored(IEnumerable<TyreStretch> restored)
    {
        foreach (var s in restored)
            if (!_live.Contains(s)) _live.Add(s);
        Check(restoring: true);
    }

    /// <summary>
    /// Every live stretch fits: it does not overlap another of its set (<c>tyres.setOnTwice</c>) or of another set (<c>tyres.twoSetsAtOnce</c>;
    /// <c>tyres.restoreConflict</c> while restoring), starts on or after its set's <see cref="TyreSet.Since"/> (<c>tyres.beforeSince</c>), and
    /// a retired set's ends by the day it was retired (<c>tyres.setRetired</c>).
    /// </summary>
    public void Check(bool restoring = false)
    {
        var ordered = _live.OrderBy(s => s.From).ToList();
        foreach (var s in ordered)
        {
            var set = _sets[s.SetId];
            if (s.From < set.Since)
                throw new DomainException("tyres.beforeSince", "A stretch cannot start before the set was in use.", new { Set = set.Name, Since = Iso(set.Since) });
            if (set.RetiredOn is { } retired && (s.Until is null || s.Until > retired))
                throw new DomainException("tyres.setRetired", "A retired tyre set cannot go on the car.", new { Set = set.Name });
        }
        // Ordered by start, a stretch that overlaps any later one overlaps the next one, so neighbours are enough.
        for (var i = 0; i + 1 < ordered.Count; i++)
        {
            var (a, b) = (ordered[i], ordered[i + 1]);
            if (b.From >= (a.Until ?? DateOnly.MaxValue)) continue;
            var args = new { Set = _sets[b.SetId].Name, From = Iso(b.From), Until = b.Until is { } u ? Iso(u) : null };
            if (restoring) throw new DomainException("tyres.restoreConflict", "It cannot be restored as it is: another stretch is on the car then.", args);
            if (a.SetId == b.SetId) throw new DomainException("tyres.setOnTwice", "This tyre set is on the car then already.", args);
            throw new DomainException("tyres.twoSetsAtOnce", "Another tyre set is on the car then.", args);
        }
    }

    private TyreChanges Checked(TyreChanges changes)
    {
        Check();
        return changes;
    }

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd");
}
