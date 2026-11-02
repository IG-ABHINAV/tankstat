namespace Tankstat.Domain.Charts;

/// <summary>
/// A chart a user composed for a vehicle's dashboard from the data the app can provide (see <see cref="ChartConfig"/>). It is the
/// creator's own, or shared with everyone who can see the vehicle's logs. It stores only the recipe; the numbers are calculated when shown.
/// </summary>
public sealed class VehicleChart
{
    public const int MaxTitleLength = 80;

    private VehicleChart() { } // EF Core

    public Guid Id { get; private set; }
    public Guid VehicleId { get; private set; }
    public Guid CreatedById { get; private set; }
    public string Title { get; private set; } = "";
    public ChartMetric Metric { get; private set; }
    public ChartGrouping Grouping { get; private set; }
    public ChartKind Kind { get; private set; }
    public ChartRange Range { get; private set; }
    public bool Stacked { get; private set; }
    public ChartTrend Trend { get; private set; }
    public bool RunningTotal { get; private set; }
    public bool CompareYearEarlier { get; private set; }
    public ChartMetric? SecondMetric { get; private set; }
    public ChartKind? SecondKind { get; private set; }

    /// <summary>The first and last day when <see cref="Range"/> is custom, otherwise null.</summary>
    public DateOnly? RangeFrom { get; private set; }
    public DateOnly? RangeTo { get; private set; }

    /// <summary>Shown to everyone who can see the vehicle's logs, not only to its creator.</summary>
    public bool IsShared { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public ChartConfig Config => new(Metric, Grouping, Kind, Range, Stacked, RangeFrom, RangeTo, Trend, RunningTotal, CompareYearEarlier, SecondMetric, SecondKind);

    public static VehicleChart Create(Guid vehicleId, Guid createdById, string title, ChartConfig config, bool shared, DateTimeOffset now)
    {
        var chart = new VehicleChart { Id = Guid.NewGuid(), VehicleId = vehicleId, CreatedById = createdById, CreatedAt = now };
        chart.Apply(title, config, shared);
        return chart;
    }

    public void Update(string title, ChartConfig config, bool shared) => Apply(title, config, shared);

    private void Apply(string title, ChartConfig config, bool shared)
    {
        var trimmed = title?.Trim() ?? "";
        if (trimmed.Length == 0) throw new DomainException("chart.titleRequired", "The chart needs a title.");
        if (trimmed.Length > MaxTitleLength)
            throw new DomainException("chart.titleTooLong", $"The title can be at most {MaxTitleLength} characters.", new { Max = MaxTitleLength });
        config = config.Validated();

        Title = trimmed;
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
        IsShared = shared;
    }
}
