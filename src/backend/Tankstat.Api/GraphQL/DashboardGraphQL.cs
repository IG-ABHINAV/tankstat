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

/// <summary>
/// The saved chart exposes its recipe as plain fields; the numbers come from <c>vehicleChartData</c>. It is the type an older app reads, so it
/// stays as it was (#273): only charts reach it (<c>vehicleCharts</c>, <c>saveVehicleChart</c>), its chart fields stay non-null although the
/// columns are nullable for a saved key figure, and a key figure's fields are not on it (<c>SavedWidget</c> has both kinds).
/// </summary>
public sealed class VehicleChartType : ObjectType<VehicleChart>
{
    protected override void Configure(IObjectTypeDescriptor<VehicleChart> descriptor)
    {
        descriptor.Ignore(c => c.Config);
        descriptor.Ignore(c => c.CreatedById);
        // Declared fields come first: the three before the pinned ones keep the type's field order as it was.
        descriptor.Field(c => c.Id);
        descriptor.Field(c => c.VehicleId);
        descriptor.Field(c => c.Title);
        descriptor.Field(c => c.Metric).Type<NonNullType<EnumType<ChartMetric>>>();
        descriptor.Field(c => c.Grouping).Type<NonNullType<EnumType<ChartGrouping>>>();
        descriptor.Field(c => c.Kind).Type<NonNullType<EnumType<ChartKind>>>();
        descriptor.Field(c => c.Range).Type<NonNullType<EnumType<ChartRange>>>();
        descriptor.Ignore(c => c.Widget);
        descriptor.Ignore(c => c.Measure);
        descriptor.Ignore(c => c.Period);
        descriptor.Ignore(c => c.Sparkline);
        descriptor.Ignore(c => c.Template);
        descriptor.Ignore(c => c.Figure);
    }
}

/// <summary>A chart's recipe as data (a saved widget's, a tile's): its fields only, never the domain's check.</summary>
public sealed class ChartConfigType : ObjectType<ChartConfig>
{
    protected override void Configure(IObjectTypeDescriptor<ChartConfig> descriptor) => descriptor.Ignore(c => c.Validated());
}

/// <summary>A key figure's recipe (#273): its fields only, never the domain's check.</summary>
public sealed class FigureRecipeType : ObjectType<FigureRecipe>
{
    protected override void Configure(IObjectTypeDescriptor<FigureRecipe> descriptor) => descriptor.Ignore(r => r.Validated(default));
}

/// <summary>A <c>tile:</c> tile's own recipe (#273): its fields only, never the domain's check.</summary>
public sealed class TileRecipeType : ObjectType<TileRecipe>
{
    protected override void Configure(IObjectTypeDescriptor<TileRecipe> descriptor) => descriptor.Ignore(r => r.Validated(default));
}

/// <param name="Period">The period, compared with what; null (or left out) for a point in time (the odometer, the last fill-up).</param>
/// <param name="Sparkline">Draw a sparkline under the figure; null or left out is false.</param>
/// <param name="Template">The built-in figure it was made from (<c>thisMonth</c>, <c>spendingTrend</c>, …), which words its default title.</param>
public sealed record FigureRecipeInput(FigureMeasure Measure, TrendPeriodInput? Period = null, bool? Sparkline = null, string? Template = null)
{
    public FigureRecipe ToDomain() => new(Measure, Period?.ToDomain(), Sparkline ?? false, Template);
}

/// <summary>A <c>tile:</c> tile's own recipe (#273): a chart's <paramref name="Chart"/> or a key figure's <paramref name="Figure"/>, the one its kind names.</summary>
/// <param name="Title">A chart's title (required); a key figure's may be left out for its default title.</param>
public sealed record TileRecipeInput(WidgetKind Widget, string? Title, ChartConfigInput? Chart = null, FigureRecipeInput? Figure = null)
{
    public TileRecipe ToDomain() => new(Widget, Title, Chart?.ToDomain(), Figure?.ToDomain());
}

