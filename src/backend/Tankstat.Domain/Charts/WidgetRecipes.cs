using System.Text.Json.Serialization;

namespace Tankstat.Domain.Charts;

/// <summary>What a dashboard widget is (#273): a chart or a key figure. Stored and sent by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WidgetKind>))]
public enum WidgetKind
{
    Chart,
    Figure,
}

/// <summary>
/// A key figure's recipe (#273, over #272's <see cref="FigureMeasure"/>): what it measures, over what period and compared with what (the
/// period's <see cref="TrendPeriod.Against"/>; none for a point in time), whether it draws a sparkline, and the built-in figure it was made
/// from (<paramref name="Template"/>, in a figure id's shape: the web app words its default title from it).
/// </summary>
public sealed record FigureRecipe(FigureMeasure Measure, TrendPeriod? Period = null, bool Sparkline = false, string? Template = null)
{
    /// <summary>
    /// The recipe as it may be stored, checked on <paramref name="today"/>: the period by <see cref="TrendPeriod.Validated"/> with its keys,
    /// and suiting the measure (<c>trend.invalid</c> for a period with a point in time, none with a measure over one, or an unknown
    /// measure, as <c>vehicleFigure</c> refuses); <c>chart.invalid</c> for a template out of a figure id's shape.
    /// </summary>
    public FigureRecipe Validated(DateOnly today)
    {
        if (!Enum.IsDefined(Measure) || Measure.IsPointInTime() != Period is null) throw new DomainException("trend.invalid", "This period is not valid.");
        if (Template is not null && !DashboardLayoutValues.IsFigureId(Template)) throw new DomainException("chart.invalid", "This chart is not valid.");
        return this with { Period = Period?.Validated(today) };
    }
}

/// <summary>
/// The recipe a <c>tile:&lt;guid&gt;</c> tile carries in the arrangement itself (#273, the owner's A + B): its kind, its title, and a chart's
/// <see cref="ChartConfig"/> or a figure's <see cref="FigureRecipe"/>, exactly the one its kind names. Whoever may change the arrangement may
/// change it; a saved widget (<c>chart:&lt;guid&gt;</c>) is a <see cref="VehicleChart"/> row instead.
/// </summary>
/// <param name="Title">A chart's title, required unless it has a template; a figure's may be empty (the web app's default title, from its
/// template).</param>
/// <param name="Template">A chart's ready-made chart it was made from (#275: <c>monthlyCosts</c>, in a figure id's shape), which words its
/// default title in the viewer's language; a figure keeps its template in its own recipe.</param>
public sealed record TileRecipe(WidgetKind Widget, string? Title, ChartConfig? Chart = null, FigureRecipe? Figure = null, string? Template = null)
{
    /// <summary>
    /// The recipe as it may be stored, checked on <paramref name="today"/>: exactly the recipe its kind names (<c>dashboard.itemInvalid</c>
    /// otherwise, as for any entry out of shape, a template too, and a <see cref="Template"/> on a figure), the title as a saved chart's
    /// (<c>chart.titleRequired</c> for a chart without one or a template, <c>chart.titleTooLong</c>), a chart by
    /// <see cref="ChartConfig.Validated"/> and a figure by <see cref="FigureRecipe.Validated"/>, with their keys.
    /// </summary>
    public TileRecipe Validated(DateOnly today)
    {
        var shaped = Widget switch
        {
            WidgetKind.Chart => Chart is not null && Figure is null && (Template is null || DashboardLayoutValues.IsFigureId(Template)),
            WidgetKind.Figure => Figure is not null && Chart is null && Template is null && (Figure.Template is null || DashboardLayoutValues.IsFigureId(Figure.Template)),
            _ => false,
        };
        if (!shaped) throw new DomainException("dashboard.itemInvalid", "An entry of the dashboard is not valid.");
        var title = Title?.Trim() ?? "";
        if (title.Length > VehicleChart.MaxTitleLength)
            throw new DomainException("chart.titleTooLong", $"The title can be at most {VehicleChart.MaxTitleLength} characters.", new { Max = VehicleChart.MaxTitleLength });
        if (Widget == WidgetKind.Chart && title.Length == 0 && Template is null) throw new DomainException("chart.titleRequired", "The chart needs a title.");
        return new(Widget, title.Length == 0 ? null : title, Chart?.Validated(), Figure?.Validated(today), Template);
    }
}
