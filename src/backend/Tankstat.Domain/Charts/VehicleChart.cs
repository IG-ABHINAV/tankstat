namespace Tankstat.Domain.Charts;

/// <summary>
/// A saved widget (#273): a chart or a key figure a user composed for a vehicle's dashboard, from the data the app can provide (a chart's
/// <see cref="ChartConfig"/>, a figure's <see cref="FigureRecipe"/>: exactly the one its <see cref="Widget"/> names, the other's columns null).
/// It is the creator's own, or shared with everyone who can see the vehicle's logs. It stores only the recipe; the numbers are calculated
/// when shown. Still called a chart (its table, its GraphQL type): older apps read it as one, and only ever get the charts.
/// </summary>
public sealed class VehicleChart
{
    public const int MaxTitleLength = 80;

    private VehicleChart() { } // EF Core

    public Guid Id { get; private set; }
    public Guid VehicleId { get; private set; }
    public Guid CreatedById { get; private set; }
    public string Title { get; private set; } = "";

    /// <summary>A chart or a key figure; every row from before #273 is a chart.</summary>
    public WidgetKind Widget { get; private set; }

    // A chart's recipe; null on a key figure.
    public ChartMetric? Metric { get; private set; }
    public ChartGrouping? Grouping { get; private set; }
    public ChartKind? Kind { get; private set; }
    public ChartRange? Range { get; private set; }
    public bool Stacked { get; private set; }
    public ChartTrend Trend { get; private set; }
    public bool RunningTotal { get; private set; }
    public bool CompareYearEarlier { get; private set; }
    public ChartMetric? SecondMetric { get; private set; }
    public ChartKind? SecondKind { get; private set; }

    /// <summary>The first and last day when <see cref="Range"/> is custom, otherwise null.</summary>
    public DateOnly? RangeFrom { get; private set; }
    public DateOnly? RangeTo { get; private set; }

    // A key figure's recipe; null (false) on a chart.
    public FigureMeasure? Measure { get; private set; }
    /// <summary>A key figure's period, stored as JSON as a tile's is; null for a point in time.</summary>
    public TrendPeriod? Period { get; private set; }
    public bool Sparkline { get; private set; }
    /// <summary>The built-in figure it was made from (in a figure id's shape), which words its default title.</summary>
    public string? Template { get; private set; }

    /// <summary>Shown to everyone who can see the vehicle's logs, not only to its creator.</summary>
    public bool IsShared { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>A chart's recipe; null on a key figure.</summary>
    public ChartConfig? Config => Widget == WidgetKind.Chart && Metric is { } metric && Grouping is { } grouping && Kind is { } kind && Range is { } range
        ? new(metric, grouping, kind, range, Stacked, RangeFrom, RangeTo, Trend, RunningTotal, CompareYearEarlier, SecondMetric, SecondKind)
        : null;

    /// <summary>A key figure's recipe; null on a chart.</summary>
    public FigureRecipe? Figure => Widget == WidgetKind.Figure && Measure is { } measure ? new(measure, Period, Sparkline, Template) : null;

    public static VehicleChart Create(Guid vehicleId, Guid createdById, string title, ChartConfig config, bool shared, DateTimeOffset now) =>
        Create(vehicleId, createdById, title, config, null, shared, now);

    /// <summary>A saved chart (<paramref name="config"/>) or key figure (<paramref name="figure"/>): exactly one of them (<c>chart.invalid</c>).</summary>
    public static VehicleChart Create(Guid vehicleId, Guid createdById, string title, ChartConfig? config, FigureRecipe? figure, bool shared, DateTimeOffset now)
    {
        var chart = new VehicleChart
        {
            Id = Guid.NewGuid(), VehicleId = vehicleId, CreatedById = createdById, CreatedAt = now,
            Widget = config is not null && figure is null ? WidgetKind.Chart : WidgetKind.Figure,
        };
        chart.Apply(title, config, figure, shared, now);
        return chart;
    }

    public void Update(string title, ChartConfig config, bool shared) => Apply(title, config, null, shared, CreatedAt);

    /// <summary>Changes the widget within its kind: a chart takes a config, a key figure a recipe (<c>chart.invalid</c> otherwise).</summary>
    public void Update(string title, ChartConfig? config, FigureRecipe? figure, bool shared, DateTimeOffset now) => Apply(title, config, figure, shared, now);

    private void Apply(string title, ChartConfig? config, FigureRecipe? figure, bool shared, DateTimeOffset now)
    {
        var trimmed = title?.Trim() ?? "";
        if (trimmed.Length == 0) throw new DomainException("chart.titleRequired", "The chart needs a title.");
        if (trimmed.Length > MaxTitleLength)
            throw new DomainException("chart.titleTooLong", $"The title can be at most {MaxTitleLength} characters.", new { Max = MaxTitleLength });
        var fits = Widget switch
        {
            WidgetKind.Chart => config is not null && figure is null,
            WidgetKind.Figure => figure is not null && config is null,
            _ => false,
        };
        if (!fits) throw new DomainException("chart.invalid", "This chart is not valid.");

        Title = trimmed;
        IsShared = shared;
        if (config is not null)
        {
            config = config.Validated();
            Metric = config.Metric;
            Grouping = config.Grouping;
            Kind = config.Kind;
            Range = config.Range;
            Stacked = config.Stacked;
            RangeFrom = config.From;
            RangeTo = config.To;
            Trend = config.Trend;
            RunningTotal = config.RunningTotal;
            CompareYearEarlier = config.CompareYearEarlier;
            SecondMetric = config.SecondMetric;
            SecondKind = config.SecondKind;
        }
        else
        {
            // The UTC date, as a tile's period is checked (DashboardLayout.Apply) and the statistics' "today".
            var recipe = figure!.Validated(DateOnly.FromDateTime(now.UtcDateTime));
            Measure = recipe.Measure;
            Period = recipe.Period;
            Sparkline = recipe.Sparkline;
            Template = recipe.Template;
        }
    }
}
