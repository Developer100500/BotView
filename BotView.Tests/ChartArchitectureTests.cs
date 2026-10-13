using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BotView.Chart;
using BotView.Chart.IndicatorPane;
using BotView.Chart.TechnicalAnalysis;
using BotView.Models;
using BotView.Services;

namespace BotView.Tests;

public sealed class ChartArchitectureTests
{
	private const long Start = 1_700_000_000_000;
	private static OHLCV Candle(int index, double price = 100) =>
		new(Start + index * 60_000L, price - 1, price + 2, price - 2, price, 1000);

	private static ChartController Ready(out CandleSeries series)
	{
		series = new CandleSeries();
		series.LoadInitial(Enumerable.Range(0, 200).Select(i => Candle(i, 100 + Math.Sin(i / 5.0))));
		var controller = new ChartController();
		controller.SetViewSize(870, 550);
		controller.SetSeries(series, "1m");
		controller.AddRSI();
		return controller;
	}

	private static void AssertSameViewport(ViewportClippingCoords expected, ViewportClippingCoords actual)
	{
		Assert.Equal(expected.minTime, actual.minTime);
		Assert.Equal(expected.maxTime, actual.maxTime);
		Assert.Equal(expected.minPrice, actual.minPrice, 8);
		Assert.Equal(expected.maxPrice, actual.maxPrice, 8);
	}

