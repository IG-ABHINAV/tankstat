using Tankstat.Domain;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Vehicles;
using Tankstat.TestSupport;

namespace Tankstat.Domain.UnitTests;

public class RefuelingTests
{
    private static readonly DateOnly Day = new(2026, 10, 1);
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Creator = Guid.NewGuid();

    [Fact]
    public void Create_SetsFields_AndLinksItsOwnReadingAndCost()
    {
        var vehicleId = Guid.NewGuid();

        var r = TestData.Refueling(
            Owner, Creator, vehicleId, Day, volume: 42.5m, totalCost: 80.75m, odometer: 12345, isFullTank: true, currency: "huf", note: "  road trip ",
            missedPreviousFillUp: true);

        Assert.Equal((Owner, Creator, vehicleId, Day), (r.OwnerId, r.CreatedById, r.VehicleId, r.Date));
        Assert.Equal((42.5m, 80.75m, "HUF", 12345L, true, true, "road trip"), (r.Volume, r.TotalCost, r.Currency, r.Odometer, r.IsFullTank, r.MissedPreviousFillUp, r.Note));
        Assert.Equal(r.OdometerReading!.Id, r.OdometerReadingId);
        Assert.Equal(r.Cost!.Id, r.CostId);
        Assert.Equal((vehicleId, Day, 12345L), (r.OdometerReading.VehicleId, r.OdometerReading.Date, r.OdometerReading.Value));
        Assert.Equal((vehicleId, Day, 80.75m, "HUF"), (r.Cost.VehicleId, r.Cost.Date, r.Cost.Amount, r.Cost.Currency));
    }

    [Fact]
    public void PricePerUnit_IsDerivedFromCostAndVolume() =>
        Assert.Equal(1.9m, TestData.Refueling(Owner, Creator, Guid.NewGuid(), Day, volume: 40, totalCost: 76).PricePerUnit);

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    public void Create_RejectsAVolumeThatIsNotPositive(double volume, double cost) =>
        Assert.Equal("refueling.volumePositive",
            Assert.Throws<DomainException>(() => TestData.Refueling(Owner, Creator, Guid.NewGuid(), Day, (decimal)volume, (decimal)cost)).Key);

    [Fact]
    public void Create_RejectsNegativeMoneyAndOdometer_AndBadCurrencies()
    {
        Assert.Equal("cost.negative", Assert.Throws<DomainException>(() => TestData.Refueling(Owner, Creator, Guid.NewGuid(), Day, totalCost: -0.01m)).Key);
        Assert.Equal("odometer.negative", Assert.Throws<DomainException>(() => TestData.Refueling(Owner, Creator, Guid.NewGuid(), Day, odometer: -1)).Key);
        Assert.Equal("money.currencyInvalid", Assert.Throws<DomainException>(() => TestData.Refueling(Owner, Creator, Guid.NewGuid(), Day, currency: "EURO")).Key);
        Assert.Equal("money.currencyInvalid", Assert.Throws<DomainException>(() => TestData.Refueling(Owner, Creator, Guid.NewGuid(), Day, currency: "")).Key);
    }

    [Fact]
    public void Create_RejectsALongNote() =>
        Assert.Equal("refueling.noteTooLong", Assert.Throws<DomainException>(() => TestData.Refueling(Owner, Creator, Guid.NewGuid(), Day, note: new string('x', 501))).Key);

    [Fact]
    public void Create_RejectsReadingsAndCostsOfAnotherVehicle()
    {
        var vehicle = Guid.NewGuid();
        var other = Guid.NewGuid();

        var wrongReading = Assert.Throws<DomainException>(() => Refueling.Create(Owner, Creator, vehicle, Day, 10,
            Cost.Create(Owner, vehicle, Day, 1, "EUR"), OdometerReading.Create(Owner, other, Day, 1), true, false));
        var wrongCost = Assert.Throws<DomainException>(() => Refueling.Create(Owner, Creator, vehicle, Day, 10,
            Cost.Create(Owner, other, Day, 1, "EUR"), OdometerReading.Create(Owner, vehicle, Day, 1), true, false));

        Assert.Equal("refueling.wrongVehicle", wrongReading.Key);
        Assert.Equal("refueling.wrongVehicle", wrongCost.Key);
    }

