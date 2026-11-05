using Tankstat.Application.Access;
using Tankstat.Application.Auth;
using Tankstat.Application.Vehicles;
using Tankstat.Domain;
using Tankstat.Domain.Access;
using Tankstat.Domain.Charts;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.Stats;

/// <summary>
/// Statistics of a vehicle's logs (charts and key figures). Anyone who may view the vehicle's logs may see them; for everyone else the
/// vehicle does not exist.
/// </summary>
public sealed class StatsService(IVehicleRepository vehicles, IStatsRepository stats, AccessService access, TimeProvider clock)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<ChartData> ChartAsync(Guid vehicleId, ChartConfig config, CancellationToken ct)
    {
        config = config.Validated();
        await RequireVisibleAsync(vehicleId, ct);
        return StatsCalculator.Chart(config, await stats.LoadAsync(vehicleId, ct), Today);
    }

    /// <summary>
    /// A trend figure over its own period (#230), checked against today first (<see cref="TrendPeriod.Validated"/>), in the main currency the
    /// summary headlines; <c>vehicle.notFound</c> for a vehicle that is not visible, as a chart's.
    /// </summary>
    public async Task<TrendFigureData> TrendAsync(Guid vehicleId, TrendFigure figure, TrendPeriod period, CancellationToken ct)
    {
        var today = Today;
        period = period.Validated(today);
        // A trend figure always compares: a period without a comparison is a key figure's (vehicleFigure, #272), never an older client's.
        if (!Enum.IsDefined(figure) || period.Against == TrendComparison.None) throw new DomainException("trend.invalid", "This period is not valid.");
        await RequireVisibleAsync(vehicleId, ct);
        var data = await stats.LoadAsync(vehicleId, ct);
        return StatsCalculator.Trend(data, figure, period, today, StatsCalculator.MainCurrency(data, today));
    }

    /// <summary>
    /// A key figure (#272): <paramref name="measure"/> over <paramref name="period"/>, compared or not, or a point in time without one, checked
    /// first (<see cref="TrendPeriod.Validated"/>; <c>trend.invalid</c> for an unknown measure, a period with a point in time or none with a
    /// measure over one), in the main currency the summary headlines; <c>vehicle.notFound</c> for a vehicle that is not visible.
    /// </summary>
    public async Task<FigureData> FigureAsync(Guid vehicleId, FigureMeasure measure, TrendPeriod? period, CancellationToken ct)
    {
        var today = Today;
        period = period?.Validated(today);
        if (!Enum.IsDefined(measure) || measure.IsPointInTime() != period is null) throw new DomainException("trend.invalid", "This period is not valid.");
        await RequireVisibleAsync(vehicleId, ct);
        var data = await stats.LoadAsync(vehicleId, ct);
        return StatsCalculator.Figure(data, measure, period, today, StatsCalculator.MainCurrency(data, today));
    }

    /// <summary>Null when the vehicle is not visible.</summary>
    public async Task<VehicleSummary?> SummaryAsync(Guid vehicleId, CancellationToken ct) =>
        await IsVisibleAsync(vehicleId, ct) ? StatsCalculator.Summary(await stats.LoadAsync(vehicleId, ct), Today) : null;

    private async Task<bool> IsVisibleAsync(Guid vehicleId, CancellationToken ct)
    {
        var vehicle = await vehicles.FindAsync(vehicleId, ct);
        return vehicle is not null && await access.LogLevelAsync(vehicle, ct) >= AccessLevel.View;
    }

    private async Task RequireVisibleAsync(Guid vehicleId, CancellationToken ct)
    {
        if (!await IsVisibleAsync(vehicleId, ct)) throw new NotFoundException("vehicle.notFound", $"Vehicle {vehicleId} does not exist.", new { Id = vehicleId });
    }
}
