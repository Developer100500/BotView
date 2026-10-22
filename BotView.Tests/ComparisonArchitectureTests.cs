using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BotView.Chart;
using BotView.Chart.TechnicalAnalysis;
using BotView.Models;
using BotView.Services;

namespace BotView.Tests;

public sealed class ComparisonArchitectureTests
{
    /// <summary>Checks that replacing/removing the overlay restores only the original vertical camera.</summary>
    [Fact]
    public void RemovingReplacementRestoresOriginalPriceScaleAndKeepsTimeNavigation()
    {
        var controller = Ready();
        double originalRange = controller.State.PriceRangeInViewport;
        double originalY = controller.CameraPosition.y;
        controller.SetComparison(Series(50), "ETH/USDT");
        controller.ZoomAxis(1, 0.5);
        controller.Pan(30, 0);
        controller.SetComparison(Series(20), "SOL/USDT");
        double navigatedX = controller.CameraPosition.x;
        controller.ClearComparison();
        Assert.Equal(originalRange, controller.State.PriceRangeInViewport);
        Assert.Equal(originalY, controller.CameraPosition.y);
        Assert.Equal(navigatedX, controller.CameraPosition.x);
        Assert.DoesNotContain("%", controller.FormatPriceLabel(100));
        Assert.Null(controller.State.ComparisonAnchor);
    }

    /// <summary>Checks live auto-fit, manual vertical navigation and re-enabling auto-fit from the command.</summary>
    [Fact]
    public void DataUpdatesHonorManualScaleAndResetResumesComparisonFit()
    {
        var controller = Ready();
        var comparison = Series(50);
        controller.SetComparison(comparison, "ETH/USDT");
        var snapshot = comparison.GetSnapshot();
        var last = snapshot[snapshot.Count - 1];
        comparison.UpdateLive(new OHLCV { timestamp = last.timestamp.GetValueOrDefault(), open = 50, high = 100, low = 50, close = 100, volume = 1 }, out _);
        controller.OnComparisonChanged();
        Assert.True(controller.Viewport.maxPrice >= 200);
        controller.Pan(0, 1);
        double manualRange = controller.State.PriceRangeInViewport;
        comparison.UpdateLive(new OHLCV { timestamp = last.timestamp.GetValueOrDefault(), open = 50, high = 150, low = 50, close = 150, volume = 1 }, out _);
        controller.OnComparisonChanged();
        controller.OnLiveCandleUpdated();
        Assert.Equal(manualRange, controller.State.PriceRangeInViewport);
        controller.ResetPriceScaleCommand.Execute(null);
        Assert.True(controller.Viewport.maxPrice >= 300);
        Assert.True(controller.State.PriceRangeInViewport > manualRange);
    }

    /// <summary>Checks that a missing shared baseline produces placeholder labels and no percentage anchor.</summary>
    [Fact]
    public void NoSharedVisibleCandlesLeaveComparisonWithoutAnchor()
    {
        var controller = Ready();
        var missing = new CandleSeries();
        missing.LoadInitial(new[] { new OHLCV { timestamp = 60_001, open = 50, high = 50, low = 50, close = 50, volume = 1 } });
        controller.SetComparison(missing, "ETH/USDT");
        Assert.Null(controller.State.ComparisonAnchor);
        Assert.Equal("—", controller.FormatPriceLabel(100));
        controller.ClearComparison();
        Assert.DoesNotContain("—", controller.FormatPriceLabel(100));
    }