    [Fact]
    public void Update_KeepsTheReadingAndTheCostInStep()
    {
        var r = TestData.Refueling(Owner, Creator, Guid.NewGuid(), Day);
        var readingId = r.OdometerReadingId;
        var costId = r.CostId;

        r.Update(Day.AddDays(2), 30, 55, "USD", 2000, false, true, null);

        Assert.Equal((Day.AddDays(2), 30m, 55m, "USD", 2000L, false, true), (r.Date, r.Volume, r.TotalCost, r.Currency, r.Odometer, r.IsFullTank, r.MissedPreviousFillUp));
        Assert.Equal((readingId, Day.AddDays(2), 2000L), (r.OdometerReading!.Id, r.OdometerReading.Date, r.OdometerReading.Value));
        Assert.Equal((costId, Day.AddDays(2), 55m, "USD"), (r.Cost!.Id, r.Cost.Date, r.Cost.Amount, r.Cost.Currency));
    }

    [Fact]
    public void Update_StoresTheDiscount_AndWorksOutThePricePaidAndThePumpsPrice()
    {
        var r = TestData.Refueling(Owner, Creator, Guid.NewGuid(), Day);

        r.Update(Day, 43, 34744, "HUF", 1000, true, false, null, discount: 430); // the receipt of #204

        Assert.Equal((34744m, 430m, 35174m), (r.TotalCost, r.Discount, r.Cost!.AmountBeforeDiscount));
        Assert.Equal((808m, 818m), (r.PricePerUnit, r.PricePerUnitBeforeDiscount));

        r.Update(Day, 43, 34744, "HUF", 1000, true, false, null); // none sent: the discount goes, as a total left out would
        Assert.Null(r.Discount);
        Assert.Equal(r.PricePerUnit, r.PricePerUnitBeforeDiscount);
    }

    [Fact]
    public void Discount_NeedsAnAmount_IsNeverNegative_AndZeroIsNone()
    {
        var r = TestData.Refueling(Owner, Creator, Guid.NewGuid(), Day);

        Assert.Equal("cost.discountWithoutAmount", Assert.Throws<DomainException>(() => r.Update(Day, 40, null, "EUR", 1000, true, false, null, readingPhotos: true, discount: 5)).Key);
        Assert.Equal("cost.discountNegative", Assert.Throws<DomainException>(() => r.Update(Day, 40, 60, "EUR", 1000, true, false, null, discount: -1)).Key);
        Assert.Equal("cost.discountNegative", Assert.Throws<DomainException>(() => TestData.Refueling(Owner, Creator, Guid.NewGuid(), Day, discount: -1)).Key);
        Assert.Null(TestData.Refueling(Owner, Creator, Guid.NewGuid(), Day, discount: 0).Discount);
        Assert.Equal(4.3m, TestData.Refueling(Owner, Creator, Guid.NewGuid(), Day, discount: 4.3m).Discount); // Create keeps it: it re-normalises the cost with the discount
    }

    [Fact]
    public void Trash_And_Restore_CarryTheReadingAndCostAlong()
    {
        var r = TestData.Refueling(Owner, Creator, Guid.NewGuid(), Day);
        var now = new DateTimeOffset(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);

        r.MarkDeleted(now);
        Assert.True(r.IsDeleted);
        Assert.Equal(now, r.DeletedAt);
        Assert.True(r.OdometerReading!.IsDeleted);
        Assert.True(r.Cost!.IsDeleted);

        r.Restore();
        Assert.False(r.IsDeleted);
        Assert.False(r.OdometerReading!.IsDeleted);
        Assert.False(r.Cost.IsDeleted);
    }

    [Fact]
    public void Trash_Rules()
    {
        var r = TestData.Refueling(Owner, Creator, Guid.NewGuid(), Day);
        Assert.Equal("refueling.notTrashed", Assert.Throws<DomainException>(() => r.Restore()).Key);

        r.MarkDeleted(DateTimeOffset.UtcNow);
        Assert.Equal("refueling.alreadyTrashed", Assert.Throws<DomainException>(() => r.MarkDeleted(DateTimeOffset.UtcNow)).Key);
        Assert.Equal("refueling.trashedCannotEdit", Assert.Throws<DomainException>(() => r.Update(Day, 1, 1, "EUR", 1, true, false, null)).Key);
    }

    private static Refueling Waiting(Guid vehicle, long? odometer = null, decimal? volume = null, decimal? total = null) =>
        Refueling.Create(Owner, Creator, vehicle, Day, volume, total is { } t ? Cost.Create(Owner, vehicle, Day, t, "EUR") : null,
            odometer is { } o ? OdometerReading.Create(Owner, vehicle, Day, o) : null, isFullTank: true, missedPreviousFillUp: false, readingPhotos: true);

