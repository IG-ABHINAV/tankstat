using Tankstat.Application.Stats;
using Tankstat.Application.Users;
using Tankstat.Domain.Charts;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Api.GraphQL;

/// <param name="From">First day; only for the custom range.</param>
/// <param name="To">Last day; only for the custom range.</param>
/// <param name="Trend">A trend line over the values; null or left out is none (an older client sends none).</param>
/// <param name="RunningTotal">The sum so far in every period; null or left out is false.</param>
/// <param name="CompareYearEarlier">The same periods a year earlier beside them; null or left out is false.</param>
/// <param name="SecondMetric">A second measure in the same chart, on a scale of its own; null or left out is none.</param>
/// <param name="SecondKind">How the second measure is drawn (bars, a line or an area).</param>
public sealed record ChartConfigInput(ChartMetric Metric, ChartGrouping Grouping, ChartKind Kind, ChartRange Range, bool Stacked, DateOnly? From, DateOnly? To,
    ChartTrend? Trend = null, bool? RunningTotal = null, bool? CompareYearEarlier = null, ChartMetric? SecondMetric = null, ChartKind? SecondKind = null)
{
    public ChartConfig ToDomain() => new(Metric, Grouping, Kind, Range, Stacked, From, To, Trend ?? ChartTrend.None, RunningTotal ?? false, CompareYearEarlier ?? false, SecondMetric, SecondKind);
}

/// <param name="Id">Omit to create a chart, give it to change one.</param>
/// <param name="Shared">Show it to everyone who can see the vehicle's logs (needs edit access to them).</param>
public sealed record SaveChartInput(Guid? Id, Guid VehicleId, string Title, ChartConfigInput Config, bool Shared);

/// <summary>The saved chart exposes its recipe as plain fields; the numbers come from <c>vehicleChartData</c>.</summary>
public sealed class VehicleChartType : ObjectType<VehicleChart>
{
    protected override void Configure(IObjectTypeDescriptor<VehicleChart> descriptor)
    {
        descriptor.Ignore(c => c.Config);
        descriptor.Ignore(c => c.CreatedById);
    }
}

[ExtendObjectType<VehicleChart>]
public sealed class VehicleChartExtensions
{
    public Task<bool> GetCanEdit([Parent] VehicleChart chart, [Service] ChartService charts, CancellationToken ct) => charts.CanEditAsync(chart, ct);

    public async Task<UserRef?> GetCreatedBy([Parent] VehicleChart chart, [Service] IUserRepository users, CancellationToken ct) =>
        await users.FindByIdAsync(chart.CreatedById, ct) is { } user ? UserRef.From(user) : null;
}

[ExtendObjectType<Vehicle>]
public sealed class VehicleSummaryExtensions
{
    /// <summary>Key figures for the vehicle's card and dashboard.</summary>
    public Task<VehicleSummary?> GetSummary([Parent] Vehicle vehicle, [Service] StatsService stats, CancellationToken ct) => stats.SummaryAsync(vehicle.Id, ct);
}

[ExtendObjectType(OperationTypeNames.Query)]
public sealed class DashboardQueries
{
    /// <summary>The data of a chart, calculated from the vehicle's logs (costs per currency). Used for saved charts, presets and the builder's preview.</summary>
    public Task<ChartData> GetVehicleChartData(Guid vehicleId, ChartConfigInput config, [Service] StatsService stats, CancellationToken ct) =>
        stats.ChartAsync(vehicleId, config.ToDomain(), ct);

    /// <summary>The charts of a vehicle's dashboard: the user's own and the shared ones, oldest first.</summary>
    public Task<IReadOnlyList<VehicleChart>> GetVehicleCharts(Guid vehicleId, [Service] ChartService charts, CancellationToken ct) => charts.ListAsync(vehicleId, ct);
}

[ExtendObjectType(OperationTypeNames.Mutation)]
public sealed class DashboardMutations
{
    public Task<VehicleChart> SaveVehicleChart(SaveChartInput input, [Service] ChartService charts, CancellationToken ct)
    {
        var chart = new ChartInput(input.Title, input.Config.ToDomain(), input.Shared);
        return input.Id is { } id ? charts.UpdateAsync(id, chart, ct) : charts.CreateAsync(input.VehicleId, chart, ct);
    }

    public async Task<bool> DeleteVehicleChart(Guid id, [Service] ChartService charts, CancellationToken ct)
    {
        await charts.DeleteAsync(id, ct);
        return true;
    }
}
