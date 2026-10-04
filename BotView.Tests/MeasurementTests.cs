using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BotView.Chart;
using BotView.Chart.TechnicalAnalysis;
using BotView.Models;
using BotView.Services;

namespace BotView.Tests;

public sealed class MeasurementTests
{
    private static readonly DateTime Origin = DateTime.UnixEpoch.AddDays(1);

    [Theory]
    [InlineData(100, 110, 10, 10, false)]
    [InlineData(100, 90, -10, -10, true)]
    [InlineData(100, 100, 0, 0, false)]
    [InlineData(0.000001, 0.000002, 0.000001, 100, false)]
    public void CalculatesPriceAndPercent(double start, double end, double delta, double percent, bool falling)
    {
        var result = MeasurementMath.Calculate(new(Origin, start), new(Origin.AddMinutes(5), end), CreateSeries().GetSnapshot());
        Assert.Equal(delta, result.PriceDifference, 10);
        Assert.Equal(percent, result.Percent!.Value, 8);
        Assert.Equal(falling, result.IsFalling);
        Assert.Equal(2, result.Bars);
        Assert.Equal(TimeSpan.FromMinutes(5), result.Duration);
    }

    [Fact]
    public void CountsActualBarsAcrossGapsAndInReverse()
    {
        var snapshot = CreateSeries().GetSnapshot();
        var forward = MeasurementMath.Calculate(new(Origin.AddMinutes(1), 100), new(Origin.AddMinutes(5), 90), snapshot);
        var reverse = MeasurementMath.Calculate(new(Origin.AddMinutes(5), 100), new(Origin.AddMinutes(1), 110), snapshot);
        Assert.Equal(1, forward.Bars);
        Assert.Equal(1, reverse.Bars);
        Assert.Equal(TimeSpan.FromMinutes(4), reverse.Duration);
        Assert.True(forward.IsFalling);
        Assert.False(reverse.IsFalling);
        var same = MeasurementMath.Calculate(new(Origin, 0), new(Origin, 10), snapshot);
        Assert.Null(same.Percent);
        Assert.Contains("—", same.PriceText);
        Assert.Equal(0, same.Bars);
        Assert.Equal("0 bars, 0 с", same.TimeText);
    }

    [Fact]
    public void SnapsToNearestCandleWithEarlierTiesAndClampedEdges()
    {
        var snapshot = CreateSeries().GetSnapshot();
        Assert.Equal(0, MeasurementMath.FindNearest(snapshot, Origin.AddDays(-1)));
        Assert.Equal(0, MeasurementMath.FindNearest(snapshot, Origin.AddSeconds(30)));
        Assert.Equal(1, MeasurementMath.FindNearest(snapshot, Origin.AddMinutes(3)));
        Assert.Equal(2, MeasurementMath.FindNearest(snapshot, Origin.AddDays(1)));
    }

