using System.Windows.Media;
using System.Windows.Media.Imaging;
using BotView.Chart;
using BotView.Models;
using BotView.Services;

namespace BotView.Tests;

public sealed class ComparisonRenderTests
{
    [Fact]
    public void ComparisonRendersOnWpfDrawingSurface()
    {
        Exception? error = null;
        int purplePixels = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var primary = CreateSeries(100, 110, 120);
                var secondary = CreateSeries(50, 60, 55);
                var model = new ChartModel
                {
                    Series = primary,
                    WorldOriginTime = DateTime.UnixEpoch,
                    WorldOriginPrice = 0,
                    ChartWidth = 700,
                    ChartHeight = 380,
                    Timeframe = "1m",
                    TimeRangeInViewport = TimeSpan.FromMinutes(3),
                    PriceRangeInViewport = 40,
                    CameraPosition = new Coordinates(120, 110)
                };
                var controller = new ChartController(model);
                controller.UpdateViewportFromCamera();
                controller.SetComparison(secondary, "ETH/USDT");
                var renderer = new ChartRenderer(model, controller);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen()) renderer.Render(drawing);
                var bitmap = new RenderTargetBitmap(800, 500, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixels = new byte[800 * 500 * 4];
                bitmap.CopyPixels(pixels, 800 * 4, 0);
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i] > pixels[i + 2] + 25 && pixels[i] > pixels[i + 1] + 20)
                        purplePixels++;
                }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "WPF rendering did not finish.");
        Assert.Null(error);
        Assert.True(purplePixels > 20, "The comparison line was not painted.");
    }

    private static CandleSeries CreateSeries(params double[] closes)
    {
        var series = new CandleSeries();
        series.LoadInitial(closes.Select((close, index) =>
            new OHLCV((index + 1) * 60_000, close, close, close, close, 1)));
        return series;
    }
}
