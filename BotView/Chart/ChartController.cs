using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using BotView.ViewModels;
using BotView.Chart.TechnicalAnalysis;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BotView.Chart.IndicatorPane;
using BotView.Services;
using BotView.Models;
using BotView.Configuration;

namespace BotView.Chart;

/// <summary>Identifies which pane the mouse is currently interacting with</summary>
public enum ChartPane
{
	None,
	Main,       // Candlestick chart pane
	Indicator,  // Indicator pane at bottom
	Divider     // Divider between panes
}

/// <summary>
/// Управляет состоянием и командами графика; геометрию и шкалы делегирует отдельным компонентам.
/// </summary>
public class ChartController : INotifyPropertyChanged
{
	private readonly ChartState state;

	private ChartInteractionState interaction => state.Interaction;
	private readonly SemaphoreSlim symbolSwitch = new(1, 1);
	private int pendingSymbolSwitches;
	public ChartState State => state;
	public ChartRenderFrame PrepareRenderFrame() => new(state, Transform, Axes,
		state.Series.GetSnapshot(), cachedTimeframeSpan);
	public TechnicalAnalysisTool? SelectedTool => interaction.SelectedTool;
	public TechnicalAnalysisToolType ActiveToolType => interaction.ActiveToolType;
	public Coordinates CameraPosition => state.CameraPosition;
	public ViewportClippingCoords Viewport => state.Viewport;
	public bool IsReady => state.IsInitialized && state.ChartWidth > 0 && state.MainPaneHeight > 0 && state.Series.GetSnapshot().Count > 0;
	public IReadOnlyList<Indicator> Indicators => state.Indicators;
	public bool IsIndicatorPaneVisible => state.IsIndicatorPaneVisible;
	public bool IsSwitchingSymbol => state.IsSwitchingSymbol;
	public bool ShowCandleOutlines
	{
		get => state.ShowCandleOutlines;
		set
		{
			if (state.ShowCandleOutlines == value) return;
			state.ShowCandleOutlines = value;
			OnPropertyChanged();
			RequestRender();
		}
	}
	public double IndicatorPaneHeightRatio
	{
		get => state.IndicatorPaneHeightRatio;
		set
		{
			if (!double.IsFinite(value)) return;
			value = Math.Clamp(value, state.MinIndicatorPaneRatio, state.MaxIndicatorPaneRatio);
			if (state.IndicatorPaneHeightRatio == value) return;
			state.IndicatorPaneHeightRatio = value;
			UpdateViewportFromCamera();
		}
	}
	public ICommand FitToDataCommand { get; }
	public ICommand ResetPriceScaleCommand { get; }
	public ICommand ResetTimeScaleCommand { get; }
	public ICommand SnapLastToRightCommand { get; }
	public ICommand BeginToolCreationCommand { get; }
	public ICommand CancelInteractionCommand { get; }
	public ICommand DeleteSelectedToolCommand { get; }
	public ICommand ShowRsiCommand { get; }
	public ICommand CloseIndicatorPaneCommand { get; }
	public event PropertyChangedEventHandler? PropertyChanged;
	public event Action? RenderRequested;
	public event Action<InteractionResult>? ViewActionRequested;

	/// <summary> Событие изменения viewport (для уведомления о необходимости перерисовки) </summary>
	public event Action? ViewportChanged;

	/// <summary> Срабатывает когда viewport приближается к левому краю данных. </summary>
	public event Action? LeftEdgeApproached;

	public ChartTransform Transform { get; private set; } = null!;
	public AxisLayout Axes { get; private set; } = null!;
	private TimeSpan cachedTimeframeSpan;
	public double CandleWidthPixels => Transform.CandleWidthPixels;
	public double PriceInterval => Axes.PriceInterval;
	public TimeSpan TimeInterval => Axes.TimeInterval;
	public double IndicatorValueInterval => Axes.IndicatorValueInterval;

	public ChartController(ChartState? state = null)
	{
		this.state = state ?? new ChartState();
		FitToDataCommand = new RelayCommand(_ => FitToData(), _ => IsReady);
		ResetPriceScaleCommand = new RelayCommand(_ => ResetPriceScaleToCurrentPrice(), _ => IsReady);
		ResetTimeScaleCommand = new RelayCommand(_ => ResetTimeScaleToTimeframe(), _ => IsReady);
		SnapLastToRightCommand = new RelayCommand(_ => SnapLastCandleToRightEdge(), _ => IsReady);
		BeginToolCreationCommand = new RelayCommand(parameter =>
		{
			if (parameter is TechnicalAnalysisToolType type) BeginToolCreation(type);
		}, parameter => IsReady && !IsSwitchingSymbol && parameter is TechnicalAnalysisToolType type && IsSupportedTool(type));
		CancelInteractionCommand = new RelayCommand(RequestCancellation);
		DeleteSelectedToolCommand = new RelayCommand(_ => DeleteSelectedTool(), _ => SelectedTool != null);
		ShowRsiCommand = new RelayCommand(ShowRsi);
		CloseIndicatorPaneCommand = new RelayCommand(CloseIndicatorPane);
		RefreshRenderCaches();
	}

	public void RefreshRenderCaches()
	{
		cachedTimeframeSpan = ParseTimeframe(state.Timeframe);
		Transform = new ChartTransform(state.CameraPosition, state.TimeRangeInViewport,
			state.PriceRangeInViewport, state.WorldOriginTime, state.WorldOriginPrice,
			state.LeftMargin, state.TopMargin, state.ChartWidth, state.MainPaneHeight,
			state.IndicatorPaneTop, state.IndicatorPaneHeight, state.IndicatorCameraY,
			state.IndicatorRangeInViewport, cachedTimeframeSpan);
		Axes = new AxisLayout(state.Viewport, state.IndicatorViewport, state.MainPaneHeight,
			cachedTimeframeSpan, IsIndicatorPaneVisible);
	}

#region === COORDINATE CONVERSION METHODS ===
	public Coordinates ChartToWorld(ChartCoordinates point) => Transform.ChartToWorld(point);
	public ChartCoordinates WorldToChart(Coordinates point) => Transform.WorldToChart(point);
	public Coordinates WorldToView(Coordinates point) => Transform.WorldToView(point);
	public Coordinates ViewToWorld(Coordinates point) => Transform.ViewToWorld(point);
	internal Coordinates ChartToView(ChartCoordinates point) => Transform.ChartToView(point);
	internal double TimeToViewX(DateTime time) => Transform.TimeToViewX(time);
	internal double PriceToViewY(double price) => Transform.PriceToViewY(price);
	public ChartCoordinates ViewToChart(Coordinates point) => Transform.ViewToChart(point);
	public Coordinates IndicatorToView(DateTime time, double value) => Transform.IndicatorToView(time, value);
	internal double IndicatorValueToViewY(double value) => Transform.IndicatorValueToViewY(value);
	public (DateTime time, double value) ViewToIndicator(Coordinates point) => Transform.ViewToIndicator(point);
	/// <summary>Updates the indicator viewport based on indicator camera position</summary>
	private void RebuildIndicatorViewport()
	{
		if (!IsIndicatorPaneVisible) return;
		double minValue = state.IndicatorCameraY - state.IndicatorRangeInViewport / 2;
		double maxValue = state.IndicatorCameraY + state.IndicatorRangeInViewport / 2;
		state.IndicatorViewport = new IndicatorViewport(minValue, maxValue);
	}

