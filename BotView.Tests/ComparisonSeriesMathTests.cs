using BotView.Chart;
using BotView.Models;
using BotView.Services;

namespace BotView.Tests;

public sealed class ComparisonSeriesMathTests
{
    [Fact]
    public void AnchorMovesToFirstSharedVisibleCandle()
    {
        var primary = Series((1, 100), (2, 110), (3, 121), (4, 132));
        var comparison = Series((2, 50), (3, 55), (4, 60));

        Assert.True(ComparisonSeriesMath.TryFindAnchor(primary.GetSnapshot(), comparison.GetSnapshot(),
            1_000, 4_000, out var first));
        Assert.Equal(2_000, first.Timestamp);
        Assert.Equal(110, first.PrimaryClose);
        Assert.Equal(50, first.ComparisonClose);
        Assert.Equal(10, ComparisonSeriesMath.ToPercent(55, first.ComparisonClose), 8);
        Assert.Equal(121, ComparisonSeriesMath.ToPrimaryPrice(55, first), 8);

        Assert.True(ComparisonSeriesMath.TryFindAnchor(primary.GetSnapshot(), comparison.GetSnapshot(),
            3_000, 4_000, out var shifted));
        Assert.Equal(3_000, shifted.Timestamp);
        Assert.Equal(0, ComparisonSeriesMath.ToPercent(55, shifted.ComparisonClose));
        Assert.Equal(121, ComparisonSeriesMath.ToPrimaryPrice(55, shifted));
    }

    [Fact]
    public void ZeroOrMissingBaselineCannotProduceComparison()
    {
        var primary = Series((1, 0), (2, 100));
        var comparison = Series((1, 10));

        Assert.False(ComparisonSeriesMath.TryFindAnchor(primary.GetSnapshot(), comparison.GetSnapshot(),
            1_000, 2_000, out _));
        Assert.True(double.IsNaN(ComparisonSeriesMath.ToPercent(10, 0)));
        Assert.True(double.IsNaN(ComparisonSeriesMath.ToPrimaryPrice(10,
            new ComparisonSeriesMath.Anchor(1_000, 100, 0))));
    }

    [Fact]
    public void MissingCandlesBreakTheLine()
    {
        var timeframe = TimeSpan.FromMinutes(1);
        Assert.True(ComparisonSeriesMath.IsContinuous(60_000, 120_000, timeframe));
        Assert.False(ComparisonSeriesMath.IsContinuous(60_000, 180_000, timeframe));
        Assert.False(ComparisonSeriesMath.IsContinuous(60_000, 60_000, timeframe));
    }

    [Fact]
    public void ChartRebasesPercentLabelsWhenViewportMovesAndHonorsManualVerticalZoom()
    {
        var primary = Series((60, 100), (120, 110), (180, 120));
        var comparison = Series((60, 50), (120, 60), (180, 66));
        var model = new ChartModel
        {
            Series = primary,
            WorldOriginTime = DateTime.UnixEpoch,
            WorldOriginPrice = 0,
            ChartWidth = 800,
            ChartHeight = 400,
            Timeframe = "1m",
            TimeRangeInViewport = TimeSpan.FromMinutes(3),
            PriceRangeInViewport = 40,
            CameraPosition = new Coordinates(120, 110)
        };
        var controller = new ChartController(model);
        controller.UpdateViewportFromCamera();
        controller.SetComparison(comparison, "ETH/USDT");

        Assert.Equal(60_000, model.ComparisonAnchor!.Value.Timestamp);
        Assert.StartsWith("0", controller.FormatPriceLabel(100));
        Assert.EndsWith("%", controller.FormatPriceLabel(100));
        Assert.Contains("10", controller.FormatPriceLabel(110));
        Assert.True(model.Viewport.maxPrice >= 132); // 66 / 50 * 100

        controller.Pan(60, 0);
        Assert.Equal(120_000, model.ComparisonAnchor!.Value.Timestamp);
        Assert.StartsWith("0", controller.FormatPriceLabel(110));
        Assert.EndsWith("%", controller.FormatPriceLabel(110));

        controller.ZoomAxis(1, 0.5);
        double manualRange = model.PriceRangeInViewport;
        controller.Pan(10, 0);
        Assert.Equal(manualRange, model.PriceRangeInViewport);
    }

    private static CandleSeries Series(params (long Second, double Close)[] values)
    {
        var series = new CandleSeries();
        series.LoadInitial(values.Select(v => new OHLCV(v.Second * 1000,
            v.Close, v.Close, v.Close, v.Close, 1)));
        return series;
    }
}
