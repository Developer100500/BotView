using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BotView.Chart.ChartViews;
using BotView.Chart.IndicatorPane;
using BotView.Chart.TechnicalAnalysis;
using BotView.Models;
using BotView.Services;

namespace BotView.Chart;

/// <summary>WPF adapter for chart commands, input effects and rendering.</summary>
public class ChartView : FrameworkElement
{
	public static readonly DependencyProperty ShowCandleOutlinesProperty = DependencyProperty.Register(
		nameof(ShowCandleOutlines), typeof(bool), typeof(ChartView),
		new FrameworkPropertyMetadata(true, OnShowCandleOutlinesChanged));

	public bool ShowCandleOutlines
	{
		get => (bool)GetValue(ShowCandleOutlinesProperty);
		set => SetValue(ShowCandleOutlinesProperty, value);
	}

	public ChartController Controller { get; } = new();
	private readonly ChartRenderer renderer = new();
	private readonly ChartContextMenu toolContextMenu;
	private bool subscribed;
	public double LastRenderTimeMs { get; private set; }
	public event Action? LeftEdgeApproached;

	public ChartView()
	{
		toolContextMenu = new ChartContextMenu(this, Controller);
		Focusable = true;
		Subscribe();
		Loaded += (_, _) =>
		{
			Subscribe();
			SetCurrentValue(ShowCandleOutlinesProperty, Controller.ShowCandleOutlines);
			Controller.SetViewSize(ActualWidth, ActualHeight);
			InvalidateVisual();
		};
		Unloaded += (_, _) =>
		{
			Apply(Controller.CancelInteraction());
			Controller.RenderRequested -= OnRenderRequested;
			Controller.PropertyChanged -= OnControllerPropertyChanged;
			Controller.ViewActionRequested -= Apply;
			Controller.LeftEdgeApproached -= OnLeftEdgeApproached;
			subscribed = false;
		};
	}

	private void Subscribe()
	{
		if (subscribed) return;
		Controller.RenderRequested += OnRenderRequested;
		Controller.PropertyChanged += OnControllerPropertyChanged;
		Controller.ViewActionRequested += Apply;
		Controller.LeftEdgeApproached += OnLeftEdgeApproached;
		subscribed = true;
	}