    [Fact]
    public void ValuesMayBeLeftEmpty_OnlyWhileAPhotoIsBeingRead()
    {
        var vehicle = Guid.NewGuid();

        var error = Assert.Throws<DomainException>(() => Refueling.Create(Owner, Creator, vehicle, Day, 10, Cost.Create(Owner, vehicle, Day, 20, "EUR"), null, true, false));
        var waiting = Waiting(vehicle, volume: 10, total: 20);
        var complete = Refueling.Create(Owner, Creator, vehicle, Day, 10, Cost.Create(Owner, vehicle, Day, 20, "EUR"), OdometerReading.Create(Owner, vehicle, Day, 5), true, false, readingPhotos: true);

        Assert.Equal("log.valuesRequired", error.Key);
        Assert.Equal((ReviewState.AwaitingPhotos, LogValues.Odometer, (long?)null), (waiting.ReviewState, waiting.Missing, waiting.Odometer));
        Assert.Equal(ReviewState.None, complete.ReviewState); // nothing left for the photo to fill in
    }

    [Fact]
    public void FillFromPhoto_FillsOnlyEmptyValues_AndAsksForAReview()
    {
        var vehicle = Guid.NewGuid();
        var log = Waiting(vehicle, volume: 40);

        var changes = log.FillFromPhoto(new PhotoValues(Odometer: 123456, Volume: 99, Total: 80, Currency: "HUF"));
        var moved = log.FinishReading(readingPhotos: false);

        Assert.Equal((123456L, 40m, 80m, "HUF"), (log.Odometer, log.Volume, log.TotalCost, log.Currency));
        Assert.Equal(LogValues.Odometer | LogValues.Total, log.FilledFromPhoto);
        Assert.Same(log.OdometerReading, changes.CreatedReading);
        Assert.Same(log.Cost, changes.CreatedCost);
        Assert.Equal((vehicle, Day), (log.OdometerReading!.VehicleId, log.OdometerReading.Date));
        Assert.True(moved);
        Assert.Equal(ReviewState.NeedsReview, log.ReviewState);
    }

    [Fact]
    public void FinishReading_WaitsWhileAPhotoIsRead_ThenTellsWhatIsStillMissing()
    {
        var log = Waiting(Guid.NewGuid());
        log.FillFromPhoto(new PhotoValues(Odometer: 1000, Volume: null, Total: null, Currency: null));

        Assert.False(log.FinishReading(readingPhotos: true));
        Assert.Equal(ReviewState.AwaitingPhotos, log.ReviewState);

        Assert.True(log.FinishReading(readingPhotos: false));
        Assert.Equal((ReviewState.Incomplete, LogValues.Volume | LogValues.Total), (log.ReviewState, log.Missing));
    }

    [Fact]
    public void FillFromPhoto_LeavesOutValuesALogCannotTake_AndDoesNothingOnceNotWaiting()
    {
        var log = Waiting(Guid.NewGuid());

        log.FillFromPhoto(new PhotoValues(Odometer: -1, Volume: 0, Total: -3, Currency: "EUR"));
        Assert.Equal(LogValues.None, log.FilledFromPhoto);

        log.Update(Day, 10, 20, "EUR", 300, true, false, null);
        Assert.Equal(LinkedChanges.None, log.FillFromPhoto(new PhotoValues(999, 99, 99, "EUR")));
        Assert.Equal(300L, log.Odometer);
    }

    [Fact]
    public void ALogStillWaitingForItsValues_HasNoPricePerUnit_AndGoesToTheTrashAndBackWithoutThem()
    {
        var vehicle = Guid.NewGuid();
        var noVolume = Waiting(vehicle, total: 20);
        var noCost = Waiting(vehicle, volume: 10);

        Assert.Equal(((decimal?)null, (decimal?)null), (noVolume.PricePerUnit, noCost.PricePerUnit));

        noCost.MarkDeleted(DateTimeOffset.UtcNow);
        Assert.True(noCost.IsDeleted);
        noCost.Restore();
        Assert.Equal((false, (Cost?)null, (OdometerReading?)null), (noCost.IsDeleted, noCost.Cost, noCost.OdometerReading));
    }

    [Fact]
    public void Update_WhileAPhotoIsRead_CanLetGoOfTheOdometer_AndHandsTheReadingBack()
    {
        var log = TestData.Refueling(Owner, Creator, Guid.NewGuid(), Day);
        var reading = log.OdometerReading;

        var changes = log.Update(Day, 40, 60, "EUR", null, true, false, null, readingPhotos: true);

        Assert.Same(reading, changes.RemovedReading);
        Assert.Equal(((long?)null, (Guid?)null, ReviewState.AwaitingPhotos), (log.Odometer, log.OdometerReadingId, log.ReviewState));
    }

