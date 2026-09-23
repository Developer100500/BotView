using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using BotView.Chart.IndicatorPane;
using BotView.Models;

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
/// Контроллер графика - содержит всю логику управления камерой, конвертации координат и обработки мыши
/// </summary>
public class ChartController
{
	private readonly ChartModel model;

	// === MOUSE INTERACTION STATE ===
	private bool isDragging = false;
	private bool isScaleZooming = false;
	private bool isDraggingDivider = false;
	private ScaleZoomMode scaleZoomMode = ScaleZoomMode.None;
	private ChartPane activePane = ChartPane.None;
	private Point lastMousePosition;

	/// <summary> Режимы масштабирования через шкалы </summary>
	private enum ScaleZoomMode
	{
		None,
		TimeScale,        // Масштабирование по времени (горизонтальная ось)
		PriceScale,       // Масштабирование по цене (вертикальная ось) - main pane
		IndicatorScale    // Масштабирование по значению индикатора (вертикальная ось) - indicator pane
	}

	/// <summary> Событие изменения viewport (для уведомления о необходимости перерисовки) </summary>
	public event Action? ViewportChanged;

	/// <summary> Срабатывает когда viewport приближается к левому краю данных. </summary>
	public event Action? LeftEdgeApproached;

	// === RENDER / TRANSFORM CACHE (обновляется при изменении layout/camera/zoom) ===
	private double cachedPixelsPerSecond;
	private double cachedPixelsPerPriceUnit;
	private double cachedPixelsPerIndicatorUnit;
	private double cachedViewCenterX;
	private double cachedViewCenterY;
	private double cachedIndicatorViewCenterY;
	private double cachedCameraX;
	private double cachedCameraY;
	private double cachedIndicatorCameraY;
	private double cachedCandleWidthPixels = 2;
	private string cachedTimeframeKey = string.Empty;
	private TimeSpan cachedTimeframeSpan = TimeSpan.FromDays(1);
	private double cachedPriceInterval = 1;
	private TimeSpan cachedTimeInterval = TimeSpan.FromDays(1);
	private double cachedIndicatorValueInterval = 1;

	/// <summary>Ширина свечи в пикселях (из кэша последнего RefreshRenderCaches)</summary>
	public double CandleWidthPixels => cachedCandleWidthPixels;

	/// <summary>Оптимальный шаг цены (из кэша)</summary>
	public double PriceInterval => cachedPriceInterval;

	/// <summary>Оптимальный шаг времени (из кэша)</summary>
	public TimeSpan TimeInterval => cachedTimeInterval;

	/// <summary>Оптимальный шаг шкалы индикатора (из кэша)</summary>
	public double IndicatorValueInterval => cachedIndicatorValueInterval;


	public ChartController(ChartModel model)
	{
		this.model = model ?? throw new ArgumentNullException(nameof(model));
	}

	/// <summary>
	/// Пересчитывает коэффициенты преобразования, ширину свечи и шаги шкал.
	/// Вызывать при изменении размера, камеры, zoom или в начале Render.
	/// </summary>
	public void RefreshRenderCaches()
	{
		if (cachedTimeframeKey != model.Timeframe)
		{
			cachedTimeframeKey = model.Timeframe;
			cachedTimeframeSpan = ParseTimeframe(model.Timeframe);
		}

		double timeRangeSeconds = model.TimeRangeInViewport.TotalSeconds;
		double mainPaneHeight = model.MainPaneHeight;
		double indicatorPaneHeight = model.IndicatorPaneHeight;

		cachedPixelsPerSecond = timeRangeSeconds > 0 ? model.ChartWidth / timeRangeSeconds : 0;
		cachedPixelsPerPriceUnit = model.PriceRangeInViewport > 0
			? mainPaneHeight / model.PriceRangeInViewport
			: 0;
		cachedPixelsPerIndicatorUnit = model.IndicatorRangeInViewport > 0
			? indicatorPaneHeight / model.IndicatorRangeInViewport
			: 0;

		cachedViewCenterX = model.LeftMargin + model.ChartWidth / 2;
		cachedViewCenterY = model.TopMargin + mainPaneHeight / 2;
		cachedIndicatorViewCenterY = model.IndicatorPaneTop + indicatorPaneHeight / 2;
		cachedCameraX = model.CameraPosition.x;
		cachedCameraY = model.CameraPosition.y;
		cachedIndicatorCameraY = model.IndicatorCameraY;

		cachedCandleWidthPixels = Math.Clamp(
			cachedTimeframeSpan.TotalSeconds * cachedPixelsPerSecond * 0.6,
			2,
			50);

		cachedPriceInterval = CalculateOptimalPriceInterval();
		cachedTimeInterval = CalculateOptimalTimeInterval();
		cachedIndicatorValueInterval = CalculateOptimalIndicatorInterval();
	}

#region === COORDINATE CONVERSION METHODS ===

