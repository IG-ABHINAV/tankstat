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
}
