using Tankstat.Application.Tyres;
using Tankstat.Domain;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Tyres;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.UnitTests;

/// <summary>Tyres replaced early (#289 step 3): the old ones retired, the new ones at their corners from the day, one expense for the price.</summary>
public class TyreReplaceTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private sealed record Scene(World W, Vehicle Car, Guid Summer, Guid[] Tyres)
    {
        public TyreService Service => W.TyreService;
    }

    /// <summary>The summer set on the car since 28 Mar 2026 (today is 1 Oct 2026); its four tyres' ids, broken up only when asked.</summary>
    private static async Task<Scene> Setup(bool brokenUp = true)
    {
        var w = new World();
        w.Current.SignInAs(w.AddUser("alice@x.co"));
        var car = await w.VehicleService.AddAsync("Car", null, FuelType.Petrol, MeasurementUnits.Metric, default);
        await w.ExpenseService.AddAsync(car.Id, new Expenses.ExpenseInput(D(2026, 3, 1), "Service", null, 100, "EUR", 140_000, null), default);
        var summer = Guid.NewGuid();
        await w.TyreService.AddSetAsync(new NewTyreSet(car.Id, new TyreSetInput("Michelin Primacy 4", TyreSeason.Summer, null, D(2023, 3, 25), null, null), true, D(2026, 3, 28), null, true),
            default, summer, Guid.NewGuid());
        Guid[] tyres = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        if (brokenUp) await w.TyreService.BreakUpAsync(BreakUp(summer, tyres), default);
        return new Scene(w, car, summer, tyres);
    }

    private static TyreBreakUpInput BreakUp(Guid set, Guid[] tyres) => new(set, D(2026, 4, 1), [
        new(tyres[0], "S1", TyreCorner.FrontLeft), new(tyres[1], "S2", TyreCorner.FrontRight), new(tyres[2], "S3", TyreCorner.RearLeft), new(tyres[3], "S4", TyreCorner.RearRight)]);

    private static TyreReplaceInput TwoFront(Scene s, Guid w5, Guid w6, Guid expense) => new(s.Summer, D(2026, 7, 9), 145_500, TyreRetiredWhy.Damaged,
        [new(s.Tyres[0], w5, "S5", null, 20, 2026, Guid.NewGuid()), new(s.Tyres[1], w6, "S6", "Pilot Sport 5", 20, 2026, Guid.NewGuid())],
        new TyreFee(78_000, "HUF", "Tyres", "Bought: Michelin Primacy 4", expense));

    [Fact]
    public async Task ReplacingTwoTyres_RetiresThem_PutsTheNewOnesAtTheirCorners_AndMakesABoughtLineEachSharingOneExpense()
    {
        var s = await Setup();
        var (w5, w6, expense) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var overview = await s.Service.ReplaceTyresAsync(TwoFront(s, w5, w6, expense), default);

        var set = overview.Sets.Single(x => x.Set.Id == s.Summer);
        var (s1, s5, s6) = (set.Tyres!.Single(t => t.Tyre.Id == s.Tyres[0]), set.Tyres!.Single(t => t.Tyre.Id == w5), set.Tyres!.Single(t => t.Tyre.Id == w6));
        Assert.Equal((D(2026, 7, 9), TyreRetiredWhy.Damaged, (Guid?)w5), (s1.Tyre.RetiredOn!.Value, s1.Tyre.RetiredWhy!.Value, s1.Tyre.ReplacedById));
        Assert.Equal(D(2026, 7, 9), s1.Places.Single().Place.Until);
        Assert.Equal((TyreCorner.FrontLeft, TyreCorner.FrontRight), (s5.Corner!.Value, s6.Corner!.Value));
        Assert.True(s5.OnTheCar);
        Assert.Equal((D(2026, 7, 9), 0L, "Pilot Sport 5", 20), (s5.Tyre.Since, s5.Tyre.RoughDistance, s6.Tyre.Name, s6.Tyre.MadeWeek!.Value));
        Assert.Equal([new TyreSpent("HUF", 39_000)], s5.Spent!);
        Assert.Equal([new TyreSpent("HUF", 78_000)], set.Spent!); // one expense, counted once
        var bought = s.W.Tyres.Entries.Where(e => e.Kind == TyreEntryKind.Bought && e.TyreId is not null).ToList();
        Assert.Equal(2, bought.Count);
        Assert.Single(bought.Select(e => e.GroupId).Distinct());
        Assert.Single(bought.Select(e => e.OdometerReadingId).Distinct());
        Assert.All(bought, e => Assert.Equal(expense, e.ExpenseId));
        Assert.Equal(78_000, s.W.Expenses.Items.Single(e => e.Id == expense).Amount);
    }

    [Fact]
    public async Task TheSameReplacementSentAgain_ChangesNothing()
    {
        var s = await Setup();
        var input = TwoFront(s, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await s.Service.ReplaceTyresAsync(input, default);

        await s.Service.ReplaceTyresAsync(input, default);

        Assert.Equal(6, s.W.Tyres.Tyres.Count);
        Assert.Equal(2, s.W.Tyres.Entries.Count(e => e.Kind == TyreEntryKind.Bought && e.TyreId is not null));
    }

    [Fact]
    public async Task OnAWholeSet_TheReplacementBreaksItUpFirst()
    {
        var s = await Setup(brokenUp: false);
        var w5 = Guid.NewGuid();

        await s.Service.ReplaceTyresAsync(new TyreReplaceInput(s.Summer, D(2026, 7, 9), null, TyreRetiredWhy.Worn,
            [new(s.Tyres[2], w5, "S5")], BreakUp: BreakUp(s.Summer, s.Tyres)), default);

        Assert.NotNull(s.W.Tyres.Sets.Single(x => x.Id == s.Summer).BrokenUpOn);
        Assert.Equal((TyreRetiredWhy.Worn, (Guid?)w5), (s.W.Tyres.Tyres.Single(t => t.Id == s.Tyres[2]).RetiredWhy!.Value, s.W.Tyres.Tyres.Single(t => t.Id == s.Tyres[2]).ReplacedById));
        Assert.Equal(TyreCorner.RearLeft, s.W.Tyres.Places.Single(p => p.TyreId == w5).Corner);
    }

    [Fact]
    public async Task ARetiredSetAfterwards_RetiresOnlyItsTyresInUse_AndUnretiringBringsBackOnlyThose()
    {
        var s = await Setup();
        await s.Service.ReplaceTyresAsync(TwoFront(s, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), default);
        var winter = Guid.NewGuid();
        await s.Service.AddSetAsync(new NewTyreSet(s.Car.Id, new TyreSetInput("Continental WinterContact", TyreSeason.Winter, null, D(2022, 11, 1), null, null), false, D(2026, 9, 1), null, null),
            default, winter);
        await s.Service.SwapAsync(new TyreSwapInput(s.Car.Id, winter, D(2026, 9, 1), null, false, null, null), default, Guid.NewGuid());
        Tyre Of(Guid id) => s.W.Tyres.Tyres.Single(t => t.Id == id);

        await s.Service.RetireAsync(s.Summer, D(2026, 9, 15), default);
        Assert.Equal((TyreRetiredWhy.Damaged, TyreRetiredWhy.SetRetired), (Of(s.Tyres[0]).RetiredWhy!.Value, Of(s.Tyres[2]).RetiredWhy!.Value));

        await s.Service.UnretireAsync(s.Summer, default);
        Assert.Equal((true, false), (Of(s.Tyres[0]).IsRetired, Of(s.Tyres[2]).IsRetired)); // the replaced one stays retired
    }

    [Fact]
    public async Task ARefusedReplacement_SaysWhy_AndChangesNothing()
    {
        var s = await Setup();
        Task Replace(TyreReplaceInput input) => s.Service.ReplaceTyresAsync(input, default);
        var one = (Guid old, string label) => new TyreReplacement(old, Guid.NewGuid(), label);

        Assert.Equal("tyres.labelTaken", (await Assert.ThrowsAsync<DomainException>(() => Replace(new(s.Summer, D(2026, 7, 9), null, TyreRetiredWhy.Damaged, [one(s.Tyres[0], "s2")])))).Key);
        Assert.Equal("tyres.whyInvalid", (await Assert.ThrowsAsync<DomainException>(() => Replace(new(s.Summer, D(2026, 7, 9), null, TyreRetiredWhy.SetRetired, [one(s.Tyres[0], "S5")])))).Key);
        Assert.Equal("tyres.tapToReplace", (await Assert.ThrowsAsync<DomainException>(() => Replace(new(s.Summer, D(2026, 7, 9), null, TyreRetiredWhy.Damaged, [])))).Key);
        Assert.Equal(4, s.W.Tyres.Tyres.Count);
        Assert.DoesNotContain(s.W.Tyres.Tyres, t => t.IsRetired);
    }
}