/// <summary>A saved widget to create or change (#273): a chart's <paramref name="Chart"/> or a key figure's <paramref name="Figure"/>, exactly one.</summary>
/// <param name="Id">Omit to create one, give it to change one (within its kind).</param>
/// <param name="Shared">Show it to everyone who can see the vehicle's logs (needs edit access to them).</param>
public sealed record SaveWidgetInput(Guid? Id, Guid VehicleId, string Title, ChartConfigInput? Chart, FigureRecipeInput? Figure, bool Shared);

/// <summary>
/// A saved widget of either kind (#273): a chart (<c>chart</c>) or a key figure (<c>figure</c>), the one <c>widget</c> names. A dashboard tile
/// names it as <c>chart:&lt;id&gt;</c>. The numbers come from <c>vehicleChartData</c> and <c>vehicleFigure</c>.
/// </summary>
public sealed class SavedWidget(VehicleChart entity)
{
    [GraphQLIgnore] public VehicleChart Entity => entity;
    public Guid Id => entity.Id;
    public string Title => entity.Title;
    public WidgetKind Widget => entity.Widget;
    /// <summary>A chart's recipe; null for a key figure.</summary>
    public ChartConfig? Chart => entity.Config;
    /// <summary>A key figure's recipe; null for a chart.</summary>
    public FigureRecipe? Figure => entity.Figure;
    /// <summary>Shown to everyone who can see the vehicle's logs, not only to its creator.</summary>
    public bool IsShared => entity.IsShared;
    public DateTimeOffset CreatedAt => entity.CreatedAt;
}

[ExtendObjectType<SavedWidget>]
public sealed class SavedWidgetExtensions
{
    /// <summary>Whether the user may change it: its creator only.</summary>
    public Task<bool> GetCanEdit([Parent] SavedWidget widget, [Service] AccessService access, CancellationToken ct) =>
        VehicleChartExtensions.CanEditAsync(widget.Entity, access, ct);

    /// <summary>Whether the user may delete it: its creator, or anyone with Delete access to the vehicle's logs.</summary>
    public Task<bool> GetCanDelete([Parent] SavedWidget widget, [Service] AccessService access, LogLevelByVehicleLoader levels, CancellationToken ct) =>
        VehicleChartExtensions.CanDeleteAsync(widget.Entity, access, levels, ct);

    public Task<UserRef?> GetCreatedBy([Parent] SavedWidget widget, UserRefLoader users, CancellationToken ct) => users.LoadAsync(widget.Entity.CreatedById, ct);
}

/// <summary>A trend figure's period as stored with its tile: its fields only, never the domain's check.</summary>
public sealed class TrendPeriodType : ObjectType<TrendPeriod>
{
    protected override void Configure(IObjectTypeDescriptor<TrendPeriod> descriptor) => descriptor.Ignore(p => p.Validated(default));
}

[ExtendObjectType<VehicleChart>]
public sealed class VehicleChartExtensions
{
    /// <summary>Whether the user may change the chart: its creator only.</summary>
    public Task<bool> GetCanEdit([Parent] VehicleChart chart, [Service] AccessService access, CancellationToken ct) => CanEditAsync(chart, access, ct);

    /// <summary>Whether the user may delete the chart: its creator, or anyone with Delete access to the vehicle's logs.</summary>
    public Task<bool> GetCanDelete([Parent] VehicleChart chart, [Service] AccessService access, LogLevelByVehicleLoader levels, CancellationToken ct) =>
        CanDeleteAsync(chart, access, levels, ct);

    public Task<UserRef?> GetCreatedBy([Parent] VehicleChart chart, UserRefLoader users, CancellationToken ct) => users.LoadAsync(chart.CreatedById, ct);

    // The two rights as both types say them (VehicleChart's and SavedWidget's, #273), decided by ChartService.
    internal static async Task<bool> CanEditAsync(VehicleChart chart, AccessService access, CancellationToken ct) =>
        ChartService.MayChange(chart, await access.RequirePrincipalAsync(ct));