    /// <summary>Checks actual pointer routing and that ruler state belongs to only one chart.</summary>
    [Fact]
    public void RulerConsumesPaneClicksAndRemainsIndependentAcrossCharts()
    {
        var first = Ready();
        var second = Ready();
        first.AddRSI();
        var originalCamera = first.CameraPosition;
        first.BeginToolCreationCommand.Execute(TechnicalAnalysisToolType.Measure);
        Assert.True(first.HandlePointerDown(new(780, 100)).Handled);
        Assert.True(first.HandlePointerDown(new(300, first.State.IndicatorPaneTop + 20)).Handled);
        Assert.Equal(MeasurementPhase.Armed, first.State.Interaction.Measurement.Phase);
        var click = first.HandlePointerDown(new(300, 100));
        Assert.Equal(MouseCaptureAction.None, click.Capture);
        Assert.Equal(Cursors.Cross, first.HandlePointerUp().Cursor);
        first.HandlePointerMove(new(400, 160), true);
        first.HandlePointerDown(new(400, 160));
        Assert.Equal(MeasurementPhase.Completed, first.State.Interaction.Measurement.Phase);
        Assert.Equal(TechnicalAnalysisToolType.None, first.ActiveToolType);
        Assert.Equal(originalCamera.x, first.CameraPosition.x);
        Assert.Equal(originalCamera.y, first.CameraPosition.y);
        second.BeginToolCreation(TechnicalAnalysisToolType.TrendLine);
        second.CancelInteraction();
        Assert.Equal(MeasurementPhase.Completed, first.State.Interaction.Measurement.Phase);
        Assert.Equal(MeasurementPhase.Idle, second.State.Interaction.Measurement.Phase);
        Assert.Equal(MouseCaptureAction.Capture, first.HandlePointerDown(new(780, 100)).Capture);
        first.HandlePointerUp();
        first.HandleKey(Key.Escape, ModifierKeys.None);
        Assert.Equal(MeasurementPhase.Idle, first.State.Interaction.Measurement.Phase);
        Assert.Empty(first.State.TechnicalAnalysisManager.GetTools());
    }

    /// <summary>Checks that a prepared frame captures each series once and owns stable overlay values.</summary>
    [Fact]
    public void RenderUsesCapturedComparisonAndRulerAfterControllerClearsThem()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var primary = new CountingReader(Series(100));
                var secondary = new CountingReader(Series(50));
                var controller = Ready();
                controller.SetSeries(primary, "1m");
                controller.SetComparison(secondary, "ETH/USDT");
                controller.AddRSI();
                controller.BeginToolCreation(TechnicalAnalysisToolType.Measure);
                controller.HandlePointerDown(new(300, 100));
                controller.HandlePointerDown(new(400, 160));
                primary.Reads = secondary.Reads = 0;
                var frame = controller.PrepareRenderFrame();
                Assert.Equal(1, primary.Reads);
                Assert.Equal(1, secondary.Reads);
                controller.ClearComparison();
                controller.ClearMeasurement();
                primary.Reads = secondary.Reads = 0;
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen()) new ChartRenderer().Render(drawing, frame);
                var preview = Environment.GetEnvironmentVariable("BOTVIEW_COMPARISON_PREVIEW");
                if (preview != null)
                {
                    var bitmap = new RenderTargetBitmap(800, 500, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = System.IO.File.Create(preview);
                    encoder.Save(output);
                }
                Assert.Equal(0, primary.Reads);
                Assert.Equal(0, secondary.Reads);
                Assert.NotNull(frame.MeasurementStart);
                Assert.NotNull(frame.MeasurementEnd);
                Assert.NotNull(frame.ComparisonAnchor);
                Assert.Equal("ETH/USDT", frame.ComparisonSymbol);
                Assert.All(frame.Axes.PriceTicks, tick => Assert.EndsWith("%", tick.Label));
                Assert.True(frame.IsIndicatorPaneVisible);
                Assert.False(frame.IsCreatingTool);
            }
            catch (Exception caught) { error = caught; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(error);
    }

    /// <summary>Creates a ready chart with a three-minute viewport covering all fixture candles.</summary>
    private static ChartController Ready()
    {
        var controller = new ChartController();
        controller.SetViewSize(800, 500);
        controller.SetSeries(Series(100), "1m");
        controller.State.TimeRangeInViewport = TimeSpan.FromMinutes(3);
        controller.State.CameraPosition = new(60, 0);
        controller.UpdateViewportFromCamera();
        return controller;
    }

    /// <summary>Creates three minute candles whose close starts at price and rises by ten percent per bar.</summary>
    private static CandleSeries Series(double price)
    {
        var series = new CandleSeries();
        series.LoadInitial(Enumerable.Range(1, 3).Select(i =>
            new OHLCV { timestamp = i * 60_000, open = price, high = price * 1.2, low = price * 0.9, close = price * (1 + (i - 1) * 0.1), volume = 1 }));
        return series;
    }

    private sealed class CountingReader(CandleSeries series) : ICandleSeriesReader
    {
        public int Reads;
        /// <summary>Counts snapshot requests and returns the constructor-supplied series snapshot.</summary>
        public CandleSeriesSnapshot GetSnapshot() { Reads++; return series.GetSnapshot(); }
    }
}