    [Fact]
    public void Update_ByAPerson_FinishesTheReview_AndCanLetGoOfValuesOnlyWhileReading()
    {
        var log = Waiting(Guid.NewGuid());
        log.FillFromPhoto(new PhotoValues(500, 10, 20, "EUR"));
        log.FinishReading(false);

        var none = log.Update(Day, 10, 20, "EUR", 500, true, false, null);
        Assert.Equal((ReviewState.None, LogValues.None, LinkedChanges.None), (log.ReviewState, log.FilledFromPhoto, none));

        Assert.Equal("log.valuesRequired", Assert.Throws<DomainException>(() => log.Update(Day, 10, null, null, 500, true, false, null)).Key);
        var cost = log.Cost;
        var removed = log.Update(Day, 10, null, null, 500, true, false, null, readingPhotos: true);
        Assert.Same(cost, removed.RemovedCost);
        Assert.Equal(ReviewState.AwaitingPhotos, log.ReviewState);
    }
}

public class OdometerCostAndUnitsTests
{
    [Fact]
    public void Odometer_IsAnyNonNegativeNumber_WithNoUpperLimit()
    {
        Assert.Equal(0, OdometerValue.From(0).Value);
        Assert.Equal(long.MaxValue, OdometerValue.From(long.MaxValue).Value);
        Assert.Equal("123456", OdometerValue.From(123456).ToString()); // shown as the plain number
        Assert.Equal("odometer.negative", Assert.Throws<DomainException>(() => OdometerValue.From(-1)).Key);
    }

    [Fact]
    public void Reading_UpdatesAndFollowsItsOwnersLifecycle()
    {
        var reading = OdometerReading.Create(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), 100);

        reading.Update(new DateOnly(2026, 2, 1), 250);
        reading.MarkDeleted(DateTimeOffset.UtcNow);
        Assert.True(reading.IsDeleted);
        reading.Restore();

        Assert.Equal((new DateOnly(2026, 2, 1), 250L, false), (reading.Date, reading.Value, reading.IsDeleted));
        Assert.Equal("odometer.negative", Assert.Throws<DomainException>(() => reading.Update(reading.Date, -5)).Key);
    }

    [Fact]
    public void Cost_StoresAmountAndCurrencyTogether()
    {
        var cost = Cost.Create(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), 12.5m, " eur ");

        Assert.Equal((12.5m, "EUR"), (cost.Amount, cost.Currency));
        cost.Update(new DateOnly(2026, 1, 2), 0, "huf");
        Assert.Equal((0m, "HUF"), (cost.Amount, cost.Currency)); // free is fine
        Assert.Equal("cost.negative", Assert.Throws<DomainException>(() => cost.Update(cost.Date, -1, "EUR")).Key);
        Assert.Equal("money.currencyInvalid", Assert.Throws<DomainException>(() => cost.Update(cost.Date, 1, "E1R")).Key);
    }

    [Theory]
    [InlineData("EUR", "EUR")]
    [InlineData(" usd ", "USD")]
    [InlineData("huf", "HUF")]
    public void CurrencyCodes_AreNormalized(string input, string expected) => Assert.Equal(expected, CurrencyCode.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("12A")]
    public void CurrencyCodes_AreValidated(string? input) =>
        Assert.Equal("money.currencyInvalid", Assert.Throws<DomainException>(() => CurrencyCode.Normalize(input)).Key);

    [Fact]
    public void Units_CoverDistanceAndVolume_AndRejectUnknownValues()
    {
        var us = MeasurementUnits.Create(DistanceUnit.Miles, VolumeUnit.UsGallons);

        Assert.Equal((DistanceUnit.Miles, VolumeUnit.UsGallons), (us.Distance, us.Volume));
        Assert.Equal((DistanceUnit.Kilometers, VolumeUnit.Liters), (MeasurementUnits.Metric.Distance, MeasurementUnits.Metric.Volume));
        Assert.Equal(MeasurementUnits.Metric, MeasurementUnits.Create(DistanceUnit.Kilometers, VolumeUnit.Liters)); // value equality
        Assert.Equal("vehicle.unknownUnit", Assert.Throws<DomainException>(() => MeasurementUnits.Create((DistanceUnit)9, VolumeUnit.Liters)).Key);
        Assert.Equal("vehicle.unknownUnit", Assert.Throws<DomainException>(() => MeasurementUnits.Create(DistanceUnit.Miles, (VolumeUnit)9)).Key);
    }

    [Fact]
    public void AVehicle_KeepsItsUnits()
    {
        var v = TestData.Vehicle(Guid.NewGuid(), units: MeasurementUnits.Create(DistanceUnit.Miles, VolumeUnit.ImperialGallons));

        Assert.Equal(VolumeUnit.ImperialGallons, v.Units.Volume);
        v.Update("Renamed", null, FuelType.Diesel, MeasurementUnits.Metric);
        Assert.Equal(DistanceUnit.Kilometers, v.Units.Distance);
    }
}
