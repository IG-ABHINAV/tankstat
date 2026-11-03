using Microsoft.Extensions.Logging;
using Tankstat.Application.Access;
using Tankstat.Application.Auth;
using Tankstat.Application.Vehicles;
using Tankstat.Domain;
using Tankstat.Domain.Access;
using Tankstat.Domain.Charts;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.Stats;

public sealed record ChartInput(string Title, ChartConfig Config, bool Shared);

/// <summary>
/// The charts users compose for a vehicle's dashboard. Anyone who can view the vehicle's logs may make charts for themselves; sharing a chart
/// with everyone who sees the vehicle needs Edit access to its logs. A chart can be changed by its creator (and deleted by anyone with Delete access).
/// </summary>
public sealed class ChartService(IVehicleRepository vehicles, IVehicleChartRepository charts, AccessService access, TimeProvider clock, ILogger<ChartService> logger)
{
    public const int MaxPerUserAndVehicle = 30;

    public async Task<IReadOnlyList<VehicleChart>> ListAsync(Guid vehicleId, CancellationToken ct)
    {
        var user = await access.RequirePrincipalAsync(ct);
        var (vehicle, _) = await VisibleAsync(vehicleId, ct, throwIfMissing: false);
        return vehicle is null ? [] : await charts.ListVisibleAsync(vehicleId, user.Id, ct);
    }

    public async Task<VehicleChart> CreateAsync(Guid vehicleId, ChartInput input, CancellationToken ct)
    {
        var user = await access.RequirePrincipalAsync(ct);
        var (_, level) = await VisibleAsync(vehicleId, ct);
        RequireShareRight(input.Shared, level);
        if (await charts.CountByUserAsync(vehicleId, user.Id, ct) >= MaxPerUserAndVehicle)
            throw new DomainException("chart.limit", $"You can have at most {MaxPerUserAndVehicle} charts for a vehicle.", new { Max = MaxPerUserAndVehicle });

        var chart = VehicleChart.Create(vehicleId, user.Id, input.Title, input.Config, input.Shared, clock.GetUtcNow());
        await charts.AddAsync(chart, ct);
        logger.LogDebug("User {UserId} created chart {ChartId} for vehicle {VehicleId}", user.Id, chart.Id, vehicleId);
        return chart;
    }

    public async Task<VehicleChart> UpdateAsync(Guid id, ChartInput input, CancellationToken ct)
    {
        var (chart, level, user) = await EditableAsync(id, ct);
        RequireShareRight(input.Shared, level);
        chart.Update(input.Title, input.Config, input.Shared);
        await charts.UpdateAsync(chart, ct);
        logger.LogDebug("Chart {ChartId} updated", chart.Id);
        return chart;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var (chart, _, _) = await EditableAsync(id, ct, allowDeleteLevel: true);
        await charts.RemoveAsync(chart, ct);
        logger.LogDebug("Chart {ChartId} deleted", chart.Id);
    }

    // The two rights on a chart, decided here alone (#210): the mutations refuse by them and GraphQL's `canEdit`/`canDelete` say them, so
    // the UI never offers what the server refuses.

    /// <summary>Whether the user may change the chart: its creator only.</summary>
    public static bool MayChange(VehicleChart chart, Principal user) => chart.CreatedById == user.Id;

    /// <summary>Whether the user may delete the chart: its creator, or anyone with Delete access to the vehicle's logs (<paramref name="level"/>).</summary>
    public static bool MayDelete(VehicleChart chart, Principal user, AccessLevel level) => MayChange(chart, user) || level >= AccessLevel.Delete;

    private static void RequireShareRight(bool shared, AccessLevel level)
    {
        if (shared && level < AccessLevel.Edit) throw new ForbiddenException("chart.needEditToShare", "You need edit access to the vehicle's logs to share a chart.");
    }

    private async Task<(Vehicle? Vehicle, AccessLevel Level)> VisibleAsync(Guid vehicleId, CancellationToken ct, bool throwIfMissing = true)
    {
        var vehicle = await vehicles.FindAsync(vehicleId, ct);
        var level = vehicle is null ? AccessLevel.None : await access.LogLevelAsync(vehicle, ct);
        if (vehicle is not null && level >= AccessLevel.View) return (vehicle, level);
        return throwIfMissing ? throw new NotFoundException("vehicle.notFound", $"Vehicle {vehicleId} does not exist.", new { Id = vehicleId }) : (null, AccessLevel.None);
    }

    /// <summary>The chart, if the user may see it; changing it is for its creator (or, for deleting, anyone with Delete access).</summary>
    private async Task<(VehicleChart Chart, AccessLevel Level, Principal User)> EditableAsync(Guid id, CancellationToken ct, bool allowDeleteLevel = false)
    {
        var user = await access.RequirePrincipalAsync(ct);
        var chart = await charts.FindAsync(id, ct);
        var vehicle = chart is null ? null : await vehicles.FindAsync(chart.VehicleId, ct);
        var level = vehicle is null ? AccessLevel.None : await access.LogLevelAsync(vehicle, ct);
        if (chart is null || level < AccessLevel.View || !(chart.IsShared || chart.CreatedById == user.Id))
            throw new NotFoundException("chart.notFound", $"Chart {id} does not exist.", new { Id = id });
        if (!(allowDeleteLevel ? MayDelete(chart, user, level) : MayChange(chart, user)))
            throw new ForbiddenException("chart.notYours", "Only the creator can change this chart.");
        return (chart, level, user);
    }
}
