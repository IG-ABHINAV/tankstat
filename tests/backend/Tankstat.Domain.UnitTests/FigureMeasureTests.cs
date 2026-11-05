using Tankstat.Domain.Charts;

namespace Tankstat.Domain.UnitTests;

/// <summary>What a key figure measures (#272): every chart metric over a period, plus two points in time.</summary>
public class FigureMeasureTests
{
    [Fact]
    public void EveryMeasureOverAPeriod_IsTheChartMetricOfItsName_AndEveryChartMetricIsOne()
    {
        var overPeriods = Enum.GetValues<FigureMeasure>().Where(m => !m.IsPointInTime()).ToList();

        Assert.All(overPeriods, m => Assert.Equal(m.ToString(), m.ToMetric()?.ToString()));
        Assert.Equal(Enum.GetValues<ChartMetric>().Order(), overPeriods.Select(m => m.ToMetric()!.Value).Order());
    }

    [Fact]
    public void TheOdometerAndTheLastFillUp_AreAPointInTime_WithNoMetric()
    {
        Assert.Equal([FigureMeasure.Odometer, FigureMeasure.LastFillUp], Enum.GetValues<FigureMeasure>().Where(m => m.IsPointInTime()));
        Assert.Null(FigureMeasure.Odometer.ToMetric());
        Assert.Null(FigureMeasure.LastFillUp.ToMetric());
    }
}
