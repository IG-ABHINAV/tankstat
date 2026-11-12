namespace Tankstat.Domain.Tyres;

/// <summary>An amount in one currency (never converted).</summary>
public sealed record TyreSpent(string Currency, decimal Amount);

/// <summary>
/// A live line of the tyre log as its costs count it: its set (or none), what was done, and its live expense's amount, if any; with the
/// expense's id and the single tyre it is for (#289), since the lines made together for several tyres share one expense.
/// </summary>
public sealed record TyreLineCost(Guid? SetId, TyreEntryKind Kind, decimal? Amount, string? Currency, Guid? ExpenseId = null, Guid? TyreId = null);

/// <summary>What the tyres cost (#288 step 4): per set and for lines of no set, per currency, and how many repairs each set had; per single tyre (#289) too.</summary>
/// <param name="All">Every set's and every line of no set's, together: the block's first row.</param>
/// <param name="TyreSpent">A single tyre's: each of its lines' expense by its even share among the live lines holding it (the owner's decision 40).</param>
/// <param name="TyreRepairs">A single tyre's punctures repaired and valves replaced.</param>
public sealed record TyreCosts(
    IReadOnlyDictionary<Guid, IReadOnlyList<TyreSpent>> Spent, IReadOnlyDictionary<Guid, int> Repairs, IReadOnlyList<TyreSpent> NoSet, IReadOnlyList<TyreSpent> All,
    IReadOnlyDictionary<Guid, IReadOnlyList<TyreSpent>>? TyreSpent = null, IReadOnlyDictionary<Guid, int>? TyreRepairs = null)
{
    public IReadOnlyList<TyreSpent> SpentOn(Guid setId) => Spent.TryGetValue(setId, out var spent) ? spent : [];
    public int RepairsOf(Guid setId) => Repairs.GetValueOrDefault(setId);
    public IReadOnlyList<TyreSpent> SpentOnTyre(Guid tyreId) => TyreSpent is not null && TyreSpent.TryGetValue(tyreId, out var spent) ? spent : [];
    public int RepairsOfTyre(Guid tyreId) => TyreRepairs?.GetValueOrDefault(tyreId) ?? 0;
}

/// <summary>
/// The costs worked out on read, nothing stored (#288 step 4), pure like <see cref="TyreCalculator"/>: a set's *Spent* is the sum of the live
/// expenses of its live lines, per currency, an expense the lines made together share counted once (#289); its *Repairs* the count of its
/// live punctures repaired and valves replaced (the owner's decision 9), a repair of two tyres being two. A single tyre's *Spent* counts each
/// of its lines' expense by its even share among the lines holding it, rounded to the cent; its *Repairs* its own. The caller hands over
/// only live lines and only live expenses' amounts.
/// </summary>
public static class TyreCostCalculator
{
    public static bool IsRepair(TyreEntryKind kind) => kind is TyreEntryKind.PunctureRepaired or TyreEntryKind.ValveReplaced;

    public static TyreCosts Costs(IEnumerable<TyreLineCost> lines)
    {
        var all = lines.ToList();
        var paid = all.Where(l => l.Amount is not null && !string.IsNullOrEmpty(l.Currency)).ToList();
        // One expense, counted once wherever it is summed (a line without an expense id is one of its own, even one equal to another).
        static IEnumerable<TyreLineCost> Once(IEnumerable<TyreLineCost> ls) =>
            ls.Where(l => l.ExpenseId is null).Concat(ls.Where(l => l.ExpenseId is not null).DistinctBy(l => l.ExpenseId));
        var spent = paid.Where(l => l.SetId is not null).GroupBy(l => l.SetId!.Value).ToDictionary(g => g.Key, g => Sum(Once(g)));
        var repairs = all.Where(l => l.SetId is not null && IsRepair(l.Kind)).GroupBy(l => l.SetId!.Value).ToDictionary(g => g.Key, g => g.Count());
        var holders = paid.Where(l => l.ExpenseId is not null).GroupBy(l => l.ExpenseId!.Value).ToDictionary(g => g.Key, g => g.Count());
        var tyreSpent = paid.Where(l => l.TyreId is not null).GroupBy(l => l.TyreId!.Value).ToDictionary(g => g.Key, g => Sum(g.Select(l =>
            l with { Amount = Math.Round(l.Amount!.Value / (l.ExpenseId is { } x ? holders[x] : 1), 2, MidpointRounding.AwayFromZero) })));
        var tyreRepairs = all.Where(l => l.TyreId is not null && IsRepair(l.Kind)).GroupBy(l => l.TyreId!.Value).ToDictionary(g => g.Key, g => g.Count());
        return new TyreCosts(spent, repairs, Sum(Once(paid.Where(l => l.SetId is null))), Sum(Once(paid)), tyreSpent, tyreRepairs);
    }

    /// <summary>Per currency, the largest first (then by code), as one figure each.</summary>
    private static IReadOnlyList<TyreSpent> Sum(IEnumerable<TyreLineCost> lines) =>
        [.. lines.GroupBy(l => l.Currency!).Select(g => new TyreSpent(g.Key, g.Sum(l => l.Amount!.Value))).OrderByDescending(s => s.Amount).ThenBy(s => s.Currency, StringComparer.Ordinal)];
}