	[Fact]
	public void EmptyStateDisablesDataCommandsAndHasConsistentInitialGeometry()
	{
		var controller = new ChartController();
		Assert.Equal(0, controller.State.Series.GetSnapshot().Count);
		Assert.False(controller.FitToDataCommand.CanExecute(null));
		Assert.False(controller.BeginToolCreationCommand.CanExecute(TechnicalAnalysisToolType.TrendLine));
		Assert.False(controller.DeleteSelectedToolCommand.CanExecute(null));
		Assert.Null(controller.State.DataMinPrice);
		Assert.Null(controller.State.DataMaxPrice);
		controller.SetViewSize(70, 52);
		var converted = controller.Transform.ChartToView(new(DateTime.UnixEpoch, 0));
		Assert.True(double.IsFinite(converted.x) && double.IsFinite(converted.y));
		Assert.False(controller.IsReady);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void CameraInitializesAsSoonAsSizeAndDataAreAvailable(bool sizeFirst)
	{
		var controller = new ChartController();
		var series = new CandleSeries();
		series.LoadInitial([Candle(0), Candle(1)]);
		if (sizeFirst) controller.SetViewSize(870, 550);
		controller.SetSeries(series, "1m");
		if (!sizeFirst)
		{
			Assert.False(controller.State.IsInitialized);
			controller.SetViewSize(870, 550);
		}
		Assert.True(controller.IsReady);
		Assert.True(controller.FitToDataCommand.CanExecute(null));
		Assert.Equal(TimeSpan.FromMinutes(100), controller.State.TimeRangeInViewport);
		Assert.Equal(20, controller.State.PriceRangeInViewport, 8);
		Assert.Equal(series.GetSnapshot()[1].GetDateTime(), controller.Viewport.maxTime);
		var bottomLeft = controller.Transform.ViewToChart(new(10, controller.State.DividerY));
		Assert.Equal(controller.Viewport.minPrice, bottomLeft.price, 8);
		Assert.Equal(controller.Viewport.minTime, bottomLeft.time);
	}

	[Fact]
	public void CoordinateRoundTripsWorkBeforeRenderingAfterEveryGeometryChange()
	{
		var controller = Ready(out _);
		controller.Pan(360, 2);
		controller.ZoomAxis(0.7, 1.3);
		controller.SetViewSize(1270, 750);
		controller.IndicatorPaneHeightRatio = 0.4;
		controller.ZoomIndicatorAxis(0.6);
		var point = new ChartCoordinates(controller.Viewport.minTime.AddMinutes(20), 99.1234);
		var pixels = controller.Transform.ChartToView(point);
		var restored = controller.Transform.ViewToChart(pixels);
		Assert.InRange(Math.Abs((restored.time - point.time).TotalMilliseconds), 0, 0.01);
		Assert.Equal(point.price, restored.price, 9);
		var indicator = controller.Transform.IndicatorToView(point.time, 43.21);
		var restoredIndicator = controller.Transform.ViewToIndicator(indicator);
		Assert.InRange(Math.Abs((restoredIndicator.time - point.time).TotalMilliseconds), 0, 0.01);
		Assert.Equal(43.21, restoredIndicator.value, 9);
		Assert.Equal(controller.State.IndicatorPaneHeight / controller.State.IndicatorRangeInViewport,
			controller.Transform.PixelsPerIndicatorUnit, 9);
	}

	[Theory]
	[InlineData(0.00000001, 0.1)]
	[InlineData(100000000, 10)]
	public void ZoomKeepsThePointUnderCursorAtScaleLimits(double firstFactor, double nextFactor)
	{
		var controller = Ready(out _);
		controller.ZoomAxis(firstFactor, firstFactor);
		var mouse = new Coordinates(267, 143);
		var before = controller.Transform.ViewToChart(mouse);
		controller.ZoomAtScreenPoint(mouse.x, mouse.y, nextFactor);
		var after = controller.Transform.ViewToChart(mouse);
		Assert.InRange(Math.Abs((after.time - before.time).TotalMilliseconds), 0, 0.01);
		Assert.Equal(before.price, after.price, 7);
	}

	[Fact]
	public void CandleAndHistoryUpdatesPreserveCameraAndVisibleRange()
	{
		var controller = Ready(out var series);
		controller.Pan(-3600, 3);
		var before = controller.Viewport;
		var camera = controller.CameraPosition;
		int geometryEvents = 0, renderEvents = 0;
		controller.ViewportChanged += () => geometryEvents++;
		controller.RenderRequested += () => renderEvents++;
		series.UpdateLive(Candle(200, 500), out _);
		controller.OnLiveCandleUpdated();
		Assert.Equal(0, geometryEvents);
		Assert.Equal(1, renderEvents);
		series.UpdateLive(Candle(201, 600), out _);
		controller.OnCandleClosed();
		AssertSameViewport(before, controller.Viewport);
		Assert.True(controller.State.DataMaxPrice > before.maxPrice);
		series.PrependHistory([Candle(-1, 1)]);
		controller.OnHistoryExtended();
		AssertSameViewport(before, controller.Viewport);
		Assert.Equal(camera.x, controller.CameraPosition.x);
		Assert.Equal(camera.y, controller.CameraPosition.y);
		var edge = controller.Transform.ViewToChart(new(10, controller.State.DividerY));
		Assert.Equal(before.minPrice, edge.price, 8);
		Assert.Equal(before.minTime, edge.time);
	}

	[Fact]
	public void AxesPreservePricePrecisionAndTimeLabelPolicy()
	{
		var time = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
		var axes = new AxisLayout(new(0, 12, time, time.AddHours(1)), new(0, 100), 275, TimeSpan.FromMinutes(1));
		Assert.Equal(2.5, axes.PriceInterval);
		Assert.Equal(0.1, axes.GetMinimumDisplayedPriceStep());
		Assert.Equal(2.5.ToString("F1"), axes.FormatPriceLabel(2.5));
		Assert.Equal(time.ToString("dd.MM.yyyy HH:mm"), axes.FormatCrosshairTimeLabel(time));
		var daily = new AxisLayout(new(0.05, 0.06, time, time.AddDays(100)), new(0, 100), 275, TimeSpan.FromDays(1));
		Assert.True(daily.GetMinimumDisplayedPriceStep() <= 0.001);
		Assert.Equal(time.ToString("dd.MM.yyyy"), daily.FormatCrosshairTimeLabel(time));
		Assert.Equal(DateTimeKind.Utc, axes.RoundDownToInterval(time, TimeSpan.FromMinutes(5)).Kind);
		Assert.All(axes.PriceTicks, tick => Assert.Equal(axes.FormatPriceLabel(tick.Value), tick.Label));
		Assert.All(axes.TimeTicks, tick => Assert.Equal(axes.FormatTimeLabel(tick.Value, axes.TimeInterval), tick.Label));
	}

	[Theory]
	[InlineData(TechnicalAnalysisToolType.HorizontalLine, 1)]
	[InlineData(TechnicalAnalysisToolType.HorizontalRay, 1)]
	[InlineData(TechnicalAnalysisToolType.TrendLine, 2)]
	[InlineData(TechnicalAnalysisToolType.TrendChannel, 3)]
	[InlineData(TechnicalAnalysisToolType.Rectangle, 2)]
	public void CreationFinishesAfterTheRequiredNumberOfClicks(TechnicalAnalysisToolType type, int clicks)
	{
		var controller = Ready(out _);
		controller.BeginToolCreationCommand.Execute(type);
		for (int i = 0; i < clicks; i++)
		{
			controller.HandlePointerMove(new(200 + i * 50, 100 + i * 40), false);
			Assert.True(controller.HandlePointerDown(new(200 + i * 50, 100 + i * 40)).Handled);
			if (i < clicks - 1) Assert.Equal(type, controller.ActiveToolType);
		}
		Assert.Equal(TechnicalAnalysisToolType.None, controller.ActiveToolType);
		Assert.Single(controller.State.TechnicalAnalysisManager.GetTools());
		Assert.Null(controller.State.Interaction.CreatingToolInstance);
	}

	[Fact]
	public void CreationSelectionAndCrosshairAreIndependentBetweenCharts()
	{
		var first = Ready(out _);
		var second = Ready(out _);
		first.BeginToolCreation(TechnicalAnalysisToolType.TrendLine);
		first.HandlePointerDown(new(200, 100));
		first.HandlePointerMove(new(250, 140), false);
		Assert.Equal(1, first.State.Interaction.CreationStep);
		Assert.Equal(TechnicalAnalysisToolType.None, second.ActiveToolType);
		Assert.False(second.State.Interaction.CrosshairVisible);
		second.BeginToolCreation(TechnicalAnalysisToolType.HorizontalLine);
		second.HandlePointerDown(new(300, 150));
		second.HandlePointerDown(new(300, 150));
		second.HandlePointerUp();
		Assert.NotNull(second.SelectedTool);
		Assert.Null(first.SelectedTool);
		second.CancelInteraction();
		Assert.Equal(TechnicalAnalysisToolType.TrendLine, first.ActiveToolType);
		Assert.Equal(1, first.State.Interaction.CreationStep);
	}

	[Fact]
	public void ClickSelectsAndDragStartsAtFourPixelsWithoutSelecting()
	{
		var controller = Ready(out _);
		controller.BeginToolCreation(TechnicalAnalysisToolType.HorizontalLine);
		controller.HandlePointerDown(new(250, 150));
		var tool = Assert.IsType<HorizontalLine>(Assert.Single(controller.State.TechnicalAnalysisManager.GetTools()));
		double price = tool.Price;
		controller.HandlePointerDown(new(250, 150));
		controller.HandlePointerMove(new(250, 153), true);
		Assert.Equal(price, tool.Price);
		controller.HandlePointerUp();
		Assert.Same(tool, controller.SelectedTool);
		controller.CancelInteraction();
		controller.HandlePointerDown(new(250, 150));
		controller.HandlePointerMove(new(250, 154), true);
		controller.HandlePointerUp();
		Assert.Null(controller.SelectedTool);
		Assert.Equal(price - 4 / controller.Transform.PixelsPerPriceUnit, tool.Price, 8);
	}

	[Fact]
	public void SelectedControlPointCanMoveThenKeyboardChangesPriceAndDeleteClearsSelection()
	{
		RunSta(() =>
		{
			var controller = Ready(out _);
			controller.BeginToolCreation(TechnicalAnalysisToolType.HorizontalLine);
			controller.HandlePointerDown(new(250, 150));
			controller.HandlePointerDown(new(250, 150));
			controller.HandlePointerUp();
			var tool = Assert.IsType<HorizontalLine>(controller.SelectedTool);
			Render(controller.PrepareRenderFrame()); // Populates the existing control-point geometry cache.
			var center = new Point(controller.State.LeftMargin + controller.State.ChartWidth / 2,
				controller.Transform.PriceToViewY(tool.Price));
			Assert.Equal(MouseCaptureAction.Capture, controller.HandlePointerDown(center).Capture);
			Assert.Equal(0, controller.State.Interaction.EditingControlPointIndex);
			controller.HandlePointerMove(new(center.X, center.Y + 20), true);
			controller.HandlePointerUp();
			double before = tool.Price;
			Assert.True(controller.HandleKey(Key.Up, ModifierKeys.None).Handled);
			Assert.Equal(before + controller.Axes.GetMinimumDisplayedPriceStep(), tool.Price, 8);
			Assert.False(controller.HandleKey(Key.Down, ModifierKeys.Control).Handled);
			Assert.True(controller.DeleteSelectedToolCommand.CanExecute(null));
			controller.DeleteSelectedToolCommand.Execute(null);
			Assert.Null(controller.SelectedTool);
			Assert.Empty(controller.State.TechnicalAnalysisManager.GetTools());
			Assert.False(controller.DeleteSelectedToolCommand.CanExecute(null));
		});
	}

	[Fact]
	public void CaptureLossStopsNavigationAndEscapeCancelsUnfinishedTool()
	{
		var controller = Ready(out _);
		controller.HandlePointerDown(new(300, 130));
		controller.HandlePointerMove(new(330, 140), true);
		controller.HandleCaptureLost();
		var before = controller.CameraPosition;
		controller.HandlePointerMove(new(400, 200), true);
		Assert.Equal(before.x, controller.CameraPosition.x);
		Assert.Equal(before.y, controller.CameraPosition.y);
		controller.BeginToolCreation(TechnicalAnalysisToolType.TrendChannel);
		controller.HandlePointerDown(new(200, 100));
		controller.HandleKey(Key.Escape, ModifierKeys.None);
		Assert.Equal(TechnicalAnalysisToolType.None, controller.ActiveToolType);
		Assert.Empty(controller.State.TechnicalAnalysisManager.GetTools());
		controller.BeginToolCreation(TechnicalAnalysisToolType.TrendLine);
		controller.CancelInteractionCommand.Execute(null);
		Assert.Equal(TechnicalAnalysisToolType.None, controller.ActiveToolType);
	}

	[Fact]
	public void PropertyAndCommandNotificationsFollowStateChanges()
	{
		var controller = Ready(out _);
		var properties = new List<string?>();
		int commandEvents = 0, renderEvents = 0;
		controller.PropertyChanged += (_, args) => properties.Add(args.PropertyName);
		controller.BeginToolCreationCommand.CanExecuteChanged += (_, _) => commandEvents++;
		controller.RenderRequested += () => renderEvents++;
		controller.ShowCandleOutlines = false;
		controller.ShowCandleOutlines = false;
		Assert.Equal(1, renderEvents);
		Assert.Single(properties);
		controller.Pan(100, 1);
		controller.IndicatorPaneHeightRatio = 0.3;
		controller.BeginToolCreation(TechnicalAnalysisToolType.TrendLine);
		Assert.Contains(nameof(ChartController.CameraPosition), properties);
		Assert.Contains(nameof(ChartController.Viewport), properties);
		Assert.Contains(nameof(ChartController.IndicatorPaneHeightRatio), properties);
		Assert.Contains(nameof(ChartController.ActiveToolType), properties);
		Assert.True(commandEvents > 0);
	}

	[Fact]
	public void FrameReadsOneSnapshotAndIsUnaffectedByLaterLiveUpdatesOrCameraMoves()
	{
		RunSta(() =>
		{
			var controller = Ready(out var series);
			series.UpdateLive(Candle(200, 101), out _);
			var reader = new CountingReader(series);
			controller.SetSeries(reader, "1m");
			reader.Reads = 0;
			var frame = controller.PrepareRenderFrame();
			Assert.Equal(1, reader.Reads);
			var before = frame.Viewport;
			series.UpdateLive(Candle(200, 102), out _);
			controller.Pan(100, 1);
			Assert.Equal(101, frame.Candles[frame.Candles.Count - 1].close);
			AssertSameViewport(before, frame.Viewport);
			Render(frame);
		});
	}

	[Fact]
	public void RecalculationUsesSameSnapshotAndIndicatorCollectionsCannotBeChangedThroughTheFacade()
	{
		var controller = Ready(out var series);
		var rsi = controller.AddRSI();
		var expected = new RSIIndicator();
		var snapshot = series.GetSnapshot();
		expected.Calculate(snapshot, i => snapshot[i].GetDateTime());
		Assert.Equal(expected.Points.Select(point => point.Value), rsi.Points.Select(point => point.Value));
		controller.GetIndicators().Clear();
		Assert.Single(controller.Indicators);
		Assert.Throws<NotSupportedException>(() => ((IList<Indicator>)controller.Indicators).Clear());
		series.UpdateLive(Candle(200), out _);
		series.UpdateLive(Candle(201), out _);
		controller.OnCandleClosed();
		snapshot = series.GetSnapshot();
		expected.Calculate(snapshot, i => snapshot[i].GetDateTime());
		Assert.Equal(expected.Points.Select(point => point.Value), rsi.Points.Select(point => point.Value));
	}

	[Fact]
	public void DependencyPropertySynchronizesBothWaysAndKeepsItsBinding()
	{
		RunSta(() =>
		{
			var view = new ChartView();
			BindingOperations.SetBinding(view, ChartView.ShowCandleOutlinesProperty,
				new Binding(nameof(ChartController.ShowCandleOutlines)) { Source = view.Controller, Mode = BindingMode.TwoWay });
			view.Controller.ShowCandleOutlines = false;
			Assert.False(view.ShowCandleOutlines);
			view.SetCurrentValue(ChartView.ShowCandleOutlinesProperty, true);
			Assert.True(view.Controller.ShowCandleOutlines);
			Assert.True(BindingOperations.IsDataBound(view, ChartView.ShowCandleOutlinesProperty));
			view.Measure(new Size(870, 550));
			view.Arrange(new Rect(0, 0, 870, 550));
			Ready(out var series);
			view.SetSeries(series, "1m");
			view.BeginToolCreation(TechnicalAnalysisToolType.TrendLine);
			view.Controller.HandlePointerDown(new(200, 100));
			view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
			Assert.Equal(TechnicalAnalysisToolType.None, view.Controller.ActiveToolType);
			view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
			view.Controller.ShowCandleOutlines = false;
			Assert.False(view.ShowCandleOutlines);
		});
	}

	[Fact]
	public void DrawingAllLayersDoesNotChangeControllerStateAndCanExportAPreview()
	{
		RunSta(() =>
		{
			var controller = Ready(out _);
			controller.AddRSI();
			controller.BeginToolCreation(TechnicalAnalysisToolType.HorizontalLine);
			controller.HandlePointerDown(new(250, 150));
			controller.HandleContextMenu(new(250, 150), false);
			controller.HandlePointerMove(new(350, 200), false);
			var before = controller.Viewport;
			var selected = controller.SelectedTool;
			int notifications = 0;
			controller.RenderRequested += () => notifications++;
			var bitmap = Render(controller.PrepareRenderFrame());
			AssertSameViewport(before, controller.Viewport);
			Assert.Same(selected, controller.SelectedTool);
			Assert.Equal(0, notifications);
			var preview = Environment.GetEnvironmentVariable("BOTVIEW_CHART_PREVIEW");
			if (!string.IsNullOrEmpty(preview))
			{
				var encoder = new PngBitmapEncoder();
				encoder.Frames.Add(BitmapFrame.Create(bitmap));
				using var stream = System.IO.File.Create(preview);
				encoder.Save(stream);
			}
		});
	}

	[Fact]
	public void SymbolChangesAreSerializedAndNotificationsStayOnTheUiContext()
	{
		RunSta(() =>
		{
			var dispatcher = Dispatcher.CurrentDispatcher;
			SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
			async Task Check()
			{
				int uiThread = Environment.CurrentManagedThreadId;
				string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "botview-chart-" + Guid.NewGuid().ToString("N"));
				System.IO.Directory.CreateDirectory(folder);
				try
				{
					foreach (var (symbol, price) in new[] { ("A", 101.0), ("B", 102.0) })
						System.IO.File.WriteAllText(System.IO.Path.Combine(folder, symbol + ".json"),
							new Newtonsoft.Json.Linq.JArray(new HorizontalLine(price, Brushes.Red).toJson()).ToString());
					var state = new ChartState(new TechnicalAnalysisManager(folder));
				var controller = new ChartController(state);
				var series = new CandleSeries();
				series.LoadInitial([Candle(0), Candle(1)]);
				controller.SetViewSize(870, 550);
				controller.SetSeries(series, "1m");
				controller.RenderRequested += () => Assert.Equal(uiThread, Environment.CurrentManagedThreadId);
					controller.ViewActionRequested += _ =>
					{
						if (!controller.IsSwitchingSymbol) return;
						Assert.False(controller.BeginToolCreationCommand.CanExecute(TechnicalAnalysisToolType.TrendLine));
						controller.BeginToolCreation(TechnicalAnalysisToolType.TrendLine);
						Assert.Equal(TechnicalAnalysisToolType.None, controller.ActiveToolType);
					};
				controller.BeginToolCreation(TechnicalAnalysisToolType.TrendLine);
				controller.HandlePointerDown(new(200, 100));
				var first = controller.SetSymbolAsync("A");
				var second = controller.SetSymbolAsync("B");
				await Task.WhenAll(first, second);
				Assert.Equal("B", state.TechnicalAnalysisManager.CurrentSymbol);
					Assert.False(controller.IsSwitchingSymbol);
				Assert.Equal(102, Assert.IsType<HorizontalLine>(Assert.Single(state.TechnicalAnalysisManager.GetTools())).Price);
				Assert.Equal(TechnicalAnalysisToolType.None, controller.ActiveToolType);
				Assert.Null(controller.SelectedTool);
				await controller.SetSymbolAsync("A");
				Assert.Equal(101, Assert.IsType<HorizontalLine>(Assert.Single(state.TechnicalAnalysisManager.GetTools())).Price);
				Assert.True(state.TechnicalAnalysisManager.GetTools() is System.Collections.ObjectModel.ReadOnlyCollection<TechnicalAnalysisTool>);
				Assert.False(controller.ApplyToolStyle(new HorizontalLine(999, Brushes.Blue), style => style.Thickness = 4));
			}
				finally { System.IO.Directory.Delete(folder, true); }
			}
			var task = Check();
			_ = task.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background), TaskScheduler.Default);
			Dispatcher.Run();
			task.GetAwaiter().GetResult();
		});
	}

	[Fact]
	public void DividerAndAxisGesturesUpdateTheAppropriateGeometry()
	{
		var controller = Ready(out _);
		controller.HandlePointerDown(new(200, controller.State.DividerY + 1));
		controller.HandlePointerMove(new(200, 300), true);
		controller.HandlePointerUp();
		Assert.Equal(300, controller.State.DividerY, 8);
		var timeRange = controller.State.TimeRangeInViewport;
		var priceRange = controller.State.PriceRangeInViewport;
		controller.HandlePointerDown(new(400, 530));
		controller.HandlePointerMove(new(410, 530), true);
		controller.HandlePointerUp();
		Assert.True(controller.State.TimeRangeInViewport > timeRange);
		Assert.Equal(priceRange, controller.State.PriceRangeInViewport);
		controller.HandlePointerDown(new(830, 150));
		controller.HandlePointerMove(new(830, 160), true);
		controller.HandlePointerUp();
		Assert.True(controller.State.PriceRangeInViewport > priceRange);
		var indicatorRange = controller.State.IndicatorRangeInViewport;
		controller.HandlePointerDown(new(830, 450));
		controller.HandlePointerMove(new(830, 460), true);
		controller.HandlePointerUp();
		Assert.True(controller.State.IndicatorRangeInViewport > indicatorRange);
		Assert.Equal(controller.State.IndicatorPaneHeight / controller.State.IndicatorRangeInViewport,
			controller.Transform.PixelsPerIndicatorUnit, 8);
	}

	[Fact]
	public void CapturedFrameUsesItsOwnFallbackTimesAfterSeriesReplacement()
	{
		var controller = Ready(out _);
		var legacy = new CandleSeries();
		legacy.LoadInitial([new OHLCV(0, 100, 101, 99, 100, 1), Candle(1)]);
		controller.SetSeries(legacy, "1m");
		var frame = controller.PrepareRenderFrame();
		var oldTime = frame.GetCandleTime(0);
		var replacement = new CandleSeries();
		replacement.LoadInitial([Candle(500), Candle(501)]);
		controller.SetSeries(replacement, "1h");
		Assert.Equal(oldTime, frame.GetCandleTime(0));
		Assert.NotEqual(controller.GetCandleTime(0), frame.GetCandleTime(0));
	}

	[Fact]
	public void ClosingPaneGivesMainChartAllSpaceAndPreservesItsCamera()
	{
		var controller = Ready(out _);
		controller.IndicatorPaneHeightRatio = 0.4;
		var before = controller.Viewport;
		double scale = controller.Transform.PixelsPerPriceUnit;
		controller.CloseIndicatorPaneCommand.Execute(null);
		Assert.False(controller.IsIndicatorPaneVisible);
		Assert.Empty(controller.Indicators);
		Assert.Equal(controller.State.ChartHeight, controller.State.MainPaneHeight);
		Assert.Equal(0, controller.State.IndicatorPaneHeight);
		Assert.Equal(0, controller.State.DividerHeight);
		Assert.True(controller.Transform.PixelsPerPriceUnit > scale);
		Assert.Equal(0, controller.Transform.PixelsPerIndicatorUnit);
		Assert.Empty(controller.Axes.IndicatorTicks);
		Assert.Equal(0, controller.Axes.IndicatorValueInterval);
		AssertSameViewport(before, controller.Viewport);
		var frame = controller.PrepareRenderFrame();
		Assert.False(frame.IsIndicatorPaneVisible);
		Assert.Empty(frame.Indicators);
		Assert.Equal(frame.MainPaneRect.Bottom, frame.TimeScaleY);
		Assert.Equal(ChartPane.Main, controller.DetectPane(new(300, 450)));
		Assert.NotEqual(ChartPane.Divider, controller.DetectPane(new(300, 520)));
		controller.ShowRsiCommand.Execute(null);
		Assert.True(controller.IsIndicatorPaneVisible);
		Assert.Equal(0.4, controller.IndicatorPaneHeightRatio);
		Assert.Equal(scale, controller.Transform.PixelsPerPriceUnit, 8);
	}

	[Fact]
	public void ClosedPaneDoesNotCalculateReadSeriesOrDrawIndicators()
	{
		RunSta(() =>
		{
			var controller = Ready(out var series);
			var oldRsi = Assert.IsType<RSIIndicator>(Assert.Single(controller.Indicators));
			var oldValues = oldRsi.Points.Select(point => point.Value).ToArray();
			var spy = new CountingIndicator();
			controller.AddIndicator(spy);
			var reader = new CountingReader(series);
			controller.SetSeries(reader, "1m");
			Render(controller.PrepareRenderFrame());
			Assert.Equal(1, spy.DrawCalls);
			Assert.Equal(1, spy.ReferenceCalls);
			controller.CloseIndicatorPane();
			reader.Reads = 0;
			controller.RecalculateIndicators();
			controller.RecalculateIndicators(false);
			controller.AutoFitIndicatorViewport();
			controller.UpdateIndicatorViewport();
			controller.ZoomIndicatorAxis(2);
			Assert.Equal(0, reader.Reads);
			series.UpdateLive(Candle(200, 500), out _);
			series.UpdateLive(Candle(201, 600), out _);
			controller.OnCandleClosed();
			series.PrependHistory([Candle(-1)]);
			controller.OnHistoryExtended();
			controller.SetSeries(series, "1h");
			Render(controller.PrepareRenderFrame());
			Assert.False(controller.IsIndicatorPaneVisible);
			Assert.Equal(oldValues, oldRsi.Points.Select(point => point.Value));
			Assert.Equal(1, spy.DrawCalls);
			Assert.Equal(1, spy.ReferenceCalls);
			Assert.Empty(controller.Axes.IndicatorTicks);
		});
	}

	[Fact]
	public void RsiButtonIsANoOpWhileOpenAndReopensWithCurrentData()
	{
		var controller = Ready(out var series);
		var existing = Assert.IsType<RSIIndicator>(Assert.Single(controller.Indicators));
		existing.Period = 7;
		var reader = new CountingReader(series);
		controller.SetSeries(reader, "1m");
		reader.Reads = 0;
		int renderRequests = 0;
		controller.RenderRequested += () => renderRequests++;
		controller.ShowRsiCommand.Execute(null);
		Assert.Same(existing, Assert.Single(controller.Indicators));
		Assert.Equal(7, existing.Period);
		Assert.Equal(0, reader.Reads);
		Assert.Equal(0, renderRequests);
		controller.CloseIndicatorPane();
		series.UpdateLive(Candle(200, 500), out _);
		controller.OnCandleClosed();
		controller.ShowRsiCommand.Execute(null);
		var reopened = Assert.IsType<RSIIndicator>(Assert.Single(controller.Indicators));
		Assert.NotSame(existing, reopened);
		Assert.Equal(14, reopened.Period);
		var snapshot = series.GetSnapshot();
		var expected = new RSIIndicator();
		expected.Calculate(snapshot, i => snapshot[i].GetDateTime());
		Assert.Equal(expected.Points.Select(point => point.Value), reopened.Points.Select(point => point.Value));
	}

	[Fact]
	public void NativeCloseButtonIsPositionedAtPaneTopRightAndRemovedWhenClosed()
	{
		RunSta(() =>
		{
			var view = new ChartView();
			var series = new CandleSeries();
			series.LoadInitial(Enumerable.Range(0, 200).Select(i => Candle(i, 100 + Math.Sin(i / 5.0))));
			view.SetSeries(series, "1m");
			Assert.Equal(0, VisualTreeHelper.GetChildrenCount(view));
			view.ShowRsi();
			view.Measure(new Size(870, 550));
			view.Arrange(new Rect(0, 0, 870, 550));
			view.UpdateLayout();
			Assert.Equal(1, VisualTreeHelper.GetChildrenCount(view));
			var button = Assert.IsType<System.Windows.Controls.Button>(VisualTreeHelper.GetChild(view, 0));
			button.ApplyTemplate();
			view.UpdateLayout();
			var position = button.TranslatePoint(new Point(), view);
			Assert.Equal(view.Controller.State.IndicatorCloseButtonBounds.Location, position);
			Assert.Equal("×", button.Content);
			Assert.True(button.ActualWidth > 0 && button.ActualHeight > 0);
			ExportViewPreview(view, "BOTVIEW_INDICATORS_OPEN_PREVIEW");
			view.SetIndicatorPaneRatio(0.4);
			view.UpdateLayout();
			Assert.Equal(view.Controller.State.IndicatorCloseButtonBounds.Location, button.TranslatePoint(new Point(), view));
			var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
			var invoke = (System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(
				System.Windows.Automation.Peers.PatternInterface.Invoke);
			invoke.Invoke();
			Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
			Assert.False(view.Controller.IsIndicatorPaneVisible);
			Assert.Equal(0, VisualTreeHelper.GetChildrenCount(view));
			Assert.Empty(view.GetIndicators());
			view.UpdateLayout();
			ExportViewPreview(view, "BOTVIEW_INDICATORS_CLOSED_PREVIEW");
			view.ShowRsi();
			Assert.Equal(1, VisualTreeHelper.GetChildrenCount(view));
			Assert.Single(view.GetIndicators());
		});
	}

	[Fact]
	public void PaneCanBeClosedBeforeDataAndReopenedBeforeFirstRender()
	{
		var controller = new ChartController();
		controller.ShowRsi();
		Assert.True(controller.IsIndicatorPaneVisible);
		Assert.Empty(Assert.IsType<RSIIndicator>(Assert.Single(controller.Indicators)).Points);
		controller.CloseIndicatorPane();
		controller.SetViewSize(870, 550);
		var series = new CandleSeries();
		series.LoadInitial(Enumerable.Range(0, 200).Select(i => Candle(i)));
		controller.SetSeries(series, "1m");
		Assert.False(controller.IsIndicatorPaneVisible);
		controller.ShowRsi();
		Assert.True(controller.IsIndicatorPaneVisible);
		Assert.NotEmpty(Assert.IsType<RSIIndicator>(Assert.Single(controller.Indicators)).Points);
	}

	private static void ExportViewPreview(ChartView view, string variable)
	{
		var path = Environment.GetEnvironmentVariable(variable);
		if (string.IsNullOrEmpty(path)) return;
		var bitmap = new RenderTargetBitmap(870, 550, 96, 96, PixelFormats.Pbgra32);
		bitmap.Render(view);
		var encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(bitmap));
		using var stream = System.IO.File.Create(path);
		encoder.Save(stream);
	}

	private sealed class CountingIndicator : Indicator
	{
		public int DrawCalls, ReferenceCalls;
		public CountingIndicator() => Points.Add(new(DateTime.UnixEpoch, 50));
		public override void Draw(IndicatorDrawContext context) => DrawCalls++;
		public override void DrawReferenceLines(IndicatorDrawContext context) => ReferenceCalls++;
	}

	private static RenderTargetBitmap Render(ChartRenderFrame frame)
	{
		var visual = new DrawingVisual();
		using (var context = visual.RenderOpen())
		{
			context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 870, 550));
			new ChartRenderer().Render(context, frame);
		}
		var bitmap = new RenderTargetBitmap(870, 550, 96, 96, PixelFormats.Pbgra32);
		bitmap.Render(visual);
		return bitmap;
	}

	private sealed class CountingReader(CandleSeries series) : ICandleSeriesReader
	{
		public int Reads;
		public CandleSeriesSnapshot GetSnapshot() { Reads++; return series.GetSnapshot(); }
	}

	private static void RunSta(Action action)
	{
		Exception? error = null;
		var thread = new Thread(() =>
		{
			try { action(); }
			catch (Exception caught) { error = caught; }
		});
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA chart check timed out.");
		if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
	}
}