	/// <summary>Detects which pane a point is in</summary>
	public ChartPane DetectPane(Point point)
	{
		// Check if in divider area
		if (IsIndicatorPaneVisible && point.Y >= state.DividerY && point.Y <= state.DividerY + state.DividerHeight &&
			point.X >= state.LeftMargin && point.X <= state.LeftMargin + state.ChartWidth)
		{
			return ChartPane.Divider;
		}

		// Check if in main pane
		if (point.Y >= state.TopMargin && point.Y < state.DividerY &&
			point.X >= state.LeftMargin && point.X <= state.LeftMargin + state.ChartWidth)
		{
			return ChartPane.Main;
		}

		// Check if in indicator pane
		if (IsIndicatorPaneVisible && point.Y > state.IndicatorPaneTop && point.Y <= state.IndicatorPaneTop + state.IndicatorPaneHeight &&
			point.X >= state.LeftMargin && point.X <= state.LeftMargin + state.ChartWidth)
		{
			return ChartPane.Indicator;
		}

		return ChartPane.None;
	}

	/// <summary>Обновляет viewport на основе текущей позиции камеры</summary>
	public void UpdateViewportFromCamera()
	{
		// Вычисляем границы viewport в chart координатах
		Coordinates topLeft = new Coordinates(
			state.CameraPosition.x - state.TimeRangeInViewport.TotalSeconds / 2,
			state.CameraPosition.y + state.PriceRangeInViewport / 2
		);
			
		Coordinates bottomRight = new Coordinates(
			state.CameraPosition.x + state.TimeRangeInViewport.TotalSeconds / 2,
			state.CameraPosition.y - state.PriceRangeInViewport / 2
		);

		ChartCoordinates topLeftChart = new(state.WorldOriginTime.AddSeconds(topLeft.x), state.WorldOriginPrice + topLeft.y);
		ChartCoordinates bottomRightChart = new(state.WorldOriginTime.AddSeconds(bottomRight.x), state.WorldOriginPrice + bottomRight.y);

		state.Viewport = new ViewportClippingCoords(
			bottomRightChart.price,  // minPrice
			topLeftChart.price,      // maxPrice
			topLeftChart.time,       // minTime
			bottomRightChart.time    // maxTime
		);

		// Also update indicator viewport
		RebuildIndicatorViewport();

		RefreshRenderCaches();

		// Помечаем все инструменты технического анализа для перерисовки при изменении viewport
		state.TechnicalAnalysisManager.MarkAllToolsForRedrawing();

		var candles = state.Series.GetSnapshot();
		if (candles.Count > 0)
		{
			var threshold = cachedTimeframeSpan * 20;
			if (state.Viewport.minTime - candles[0].GetDateTime() < threshold)
			{
				LeftEdgeApproached?.Invoke();
			}
		}

		// Уведомляем об изменении viewport
		NotifyGeometryChanged();
	}

#endregion
#region === CAMERA CONTROL METHODS ===

	/// <summary> Инициализация камеры (вызывается один раз при старте) </summary>
	public void InitializeCamera()
	{
		var candles = state.Series.GetSnapshot();
		if (candles.Count == 0 || state.ChartWidth <= 0 || state.MainPaneHeight <= 0) return;
		state.TimeRangeInViewport = ParseTimeframe(state.Timeframe) * 100;
		var last = candles[candles.Count - 1];
		state.WorldOriginTime = candles[0].GetDateTime();
		state.WorldOriginPrice = last.close;
		var center = last.GetDateTime().Subtract(TimeSpan.FromTicks(state.TimeRangeInViewport.Ticks / 2));
		state.CameraPosition = new Coordinates((center - state.WorldOriginTime).TotalSeconds, 0);
		state.PriceRangeInViewport = Math.Clamp(Math.Abs(last.close) * 0.20, 1e-8, 1000000);
		state.IsInitialized = true;
		UpdateViewportFromCamera();
	}

	/// <summary>
	/// Перемещение камеры в мировых координатах
	/// </summary>
	/// <param name="deltaWorldX">Изменение по X в мировых координатах (секунды)</param>
	/// <param name="deltaWorldY">Изменение по Y в мировых координатах (единицы цены)</param>
	public void Pan(double deltaWorldX, double deltaWorldY)
	{
		state.CameraPosition = new Coordinates(
			state.CameraPosition.x + deltaWorldX,
			state.CameraPosition.y + deltaWorldY
		);
			
		UpdateViewportFromCamera();
	}

	/// <summary>
	/// Перемещение камеры на основе движения пикселей экрана
	/// Полезно для перетаскивания мышью
	/// </summary>
	/// <param name="deltaScreenX">Изменение по X экрана (пиксели)</param>
	/// <param name="deltaScreenY">Изменение по Y экрана (пиксели)</param>
	public void PanByPixels(double deltaScreenX, double deltaScreenY)
	{
		double deltaWorldX = Transform.PixelsPerSecond > 0
			? -deltaScreenX / Transform.PixelsPerSecond
			: 0;
		double deltaWorldY = Transform.PixelsPerPriceUnit > 0
			? deltaScreenY / Transform.PixelsPerPriceUnit
			: 0;

		Pan(deltaWorldX, deltaWorldY);
	}

