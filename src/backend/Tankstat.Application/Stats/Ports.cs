using Tankstat.Domain.Charts;

namespace Tankstat.Application.Stats;

public interface IStatsRepository
{
    /// <summary>All live fill-ups, expenses and odometer readings of a vehicle, reduced to what statistics need.</summary>
    Task<StatsData> LoadAsync(Guid vehicleId, CancellationToken ct);

    /// <summary><see cref="LoadAsync"/> for many vehicles in a fixed number of queries (#311: a page of vehicle cards), every id in the answer.</summary>
    Task<IReadOnlyDictionary<Guid, StatsData>> LoadManyAsync(IReadOnlyCollection<Guid> vehicleIds, CancellationToken ct);
}

public interface IVehicleChartRepository
{
    /// <summary>The vehicle's charts that are shared, plus the ones created by <paramref name="userId"/>, oldest first.</summary>
    Task<IReadOnlyList<VehicleChart>> ListVisibleAsync(Guid vehicleId, Guid userId, CancellationToken ct);
    Task<int> CountByUserAsync(Guid vehicleId, Guid userId, CancellationToken ct);
    Task<VehicleChart?> FindAsync(Guid id, CancellationToken ct);
    Task AddAsync(VehicleChart chart, CancellationToken ct);
    Task UpdateAsync(VehicleChart chart, CancellationToken ct);
    Task RemoveAsync(VehicleChart chart, CancellationToken ct);
}

public interface IDashboardLayoutRepository
{
    Task<VehicleDashboardLayout?> FindSharedAsync(Guid vehicleId, CancellationToken ct);
    Task<UserDashboardLayout?> FindPersonalAsync(Guid userId, Guid vehicleId, CancellationToken ct);
    /// <summary>Adds the vehicle's layout or replaces it; of two first saves at once, the later wins.</summary>
    Task SaveSharedAsync(VehicleDashboardLayout layout, CancellationToken ct);
    /// <summary>Adds a person's layout or replaces it; of two first saves at once, the later wins.</summary>
    Task SavePersonalAsync(UserDashboardLayout layout, CancellationToken ct);
    Task RemoveSharedAsync(Guid vehicleId, CancellationToken ct);
    Task RemovePersonalAsync(Guid userId, Guid vehicleId, CancellationToken ct);
}
