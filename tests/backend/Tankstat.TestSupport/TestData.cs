using Tankstat.Domain.Measurements;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Vehicles;

namespace Tankstat.TestSupport;

/// <summary>Builds valid domain objects with sensible defaults, so a test only states what it is about.</summary>
public static class TestData
{
    public static Vehicle Vehicle(Guid ownerId, string name = "Car", string? licensePlate = null, FuelType fuelType = FuelType.Petrol, MeasurementUnits? units = null) =>
        Domain.Vehicles.Vehicle.Create(ownerId, name, licensePlate, fuelType, units ?? MeasurementUnits.Metric);

    /// <summary>A fill-up with its own odometer reading and cost, as the application creates it.</summary>
    public static Refueling Refueling(
        Guid ownerId, Guid createdById, Guid vehicleId, DateOnly date, decimal volume = 40, decimal totalCost = 60, long odometer = 1000,
        bool isFullTank = true, string currency = "EUR", string? note = null, bool missedPreviousFillUp = false, Guid? id = null, decimal? discount = null) =>
        Domain.Vehicles.Refueling.Create(
            ownerId, createdById, vehicleId, date, volume,
            Cost.Create(ownerId, vehicleId, date, totalCost, currency, discount),
            OdometerReading.Create(ownerId, vehicleId, date, odometer),
            isFullTank, missedPreviousFillUp, note, id: id);
}