	/// <summary>
	/// Масштабирование камеры (изменяет сколько времени и цены видно)
	/// </summary>
	/// <param name="zoomFactorX">Фактор масштабирования по X (1.0 = без изменений, 2.0 = увеличить в 2x, 0.5 = уменьшить в 2x)</param>
	/// <param name="zoomFactorY">Фактор масштабирования по Y</param>
	/// <param name="worldFocusX">Мировая X координата для фокуса масштабирования (опционально)</param>
	/// <param name="worldFocusY">Мировая Y координата для фокуса масштабирования (опционально)</param>
	public void Zoom(double zoomFactorX, double zoomFactorY, double? worldFocusX = null, double? worldFocusY = null)
	{
		if (!double.IsFinite(zoomFactorX) || !double.IsFinite(zoomFactorY) || zoomFactorX <= 0 || zoomFactorY <= 0) return;
		double focusX = worldFocusX ?? state.CameraPosition.x;
		double focusY = worldFocusY ?? state.CameraPosition.y;
		double oldTime = state.TimeRangeInViewport.TotalSeconds;
		double oldPrice = state.PriceRangeInViewport;
		double seconds = Math.Clamp(oldTime * zoomFactorX, 60, TimeSpan.FromDays(3650).TotalSeconds);
		state.TimeRangeInViewport = TimeSpan.FromSeconds(seconds);
		state.PriceRangeInViewport = Math.Clamp(oldPrice * zoomFactorY, 1e-8, 1000000);
		double appliedX = state.TimeRangeInViewport.TotalSeconds / oldTime;
		double appliedY = state.PriceRangeInViewport / oldPrice;
		state.CameraPosition = new Coordinates(
			focusX - (focusX - state.CameraPosition.x) * appliedX,
			focusY - (focusY - state.CameraPosition.y) * appliedY);
		UpdateViewportFromCamera();
	}

	/// <summary>
	/// Масштабирование к определенной точке экрана (полезно для колеса мыши)
	/// </summary>
	public void ZoomAtScreenPoint(double screenX, double screenY, double zoomFactor)
	{
		Coordinates viewCoords = new Coordinates(screenX, screenY);
		Coordinates worldPoint = ViewToWorld(viewCoords);
		Zoom(zoomFactor, zoomFactor, worldPoint.x, worldPoint.y);
	}

	/// <summary>
	/// Масштабирование к определенному времени и цене (полезно для навигации к конкретной свече)
	/// </summary>
	public void ZoomToChartPoint(DateTime time, double price, double zoomFactor)
	{
		ChartCoordinates chartCoords = new ChartCoordinates(time, price);
		Coordinates worldPoint = ChartToWorld(chartCoords);
		Zoom(zoomFactor, zoomFactor, worldPoint.x, worldPoint.y);
	}

	/// <summary>
	/// Масштабирование только по одной оси с сохранением фокуса на центре экрана
	/// </summary>
	/// <param name="timeZoomFactor">Фактор масштабирования по времени</param>
	/// <param name="priceZoomFactor">Фактор масштабирования по цене</param>
	public void ZoomAxis(double timeZoomFactor, double priceZoomFactor)
	{
		// Используем центр main pane как точку фокуса
		double centerScreenX = state.LeftMargin + state.ChartWidth / 2;
		double centerScreenY = state.TopMargin + state.MainPaneHeight / 2;
			
		Coordinates centerView = new Coordinates(centerScreenX, centerScreenY);
		Coordinates centerWorld = ViewToWorld(centerView);
			
		Zoom(timeZoomFactor, priceZoomFactor, centerWorld.x, centerWorld.y);
	}
#endregion
	// === MOUSE INTERACTION METHODS ===

	/// <summary>
	/// Обработка нажатия левой кнопки мыши
	/// </summary>
	/// <param name="mousePos">Позиция мыши</param>
	/// <returns>Информация о том, нужно ли захватить мышь</returns>
	private InteractionResult BeginNavigation(Point mousePos)
	{
		interaction.LastMousePosition = mousePos;
		interaction.ActivePane = DetectPane(mousePos);

		// Check if clicking on divider
		if (interaction.ActivePane == ChartPane.Divider)
		{
			interaction.IsDraggingDivider = true;
			return new InteractionResult(true, Cursors.SizeNS, MouseCaptureAction.Capture);
		}
			
		// Проверяем, кликнул ли пользователь в области шкал
		ScaleZoomMode detectedMode = DetectScaleArea(mousePos);
			
		if (detectedMode != ScaleZoomMode.None)
		{
			// Начинаем масштабирование через шкалы
			interaction.IsScaleZooming = true;
			interaction.ScaleZoomMode = detectedMode;
			Cursor cursor = detectedMode == ScaleZoomMode.TimeScale ? Cursors.SizeWE : Cursors.SizeNS;
			return new InteractionResult(true, cursor, MouseCaptureAction.Capture);
		}
		else
		{
			// Обычное перетаскивание графика
			interaction.IsDragging = true;
			return new InteractionResult(true, Cursors.Arrow, MouseCaptureAction.Capture);
		}
	}

	/// <summary>
	/// Обработка отпускания левой кнопки мыши
	/// </summary>
	private void EndNavigation()
	{
		interaction.IsDragging = false;
		interaction.IsScaleZooming = false;
		interaction.IsDraggingDivider = false;
		interaction.ScaleZoomMode = ScaleZoomMode.None;
		interaction.ActivePane = ChartPane.None;
	}

	/// <summary>
	/// Обработка движения мыши
	/// </summary>
	/// <param name="currentPosition">Текущая позиция мыши</param>
	/// <returns>Курсор для установки</returns>
	private Cursor? MoveNavigation(Point currentPosition)
	{
		if (interaction.IsDraggingDivider)
		{
			// Handle divider dragging to resize panes
			HandleDividerDrag(currentPosition.Y);
			interaction.LastMousePosition = currentPosition;
			return Cursors.SizeNS;
		}
		else if (interaction.IsScaleZooming)
		{
			// Масштабирование через шкалы
			double deltaX = currentPosition.X - interaction.LastMousePosition.X;
			double deltaY = currentPosition.Y - interaction.LastMousePosition.Y;
				
			HandleScaleZoom(deltaX, deltaY);
				
			interaction.LastMousePosition = currentPosition;
			return interaction.ScaleZoomMode == ScaleZoomMode.TimeScale ? Cursors.SizeWE : Cursors.SizeNS;
		}
		else if (interaction.IsDragging)
		{
			// Обычное перетаскивание графика
			double deltaX = currentPosition.X - interaction.LastMousePosition.X;
			double deltaY = currentPosition.Y - interaction.LastMousePosition.Y;
				
			PanByPixels(deltaX, deltaY);
				
			interaction.LastMousePosition = currentPosition;
			return Cursors.Arrow;
		}
		else
		{
			// Check if hovering over divider
			ChartPane hoverPane = DetectPane(currentPosition);
			if (hoverPane == ChartPane.Divider)
			{
				return Cursors.SizeNS;
			}

			// Изменяем курсор при наведении на шкалы
			ScaleZoomMode hoverMode = DetectScaleArea(currentPosition);
			return hoverMode switch
			{
				ScaleZoomMode.TimeScale => Cursors.SizeWE,
				ScaleZoomMode.PriceScale => Cursors.SizeNS,
				ScaleZoomMode.IndicatorScale => Cursors.SizeNS,
				_ => Cursors.Arrow
			};
		}
	}