	/// <summary>
	/// Конвертация из Chart координат (время/цена) в World координаты
	/// Возвращает экземпляр Coordinates для World Coordinates
	/// </summary>
	public Coordinates ChartToWorld(ChartCoordinates chartCoords)
	{
		// Время конвертируем в секунды от базовой точки
		double timeOffsetSeconds = (chartCoords.time - model.WorldOriginTime).TotalSeconds;
			
		// Цену конвертируем относительно базовой цены
		double priceOffset = chartCoords.price - model.WorldOriginPrice;

		return new Coordinates(timeOffsetSeconds, priceOffset);
	}

	/// <summary>
	/// Конвертация из World координат в Chart координаты (время/цена)
	/// </summary>
	public ChartCoordinates WorldToChart(Coordinates worldCoords)
	{
		// Конвертируем секунды обратно во время
		DateTime time = model.WorldOriginTime.AddSeconds(worldCoords.x);
			
		// Конвертируем смещение цены обратно в абсолютную цену
		double price = model.WorldOriginPrice + worldCoords.y;

		return new ChartCoordinates(time, price);
	}

	/// <summary>
	/// Конвертация из World координат в View координаты (пиксели на экране) для main pane
	/// Принимает экземпляр Coordinates для World Coordinates
	/// Возвращает экземпляр Coordinates для View Coordinates
	/// </summary>
	public Coordinates WorldToView(Coordinates worldCoords)
	{
		double relativeX = worldCoords.x - cachedCameraX;
		double relativeY = worldCoords.y - cachedCameraY;
		double screenX = cachedViewCenterX + (relativeX * cachedPixelsPerSecond);
		double screenY = cachedViewCenterY - (relativeY * cachedPixelsPerPriceUnit); // Flip Y
		return new Coordinates(screenX, screenY);
	}

	/// <summary>
	/// Конвертация из View координат (пиксели) в World координаты для main pane
	/// Полезно для обработки мыши
	/// Принимает экземпляр Coordinates для View Coordinates
	/// Возвращает экземпляр Coordinates для World Coordinates
	/// </summary>
	public Coordinates ViewToWorld(Coordinates viewCoords)
	{
		double relativeScreenX = viewCoords.x - cachedViewCenterX;
		double relativeScreenY = -(viewCoords.y - cachedViewCenterY); // Flip Y

		double worldX = cachedPixelsPerSecond > 0
			? cachedCameraX + (relativeScreenX / cachedPixelsPerSecond)
			: cachedCameraX;
		double worldY = cachedPixelsPerPriceUnit > 0
			? cachedCameraY + (relativeScreenY / cachedPixelsPerPriceUnit)
			: cachedCameraY;

		return new Coordinates(worldX, worldY);
	}

	/// <summary>
	/// Прямая конвертация из Chart координат в View координаты
	/// Возвращает экземпляр Coordinates для View Coordinates
	/// </summary>
	internal Coordinates ChartToView(ChartCoordinates chartCoords)
	{
		double worldX = (chartCoords.time - model.WorldOriginTime).TotalSeconds;
		double worldY = chartCoords.price - model.WorldOriginPrice;
		double screenX = cachedViewCenterX + ((worldX - cachedCameraX) * cachedPixelsPerSecond);
		double screenY = cachedViewCenterY - ((worldY - cachedCameraY) * cachedPixelsPerPriceUnit);
		return new Coordinates(screenX, screenY);
	}

	/// <summary>X в View для времени (общий для OHLC одной свечи)</summary>
	internal double TimeToViewX(DateTime time)
	{
		double worldX = (time - model.WorldOriginTime).TotalSeconds;
		return cachedViewCenterX + ((worldX - cachedCameraX) * cachedPixelsPerSecond);
	}

	/// <summary>Y в View для цены</summary>
	internal double PriceToViewY(double price)
	{
		double worldY = price - model.WorldOriginPrice;
		return cachedViewCenterY - ((worldY - cachedCameraY) * cachedPixelsPerPriceUnit);
	}

	/// <summary>
	/// Прямая конвертация из View координат в Chart координаты
	/// Принимает экземпляр Coordinates для View Coordinates
	/// </summary>
	public ChartCoordinates ViewToChart(Coordinates viewCoords)
	{
		Coordinates worldCoords = ViewToWorld(viewCoords);
		return WorldToChart(worldCoords);
	}

	// === INDICATOR PANE COORDINATE METHODS ===

	/// <summary>Converts indicator value and time to View coordinates in the indicator pane</summary>
	public Coordinates IndicatorToView(DateTime time, double indicatorValue)
	{
		double timeOffsetSeconds = (time - model.WorldOriginTime).TotalSeconds;
		double screenX = cachedViewCenterX + ((timeOffsetSeconds - cachedCameraX) * cachedPixelsPerSecond);
		double relativeY = indicatorValue - cachedIndicatorCameraY;
		double screenY = cachedIndicatorViewCenterY - (relativeY * cachedPixelsPerIndicatorUnit);
		return new Coordinates(screenX, screenY);
	}

