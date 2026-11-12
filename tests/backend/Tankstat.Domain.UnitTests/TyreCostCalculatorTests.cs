using Tankstat.Domain.Tyres;

namespace Tankstat.Domain.UnitTests;

/// <summary>What the tyres cost (#288 step 4): per set and currency, repairs counted, the block's rows adding up to All.</summary>
public class TyreCostCalculatorTests
{
    private static readonly Guid Summer = Guid.NewGuid();
    private static readonly Guid Winter = Guid.NewGuid();

    [Fact]
    public void ASetsSpent_IsItsLinesAmounts_PerCurrency_AndRepairsCountPuncturesAndValves()
    {
        var costs = TyreCostCalculator.Costs([
            new(Summer, TyreEntryKind.PunctureRepaired, 25, "EUR"),
            new(Summer, TyreEntryKind.ValveReplaced, null, null), // a repair without an amount still counts as one
            new(Summer, TyreEntryKind.Bought, 120_000, "HUF"),
            new(Summer, TyreEntryKind.StorageFee, 15, "EUR"),
            new(Winter, TyreEntryKind.WheelBalancing, 30_000, "HUF"),
            new(null, TyreEntryKind.Other, 8, "EUR"),
            new(null, TyreEntryKind.PunctureRepaired, null, null), // of no set: no set's repair
        ]);

        Assert.Equal([new TyreSpent("HUF", 120_000), new TyreSpent("EUR", 40)], costs.SpentOn(Summer));
        Assert.Equal([new TyreSpent("HUF", 30_000)], costs.SpentOn(Winter));
        Assert.Equal((2, 0), (costs.RepairsOf(Summer), costs.RepairsOf(Winter)));
        Assert.Equal([new TyreSpent("EUR", 8)], costs.NoSet);
        Assert.Equal([new TyreSpent("HUF", 150_000), new TyreSpent("EUR", 48)], costs.All); // the rows add up to All
    }

    [Fact]
    public void Nothing_IsNothing()
    {
        var costs = TyreCostCalculator.Costs([]);

        Assert.Equal((0, 0, 0), (costs.SpentOn(Summer).Count, costs.NoSet.Count, costs.All.Count));
    }

    [Fact]
    public void OneExpenseSharedByTwoTyres_CountsOnceForTheSet_AndByItsEvenShareForEachTyre_AndTwoRepairs()
    {
        var (w3, w4, x) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var costs = TyreCostCalculator.Costs([
            new(Winter, TyreEntryKind.PunctureRepaired, 13_000, "HUF", x, w3),
            new(Winter, TyreEntryKind.PunctureRepaired, 13_000, "HUF", x, w4),
            new(Winter, TyreEntryKind.StorageFee, 5_001, "HUF", Guid.NewGuid()), // the set's own line
            new(Summer, TyreEntryKind.Other, 9, "EUR", Guid.NewGuid(), Guid.NewGuid()),
        ]);

        Assert.Equal([new TyreSpent("HUF", 18_001)], costs.SpentOn(Winter)); // 13,000 once
        Assert.Equal([new TyreSpent("HUF", 6_500)], costs.SpentOnTyre(w3));
        Assert.Equal([new TyreSpent("HUF", 6_500)], costs.SpentOnTyre(w4));
        Assert.Equal((2, 1, 1), (costs.RepairsOf(Winter), costs.RepairsOfTyre(w3), costs.RepairsOfTyre(w4)));
        Assert.Equal([new TyreSpent("HUF", 18_001), new TyreSpent("EUR", 9)], costs.All);
    }

    [Fact]
    public void AnUnevenShare_IsRoundedToTheCent()
    {
        var x = Guid.NewGuid();
        Guid[] tyres = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        var costs = TyreCostCalculator.Costs(tyres.Select(t => new TyreLineCost(Winter, TyreEntryKind.WheelBalancing, 10, "EUR", x, t)));
        Assert.All(tyres, t => Assert.Equal([new TyreSpent("EUR", 3.33m)], costs.SpentOnTyre(t)));
        Assert.Equal([new TyreSpent("EUR", 10)], costs.SpentOn(Winter));
    }
}