	/// <summary>Handles divider dragging to resize panes</summary>
	private void HandleDividerDrag(double newY)
	{
		double available = state.ChartHeight - state.DividerHeight;
		if (available <= 0) return;
		IndicatorPaneHeightRatio = 1 - (newY - state.TopMargin) / available;
	}

	/// <summary>
	/// Обработка колесика мыши
	/// </summary>
	/// <param name="mousePosition">Позиция мыши</param>
	/// <param name="delta">Изменение колесика</param>
	public void HandleMouseWheel(Point mousePosition, int delta)
	{
		if (!IsReady || delta == 0) return;
		// Масштабирование к позиции курсора
		double zoomFactor = delta > 0 ? 0.9 : 1.1; // Zoom in/out
		ZoomAtScreenPoint(mousePosition.X, mousePosition.Y, zoomFactor);
	}

	/// <summary>
	/// Определяет, находится ли точка в области шкал
	/// </summary>
	/// <param name="point">Точка для проверки</param>
	/// <returns>Режим масштабирования или None</returns>
	private ScaleZoomMode DetectScaleArea(Point point)
	{
		const double tolerance = 15; // Погрешность в пикселях

		// Проверяем область шкалы времени (внизу под indicator pane)
		double timeScaleY = state.IndicatorPaneTop + state.IndicatorPaneHeight;
		if (point.Y >= timeScaleY - tolerance && point.Y <= timeScaleY + state.BottomMargin &&
			point.X >= state.LeftMargin && point.X <= state.LeftMargin + state.ChartWidth)
		{
			return ScaleZoomMode.TimeScale;
		}

		// Проверяем область шкалы цены main pane (справа от main pane)
		double priceScaleX = state.LeftMargin + state.ChartWidth;
		if (point.X >= priceScaleX - tolerance && point.X <= priceScaleX + state.RightMargin &&
			point.Y >= state.TopMargin && point.Y < state.DividerY)
		{
			return ScaleZoomMode.PriceScale;
		}

		// Проверяем область шкалы индикатора (справа от indicator pane)
		if (IsIndicatorPaneVisible && point.X >= priceScaleX - tolerance && point.X <= priceScaleX + state.RightMargin &&
			point.Y > state.IndicatorPaneTop && point.Y <= state.IndicatorPaneTop + state.IndicatorPaneHeight)
		{
			return ScaleZoomMode.IndicatorScale;
		}

		return ScaleZoomMode.None;
	}

	/// <summary>
	/// Обрабатывает масштабирование через шкалы
	/// </summary>
	/// <param name="deltaX">Изменение по X</param>
	/// <param name="deltaY">Изменение по Y</param>
	private void HandleScaleZoom(double deltaX, double deltaY)
	{
		const double sensitivity = 0.01; // Чувствительность масштабирования

		switch (interaction.ScaleZoomMode)
		{
			case ScaleZoomMode.TimeScale:
				// Масштабирование по времени (влево-вправо)
				// Положительное deltaX = движение вправо = увеличение масштаба (zoom out)
				// Отрицательное deltaX = движение влево = уменьшение масштаба (zoom in)
				double timeZoomFactor = 1.0 + (deltaX * sensitivity);
				timeZoomFactor = Math.Clamp(timeZoomFactor, 0.5, 2.0); // Ограничиваем скорость
					
				// Масштабируем только по времени, цену оставляем без изменений
				ZoomAxis(timeZoomFactor, 1.0);
				break;

			case ScaleZoomMode.PriceScale:
				// Масштабирование по цене (вверх-вниз) for main pane
				// Положительное deltaY = движение вниз = увеличение масштаба (zoom out)
				// Отрицательное deltaY = движение вверх = уменьшение масштаба (zoom in)
				double priceZoomFactor = 1.0 + (deltaY * sensitivity);
				priceZoomFactor = Math.Clamp(priceZoomFactor, 0.5, 2.0); // Ограничиваем скорость
					
				// Масштабируем только по цене, время оставляем без изменений
				ZoomAxis(1.0, priceZoomFactor);
				break;

			case ScaleZoomMode.IndicatorScale:
				// Масштабирование по значению индикатора (вверх-вниз) for indicator pane
				double indicatorZoomFactor = 1.0 + (deltaY * sensitivity);
				indicatorZoomFactor = Math.Clamp(indicatorZoomFactor, 0.5, 2.0);
				
				ZoomIndicatorAxis(indicatorZoomFactor);
				break;
		}
	}

	/// <summary>Zooms the indicator pane Y-axis independently</summary>
	public void ZoomIndicatorAxis(double zoomFactor)
	{
		if (!IsIndicatorPaneVisible) return;
		if (!double.IsFinite(zoomFactor) || zoomFactor <= 0) return;
		state.IndicatorRangeInViewport = Math.Clamp(state.IndicatorRangeInViewport * zoomFactor, 1, 10000);
		UpdateViewportFromCamera();
	}

	// === PUBLIC API METHODS ===

	///<summary> Устанавливает новые данные свечей </summary>
	public void SetCandlestickData(CandlestickData newData)
	{
		var series = new CandleSeries();
		series.LoadInitial(newData.candles ?? Array.Empty<OHLCV>());
		SetSeries(series, newData.timeframe);
	}

	public void SetSeries(ICandleSeriesReader series, string timeframe)
	{
		state.Series = series ?? throw new ArgumentNullException(nameof(series));
		state.Timeframe = timeframe;
		state.UpdateDataRange();
		if (series.GetSnapshot().Count == 0) state.IsInitialized = false;
		RecalculateIndicators(false);
		if (!state.IsInitialized && series.GetSnapshot().Count > 0 && state.ChartWidth > 0 && state.MainPaneHeight > 0)
			InitializeCamera();
		else UpdateViewportFromCamera();
	}

	/// <summary>Re-evaluates the viewport after history was added to the shared series.</summary>
	public void OnHistoryExtended()
	{
		state.UpdateDataRange();
		RecalculateIndicators(false);
		UpdateViewportFromCamera();
	}

	/// <summary>Requests a redraw after the shared live candle changes.</summary>
	public void OnLiveCandleUpdated() => RequestRender();

	/// <summary>Refreshes the data range after the shared series closes a candle.</summary>
	public void OnCandleClosed()
	{
		state.UpdateDataRange();
		RecalculateIndicators(false);
		RefreshRenderCaches();
		RequestRender();
	}