	/// <summary>Y в View для значения индикатора</summary>
	internal double IndicatorValueToViewY(double indicatorValue)
	{
		double relativeY = indicatorValue - cachedIndicatorCameraY;
		return cachedIndicatorViewCenterY - (relativeY * cachedPixelsPerIndicatorUnit);
	}

	/// <summary>Converts View coordinates in the indicator pane to time and indicator value</summary>
	public (DateTime time, double value) ViewToIndicator(Coordinates viewCoords)
	{
		double relativeScreenX = viewCoords.x - cachedViewCenterX;
		double worldX = cachedPixelsPerSecond > 0
			? cachedCameraX + (relativeScreenX / cachedPixelsPerSecond)
			: cachedCameraX;
		DateTime time = model.WorldOriginTime.AddSeconds(worldX);

		double relativeScreenY = -(viewCoords.y - cachedIndicatorViewCenterY);
		double value = cachedPixelsPerIndicatorUnit > 0
			? cachedIndicatorCameraY + (relativeScreenY / cachedPixelsPerIndicatorUnit)
			: cachedIndicatorCameraY;

		return (time, value);
	}

	/// <summary>Updates the indicator viewport based on indicator camera position</summary>
	public void UpdateIndicatorViewport()
	{
		double minValue = model.IndicatorCameraY - model.IndicatorRangeInViewport / 2;
		double maxValue = model.IndicatorCameraY + model.IndicatorRangeInViewport / 2;
		model.IndicatorViewport = new IndicatorViewport(minValue, maxValue);
	}

	/// <summary>Detects which pane a point is in</summary>
	public ChartPane DetectPane(Point point)
	{
		// Check if in divider area
		if (point.Y >= model.DividerY && point.Y <= model.DividerY + model.DividerHeight &&
			point.X >= model.LeftMargin && point.X <= model.LeftMargin + model.ChartWidth)
		{
			return ChartPane.Divider;
		}

		// Check if in main pane
		if (point.Y >= model.TopMargin && point.Y < model.DividerY &&
			point.X >= model.LeftMargin && point.X <= model.LeftMargin + model.ChartWidth)
		{
			return ChartPane.Main;
		}

		// Check if in indicator pane
		if (point.Y > model.IndicatorPaneTop && point.Y <= model.IndicatorPaneTop + model.IndicatorPaneHeight &&
			point.X >= model.LeftMargin && point.X <= model.LeftMargin + model.ChartWidth)
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
			model.CameraPosition.x - model.TimeRangeInViewport.TotalSeconds / 2,
			model.CameraPosition.y + model.PriceRangeInViewport / 2
		);
			
		Coordinates bottomRight = new Coordinates(
			model.CameraPosition.x + model.TimeRangeInViewport.TotalSeconds / 2,
			model.CameraPosition.y - model.PriceRangeInViewport / 2
		);

		ChartCoordinates topLeftChart = WorldToChart(topLeft);
		ChartCoordinates bottomRightChart = WorldToChart(bottomRight);

		model.Viewport = new ViewportClippingCoords(
			bottomRightChart.price,  // minPrice
			topLeftChart.price,      // maxPrice
			topLeftChart.time,       // minTime
			bottomRightChart.time    // maxTime
		);

		// Also update indicator viewport
		UpdateIndicatorViewport();

		RefreshRenderCaches();

		// Помечаем все инструменты технического анализа для перерисовки при изменении viewport
		model.TechnicalAnalysisManager.MarkAllToolsForRedrawing();

		var candles = model.CandlestickData.candles;
		if (candles?.Length > 0)
		{
			var threshold = cachedTimeframeSpan * 20;
			if (model.Viewport.minTime - model.CandlestickData.beginTime < threshold)
			{
				LeftEdgeApproached?.Invoke();
			}
		}

		// Уведомляем об изменении viewport
		ViewportChanged?.Invoke();
	}

