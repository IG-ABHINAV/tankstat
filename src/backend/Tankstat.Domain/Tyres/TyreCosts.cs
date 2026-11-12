namespace Tankstat.Domain.Tyres;

/// <summary>An amount in one currency (never converted).</summary>
public sealed record TyreSpent(string Currency, decimal Amount);

/// <summary>A live line of the tyre log as its costs count it: its set (or none), what was done, and its live expense's amount, if any.</summary>
public sealed record TyreLineCost(Guid? SetId, TyreEntryKind Kind, decimal? Amount, string? Currency);

/// <summary>What the tyres cost (#288 step 4): per set and for lines of no set, per currency, and how many repairs each set had.</summary>
/// <param name="All">Every set's and every line of no set's, together: the block's first row.</param>
public sealed record TyreCosts(
    IReadOnlyDictionary<Guid, IReadOnlyList<TyreSpent>> Spent, IReadOnlyDictionary<Guid, int> Repairs, IReadOnlyList<TyreSpent> NoSet, IReadOnlyList<TyreSpent> All)
{
    public IReadOnlyList<TyreSpent> SpentOn(Guid setId) => Spent.TryGetValue(setId, out var spent) ? spent : [];
    public int RepairsOf(Guid setId) => Repairs.GetValueOrDefault(setId);
}

/// <summary>
/// The costs worked out on read, nothing stored (#288 step 4), pure like <see cref="TyreCalculator"/>: a set's *Spent* is the sum of the live
/// expenses of its live lines, per currency; its *Repairs* the count of its live punctures repaired and valves replaced (the owner's
/// decision 9), with an amount or not. The caller hands over only live lines and only live expenses' amounts.
/// </summary>
public static class TyreCostCalculator
{
    public static bool IsRepair(TyreEntryKind kind) => kind is TyreEntryKind.PunctureRepaired or TyreEntryKind.ValveReplaced;

    public static TyreCosts Costs(IEnumerable<TyreLineCost> lines)
    {
        var all = lines.ToList();
        var paid = all.Where(l => l.Amount is not null && !string.IsNullOrEmpty(l.Currency)).ToList();
        var spent = paid.Where(l => l.SetId is not null).GroupBy(l => l.SetId!.Value).ToDictionary(g => g.Key, g => Sum(g));
        var repairs = all.Where(l => l.SetId is not null && IsRepair(l.Kind)).GroupBy(l => l.SetId!.Value).ToDictionary(g => g.Key, g => g.Count());
        return new TyreCosts(spent, repairs, Sum(paid.Where(l => l.SetId is null)), Sum(paid));
    }

    /// <summary>Per currency, the largest first (then by code), as one figure each.</summary>
    private static IReadOnlyList<TyreSpent> Sum(IEnumerable<TyreLineCost> lines) =>
        [.. lines.GroupBy(l => l.Currency!).Select(g => new TyreSpent(g.Key, g.Sum(l => l.Amount!.Value))).OrderByDescending(s => s.Amount).ThenBy(s => s.Currency, StringComparer.Ordinal)];
}