	/// <summary>
	/// Центрирует график на определенном времени
	/// </summary>
	public void CenterOnTime(DateTime time)
	{
		ChartCoordinates chartCoords = new ChartCoordinates(time, (state.Viewport.maxPrice + state.Viewport.minPrice) / 2);
		Coordinates worldCoords = ChartToWorld(chartCoords);
			
		state.CameraPosition = new Coordinates(worldCoords.x, state.CameraPosition.y);
		UpdateViewportFromCamera();
	}

	/// <summary>
	/// Центрирует график на определенной цене
	/// </summary>
	public void CenterOnPrice(double price)
	{
		ChartCoordinates chartCoords = new ChartCoordinates(DateTime.Now, price);
		Coordinates worldCoords = ChartToWorld(chartCoords);
			
		state.CameraPosition = new Coordinates(state.CameraPosition.x, worldCoords.y);
		UpdateViewportFromCamera();
	}

	/// <summary>
	/// Подгоняет график чтобы показать все данные
	/// </summary>
	public void FitToData()
	{
		var candles = state.Series.GetSnapshot();
		if (candles.Count == 0)
			return;

		TimeSpan dataTimeRange = candles[candles.Count - 1].GetDateTime() - candles[0].GetDateTime();
		state.TimeRangeInViewport = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(1).Ticks, (long)(dataTimeRange.Ticks * 1.2))); // 20% padding

		DateTime lastCandleTime = candles[candles.Count - 1].GetDateTime();
		TimeSpan halfRange = TimeSpan.FromTicks(state.TimeRangeInViewport.Ticks / 2);
		DateTime centerTime = lastCandleTime.Subtract(halfRange);
		double centerPrice = candles[candles.Count - 1].close;

		ChartCoordinates centerChart = new ChartCoordinates(centerTime, centerPrice);
		state.CameraPosition = ChartToWorld(centerChart);

		ResetPriceScaleToCurrentPrice();
	}

	/// <summary> Сбрасывает вертикальный масштаб: ±10% от текущей цены на всю высоту экрана </summary>
	public void ResetPriceScaleToCurrentPrice()
	{
		var candles = state.Series.GetSnapshot();
		if (candles.Count == 0)
			return;

		double currentPrice = candles[candles.Count - 1].close;
		if (!double.IsFinite(currentPrice) || Math.Abs(currentPrice) < double.Epsilon)
			return;

		// Полный видимый диапазон = 20% цены: 10% вниз и 10% вверх от текущей цены
		state.PriceRangeInViewport = Math.Clamp(Math.Abs(currentPrice) * 0.20, 1e-8, 1000000);

		Coordinates priceWorld = ChartToWorld(new ChartCoordinates(DateTime.Now, currentPrice));
		state.CameraPosition = new Coordinates(state.CameraPosition.x, priceWorld.y);

		UpdateViewportFromCamera();
	}

	/// <summary> Сбрасывает горизонтальный масштаб: фиксированное число свечей текущего таймфрейма в ширину экрана </summary>
	public void ResetTimeScaleToTimeframe()
	{
		const int targetVisibleCandles = 100;

		TimeSpan candleDuration = ParseTimeframe(state.Timeframe);
		if (candleDuration <= TimeSpan.Zero)
			return;

		state.TimeRangeInViewport = TimeSpan.FromTicks(candleDuration.Ticks * targetVisibleCandles);
		UpdateViewportFromCamera();
	}

	/// <summary>
	/// Получает текущий viewport в chart координатах
	/// </summary>
	public ViewportClippingCoords GetCurrentViewport()
	{
		return state.Viewport;
	}

	/// <summary>
	/// Получает позицию камеры в chart координатах
	/// </summary>
	public ChartCoordinates GetCameraPosition()
	{
		return WorldToChart(state.CameraPosition);
	}

	/// <summary>
	/// Позиционирует камеру так, чтобы тело последней свечи касалось правой границы viewport.
	/// </summary>
	public void SnapLastCandleToRightEdge()
	{
		var candles = state.Series.GetSnapshot();
		if (candles.Count == 0)
			return;

		int lastIndex = candles.Count - 1;
		DateTime lastCandleTime = CandleTime.Get(candles, lastIndex, cachedTimeframeSpan);

		double pixelsPerSecond = state.ChartWidth / state.TimeRangeInViewport.TotalSeconds;
		if (pixelsPerSecond <= 0)
			return;

		double halfViewportSeconds = state.TimeRangeInViewport.TotalSeconds / 2.0;
		double candleHalfWidthSeconds = (GetCandleWidthPixels() / 2.0) / pixelsPerSecond;

		// Смещаем центр камеры так, чтобы правая грань тела свечи касалась правой границы области графика.
		double targetOffsetSeconds = halfViewportSeconds - candleHalfWidthSeconds;

		var lastCandle = candles[lastIndex];
		Coordinates lastCandleWorld = ChartToWorld(new ChartCoordinates(lastCandleTime, lastCandle.close));

		state.CameraPosition = new Coordinates(
			lastCandleWorld.x - targetOffsetSeconds,
			lastCandleWorld.y
		);

		UpdateViewportFromCamera();
	}

	// === HELPER METHODS ===

	/// <summary>
	/// Получает время свечи на основе индекса и timeframe
	/// Uses timestamp from OHLCV data when available, falls back to calculation for backward compatibility
	/// </summary>
	public DateTime GetCandleTime(int candleIndex) =>
		CandleTime.Get(state.Series.GetSnapshot(), candleIndex, ParseTimeframe(state.Timeframe));

	/// <summary>
	/// Парсит строку timeframe в TimeSpan
	/// </summary>
	public TimeSpan ParseTimeframe(string timeframe)
	{
		return MarketCatalog.TryGetTimeframe(timeframe, out var definition)
			? definition!.Duration
			: MarketCatalog.GetTimeframe(MarketCatalog.DefaultTimeframeId).Duration;
	}

	/// <summary>
	/// Вычисляет ширину свечи в пикселях на основе текущего масштаба
	/// </summary>
	public double GetCandleWidthPixels()
	{
		return Transform.CandleWidthPixels;
	}

	public double CalculateOptimalIndicatorInterval() => Axes.IndicatorValueInterval;
	public TimeSpan CalculateOptimalTimeInterval() => Axes.TimeInterval;
	public double CalculateOptimalPriceInterval() => Axes.PriceInterval;
	public DateTime RoundDownToInterval(DateTime time, TimeSpan interval) => Axes.RoundDownToInterval(time, interval);
	public string FormatTimeLabel(DateTime time, TimeSpan interval) => Axes.FormatTimeLabel(time, interval);
	public string FormatPriceLabel(double price, double? interval = null) => Axes.FormatPriceLabel(price, interval);
	public double GetMinimumDisplayedPriceStep() => Axes.GetMinimumDisplayedPriceStep();
	public string FormatCrosshairTimeLabel(DateTime time) => Axes.FormatCrosshairTimeLabel(time);

	private void HandleTrendLineCreation(ChartCoordinates chartCoords)
	{
		if (interaction.CreationStep == 0)
		{
			// Первый клик: сохраняем первую точку
			interaction.CreationPoints[0] = chartCoords;
			interaction.CreationStep = 1;
			NotifyInteractionChanged();
		}
		else if (interaction.CreationStep == 1 && interaction.CreationPoints[0].HasValue)
		{
			// Второй клик: создаём линию
			var firstPoint = interaction.CreationPoints[0]!.Value;
			var newTool = new TrendLine(
				startTime: firstPoint.time,
				startPrice: firstPoint.price,
				endTime: chartCoords.time,
				endPrice: chartCoords.price,
				color: Brushes.Blue,
				thickness: 2.0,
				style: LineStyle.Solid
			);

			state.TechnicalAnalysisManager.AddTool(newTool, TechnicalAnalysisToolType.TrendLine);
			StopCreating();
			NotifyInteractionChanged();
		}
	}

	/// <summary>Обработка создания трендового канала (три клика)</summary>
	private void HandleTrendChannelCreation(ChartCoordinates chartCoords)
	{
		if (interaction.CreationStep == 0)
		{
			// Первый клик: сохраняем первую точку
			interaction.CreationPoints[0] = chartCoords;
			interaction.CreationStep = 1;

			NotifyInteractionChanged();
		}
		else if (interaction.CreationStep == 1 && interaction.CreationPoints[0].HasValue)
		{
			// Второй клик: создаём первую линию канала и переходим к шагу 2
			var firstPoint = interaction.CreationPoints[0]!.Value;
			
			// Создаём временный канал с нулевым offset для превью
			var channel = new TrendChannel(
				startTime: firstPoint.time,
				startPrice: firstPoint.price,
				endTime: chartCoords.time,
				endPrice: chartCoords.price,
				parallelOffset: 0,
				color: Brushes.Green,
				thickness: 2.0,
				style: LineStyle.Solid
			);

			interaction.CreationPoints[1] = chartCoords;
			interaction.CreatingToolInstance = channel;
			interaction.CreationStep = 2;
			NotifyInteractionChanged();
		}
		else if (interaction.CreationStep == 2 && 
				 interaction.CreatingToolInstance is TrendChannel channel)
		{
			// Третий клик: устанавливаем offset параллельной линии и завершаем создание
			double parallelOffset = chartCoords.price - channel.StartPrice;
			channel.ParallelOffset = parallelOffset;

			state.TechnicalAnalysisManager.AddTool(channel, TechnicalAnalysisToolType.TrendChannel);
			StopCreating();
			NotifyInteractionChanged();
		}
	}

	/// <summary>Обработка создания прямоугольника (два клика)</summary>
	private void HandleRectangleCreation(ChartCoordinates chartCoords)
	{
		if (interaction.CreationStep == 0)
		{
			// Первый клик: сохраняем первый угол
			interaction.CreationPoints[0] = chartCoords;
			interaction.CreationStep = 1;
			NotifyInteractionChanged();
		}
		else if (interaction.CreationStep == 1 && interaction.CreationPoints[0].HasValue)
		{
			// Второй клик: создаём прямоугольник по диагонали
			var firstPoint = interaction.CreationPoints[0]!.Value;
			var newTool = new TechnicalAnalysis.Rectangle(
				startTime: firstPoint.time,
				startPrice: firstPoint.price,
				endTime: chartCoords.time,
				endPrice: chartCoords.price,
				color: Brushes.Orange,
				thickness: 2.0,
				style: LineStyle.Solid
			);

			state.TechnicalAnalysisManager.AddTool(newTool, TechnicalAnalysisToolType.Rectangle);
			StopCreating();
			NotifyInteractionChanged();
		}
	}


	private void OnPropertyChanged([CallerMemberName] string? name = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

	private void RequestRender() => RenderRequested?.Invoke();

	private void RefreshCommandAvailability()
	{
		foreach (var command in new[] { FitToDataCommand, ResetPriceScaleCommand,
			ResetTimeScaleCommand, SnapLastToRightCommand, BeginToolCreationCommand,
			CancelInteractionCommand, DeleteSelectedToolCommand })
			((RelayCommand)command).RaiseCanExecuteChanged();
	}

	private void NotifyGeometryChanged()
	{
		OnPropertyChanged(nameof(CameraPosition));
		OnPropertyChanged(nameof(Viewport));
		OnPropertyChanged(nameof(IndicatorPaneHeightRatio));
		OnPropertyChanged(nameof(IsReady));
		RefreshCommandAvailability();
		ViewportChanged?.Invoke();
		RequestRender();
	}

	private void NotifyInteractionChanged()
	{
		OnPropertyChanged(nameof(SelectedTool));
		OnPropertyChanged(nameof(ActiveToolType));
		RefreshCommandAvailability();
		RequestRender();
	}

	public void SetViewSize(double width, double height)
	{
		width = double.IsFinite(width) ? Math.Max(0, width - state.LeftMargin - state.RightMargin) : 0;
		height = double.IsFinite(height) ? Math.Max(0, height - state.TopMargin - state.BottomMargin) : 0;
		if (state.ChartWidth == width && state.ChartHeight == height) return;
		state.ChartWidth = width;
		state.ChartHeight = height;
		if (!state.IsInitialized && state.Series.GetSnapshot().Count > 0 && width > 0 && state.MainPaneHeight > 0)
			InitializeCamera();
		else UpdateViewportFromCamera();
	}

	public void UpdateIndicatorViewport()
	{
		if (IsIndicatorPaneVisible) UpdateViewportFromCamera();
	}

	private static bool IsSupportedTool(TechnicalAnalysisToolType type) => type is
		TechnicalAnalysisToolType.HorizontalLine or TechnicalAnalysisToolType.HorizontalRay or
		TechnicalAnalysisToolType.TrendLine or TechnicalAnalysisToolType.TrendChannel or
		TechnicalAnalysisToolType.Rectangle;

	public void BeginToolCreation(TechnicalAnalysisToolType type)
	{
		if (!IsReady || IsSwitchingSymbol || !IsSupportedTool(type)) return;
		CancelInteraction();
		SetSelectedTool(null);
		interaction.ActiveToolType = type;
		NotifyInteractionChanged();
		ViewActionRequested?.Invoke(new(true, Cursors.Cross, MouseCaptureAction.Release,
			true, ContextMenuAction.Close));
	}

	private void StopCreating()
	{
		interaction.ActiveToolType = TechnicalAnalysisToolType.None;
		interaction.CreationStep = 0;
		Array.Clear(interaction.CreationPoints);
		interaction.CreatingToolInstance = null;
		interaction.CurrentMouseChartCoords = null;
	}

	private void SetSelectedTool(TechnicalAnalysisTool? tool)
	{
		if (ReferenceEquals(SelectedTool, tool)) return;
		SelectedTool?.SetEditMode(false);
		interaction.SelectedTool = tool;
		interaction.EditingControlPointIndex = -1;
		tool?.SetEditMode(true);
		NotifyInteractionChanged();
	}

	public InteractionResult CancelInteraction()
	{
		HandleCaptureLost();
		interaction.CrosshairVisible = false;
		interaction.CrosshairPane = ChartPane.None;
		StopCreating();
		SetSelectedTool(null);
		NotifyInteractionChanged();
		return new(true, Cursors.Arrow, MouseCaptureAction.Release, Menu: ContextMenuAction.Close);
	}

	private void RequestCancellation()
	{
		var result = CancelInteraction();
		ViewActionRequested?.Invoke(result);
	}

	public void HandleCaptureLost()
	{
		EndNavigation();
		interaction.PendingTool = null;
		interaction.IsDraggingToolBody = false;
		interaction.EditingControlPointIndex = -1;
		RequestRender();
	}

	public InteractionResult HandlePointerDown(Point point)
	{
		if (!IsReady || IsSwitchingSymbol) return new();
		var coordinates = new Coordinates(point.X, point.Y);
		if (interaction.IsCreatingTool)
		{
			CreateToolAt(ViewToChart(coordinates));
			return new(true, interaction.IsCreatingTool ? Cursors.Cross : Cursors.Arrow);
		}
		if (SelectedTool is { IsBeingEdited: true } editing)
		{
			int index = editing.GetControlPointIndex(coordinates, Transform.ChartToView);
			if (index >= 0)
			{
				interaction.EditingControlPointIndex = index;
				return new(true, editing.GetControlPointCursor(index), MouseCaptureAction.Capture, true);
			}
		}
		var tool = state.TechnicalAnalysisManager.GetToolAtPoint(coordinates, Transform.ChartToView, state.Viewport);
		if (tool != null)
		{
			if (SelectedTool != tool) SetSelectedTool(null);
			interaction.PendingTool = tool;
			interaction.PendingStartView = point;
			interaction.PendingLastChart = ViewToChart(coordinates);
			interaction.IsDraggingToolBody = false;
			return new(true, tool.GetEditCursor(), MouseCaptureAction.Capture, true);
		}
		SetSelectedTool(null);
		var navigation = BeginNavigation(point);
		return new(true, navigation.Cursor, navigation.Capture, true);
	}

	public InteractionResult HandlePointerUp()
	{
		if (interaction.PendingTool is { } tool && !interaction.IsDraggingToolBody)
			SetSelectedTool(tool);
		HandleCaptureLost();
		return new(true, Cursors.Arrow, MouseCaptureAction.Release);
	}

	public InteractionResult HandlePointerMove(Point point, bool leftButtonPressed)
	{
		var pane = DetectPane(point);
		interaction.CrosshairVisible = pane != ChartPane.None;
		interaction.CrosshairViewPosition = point;
		interaction.CrosshairPane = pane;
		var coordinates = new Coordinates(point.X, point.Y);
		if (IsSwitchingSymbol)
		{
			RequestRender();
			return new(Cursor: Cursors.Arrow);
		}
		if (interaction.IsCreatingTool)
		{
			interaction.CurrentMouseChartCoords = ViewToChart(coordinates);
			RequestRender();
			return new(true, Cursors.Cross);
		}
		if (interaction.PendingTool is { } pending && leftButtonPressed)
		{
			var delta = point - interaction.PendingStartView;
			if (delta.LengthSquared >= 16) interaction.IsDraggingToolBody = true;
			if (interaction.IsDraggingToolBody)
			{
				var current = ViewToChart(coordinates);
				pending.Translate(current.time - interaction.PendingLastChart.time,
					current.price - interaction.PendingLastChart.price);
				interaction.PendingLastChart = current;
			}
			RequestRender();
			return new(true, pending.GetEditCursor());
		}
		if (SelectedTool is { } editing && interaction.EditingControlPointIndex >= 0 && leftButtonPressed)
		{
			editing.UpdateControlPoint(interaction.EditingControlPointIndex, ViewToChart(coordinates));
			RequestRender();
			return new(true, editing.GetControlPointCursor(interaction.EditingControlPointIndex));
		}
		if (!leftButtonPressed && (interaction.IsDragging || interaction.IsScaleZooming ||
			interaction.IsDraggingDivider || interaction.PendingTool != null || interaction.EditingControlPointIndex >= 0))
			HandleCaptureLost();
		Cursor? cursor = null;
		if (!leftButtonPressed)
		{
			if (SelectedTool is { } selected)
			{
				int index = selected.GetControlPointIndex(coordinates, Transform.ChartToView);
				if (index >= 0) cursor = selected.GetControlPointCursor(index);
			}
			cursor ??= state.TechnicalAnalysisManager.GetToolAtPoint(coordinates,
				Transform.ChartToView, state.Viewport)?.GetHoverCursor();
		}
		cursor ??= MoveNavigation(point);
		RequestRender();
		return new(Cursor: cursor);
	}

	public void HandlePointerLeave(bool isCaptured)
	{
		if (isCaptured) return;
		interaction.CrosshairVisible = false;
		interaction.CrosshairPane = ChartPane.None;
		RequestRender();
	}

	public InteractionResult HandleContextMenu(Point point, bool leftButtonPressed)
	{
		if (IsSwitchingSymbol || interaction.IsCreatingTool || leftButtonPressed) return new();
		var tool = state.TechnicalAnalysisManager.GetToolAtPoint(new(point.X, point.Y),
			Transform.ChartToView, state.Viewport);
		if (tool is not IStrokeStyleTool) return new();
		SetSelectedTool(tool);
		return new(true, RequestFocus: true, Menu: ContextMenuAction.Open,
			MenuTool: tool, MenuPosition: point);
	}

	public InteractionResult HandleKey(Key key, ModifierKeys modifiers)
	{
		if (key == Key.Escape) return CancelInteraction();
		if (key == Key.Delete && SelectedTool != null)
		{
			DeleteSelectedTool();
			return new(true, Menu: ContextMenuAction.Close);
		}
		if (key is Key.Up or Key.Down && modifiers == ModifierKeys.None &&
			SelectedTool is HorizontalLine or HorizontalRay)
		{
			double step = Axes.GetMinimumDisplayedPriceStep();
			SelectedTool.Translate(TimeSpan.Zero, key == Key.Up ? step : -step);
			SelectedTool.NeedsRedrawing = true;
			RequestRender();
			return new(true);
		}
		return new();
	}

	private void CreateToolAt(ChartCoordinates point)
	{
		switch (ActiveToolType)
		{
			case TechnicalAnalysisToolType.TrendLine: HandleTrendLineCreation(point); return;
			case TechnicalAnalysisToolType.TrendChannel: HandleTrendChannelCreation(point); return;
			case TechnicalAnalysisToolType.Rectangle: HandleRectangleCreation(point); return;
		}
		TechnicalAnalysisTool? tool = ActiveToolType switch
		{
			TechnicalAnalysisToolType.HorizontalLine => new HorizontalLine(point.price, System.Windows.Media.Brushes.Red),
			TechnicalAnalysisToolType.HorizontalRay => new HorizontalRay(point.time, point.price, System.Windows.Media.Brushes.OrangeRed),
			_ => null
		};
		if (tool != null) state.TechnicalAnalysisManager.AddTool(tool, ActiveToolType);
		StopCreating();
		NotifyInteractionChanged();
	}

	public void DeleteSelectedTool()
	{
		if (SelectedTool is not { } tool) return;
		HandleCaptureLost();
		state.TechnicalAnalysisManager.RemoveTool(tool);
		SetSelectedTool(null);
		SaveTools();
		ViewActionRequested?.Invoke(new(true, Cursors.Arrow, MouseCaptureAction.Release,
			Menu: ContextMenuAction.Close));
	}

	public bool ApplyToolStyle(TechnicalAnalysisTool tool, Action<IStrokeStyleTool> change)
	{
		if (IsSwitchingSymbol || tool is not IStrokeStyleTool style || !state.TechnicalAnalysisManager.GetTools().Contains(tool)) return false;
		change(style);
		tool.NeedsRedrawing = true;
		RequestRender();
		SaveTools();
		return true;
	}

	public async Task SetSymbolAsync(string symbol)
	{
		pendingSymbolSwitches++;
		state.IsSwitchingSymbol = true;
		OnPropertyChanged(nameof(IsSwitchingSymbol));
		RefreshCommandAvailability();
		RequestCancellation();
		await symbolSwitch.WaitAsync();
		try
		{
			// A gesture may have started while this switch waited behind another load.
			RequestCancellation();
			await state.TechnicalAnalysisManager.SetSymbolAsync(symbol);
			RequestCancellation();
			NotifyInteractionChanged();
		}
		finally
		{
			symbolSwitch.Release();
			state.IsSwitchingSymbol = --pendingSymbolSwitches > 0;
			OnPropertyChanged(nameof(IsSwitchingSymbol));
			RefreshCommandAvailability();
		}
	}

	public void SaveTools() => state.TechnicalAnalysisManager.SaveTools();

	public void AddIndicator(Indicator indicator)
	{
		bool opening = !IsIndicatorPaneVisible;
		state.IsIndicatorPaneVisible = true;
		state.MutableIndicators.Add(indicator);
		AutoFitIndicatorViewport();
		OnPropertyChanged(nameof(Indicators));
		if (opening) OnPropertyChanged(nameof(IsIndicatorPaneVisible));
	}

	public void RemoveIndicator(string id)
	{
		state.MutableIndicators.RemoveAll(indicator => indicator.Id == id);
		if (state.MutableIndicators.Count == 0)
		{
			CloseIndicatorPane();
			return;
		}
		AutoFitIndicatorViewport();
		OnPropertyChanged(nameof(Indicators));
	}

	public void ClearIndicators() => CloseIndicatorPane();

	public void CloseIndicatorPane()
	{
		if (!IsIndicatorPaneVisible) return;
		HandleCaptureLost();
		state.IsIndicatorPaneVisible = false;
		state.MutableIndicators.Clear();
		interaction.CrosshairVisible = false;
		interaction.CrosshairPane = ChartPane.None;
		UpdateViewportFromCamera();
		OnPropertyChanged(nameof(Indicators));
		OnPropertyChanged(nameof(IsIndicatorPaneVisible));
		ViewActionRequested?.Invoke(new(true, interaction.IsCreatingTool ? Cursors.Cross : Cursors.Arrow,
			MouseCaptureAction.Release, Menu: ContextMenuAction.Close));
	}

	/// <summary>The toolbar action is idempotent while any indicator pane is open.</summary>
	public void ShowRsi()
	{
		if (IsIndicatorPaneVisible) return;
		AddRSI();
	}

	public List<Indicator> GetIndicators() => state.Indicators.ToList();

	public void AutoFitIndicatorViewport()
	{
		if (!IsIndicatorPaneVisible) return;
		// Preserve the current indicator scaling policy; RSI uses its explicit range.
		if (!state.Indicators.Any(indicator => indicator.IsVisible && indicator.Points.Count > 0))
		{
			state.IndicatorCameraY = 50;
			state.IndicatorRangeInViewport = 100;
		}
		UpdateViewportFromCamera();
	}

	public RSIIndicator AddRSI(int period = 14)
	{
		bool opening = !IsIndicatorPaneVisible;
		state.IsIndicatorPaneVisible = true;
		state.MutableIndicators.RemoveAll(indicator => indicator.Id == "rsi");
		var rsi = new RSIIndicator(period);
		var snapshot = state.Series.GetSnapshot();
		rsi.Calculate(snapshot, i => CandleTime.Get(snapshot, i, ParseTimeframe(state.Timeframe)));
		state.MutableIndicators.Add(rsi);
		state.IndicatorCameraY = 50;
		state.IndicatorRangeInViewport = 110;
		UpdateViewportFromCamera();
		OnPropertyChanged(nameof(Indicators));
		if (opening) OnPropertyChanged(nameof(IsIndicatorPaneVisible));
		return rsi;
	}

	public void RecalculateIndicators(bool invalidateVisual = true)
	{
		if (!IsIndicatorPaneVisible || state.Indicators.Count == 0) return;
		var snapshot = state.Series.GetSnapshot();
		var timeframe = ParseTimeframe(state.Timeframe);
		foreach (var rsi in state.Indicators.OfType<RSIIndicator>())
			rsi.Calculate(snapshot, i => CandleTime.Get(snapshot, i, timeframe));
		RebuildIndicatorViewport();
		if (invalidateVisual) UpdateViewportFromCamera();
	}

}