#endregion
#region === CAMERA CONTROL METHODS ===

	/// <summary> Инициализация камеры (вызывается один раз при старте) </summary>
	public void InitializeCamera()
	{
		model.UpdateDataRange();

		ResetTimeScaleToTimeframe();

		var lastCandle = model.CandlestickData.candles[^1];
		var centerTime = model.CandlestickData.endTime.Subtract(TimeSpan.FromTicks(model.TimeRangeInViewport.Ticks / 2));
		model.CameraPosition = ChartToWorld(new ChartCoordinates(centerTime, lastCandle.close));

		ResetPriceScaleToCurrentPrice();
	}

	/// <summary>
	/// Перемещение камеры в мировых координатах
	/// </summary>
	/// <param name="deltaWorldX">Изменение по X в мировых координатах (секунды)</param>
	/// <param name="deltaWorldY">Изменение по Y в мировых координатах (единицы цены)</param>
	public void Pan(double deltaWorldX, double deltaWorldY)
	{
		model.CameraPosition = new Coordinates(
			model.CameraPosition.x + deltaWorldX,
			model.CameraPosition.y + deltaWorldY
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
		double deltaWorldX = cachedPixelsPerSecond > 0
			? -deltaScreenX / cachedPixelsPerSecond
			: 0;
		double deltaWorldY = cachedPixelsPerPriceUnit > 0
			? deltaScreenY / cachedPixelsPerPriceUnit
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
		// Используем позицию камеры как точку фокуса по умолчанию
		double focusX = worldFocusX ?? model.CameraPosition.x;
		double focusY = worldFocusY ?? model.CameraPosition.y;

		// Вычисляем смещение от камеры до точки фокуса
		double offsetX = focusX - model.CameraPosition.x;
		double offsetY = focusY - model.CameraPosition.y;

		// Применяем масштабирование
		model.TimeRangeInViewport = TimeSpan.FromTicks((long)(model.TimeRangeInViewport.Ticks * zoomFactorX));
		model.PriceRangeInViewport *= zoomFactorY;

		// Ограничиваем уровни масштабирования разумными значениями
		if (model.TimeRangeInViewport.TotalSeconds < 60) // минимум 1 минута
			model.TimeRangeInViewport = TimeSpan.FromMinutes(1);
		if (model.TimeRangeInViewport.TotalDays > 3650) // максимум 10 лет
			model.TimeRangeInViewport = TimeSpan.FromDays(3650);

		// Нижний предел достаточно мал для низких цен (например 0.0552 × 10% = 0.00552)
		model.PriceRangeInViewport = Math.Clamp(model.PriceRangeInViewport, 1e-8, 1000000);

		// Корректируем позицию камеры чтобы точка фокуса осталась в той же экранной позиции
		model.CameraPosition = new Coordinates(
			focusX - offsetX * zoomFactorX,
			focusY - offsetY * zoomFactorY
		);

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
		double centerScreenX = model.LeftMargin + model.ChartWidth / 2;
		double centerScreenY = model.TopMargin + model.MainPaneHeight / 2;
			
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
	public MouseInteractionResult HandleMouseLeftButtonDown(Point mousePos)
	{
		lastMousePosition = mousePos;
		activePane = DetectPane(mousePos);

		// Check if clicking on divider
		if (activePane == ChartPane.Divider)
		{
			isDraggingDivider = true;
			return new MouseInteractionResult { ShouldCaptureMouse = true, Cursor = Cursors.SizeNS };
		}
			
		// Проверяем, кликнул ли пользователь в области шкал
		ScaleZoomMode detectedMode = DetectScaleArea(mousePos);
			
		if (detectedMode != ScaleZoomMode.None)
		{
			// Начинаем масштабирование через шкалы
			isScaleZooming = true;
			scaleZoomMode = detectedMode;
			Cursor cursor = detectedMode == ScaleZoomMode.TimeScale ? Cursors.SizeWE : Cursors.SizeNS;
			return new MouseInteractionResult { ShouldCaptureMouse = true, Cursor = cursor };
		}
		else
		{
			// Обычное перетаскивание графика
			isDragging = true;
			return new MouseInteractionResult { ShouldCaptureMouse = true, Cursor = Cursors.Arrow };
		}
	}

	/// <summary>
	/// Обработка отпускания левой кнопки мыши
	/// </summary>
	public void HandleMouseLeftButtonUp()
	{
		isDragging = false;
		isScaleZooming = false;
		isDraggingDivider = false;
		scaleZoomMode = ScaleZoomMode.None;
		activePane = ChartPane.None;
	}

	/// <summary>
	/// Обработка движения мыши
	/// </summary>
	/// <param name="currentPosition">Текущая позиция мыши</param>
	/// <returns>Курсор для установки</returns>
	public Cursor? HandleMouseMove(Point currentPosition)
	{
		if (isDraggingDivider)
		{
			// Handle divider dragging to resize panes
			HandleDividerDrag(currentPosition.Y);
			lastMousePosition = currentPosition;
			return Cursors.SizeNS;
		}
		else if (isScaleZooming)
		{
			// Масштабирование через шкалы
			double deltaX = currentPosition.X - lastMousePosition.X;
			double deltaY = currentPosition.Y - lastMousePosition.Y;
				
			HandleScaleZoom(deltaX, deltaY);
				
			lastMousePosition = currentPosition;
			return scaleZoomMode == ScaleZoomMode.TimeScale ? Cursors.SizeWE : Cursors.SizeNS;
		}
		else if (isDragging)
		{
			// Обычное перетаскивание графика
			double deltaX = currentPosition.X - lastMousePosition.X;
			double deltaY = currentPosition.Y - lastMousePosition.Y;
				
			PanByPixels(deltaX, deltaY);
				
			lastMousePosition = currentPosition;
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
		// Calculate available height for panes (excluding margins and divider)
		double totalAvailableHeight = model.ChartHeight - model.DividerHeight;
		
		// Calculate new main pane height based on mouse position
		double newMainPaneHeight = newY - model.TopMargin;
		
		// Calculate the new ratio
		double newRatio = 1.0 - (newMainPaneHeight / totalAvailableHeight);
		
		// Clamp to valid range
		newRatio = Math.Clamp(newRatio, model.MinIndicatorPaneRatio, model.MaxIndicatorPaneRatio);
		
		model.IndicatorPaneHeightRatio = newRatio;

		RefreshRenderCaches();
		
		// Trigger viewport update and redraw
		ViewportChanged?.Invoke();
	}

	/// <summary>
	/// Обработка колесика мыши
	/// </summary>
	/// <param name="mousePosition">Позиция мыши</param>
	/// <param name="delta">Изменение колесика</param>
	public void HandleMouseWheel(Point mousePosition, int delta)
	{
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
		double timeScaleY = model.IndicatorPaneTop + model.IndicatorPaneHeight;
		if (point.Y >= timeScaleY - tolerance && point.Y <= timeScaleY + model.BottomMargin &&
			point.X >= model.LeftMargin && point.X <= model.LeftMargin + model.ChartWidth)
		{
			return ScaleZoomMode.TimeScale;
		}

		// Проверяем область шкалы цены main pane (справа от main pane)
		double priceScaleX = model.LeftMargin + model.ChartWidth;
		if (point.X >= priceScaleX - tolerance && point.X <= priceScaleX + model.RightMargin &&
			point.Y >= model.TopMargin && point.Y < model.DividerY)
		{
			return ScaleZoomMode.PriceScale;
		}

		// Проверяем область шкалы индикатора (справа от indicator pane)
		if (point.X >= priceScaleX - tolerance && point.X <= priceScaleX + model.RightMargin &&
			point.Y > model.IndicatorPaneTop && point.Y <= model.IndicatorPaneTop + model.IndicatorPaneHeight)
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

		switch (scaleZoomMode)
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
		model.IndicatorRangeInViewport *= zoomFactor;
		model.IndicatorRangeInViewport = Math.Clamp(model.IndicatorRangeInViewport, 1, 10000);
		
		UpdateIndicatorViewport();
		ViewportChanged?.Invoke();
	}

	// === PUBLIC API METHODS ===

	/// <summary>
	/// Устанавливает новые данные свечей
	/// </summary>
	public void SetCandlestickData(CandlestickData newData)
	{
		model.CandlestickData = newData;
		model.Timeframe = newData.timeframe;

		// Пересчитываем диапазон данных
		model.UpdateDataRange();

		// Помечаем все инструменты для перерисовки при смене данных (таймфрейм или торговая пара)
		model.TechnicalAnalysisManager.MarkAllToolsForRedrawing();

		// Если камера еще не инициализирована, инициализируем её
		if (!model.IsInitialized && model.ChartWidth > 0 && model.ChartHeight > 0)
		{
			InitializeCamera();
			model.IsInitialized = true;
		}
		else
		{
			RefreshRenderCaches();
		}
	}

	/// <summary> Prepends older candles to the left edge of chart data. </summary>
	public void PrependCandles(OHLCV[] older)
	{
		if (older == null || older.Length == 0)
		{
			return;
		}

		var existing = model.CandlestickData.candles ?? Array.Empty<OHLCV>();
		var merged = older
			.Concat(existing)
			.GroupBy(c => c.timestamp)
			.Select(g => g.Last())
			.OrderBy(c => c.timestamp)
			.ToArray();

		model.CandlestickData = new CandlestickData(
			model.CandlestickData.timeframe,
			merged[0].GetDateTime(),
			merged[^1].GetDateTime(),
			merged);

		model.UpdateDataRange();

		// Re-evaluate the left edge after prepending so zooming out can request another batch.
		UpdateViewportFromCamera();
	}

	/// <summary> Updates or appends the last candle in chart data. </summary>
	public void UpdateLastCandle(OHLCV updated)
	{
		var data = model.CandlestickData;
		var candles = data.candles;
		if (candles == null || candles.Length == 0)
		{
			model.CandlestickData = new CandlestickData(
				data.timeframe,
				updated.GetDateTime(),
				updated.GetDateTime(),
				new[] { updated });
			ViewportChanged?.Invoke();
			return;
		}

		if (candles[^1].timestamp == updated.timestamp)
		{
			candles[^1] = updated;
			data.endTime = updated.GetDateTime();
		}
		else if (updated.timestamp > candles[^1].timestamp)
		{
			var next = new OHLCV[candles.Length + 1];
			Array.Copy(candles, next, candles.Length);
			next[^1] = updated;
			candles = next;
			data.endTime = updated.GetDateTime();
		}
		else
		{
			return;
		}

		data.candles = candles;
		model.CandlestickData = data;
		ViewportChanged?.Invoke();
	}

	/// <summary> Finalizes closed candle and appends newly opened live candle. </summary>
	public void AppendLiveCandle(OHLCV closed, OHLCV newOpen)
	{
		var data = model.CandlestickData;
		var candles = data.candles;
		if (candles == null || candles.Length == 0)
		{
			model.CandlestickData = new CandlestickData(
				data.timeframe,
				newOpen.GetDateTime(),
				newOpen.GetDateTime(),
				new[] { newOpen });
			model.UpdateDataRange();
			ViewportChanged?.Invoke();
			return;
		}

		candles[^1] = closed;
		var next = new OHLCV[candles.Length + 1];
		Array.Copy(candles, next, candles.Length);
		next[^1] = newOpen;
		data.candles = next;
		data.endTime = newOpen.GetDateTime();
		model.CandlestickData = data;
		model.UpdateDataRange();
		ViewportChanged?.Invoke();
	}

	/// <summary>
	/// Центрирует график на определенном времени
	/// </summary>
	public void CenterOnTime(DateTime time)
	{
		ChartCoordinates chartCoords = new ChartCoordinates(time, (model.Viewport.maxPrice + model.Viewport.minPrice) / 2);
		Coordinates worldCoords = ChartToWorld(chartCoords);
			
		model.CameraPosition = new Coordinates(worldCoords.x, model.CameraPosition.y);
		UpdateViewportFromCamera();
	}

	/// <summary>
	/// Центрирует график на определенной цене
	/// </summary>
	public void CenterOnPrice(double price)
	{
		ChartCoordinates chartCoords = new ChartCoordinates(DateTime.Now, price);
		Coordinates worldCoords = ChartToWorld(chartCoords);
			
		model.CameraPosition = new Coordinates(model.CameraPosition.x, worldCoords.y);
		UpdateViewportFromCamera();
	}

	/// <summary>
	/// Подгоняет график чтобы показать все данные
	/// </summary>
	public void FitToData()
	{
		if (model.CandlestickData.candles == null || model.CandlestickData.candles.Length == 0)
			return;

		TimeSpan dataTimeRange = model.CandlestickData.endTime - model.CandlestickData.beginTime;
		model.TimeRangeInViewport = TimeSpan.FromTicks((long)(dataTimeRange.Ticks * 1.2)); // 20% padding

		DateTime lastCandleTime = model.CandlestickData.endTime;
		TimeSpan halfRange = TimeSpan.FromTicks(model.TimeRangeInViewport.Ticks / 2);
		DateTime centerTime = lastCandleTime.Subtract(halfRange);
		double centerPrice = model.CandlestickData.candles[^1].close;

		ChartCoordinates centerChart = new ChartCoordinates(centerTime, centerPrice);
		model.CameraPosition = ChartToWorld(centerChart);

		ResetPriceScaleToCurrentPrice();
	}

	/// <summary> Сбрасывает вертикальный масштаб: ±5% от текущей цены на всю высоту экрана </summary>
	public void ResetPriceScaleToCurrentPrice()
	{
		var candles = model.CandlestickData.candles;
		if (candles == null || candles.Length == 0)
			return;

		double currentPrice = candles[^1].close;
		if (!double.IsFinite(currentPrice) || Math.Abs(currentPrice) < double.Epsilon)
			return;

		// Полный видимый диапазон = 10% цены: 5% вниз и 5% вверх от текущей цены
		model.PriceRangeInViewport = Math.Abs(currentPrice) * 0.20;

		Coordinates priceWorld = ChartToWorld(new ChartCoordinates(DateTime.Now, currentPrice));
		model.CameraPosition = new Coordinates(model.CameraPosition.x, priceWorld.y);

		UpdateViewportFromCamera();
	}

	/// <summary> Сбрасывает горизонтальный масштаб: фиксированное число свечей текущего таймфрейма в ширину экрана </summary>
	public void ResetTimeScaleToTimeframe()
	{
		const int targetVisibleCandles = 100;

		TimeSpan candleDuration = ParseTimeframe(model.Timeframe);
		if (candleDuration <= TimeSpan.Zero)
			return;

		model.TimeRangeInViewport = TimeSpan.FromTicks(candleDuration.Ticks * targetVisibleCandles);
		UpdateViewportFromCamera();
	}

	/// <summary>
	/// Получает текущий viewport в chart координатах
	/// </summary>
	public ViewportClippingCoords GetCurrentViewport()
	{
		return model.Viewport;
	}

	/// <summary>
	/// Получает позицию камеры в chart координатах
	/// </summary>
	public ChartCoordinates GetCameraPosition()
	{
		return WorldToChart(model.CameraPosition);
	}

	/// <summary>
	/// Позиционирует камеру так, чтобы тело последней свечи касалось правой границы viewport.
	/// </summary>
	public void SnapLastCandleToRightEdge()
	{
		if (model.CandlestickData.candles == null || model.CandlestickData.candles.Length == 0)
			return;

		int lastIndex = model.CandlestickData.candles.Length - 1;
		DateTime lastCandleTime = GetCandleTime(lastIndex);

		double pixelsPerSecond = model.ChartWidth / model.TimeRangeInViewport.TotalSeconds;
		if (pixelsPerSecond <= 0)
			return;

		double halfViewportSeconds = model.TimeRangeInViewport.TotalSeconds / 2.0;
		double candleHalfWidthSeconds = (GetCandleWidthPixels() / 2.0) / pixelsPerSecond;

		// Смещаем центр камеры так, чтобы правая грань тела свечи касалась правой границы области графика.
		double targetOffsetSeconds = halfViewportSeconds - candleHalfWidthSeconds;

		var lastCandle = model.CandlestickData.candles[lastIndex];
		Coordinates lastCandleWorld = ChartToWorld(new ChartCoordinates(lastCandleTime, lastCandle.close));

		model.CameraPosition = new Coordinates(
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
	public DateTime GetCandleTime(int candleIndex)
	{
		// Check if we have valid candles data and the index is within bounds
		var candlesticks = model.CandlestickData.candles;
		if (candlesticks != null && candleIndex >= 0 && candleIndex < candlesticks.Length)
		{
			// Use timestamp from OHLCV data if it's not the default value (0)
			if (candlesticks[candleIndex].timestamp > 0)
			{
				return candlesticks[candleIndex].GetDateTime();
			}
		}

			
		// Fallback to calculation based on timeframe for backward compatibility
		if (cachedTimeframeKey != model.Timeframe)
		{
			cachedTimeframeKey = model.Timeframe;
			cachedTimeframeSpan = ParseTimeframe(model.Timeframe);
		}
		return model.CandlestickData.beginTime.Add(TimeSpan.FromTicks(cachedTimeframeSpan.Ticks * candleIndex));
	}

	/// <summary>
	/// Парсит строку timeframe в TimeSpan
	/// </summary>
	public TimeSpan ParseTimeframe(string timeframe)
	{
		return timeframe.ToLower() switch
		{
			"1m" => TimeSpan.FromMinutes(1),
			"5m" => TimeSpan.FromMinutes(5),
			"15m" => TimeSpan.FromMinutes(15),
			"30m" => TimeSpan.FromMinutes(30),
			"1h" => TimeSpan.FromHours(1),
			"4h" => TimeSpan.FromHours(4),
			"1d" => TimeSpan.FromDays(1),
			"1w" => TimeSpan.FromDays(7),
			"1M" => TimeSpan.FromDays(30), // Приблизительно
			_ => TimeSpan.FromDays(1) // По умолчанию 1 день
		};
	}

	/// <summary>
	/// Вычисляет ширину свечи в пикселях на основе текущего масштаба
	/// </summary>
	public double GetCandleWidthPixels()
	{
		return cachedCandleWidthPixels;
	}

	/// <summary>Вычисляет оптимальный интервал для меток шкалы индикатора</summary>
	public double CalculateOptimalIndicatorInterval()
	{
		var viewport = model.IndicatorViewport;
		double valueRange = viewport.MaxValue - viewport.MinValue;
		if (valueRange <= 0 || !double.IsFinite(valueRange))
			return 1;

		double rawInterval = valueRange / 5; // Target ~5 ticks
		double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(rawInterval, 0.001))));
		double normalizedInterval = rawInterval / magnitude;
		if (normalizedInterval <= 1) return magnitude;
		if (normalizedInterval <= 2) return 2 * magnitude;
		if (normalizedInterval <= 5) return 5 * magnitude;
		return 10 * magnitude;
	}

	/// <summary>
	/// Вычисляет оптимальный интервал для меток времени
	/// </summary>
	public TimeSpan CalculateOptimalTimeInterval()
	{
		TimeSpan viewportRange = model.Viewport.maxTime - model.Viewport.minTime;
		double totalSeconds = viewportRange.TotalSeconds;

		// Целевое количество меток на экране (примерно 5-10)
		int targetTickCount = 10;
		double secondsPerTick = totalSeconds / targetTickCount;

		// Выбираем подходящий интервал
		if (secondsPerTick <= 60) // Меньше минуты
			return TimeSpan.FromSeconds(Math.Max(1, Math.Round(secondsPerTick / 10) * 10));
		else if (secondsPerTick <= 3600) // Меньше часа
			return TimeSpan.FromMinutes(Math.Max(1, Math.Round(secondsPerTick / 60 / 5) * 5));
		else if (secondsPerTick <= 86400) // Меньше дня
			return TimeSpan.FromHours(Math.Max(1, Math.Round(secondsPerTick / 3600)));
		else if (secondsPerTick <= 604800) // Меньше недели
			return TimeSpan.FromDays(Math.Max(1, Math.Round(secondsPerTick / 86400)));
		else // Больше недели
			return TimeSpan.FromDays(Math.Max(7, Math.Round(secondsPerTick / 86400 / 7) * 7));
	}

	/// <summary>
	/// Вычисляет оптимальный интервал для меток цены
	/// </summary>
	public double CalculateOptimalPriceInterval()
	{
		double priceRange = model.Viewport.maxPrice - model.Viewport.minPrice;
		if (priceRange <= 0 || !double.IsFinite(priceRange))
			return 1;

		int targetTickCount = GetTargetPriceSectionCount();
		double rawInterval = priceRange / targetTickCount;

		return RoundUpToNicePriceStep(rawInterval);
	}

	/// <summary> Возвращает целевое число ценовых секций по высоте main pane </summary>
	private int GetTargetPriceSectionCount()
	{
		const double pixelsPerSection = 55;
		const int minSections = 4;
		const int maxSections = 10;

		if (model.MainPaneHeight <= 0)
			return maxSections;

		int sections = (int)Math.Round(model.MainPaneHeight / pixelsPerSection);
		return Math.Clamp(sections, minSections, maxSections);
	}

	/// <summary> Округляет шаг цены вверх до ряда 1 / 2 / 2.5 / 5 / 10 × 10ⁿ </summary>
	private static double RoundUpToNicePriceStep(double rawStep)
	{
		if (rawStep <= 0 || !double.IsFinite(rawStep))
			return 1;

		double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
		double normalized = rawStep / magnitude;

		double niceNormalized = normalized switch
		{
			<= 1.0 => 1.0,
			<= 2.0 => 2.0,
			<= 2.5 => 2.5,
			<= 5.0 => 5.0,
			_ => 10.0
		};

		return niceNormalized * magnitude;
	}

	/// <summary> Определяет число знаков после запятой по шагу цены </summary>
	private static int GetDecimalPlacesForPriceStep(double priceInterval)
	{
		if (priceInterval <= 0 || !double.IsFinite(priceInterval))
			return 4;

		double magnitude = Math.Pow(10, Math.Floor(Math.Log10(priceInterval)));
		double normalized = priceInterval / magnitude;
		int baseDecimals = (int)Math.Max(0, -Math.Floor(Math.Log10(magnitude)));

		if (Math.Abs(normalized - 2.5) < 0.01)
			return baseDecimals + 1;

		return baseDecimals;
	}

	/// <summary>
	/// Округляет время вниз до ближайшего интервала
	/// </summary>
	public DateTime RoundDownToInterval(DateTime time, TimeSpan interval)
	{
		long ticks = time.Ticks;
		long intervalTicks = interval.Ticks;
		long roundedTicks = (ticks / intervalTicks) * intervalTicks;
		return new DateTime(roundedTicks);
	}

	/// <summary>
	/// Форматирует подпись времени в зависимости от интервала
	/// </summary>
	public string FormatTimeLabel(DateTime time, TimeSpan interval)
	{
		if (interval.TotalSeconds < 60)
			return time.ToString("HH:mm:ss");
		else if (interval.TotalMinutes < 60)
			return time.ToString("HH:mm");
		else if (interval.TotalHours < 24)
			return time.ToString("HH:mm");
		else if (interval.TotalDays < 7)
			return time.ToString("dd.MM");
		else
			return time.ToString("dd.MM.yy");
	}

	/// <summary> Форматирует подпись цены с учётом шага шкалы </summary>
	public string FormatPriceLabel(double price, double? priceInterval = null)
	{
		double interval = priceInterval ?? CalculateOptimalPriceInterval();
		int decimals = GetDecimalPlacesForPriceStep(interval);
		return price.ToString($"F{decimals}");
	}

	/// <summary>Минимальное изменение цены, различимое в подписи текущей ценовой шкалы.</summary>
	public double GetMinimumDisplayedPriceStep()
	{
		int decimals = GetDecimalPlacesForPriceStep(PriceInterval);
		return Math.Pow(10, -decimals);
	}

	/// <summary>
	/// Formats crosshair time label: always date; time included when timeframe is shorter than 1 day.
	/// </summary>
	public string FormatCrosshairTimeLabel(DateTime time)
	{
		TimeSpan tf = ParseTimeframe(model.Timeframe);
		if (tf < TimeSpan.FromDays(1))
			return time.ToString("dd.MM.yyyy HH:mm");
		return time.ToString("dd.MM.yyyy");
	}
}

/// <summary>Результат обработки мыши для передачи информации обратно в View</summary>
public class MouseInteractionResult
{
	public bool ShouldCaptureMouse { get; set; }
	public Cursor? Cursor { get; set; }
}


