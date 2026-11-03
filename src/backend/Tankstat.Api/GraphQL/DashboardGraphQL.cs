using Tankstat.Application.Access;
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
    /// <summary>Whether the user may change the chart: its creator only.</summary>
    public async Task<bool> GetCanEdit([Parent] VehicleChart chart, [Service] AccessService access, CancellationToken ct) =>
        ChartService.MayChange(chart, await access.RequirePrincipalAsync(ct));

    /// <summary>Whether the user may delete the chart: its creator, or anyone with Delete access to the vehicle's logs.</summary>
    public async Task<bool> GetCanDelete([Parent] VehicleChart chart, [Service] AccessService access, LogLevelByVehicleLoader levels, CancellationToken ct)
    {
        var user = await access.RequirePrincipalAsync(ct);
        // The creator needs no level loaded (MayDelete holds for them anyway): the batch runs only for someone else's shared chart.
        return ChartService.MayChange(chart, user) || ChartService.MayDelete(chart, user, await levels.LoadAsync(chart.VehicleId, ct));
    }

    public Task<UserRef?> GetCreatedBy([Parent] VehicleChart chart, UserRefLoader users, CancellationToken ct) => users.LoadAsync(chart.CreatedById, ct);
}

[ExtendObjectType<Vehicle>]
public sealed class VehicleSummaryExtensions
{
    /// <summary>Key figures for the vehicle's card and dashboard.</summary>
    public Task<VehicleSummary?> GetSummary([Parent] Vehicle vehicle, [Service] StatsService stats, CancellationToken ct) => stats.SummaryAsync(vehicle.Id, ct);

    /// <summary>
    /// How the vehicle's dashboard is arranged: the vehicle's own and the user's, each null until someone made one (the Standard preset).
    /// Resolved per vehicle, so only the dashboard's query asks for it, never a list of vehicles. Null when the vehicle is not visible.
    /// </summary>
    public async Task<DashboardInfo?> GetDashboard([Parent] Vehicle vehicle, [Service] DashboardService dashboards, CancellationToken ct) =>
        await dashboards.GetAsync(vehicle.Id, ct) is { } view ? new DashboardInfo(DashboardLayoutInfo.From(view.Shared), DashboardLayoutInfo.From(view.Personal), view.CanChangeShared) : null;
}

/// <param name="Id">A key figure by its id, a ready-made chart as <c>preset:&lt;id&gt;</c>, a saved chart as <c>chart:&lt;id&gt;</c>.</param>
/// <param name="X">The column (0 to 3) and <paramref name="Y"/> the row of the tile's top left in a grid of four columns.</param>
/// <param name="W">Columns wide (1 to 4) and <paramref name="H"/> rows tall.</param>
/// <param name="Pinned">Never moved by dragging the other tiles.</param>
public sealed record DashboardTileInfo(string Id, int X, int Y, int W, int H, bool Pinned);

/// <param name="Tiles">The tiles shown (a saved chart none names is shown at the end).</param>
/// <param name="Hidden">The saved charts taken off the dashboard.</param>
public sealed record DashboardLayoutInfo(IReadOnlyList<DashboardTileInfo> Tiles, IReadOnlyList<string> Hidden, DateTimeOffset UpdatedAt)
{
    public static DashboardLayoutInfo? From(DashboardLayout? layout) =>
        layout is null ? null : new(layout.Tiles.Select(t => new DashboardTileInfo(t.Id, t.X, t.Y, t.W, t.H, t.Pinned)).ToList(), layout.Hidden, layout.UpdatedAt);
}

public sealed record DashboardTileInput(string Id, int X, int Y, int W, int H, bool? Pinned)
{
    public DashboardTile ToDomain() => new(Id, X, Y, W, H, Pinned ?? false);
}

/// <param name="Shared">The vehicle's dashboard, which everyone who sees it gets; null for the Standard preset.</param>
/// <param name="Personal">The user's own arrangement, which wins over the vehicle's for them alone; null when they made none.</param>
/// <param name="CanChangeShared">Whether the user may change the vehicle's dashboard (Edit access to its logs).</param>
public sealed record DashboardInfo(DashboardLayoutInfo? Shared, DashboardLayoutInfo? Personal, bool CanChangeShared);

/// <param name="Personal">The user's own arrangement (View access is enough), else the vehicle's (Edit access to its logs).</param>
public sealed record SaveDashboardLayoutInput(Guid VehicleId, bool Personal, IReadOnlyList<DashboardTileInput> Tiles, IReadOnlyList<string> Hidden);

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

    /// <summary>Saves the vehicle's dashboard or the user's own arrangement of it, replacing the one there was.</summary>
    public async Task<bool> SaveDashboardLayout(SaveDashboardLayoutInput input, [Service] DashboardService dashboards, CancellationToken ct)
    {
        await dashboards.SaveAsync(input.VehicleId, input.Personal, new DashboardLayoutValues(input.Tiles.Select(t => t.ToDomain()).ToList(), input.Hidden), ct);
        return true;
    }

    /// <summary>Forgets the vehicle's dashboard (back to the Standard preset) or the user's own (back to the vehicle's).</summary>
    public async Task<bool> ResetDashboardLayout(Guid vehicleId, bool personal, [Service] DashboardService dashboards, CancellationToken ct)
    {
        await dashboards.ResetAsync(vehicleId, personal, ct);
        return true;
    }
}