    [Theory]
    [InlineData(0, "0 с")]
    [InlineData(3600, "1 ч")]
    [InlineData(172920, "2 д 2 мин")]
    [InlineData(10820, "3 ч 20 с")]
    public void FormatsDurationWithoutZeroComponents(int seconds, string expected) =>
        Assert.Equal(expected, MeasurementMath.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void StateFollowsTwoClicksAndPreservesTimestampsWhenHistoryIsPrepended()
    {
        var series = CreateSeries();
        var state = new ChartMeasurement();
        state.Arm();
        state.Click(new(Origin.AddSeconds(30), 101.23), series.GetSnapshot());
        Assert.Equal(MeasurementPhase.Measuring, state.Phase);
        Assert.Equal(Origin, state.Start!.Value.time);
        Assert.Equal(101.23, state.Start.Value.price);
        state.Move(new(Origin.AddMinutes(5), 110), series.GetSnapshot());
        Assert.Equal(110, state.End!.Value.price);
        state.Click(new(Origin.AddMinutes(5), 112), series.GetSnapshot());
        state.Move(new(Origin, 50), series.GetSnapshot());
        Assert.Equal(MeasurementPhase.Completed, state.Phase);
        Assert.Equal(112, state.End.Value.price);
        series.PrependHistory(new[] { Candle(-1) });
        Assert.Equal(Origin, state.Start.Value.time);
        Assert.Equal(2, MeasurementMath.Calculate(state.Start.Value, state.End.Value, series.GetSnapshot()).Bars);
        state.Click(new(Origin, 50), series.GetSnapshot());
        Assert.Equal(MeasurementPhase.Idle, state.Phase);
        Assert.Null(state.Start);
        state.Arm();
        state.Click(new(Origin, 100), new CandleSeries().GetSnapshot());
        Assert.Equal(MeasurementPhase.Armed, state.Phase);
    }

    [Fact]
    public void ChartConsumesMeasurementClicksAndCancelsOnEscapeToolSwitchAndDataChange() => RunWpf(() =>
    {
        var view = new TestChartView();
        view.Measure(new Size(800, 500));
        view.Arrange(new Rect(0, 0, 800, 500));
        view.UpdateLayout();
        view.SetSeries(CreateSeries(), "1m");
        view.StartDrawingTool(TechnicalAnalysisToolType.Measure);
        Assert.False(TechnicalAnalysisTool.IsCreatingTool);
        Assert.True(view.HandleMeasurementClick(new Point(780, 200))); // Price scale: ignored.
        Assert.True(view.HandleMeasurementMove(new Point(400, 100)));
        Assert.True(view.HandleMeasurementClick(new Point(400, 100)));
        Assert.True(view.HandleMeasurementMove(new Point(500, 200)));
        Assert.True(view.HandleMeasurementClick(new Point(500, 200)));
        Assert.False(view.HandleMeasurementMove(new Point(400, 100)));
        Assert.False(view.HandleMeasurementClick(new Point(780, 200))); // Completed: scales work.
        Assert.True(view.HandleMeasurementClick(new Point(500, 200))); // Clear, consume.
        Assert.False(view.HandleMeasurementClick(new Point(500, 200)));
        Assert.Equal(0, view.GetTechnicalAnalysisManager().Count);
        view.StartDrawingTool(TechnicalAnalysisToolType.Measure);
        view.PressEscape();
        Assert.False(view.HandleMeasurementClick(new Point(500, 200)));
        view.StartDrawingTool(TechnicalAnalysisToolType.Measure);
        view.StartDrawingTool(TechnicalAnalysisToolType.TrendLine);
        Assert.Equal(TechnicalAnalysisToolType.TrendLine, TechnicalAnalysisTool.CreatingToolType);
        Assert.False(view.HandleMeasurementClick(new Point(500, 200)));
        view.StartDrawingTool(TechnicalAnalysisToolType.Measure);
        view.SetSeries(CreateSeries(), "5m");
        Assert.False(view.HandleMeasurementClick(new Point(500, 200)));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RendersCorrectColorWithClippedOverlayAndStableAnchors(bool falling) => RunWpf(() =>
    {
        var model = new ChartModel
        {
            Series = CreateSeries(), WorldOriginTime = Origin, WorldOriginPrice = 0, IsInitialized = true,
            ChartWidth = 700, ChartHeight = 380, Timeframe = "1m",
            TimeRangeInViewport = TimeSpan.FromMinutes(8), PriceRangeInViewport = 40,
            CameraPosition = new Coordinates(150, 110)
        };
        var controller = new ChartController(model);
        controller.UpdateViewportFromCamera();
        model.Measurement.Arm();
        model.Measurement.Click(new(Origin, falling ? 120 : 100), model.Series.GetSnapshot());
        model.Measurement.Click(new(Origin.AddMinutes(5), falling ? 100 : 120), model.Series.GetSnapshot());
        var renderer = new ChartRenderer(model, controller);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) renderer.Render(drawing);
        var bitmap = new RenderTargetBitmap(800, 430, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[800 * 430 * 4];
        bitmap.CopyPixels(pixels, 800 * 4, 0);
        int colored = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (falling ? pixels[i + 2] == 242 && pixels[i + 1] == 54 && pixels[i] == 69
                : pixels[i + 2] == 41 && pixels[i + 1] == 98 && pixels[i] == 255) colored++;
        }
        Assert.True(colored > 100, "Measurement card and arrows were not rendered.");
        var destination = Environment.GetEnvironmentVariable("BOTVIEW_MEASUREMENT_PREVIEW");
        if (destination != null)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(destination, falling ? "measurement-fall.png" : "measurement-rise.png"));
            encoder.Save(file);
        }
        var original = model.Measurement.Start;
        model.TimeRangeInViewport = TimeSpan.FromMinutes(12);
        model.ChartWidth = 600;
        controller.UpdateViewportFromCamera();
        using (var drawing = visual.RenderOpen()) renderer.Render(drawing);
        Assert.Equal(original, model.Measurement.Start);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CardStaysInsidePaneAtEdges(bool falling)
    {
        var pane = new Rect(10, 20, 700, 280);
        foreach (var area in new[] { new Rect(-100, -100, 200, 180), new Rect(680, 290, 200, 200) })
            Assert.True(pane.Contains(ChartRenderer.GetMeasurementCardRect(area, pane, new Size(210, 50), falling)));
    }

    private static CandleSeries CreateSeries()
    {
        var series = new CandleSeries();
        series.LoadInitial(new[] { Candle(0), Candle(1), Candle(5) });
        return series;
    }

    private static OHLCV Candle(int minute) => new(new DateTimeOffset(Origin.AddMinutes(minute)).ToUnixTimeMilliseconds(), 105, 115, 95, 110, 1);

    private static void RunWpf(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { TechnicalAnalysisTool.StopCreating(); TechnicalAnalysisTool.StopEditing(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF test did not finish.");
        Assert.Null(error);
    }

    private sealed class TestChartView : ChartView
    {
        public void PressEscape() => OnKeyDown(new KeyEventArgs(Keyboard.PrimaryDevice,
            new FakePresentationSource(), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    private sealed class FakePresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