    internal static async Task<bool> CanDeleteAsync(VehicleChart chart, AccessService access, LogLevelByVehicleLoader levels, CancellationToken ct)
    {
        var user = await access.RequirePrincipalAsync(ct);
        // The creator needs no level loaded (MayDelete holds for them anyway): the batch runs only for someone else's shared chart.
        return ChartService.MayChange(chart, user) || ChartService.MayDelete(chart, user, await levels.LoadAsync(chart.VehicleId, ct));
    }
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

/// <param name="Id">A key figure by its id, a ready-made chart as <c>preset:&lt;id&gt;</c>, a saved widget as <c>chart:&lt;id&gt;</c>, a tile
/// with its own recipe as <c>tile:&lt;id&gt;</c> (#273).</param>
/// <param name="X">The column (0 to 3) and <paramref name="Y"/> the row of the tile's top left in a grid of four columns.</param>
/// <param name="W">Columns wide (1 to 4) and <paramref name="H"/> rows tall.</param>
/// <param name="Pinned">Never moved by dragging the other tiles.</param>
/// <param name="Period">A key figure's own period (#230); null for its default (and for every chart). A <c>tile:</c> keeps it in its recipe.</param>
/// <param name="Recipe">A <c>tile:</c> tile's own recipe (#273); null for every other tile.</param>
public sealed record DashboardTileInfo(string Id, int X, int Y, int W, int H, bool Pinned, TrendPeriod? Period, TileRecipe? Recipe);

/// <param name="Tiles">The tiles shown (a saved chart none names is shown at the end).</param>
/// <param name="Hidden">The saved charts taken off the dashboard.</param>
public sealed record DashboardLayoutInfo(IReadOnlyList<DashboardTileInfo> Tiles, IReadOnlyList<string> Hidden, DateTimeOffset UpdatedAt)
{
    public static DashboardLayoutInfo? From(DashboardLayout? layout) =>
        layout is null ? null : new(layout.Tiles.Select(t => new DashboardTileInfo(t.Id, t.X, t.Y, t.W, t.H, t.Pinned, t.Period, t.Recipe)).ToList(), layout.Hidden, layout.UpdatedAt);
}

/// <param name="Period">An old key figure id's own period; null or left out is its default (an older client sends none).</param>
/// <param name="Recipe">A <c>tile:</c> tile's own recipe (#273), required there and refused on every other tile.</param>
public sealed record DashboardTileInput(string Id, int X, int Y, int W, int H, bool? Pinned, TrendPeriodInput? Period = null, TileRecipeInput? Recipe = null)
{
    public DashboardTile ToDomain() => new(Id, X, Y, W, H, Pinned ?? false, Period?.ToDomain(), Recipe?.ToDomain());
}

/// <summary>A trend figure's period (#230): the last N units (<paramref name="Unit"/>, <paramref name="Count"/>), since a date, this year or this month so far.</summary>
/// <param name="Whole">Weeks, months or years whole (Monday to Sunday, calendar months and years, the running one left out); null or left out is false.</param>
/// <param name="Against">What the period is compared with (nothing, <c>NONE</c>, only for a key figure, #272); null or left out is the period before.</param>
public sealed record TrendPeriodInput(TrendWindowKind Kind, TrendUnit? Unit, int? Count, DateOnly? Since, bool? Whole = null, TrendComparison? Against = null)
{
    public TrendPeriod ToDomain() => new(Kind, Unit, Count, Since, Whole ?? false, Against ?? TrendComparison.Before);
}

/// <param name="Shared">The vehicle's dashboard, which everyone who sees it gets; null for the Standard preset.</param>
/// <param name="Personal">The user's own arrangement, which wins over the vehicle's for them alone; null when they made none.</param>
/// <param name="CanChangeShared">Whether the user may change the vehicle's dashboard (Edit access to its logs).</param>
public sealed record DashboardInfo(DashboardLayoutInfo? Shared, DashboardLayoutInfo? Personal, bool CanChangeShared);

/// <param name="Personal">The user's own arrangement (View access is enough), else the vehicle's (Edit access to its logs).</param>
/// <param name="LayoutVersion">The arrangement's version the app knows (#273); left out (an older app) or older than the server's, the save is
/// refused with <c>dashboard.updateApp</c>, so an app never drops tiles it does not know. Optional, so an older app gets that sentence and
/// not a validation error.</param>
public sealed record SaveDashboardLayoutInput(Guid VehicleId, bool Personal, IReadOnlyList<DashboardTileInput> Tiles, IReadOnlyList<string> Hidden, int? LayoutVersion = null);

[ExtendObjectType(OperationTypeNames.Query)]
public sealed class DashboardQueries
{
    /// <summary>The data of a chart, calculated from the vehicle's logs (costs per currency). Used for saved charts, presets and the builder's preview.</summary>
    public Task<ChartData> GetVehicleChartData(Guid vehicleId, ChartConfigInput config, [Service] StatsService stats, CancellationToken ct) =>
        stats.ChartAsync(vehicleId, config.ToDomain(), ct);

