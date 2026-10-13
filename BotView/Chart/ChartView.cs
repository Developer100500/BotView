using System.ComponentModel;
using System.Collections;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
	private Button? indicatorCloseButton;
	private Rect arrangedCloseButtonBounds = Rect.Empty;
	public double LastRenderTimeMs { get; private set; }
	public event Action? LeftEdgeApproached;

	public ChartView()
	{
		toolContextMenu = new ChartContextMenu(this, Controller);
		Focusable = true;
		Subscribe();
		UpdateIndicatorCloseButton();
		Loaded += (_, _) =>
		{
			Subscribe();
			UpdateIndicatorCloseButton();
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

	private void OnRenderRequested()
	{
		if (indicatorCloseButton != null && arrangedCloseButtonBounds != Controller.State.IndicatorCloseButtonBounds)
			InvalidateArrange();
		InvalidateVisual();
	}
	private void OnLeftEdgeApproached() => LeftEdgeApproached?.Invoke();
	private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs args)
	{
		if (args.PropertyName == nameof(ChartController.ShowCandleOutlines))
			SetCurrentValue(ShowCandleOutlinesProperty, Controller.ShowCandleOutlines);
		else if (args.PropertyName == nameof(ChartController.IsIndicatorPaneVisible))
			UpdateIndicatorCloseButton();
	}

	private void UpdateIndicatorCloseButton()
	{
		if (Controller.IsIndicatorPaneVisible && indicatorCloseButton == null)
		{
			indicatorCloseButton = new Button
			{
				Content = "×", FontSize = 18, Padding = new Thickness(0),
				Background = Brushes.WhiteSmoke, Foreground = Brushes.DimGray,
				BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1),
				Cursor = Cursors.Hand, Focusable = false,
				ToolTip = "Закрыть панель индикаторов",
				Command = Controller.CloseIndicatorPaneCommand
			};
			System.Windows.Automation.AutomationProperties.SetName(indicatorCloseButton, "Закрыть панель индикаторов");
			AddVisualChild(indicatorCloseButton);
			AddLogicalChild(indicatorCloseButton);
		}
		else if (!Controller.IsIndicatorPaneVisible && indicatorCloseButton != null)
		{
			RemoveVisualChild(indicatorCloseButton);
			RemoveLogicalChild(indicatorCloseButton);
			indicatorCloseButton.Command = null;
			indicatorCloseButton = null;
			arrangedCloseButtonBounds = Rect.Empty;
		}
		InvalidateMeasure();
		InvalidateVisual();
	}

	protected override int VisualChildrenCount => indicatorCloseButton == null ? 0 : 1;
	protected override Visual GetVisualChild(int index) => index == 0 && indicatorCloseButton != null
		? indicatorCloseButton : throw new ArgumentOutOfRangeException(nameof(index));
	protected override IEnumerator LogicalChildren => indicatorCloseButton == null
		? Array.Empty<object>().GetEnumerator() : new object[] { indicatorCloseButton }.GetEnumerator();

	protected override Size MeasureOverride(Size availableSize)
	{
		indicatorCloseButton?.Measure(new Size(22, 22));
		return new Size();
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		Controller.SetViewSize(finalSize.Width, finalSize.Height);
		if (indicatorCloseButton != null)
		{
			arrangedCloseButtonBounds = Controller.State.IndicatorCloseButtonBounds;
			indicatorCloseButton.Arrange(arrangedCloseButtonBounds.IsEmpty ? new Rect() : arrangedCloseButtonBounds);
		}
		return finalSize;
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
		if (args.Handled) return;
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
		if (args.Handled) return;
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
	public void CloseIndicatorPane() => Controller.CloseIndicatorPane();
	public void ShowRsi() => Controller.ShowRsi();
	public List<Indicator> GetIndicators() => Controller.GetIndicators();
	public void SetIndicatorPaneRatio(double ratio) => Controller.IndicatorPaneHeightRatio = ratio;
	public void AutoFitIndicatorViewport() => Controller.AutoFitIndicatorViewport();
	public RSIIndicator AddRSI(int period = 14) => Controller.AddRSI(period);
	public void RecalculateIndicators(bool invalidateVisual = true) => Controller.RecalculateIndicators(invalidateVisual);
}