	private void OnRenderRequested() => InvalidateVisual();
	private void OnLeftEdgeApproached() => LeftEdgeApproached?.Invoke();
	private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs args)
	{
		if (args.PropertyName == nameof(ChartController.ShowCandleOutlines))
			SetCurrentValue(ShowCandleOutlinesProperty, Controller.ShowCandleOutlines);
	}

	private static void OnShowCandleOutlinesChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
	{
		var view = (ChartView)owner;
		view.Controller.ShowCandleOutlines = (bool)args.NewValue;
	}

	protected override void OnRender(DrawingContext context)
	{
		var watch = Stopwatch.StartNew();
		base.OnRender(context);
		renderer.Render(context, Controller.PrepareRenderFrame());
		LastRenderTimeMs = watch.Elapsed.TotalMilliseconds;
	}

	protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
	{
		base.OnRenderSizeChanged(sizeInfo);
		Controller.SetViewSize(ActualWidth, ActualHeight);
	}

	private void Apply(InteractionResult result)
	{
		if (result.Menu == ContextMenuAction.Close) toolContextMenu.CloseMenu();
		if (result.RequestFocus) Focus();
		if (result.Capture == MouseCaptureAction.Capture) CaptureMouse();
		else if (result.Capture == MouseCaptureAction.Release && IsMouseCaptured) ReleaseMouseCapture();
		if (result.Cursor != null) Cursor = result.Cursor;
		if (result.Menu == ContextMenuAction.Open && result.MenuTool != null)
			toolContextMenu.OpenMenu(result.MenuTool, result.MenuPosition);
	}

	protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
	{
		base.OnMouseLeftButtonDown(args);
		if (toolContextMenu.IsOpen)
		{
			toolContextMenu.CloseMenu();
			args.Handled = true;
			return;
		}
		var result = Controller.HandlePointerDown(args.GetPosition(this));
		Apply(result);
		args.Handled = result.Handled;
	}

	protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
	{
		base.OnMouseLeftButtonUp(args);
		var result = Controller.HandlePointerUp();
		Apply(result);
		args.Handled = result.Handled;
	}

	protected override void OnMouseMove(MouseEventArgs args)
	{
		base.OnMouseMove(args);
		var result = Controller.HandlePointerMove(args.GetPosition(this), args.LeftButton == MouseButtonState.Pressed);
		Apply(result);
		args.Handled = result.Handled;
	}

	protected override void OnMouseRightButtonUp(MouseButtonEventArgs args)
	{
		base.OnMouseRightButtonUp(args);
		var result = Controller.HandleContextMenu(args.GetPosition(this), args.LeftButton == MouseButtonState.Pressed);
		Apply(result);
		args.Handled = result.Handled;
	}

	protected override void OnMouseWheel(MouseWheelEventArgs args)
	{
		base.OnMouseWheel(args);
		Controller.HandleMouseWheel(args.GetPosition(this), args.Delta);
	}

	protected override void OnMouseLeave(MouseEventArgs args)
	{
		base.OnMouseLeave(args);
		Controller.HandlePointerLeave(IsMouseCaptured);
	}

	protected override void OnLostMouseCapture(MouseEventArgs args)
	{
		base.OnLostMouseCapture(args);
		Controller.HandleCaptureLost();
		Cursor = Cursors.Arrow;
	}

	protected override void OnKeyDown(KeyEventArgs args)
	{
		base.OnKeyDown(args);
		if (args.Key == Key.Escape && toolContextMenu.IsOpen)
		{
			toolContextMenu.CloseMenu();
			args.Handled = true;
			return;
		}
		var result = Controller.HandleKey(args.Key, Keyboard.Modifiers);
		Apply(result);
		args.Handled = result.Handled;
	}

	public void CloseToolContextMenu() => toolContextMenu.CloseMenu();
	public void BeginToolCreation(TechnicalAnalysisToolType type) => Controller.BeginToolCreation(type);
	public Task SetSymbolAsync(string symbol) => Controller.SetSymbolAsync(symbol);
	public void SaveTools() => Controller.SaveTools();
	public void SetCandlestickData(CandlestickData data) => Controller.SetCandlestickData(data);
	public void SetSeries(ICandleSeriesReader series, string timeframe) => Controller.SetSeries(series, timeframe);
	public void OnHistoryExtended() => Controller.OnHistoryExtended();
	public void OnLiveCandleUpdated() => Controller.OnLiveCandleUpdated();
	public void OnCandleClosed() => Controller.OnCandleClosed();
	public void CenterOnTime(DateTime time) => Controller.CenterOnTime(time);
	public void CenterOnPrice(double price) => Controller.CenterOnPrice(price);
	public void FitToData() => Controller.FitToData();
	public ViewportClippingCoords GetCurrentViewport() => Controller.GetCurrentViewport();
	public ChartCoordinates GetCameraPosition() => Controller.GetCameraPosition();
	public void SnapLastCandleToRightEdge() => Controller.SnapLastCandleToRightEdge();
	public void ResetPriceScaleToCurrentPrice() => Controller.ResetPriceScaleToCurrentPrice();
	public void ResetTimeScaleToTimeframe() => Controller.ResetTimeScaleToTimeframe();
	public void ZoomAxis(double timeFactor, double priceFactor) => Controller.ZoomAxis(timeFactor, priceFactor);
	public TechnicalAnalysisManager GetTechnicalAnalysisManager() => Controller.State.TechnicalAnalysisManager;
	internal Coordinates ChartToView(ChartCoordinates point) => Controller.Transform.ChartToView(point);
	public ChartCoordinates ViewToChart(Coordinates point) => Controller.ViewToChart(point);
	public void AddIndicator(Indicator indicator) => Controller.AddIndicator(indicator);
	public void RemoveIndicator(string id) => Controller.RemoveIndicator(id);
	public void ClearIndicators() => Controller.ClearIndicators();
	public List<Indicator> GetIndicators() => Controller.GetIndicators();
	public void SetIndicatorPaneRatio(double ratio) => Controller.IndicatorPaneHeightRatio = ratio;
	public void AutoFitIndicatorViewport() => Controller.AutoFitIndicatorViewport();
	public RSIIndicator AddRSI(int period = 14) => Controller.AddRSI(period);
	public void RecalculateIndicators(bool invalidateVisual = true) => Controller.RecalculateIndicators(invalidateVisual);
}
