using Microsoft.Extensions.DependencyInjection;
using Tankstat.Application.Access;
using Tankstat.Application.Expenses;
using Tankstat.Application.Refuelings;
using Tankstat.Application.Stats;
using Tankstat.Application.Vehicles;
using Tankstat.Domain.Charts;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Vehicles;
using Tankstat.TestSupport;

namespace Tankstat.Infrastructure.UnitTests;

public class StatsRepositoryTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 9, 1);

    private static async Task<Vehicle> AddVehicle(TestDatabase db)
    {
        var v = TestData.Vehicle(Owner);
        await db.Get<IVehicleRepository>().AddAsync(v, default);
        return v;
    }

    [Fact]
    public async Task LoadsLiveRecords_ReducedToWhatStatisticsNeed_AndLeavesOutTheTrash()
    {
        await using var db = new TestDatabase();
        var car = await AddVehicle(db);
        var other = await AddVehicle(db);
        var refuelings = db.Get<IRefuelingRepository>();
        var keep = TestData.Refueling(Owner, Owner, car.Id, Day, 40, 100, 1000, true, "EUR");
        var trashed = TestData.Refueling(Owner, Owner, car.Id, Day.AddDays(1), 30, 80, 1100);
        await refuelings.AddAsync(keep, default);
        await refuelings.AddAsync(trashed, default);
        await refuelings.AddAsync(TestData.Refueling(Owner, Owner, other.Id, Day, 10, 10, 5), default);
        var loaded = (await refuelings.FindAsync(trashed.Id, default))!;
        loaded.MarkDeleted(DateTimeOffset.UtcNow);
        await refuelings.UpdateAsync(loaded, LinkedChanges.None, default);
        var expense = Expense.Create(Owner, Owner, car.Id, Day, "Oil", "Service", Cost.Create(Owner, car.Id, Day, 35, "HUF"), OdometerReading.Create(Owner, car.Id, Day, 1050));
        await db.Get<IExpenseRepository>().AddAsync(expense, default);

        var data = await db.Get<IStatsRepository>().LoadAsync(car.Id, default);

        Assert.Equal(new FuelPoint(Day, 40, 100, "EUR", 1000, true, null), Assert.Single(data.Fuel));
        Assert.Equal(new ExpensePoint(Day, "Service", 35, "HUF"), Assert.Single(data.Expenses));
        Assert.Equal([1000L, 1050L], data.Readings.Select(r => r.Value).Order()); // the trashed fill-up's reading is gone, the expense's is there
    }

    [Fact]
    public async Task AFillUpsDiscount_IsLoadedWithIt_AndNoneIsZero()
    {
        await using var db = new TestDatabase();
        var car = await AddVehicle(db);
        var refuelings = db.Get<IRefuelingRepository>();
        await refuelings.AddAsync(TestData.Refueling(Owner, Owner, car.Id, Day, 43, 34744, 1000, true, "HUF", discount: 430), default);
        await refuelings.AddAsync(TestData.Refueling(Owner, Owner, car.Id, Day.AddDays(9), 40, 32000, 1500, true, "HUF"), default);

        var data = await db.Get<IStatsRepository>().LoadAsync(car.Id, default);

        Assert.Equal([430m, 0m], data.Fuel.OrderBy(f => f.Date).Select(f => f.Discount)); // through the database's own decimals (TEXT on SQLite)
    }

    [Fact]
    public async Task Charts_AreStoredPerVehicle_VisibleToTheirCreatorAndWhenShared_AndRemovedWithTheVehicle()
    {
        await using var db = new TestDatabase();
        var car = await AddVehicle(db);
        var repo = db.Get<IVehicleChartRepository>();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var config = new ChartConfig(ChartMetric.TotalSpend, ChartGrouping.Quarter, ChartKind.Area, ChartRange.Custom, true, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30),
            ChartTrend.MovingAverage, RunningTotal: false, CompareYearEarlier: true, SecondMetric: ChartMetric.Distance, SecondKind: ChartKind.Line);
        var now = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        await repo.AddAsync(VehicleChart.Create(car.Id, alice, "Private", config, false, now), default);
        await repo.AddAsync(VehicleChart.Create(car.Id, alice, "Shared", config with { Stacked = false, Kind = ChartKind.Bar }, true, now.AddMinutes(1)), default);

        var forAlice = await repo.ListVisibleAsync(car.Id, alice, default);
        var forBob = await repo.ListVisibleAsync(car.Id, bob, default);

        Assert.Equal(["Private", "Shared"], forAlice.Select(c => c.Title));
        Assert.Equal(["Shared"], forBob.Select(c => c.Title));
        Assert.Equal(config, forAlice[0].Config); // the whole recipe, custom dates, trend options and the second measure included
        Assert.Equal(2, await repo.CountByUserAsync(car.Id, alice, default));
        Assert.Equal(0, await repo.CountByUserAsync(car.Id, bob, default));

        var loaded = (await repo.FindAsync(forAlice[0].Id, default))!;
        loaded.Update("Renamed", config with { Range = ChartRange.Last6Months }, true);
        await repo.UpdateAsync(loaded, default);
        Assert.Equal(("Renamed", ChartRange.Last6Months, null), ((await repo.FindAsync(loaded.Id, default))!.Title, (await repo.FindAsync(loaded.Id, default))!.Range, (await repo.FindAsync(loaded.Id, default))!.RangeFrom));

        await repo.RemoveAsync(loaded, default);
        Assert.Null(await repo.FindAsync(loaded.Id, default));

        var vehicles = db.Get<IVehicleRepository>();
        var trashed = (await vehicles.FindAsync(car.Id, default))!;
        trashed.MarkDeleted(DateTimeOffset.UtcNow);
        await vehicles.UpdateAsync(trashed, default);
        await vehicles.PurgeAsync(OwnerScope.All, default);
        Assert.Empty(await repo.ListVisibleAsync(car.Id, alice, default)); // gone with the vehicle
    }

    [Fact]
    public async Task DashboardLayouts_AreStoredPerVehicleAndPerUser_ReplacedByTheNextSave_AndRemovedWithTheVehicle()
    {
        await using var db = new TestDatabase();
        var car = await AddVehicle(db);
        var repo = db.Get<IDashboardLayoutRepository>();
        var alice = Guid.NewGuid();
        var chart = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
        DashboardLayoutValues Values(params string[] charts) => new(["thisMonth", "odometer"], charts, [$"chart:{chart}"]);

        await repo.SaveSharedAsync(VehicleDashboardLayout.Create(car.Id, Values("preset:monthlyCosts"), now), default);
        await repo.SaveSharedAsync(VehicleDashboardLayout.Create(car.Id, Values("preset:distance", $"chart:{chart}"), now.AddMinutes(1)), default); // replaces it
        await repo.SavePersonalAsync(UserDashboardLayout.Create(alice, car.Id, Values(), now), default);
        // Two first saves of one person's layout at once: the one that loses the insert becomes an update.
        await Task.WhenAll(
            repo.SavePersonalAsync(UserDashboardLayout.Create(Owner, car.Id, Values("preset:a"), now), default),
            repo.SavePersonalAsync(UserDashboardLayout.Create(Owner, car.Id, Values("preset:b"), now), default));

        var shared = (await repo.FindSharedAsync(car.Id, default))!;
        Assert.Equal(["preset:distance", $"chart:{chart}"], shared.Charts);
        Assert.Equal(["thisMonth", "odometer"], shared.Figures);
        Assert.Equal(now.AddMinutes(1), shared.UpdatedAt);
        Assert.Empty((await repo.FindPersonalAsync(alice, car.Id, default))!.Charts);
        Assert.Contains((await repo.FindPersonalAsync(Owner, car.Id, default))!.Charts.Single(), new[] { "preset:a", "preset:b" });

        await repo.RemovePersonalAsync(alice, car.Id, default);
        Assert.Null(await repo.FindPersonalAsync(alice, car.Id, default));

        var vehicles = db.Get<IVehicleRepository>();
        var trashed = (await vehicles.FindAsync(car.Id, default))!;
        trashed.MarkDeleted(DateTimeOffset.UtcNow);
        await vehicles.UpdateAsync(trashed, default);
        await vehicles.PurgeAsync(OwnerScope.All, default);
        Assert.Null(await repo.FindSharedAsync(car.Id, default)); // gone with the vehicle
        Assert.Null(await repo.FindPersonalAsync(Owner, car.Id, default));
    }
}