    /// <summary>
    /// A trend figure over a period of its own (#230): the two windows compared and the figure's values in them, from the vehicle's logs
    /// (money per currency, never converted). Each trend tile asks its own; the popover asks it for the period being chosen.
    /// </summary>
    public Task<TrendFigureData> GetVehicleTrend(Guid vehicleId, TrendFigure figure, TrendPeriodInput period, [Service] StatsService stats, CancellationToken ct) =>
        stats.TrendAsync(vehicleId, figure, period.ToDomain(), ct);

    /// <summary>
    /// A key figure (#272): any measure over a period of its own, compared with the period before, a year earlier or nothing, or a point in
    /// time (the odometer, the last fill-up) without a period. The windows and the values in them, from the vehicle's logs (money per
    /// currency, never converted, the main one first). <c>vehicleTrend</c> stays for an older client.
    /// </summary>
    public Task<FigureData> GetVehicleFigure(Guid vehicleId, FigureMeasure measure, TrendPeriodInput? period, [Service] StatsService stats, CancellationToken ct) =>
        stats.FigureAsync(vehicleId, measure, period?.ToDomain(), ct);

    /// <summary>The charts of a vehicle's dashboard: the user's own and the shared ones, oldest first. Charts only: saved key figures are in <c>vehicleWidgets</c>.</summary>
    public Task<IReadOnlyList<VehicleChart>> GetVehicleCharts(Guid vehicleId, [Service] ChartService charts, CancellationToken ct) => charts.ListAsync(vehicleId, ct);

    /// <summary>The saved widgets of a vehicle's dashboard, charts and key figures (#273): the user's own and the shared ones, oldest first.</summary>
    public async Task<IReadOnlyList<SavedWidget>> GetVehicleWidgets(Guid vehicleId, [Service] ChartService charts, CancellationToken ct) =>
        (await charts.ListWidgetsAsync(vehicleId, ct)).Select(c => new SavedWidget(c)).ToList();
}

[ExtendObjectType(OperationTypeNames.Mutation)]
public sealed class DashboardMutations
{
    public Task<VehicleChart> SaveVehicleChart(SaveChartInput input, [Service] ChartService charts, CancellationToken ct)
    {
        var chart = new ChartInput(input.Title, input.Config.ToDomain(), input.Shared);
        return input.Id is { } id ? charts.UpdateAsync(id, chart, ct) : charts.CreateAsync(input.VehicleId, chart, ct);
    }

    /// <summary>Creates or changes a saved widget of either kind (#273); <c>saveVehicleChart</c> stays for an older client, charts only.</summary>
    public async Task<SavedWidget> SaveVehicleWidget(SaveWidgetInput input, [Service] ChartService charts, CancellationToken ct)
    {
        var widget = new ChartInput(input.Title, input.Chart?.ToDomain(), input.Shared, input.Figure?.ToDomain());
        return new SavedWidget(input.Id is { } id ? await charts.UpdateAsync(id, widget, ct) : await charts.CreateAsync(input.VehicleId, widget, ct));
    }

    /// <summary>Deletes a saved widget of either kind.</summary>
    public async Task<bool> DeleteVehicleChart(Guid id, [Service] ChartService charts, CancellationToken ct)
    {
        await charts.DeleteAsync(id, ct);
        return true;
    }

    /// <summary>Saves the vehicle's dashboard or the user's own arrangement of it, replacing the one there was.</summary>
    public async Task<bool> SaveDashboardLayout(SaveDashboardLayoutInput input, [Service] DashboardService dashboards, CancellationToken ct)
    {
        DashboardLayoutValues.RequireVersion(input.LayoutVersion);
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
