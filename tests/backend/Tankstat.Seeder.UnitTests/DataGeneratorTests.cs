using Microsoft.Extensions.Time.Testing;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Tyres;
using Tankstat.Domain.Vehicles;
using Tankstat.Seeder;

namespace Tankstat.Seeder.UnitTests;

public class DataGeneratorTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private static readonly DateOnly Today = new(2026, 10, 1);

    private static List<GeneratedVehicle> Generate(int vehicles = 30, int trashed = 7, IntRange? refuelings = null, int seed = 42) =>
        new DataGenerator(Clock).Generate(new SeedOptions(vehicles, trashed, refuelings ?? new IntRange(5, 25), seed, true, null, null)).ToList();

    [Fact]
    public void EveryVehicleWithALog_HasASummerAndAWinterSet_SwappedEachSpringAndAutumn_ByTheHistorysRules()
    {
        var generated = Generate(vehicles: 12, trashed: 3, refuelings: new IntRange(15, 30));

        Assert.All(generated.Where(g => g.Vehicle.IsDeleted), g => Assert.Empty(g.TyreSets ?? []));
        Assert.All(generated.Where(g => !g.Vehicle.IsDeleted), g =>
        {
            var sets = g.TyreSets!;
            Assert.Equal([TyreSeason.Summer, TyreSeason.Winter], sets.Select(s => s.Season));
            Assert.All(sets, s => Assert.Equal(g.Vehicle.Id, s.VehicleId));
            var installations = g.TyreInstallations!.OrderBy(s => s.From).ToList();
            Assert.Equal(g.Refuelings.Min(r => r.Date), installations[0].From); // from the log's first day: the rough figure stands for before
            Assert.Single(installations, s => s.Until is null); // one set on the car today
            Assert.All(installations.Zip(installations.Skip(1)), p => Assert.Equal(p.First.Until, p.Second.From)); // one after the other, never two at once
            Assert.All(installations.Zip(installations.Skip(1)), p => Assert.NotEqual(p.First.SetId, p.Second.SetId)); // summer and winter take turns
            Assert.All(installations, s => Assert.NotNull(s.PairOneFront));
            Assert.All(installations.Skip(1), s => Assert.True(s.From.Month is 3 or 4 or 10 or 11, $"a swap around April or November, not {s.From}"));
            new TyreHistory(g.Vehicle.OwnerId, g.Vehicle.Id, sets, installations).Check(); // the app's own checks pass
            // Every installation has its line in the tyre log, a swap with its id and first day (#288).
            Assert.Equal(installations.Select(s => (s.Id, TyreEntryKind.Swap, s.From, (Guid?)s.SetId)),
                g.TyreEntries!.OrderBy(e => e.Day).Select(e => (e.Id, e.Kind, e.Day, e.SetId)));
        });
    }

    [Fact]
    public void AFifthOfTheFillUps_CarryADiscount_TakenOffWhatWasPaid()
    {
        var logs = Generate(vehicles: 10, trashed: 0, refuelings: new IntRange(20, 20)).SelectMany(g => g.Refuelings).ToList();

        var discounted = logs.Where(l => l.Discount is not null).ToList();

        Assert.InRange(discounted.Count / (double)logs.Count, 0.12, 0.28);
        Assert.All(discounted, l =>
        {
            Assert.InRange(l.Discount!.Value, 0.01m, l.TotalCost!.Value * 0.031m); // one to three per cent of what was paid, to the cent
            Assert.True(l.PricePerUnitBeforeDiscount > l.PricePerUnit, "the pump's price is above the price paid");
        });
    }

    [Fact]
    public void GeneratedLogs_CarryTheirStoredConsumption_LikeLogsEnteredInTheApp()
    {
        var logs = Generate(vehicles: 5, trashed: 0, refuelings: new IntRange(30, 30)).SelectMany(g => g.Refuelings).ToList();

        var fullOnes = logs.Where(l => l.IsFullTank && l.Consumption is not null).ToList();
        Assert.NotEmpty(fullOnes);
        Assert.All(logs.Where(l => !l.IsFullTank || l.MissedPreviousFillUp), l => Assert.Null(l.Consumption));
        Assert.All(fullOnes, l => Assert.InRange(l.Consumption!.Value, 1m, 40m)); // plausible litres (or gallons) per 100 km (or miles)
    }

    [Fact]
    public void CreatesTheRequestedNumberOfLiveAndTrashedVehicles()
    {
        var all = Generate(30, 7);

        Assert.Equal(37, all.Count);
        Assert.Equal(30, all.Count(g => !g.Vehicle.IsDeleted));
        Assert.Equal(7, all.Count(g => g.Vehicle.IsDeleted));
    }

    [Fact]
    public void NothingRequested_NothingCreated() => Assert.Empty(Generate(0, 0));

    [Fact]
    public void OnlyTrashedVehiclesAreAllowedToo() => Assert.All(Generate(0, 4), g => Assert.True(g.Vehicle.IsDeleted));

    [Fact]
    public void EverythingBelongsToTheNoAuthOwner_AndNoUserIsInvolved()
    {
        var all = Generate();

        Assert.All(all, g =>
        {
            Assert.Equal(Guid.Empty, g.Vehicle.OwnerId);
            Assert.All(g.Refuelings, r => Assert.Equal(Guid.Empty, r.OwnerId));
        });
    }

    [Fact]
    public void TrashedVehicles_WereDeletedInThePast_WithinTwoMonths()
    {
        var trashed = Generate(5, 20).Where(g => g.Vehicle.IsDeleted).ToList();

        Assert.All(trashed, g =>
        {
            Assert.True(g.Vehicle.DeletedAt < Clock.GetUtcNow());
            Assert.True(g.Vehicle.DeletedAt > Clock.GetUtcNow().AddDays(-62));
        });
        Assert.True(trashed.Select(g => g.Vehicle.DeletedAt).Distinct().Count() > 1); // varied, not one timestamp
    }

    [Fact]
    public void VehicleDetails_AreVariedAndValid()
    {
        var all = Generate(200, 0);

        Assert.Equal(all.Count, all.Select(g => g.Vehicle.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(3, all.Select(g => g.Vehicle.FuelType).Distinct().Count());
        Assert.Contains(all, g => g.Vehicle.LicensePlate is null);
        Assert.All(all.Where(g => g.Vehicle.LicensePlate is not null), g => Assert.Matches("^[A-Z]{3}-[0-9]{3}$", g.Vehicle.LicensePlate!));
    }

    [Fact]
    public void RefuelingCounts_FollowTheRange_AndBelongToTheirVehicle()
    {
        var all = Generate(60, 10, new IntRange(5, 25));

        Assert.All(all, g =>
        {
            Assert.InRange(g.Refuelings.Count, 5, 25);
            Assert.All(g.Refuelings, r => Assert.Equal(g.Vehicle.Id, r.VehicleId));
        });
        Assert.True(all.Select(g => g.Refuelings.Count).Distinct().Count() > 3); // actually varies
    }

    [Fact]
    public void ExactCountAndZero()
    {
        Assert.All(Generate(10, 0, new IntRange(12, 12)), g => Assert.Equal(12, g.Refuelings.Count));
        Assert.All(Generate(10, 2, new IntRange(0, 0)), g => Assert.Empty(g.Refuelings));
    }

    [Fact]
    public void FuelLogs_AreChronologicalAndTheOdometerOnlyIncreases()
    {
        foreach (var g in Generate(80, 20, new IntRange(2, 40)))
        {
            var log = g.Refuelings;
            for (var i = 1; i < log.Count; i++)
            {
                Assert.True(log[i].Date > log[i - 1].Date, $"{g.Vehicle.Name}: dates must increase");
                Assert.True(log[i].Odometer > log[i - 1].Odometer, $"{g.Vehicle.Name}: odometer must increase");
            }
        }
    }

    [Fact]
    public void FuelLogs_NeverEndInTheFuture_AndStayRecent()
    {
        foreach (var g in Generate(80, 0, new IntRange(1, 40)))
        {
            Assert.All(g.Refuelings, r => Assert.True(r.Date <= Today));
            Assert.True(g.Refuelings[^1].Date >= Today.AddDays(-8)); // the last fill-up is recent
        }
    }

    private const double LitersPerUsGallon = 3.78541, LitersPerImperialGallon = 4.54609, MilesPerKm = 0.621371;

    private static double Liters(GeneratedVehicle g, Refueling r) => (double)r.Volume!.Value * g.Vehicle.Units.Volume switch
    {
        VolumeUnit.UsGallons => LitersPerUsGallon,
        VolumeUnit.ImperialGallons => LitersPerImperialGallon,
        _ => 1,
    };

    [Fact]
    public void Amounts_ArePositive_AndPlausibleInTheVehiclesOwnUnits()
    {
        foreach (var g in Generate(120, 10, new IntRange(5, 30)))
            foreach (var r in g.Refuelings)
            {
                Assert.InRange(Liters(g, r), 10.0, 80.0);
                Assert.True(r.TotalCost > 0);
                Assert.True(r.Odometer > 0);
                var pricePerLiter = (double)r.TotalCost / Liters(g, r);
                var (low, high) = r.Currency switch { "EUR" => (0.5, 2.5), "HUF" => (200.0, 1000.0), "GBP" => (0.4, 2.2), "USD" => (0.5, 2.6), _ => (0.0, double.MaxValue) };
                Assert.InRange(pricePerLiter, low, high);
            }
    }

    [Fact]
    public void ConsumptionFollowsThePerVehicleValue_WhateverTheUnits()
    {
        // Litres per 100 km between consecutive fill-ups stay near one value per vehicle (the generator allows +-10%, plus rounding),
        // also for vehicles that use miles and gallons. A gap with a fill-up that was not logged is left out, as the app leaves it out.
        foreach (var g in Generate(80, 0, new IntRange(10, 30)))
        {
            var log = g.Refuelings;
            var kmPerUnit = g.Vehicle.Units.Distance == DistanceUnit.Miles ? 1 / MilesPerKm : 1;
            var consumption = Enumerable.Range(1, log.Count - 1).Where(i => !log[i].MissedPreviousFillUp)
                .Select(i => Liters(g, log[i]) / ((log[i].Odometer!.Value - log[i - 1].Odometer!.Value) * kmPerUnit) * 100).ToList();

            Assert.InRange(consumption.Min(), 3.0, 15.0);
            Assert.True(consumption.Max() / consumption.Min() < 1.6, $"{g.Vehicle.Name}: consumption varies {consumption.Min():F1}..{consumption.Max():F1}");
        }
    }

    [Fact]
    public void VehiclesUseDifferentUnits_AndLogsDifferentCurrencies()
    {
        var all = Generate(300, 0, new IntRange(10, 10));

        Assert.Contains(all, g => g.Vehicle.Units == MeasurementUnits.Metric);
        Assert.Contains(all, g => g.Vehicle.Units.Distance == DistanceUnit.Miles && g.Vehicle.Units.Volume == VolumeUnit.UsGallons);
        Assert.Contains(all, g => g.Vehicle.Units.Distance == DistanceUnit.Miles && g.Vehicle.Units.Volume == VolumeUnit.ImperialGallons);
        var currencies = all.SelectMany(g => g.Refuelings).Select(r => r.Currency!).Distinct().Order().ToArray();
        Assert.Equal(["EUR", "GBP", "HUF", "USD"], currencies);
    }

    [Fact]
    public void ALogIsMostlyPaidInOneCurrency_AndSometimesAbroad()
    {
        var logs = Generate(200, 0, new IntRange(20, 20));

        var dominantShare = logs.Select(g => g.Refuelings.GroupBy(r => r.Currency).Max(c => c.Count()) / (double)g.Refuelings.Count).Average();
        Assert.InRange(dominantShare, 0.8, 0.97);
        Assert.Contains(logs, g => g.Refuelings.Select(r => r.Currency).Distinct().Count() > 1);
    }

    [Fact]
    public void ReadingsAndCosts_AreLinkedConsistentlyToTheirLogs()
    {
        foreach (var g in Generate(60, 10, new IntRange(2, 15)))
            foreach (var r in g.Refuelings)
            {
                Assert.Equal(r.OdometerReadingId, r.OdometerReading!.Id);
                Assert.Equal((g.Vehicle.Id, r.Date, r.Odometer), (r.OdometerReading.VehicleId, r.OdometerReading.Date, r.OdometerReading.Value));
                Assert.Equal(r.CostId, r.Cost!.Id);
                Assert.Equal((g.Vehicle.Id, r.Date, r.TotalCost, r.Currency), (r.Cost.VehicleId, r.Cost.Date, r.Cost.Amount, r.Cost.Currency));
                Assert.Equal(g.Vehicle.OwnerId, r.OdometerReading.OwnerId);
                Assert.Equal(g.Vehicle.OwnerId, r.Cost.OwnerId);
            }
    }

    [Fact]
    public void SameSeedSameData_DifferentSeedDifferentData()
    {
        static string Fingerprint(List<GeneratedVehicle> all) =>
            string.Join("|", all.Select(g => $"{g.Vehicle.Name}/{g.Vehicle.LicensePlate}/{g.Vehicle.FuelType}/{g.Vehicle.IsDeleted}/" +
                string.Join(",", g.Refuelings.Select(r => $"{r.Date:o}:{r.Volume}:{r.TotalCost}:{r.Currency}:{r.Odometer}:{r.IsFullTank}:{r.MissedPreviousFillUp}:{g.Vehicle.Units.Distance}"))));

        Assert.Equal(Fingerprint(Generate(seed: 5)), Fingerprint(Generate(seed: 5)));
        Assert.NotEqual(Fingerprint(Generate(seed: 5)), Fingerprint(Generate(seed: 6)));
    }

    [Fact]
    public void MostFillUpsAreFullTanks_ButNotAll()
    {
        var all = Generate(60, 0, new IntRange(20, 30)).SelectMany(g => g.Refuelings).ToList();

        var full = all.Count(r => r.IsFullTank) / (double)all.Count;
        Assert.InRange(full, 0.7, 0.95);
    }

    [Fact]
    public void NowAndThenAFillUpWasNotLogged_TheOdometerRunsOn_AndTheNextLogSaysSo()
    {
        var generated = Generate(60, 0, new IntRange(20, 30));
        var all = generated.SelectMany(g => g.Refuelings).ToList();

        var missed = all.Where(r => r.MissedPreviousFillUp).ToList();
        Assert.InRange(missed.Count / (double)all.Count, 0.005, 0.08);
        Assert.All(missed, r => Assert.Null(r.Consumption));
        Assert.All(generated, g => Assert.False(g.Refuelings[0].MissedPreviousFillUp)); // nothing before the first log to miss
    }
}
