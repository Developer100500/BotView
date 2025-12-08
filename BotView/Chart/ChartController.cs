using System;
using System.Windows;
using System.Windows.Input;
using BotView.Chart.IndicatorPane;

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

	/// <summary>
	/// Режимы масштабирования через шкалы
	/// </summary>
	private enum ScaleZoomMode
	{
		None,
		TimeScale,        // Масштабирование по времени (горизонтальная ось)
		PriceScale,       // Масштабирование по цене (вертикальная ось) - main pane
		IndicatorScale    // Масштабирование по значению индикатора (вертикальная ось) - indicator pane
	}

	/// <summary>
	/// Событие изменения viewport (для уведомления о необходимости перерисовки)
	/// </summary>
	public event Action? ViewportChanged;

	/// <summary>
	/// Конструктор ChartController
	/// </summary>
	/// <param name="model">Модель графика</param>
	public ChartController(ChartModel model)
	{
		this.model = model ?? throw new ArgumentNullException(nameof(model));
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
		// Вычисляем сколько пикселей на единицу мирового пространства (using MainPaneHeight)
		double pixelsPerSecond = model.ChartWidth / model.TimeRangeInViewport.TotalSeconds;
		double pixelsPerPriceUnit = model.MainPaneHeight / model.PriceRangeInViewport;

		// Вычисляем позицию относительно камеры
		double relativeX = worldCoords.x - model.CameraPosition.x;
		double relativeY = worldCoords.y - model.CameraPosition.y;

		// Конвертируем в экранные координаты (центрируем в main pane viewport)
		double screenX = model.LeftMargin + model.ChartWidth / 2 + (relativeX * pixelsPerSecond);
		double screenY = model.TopMargin + model.MainPaneHeight / 2 - (relativeY * pixelsPerPriceUnit); // Flip Y

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
		// Вычисляем сколько пикселей на единицу мирового пространства (using MainPaneHeight)
		double pixelsPerSecond = model.ChartWidth / model.TimeRangeInViewport.TotalSeconds;
		double pixelsPerPriceUnit = model.MainPaneHeight / model.PriceRangeInViewport;

		// Конвертируем экранную позицию в относительную позицию в main pane viewport
		double relativeScreenX = viewCoords.x - model.LeftMargin - model.ChartWidth / 2;
		double relativeScreenY = -(viewCoords.y - model.TopMargin - model.MainPaneHeight / 2); // Flip Y

		// Конвертируем в мировое пространство
		double worldX = model.CameraPosition.x + (relativeScreenX / pixelsPerSecond);
		double worldY = model.CameraPosition.y + (relativeScreenY / pixelsPerPriceUnit);

		return new Coordinates(worldX, worldY);
	}

	/// <summary>
	/// Прямая конвертация из Chart координат в View координаты
	/// Возвращает экземпляр Coordinates для View Coordinates
	/// </summary>
	internal Coordinates ChartToView(ChartCoordinates chartCoords)
	{
		Coordinates worldCoords = ChartToWorld(chartCoords);
		return WorldToView(worldCoords);
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
		// X coordinate uses shared time axis
		double pixelsPerSecond = model.ChartWidth / model.TimeRangeInViewport.TotalSeconds;
		double timeOffsetSeconds = (time - model.WorldOriginTime).TotalSeconds;
		double relativeX = timeOffsetSeconds - model.CameraPosition.x;
		double screenX = model.LeftMargin + model.ChartWidth / 2 + (relativeX * pixelsPerSecond);

		// Y coordinate uses indicator pane's own scale
		double pixelsPerUnit = model.IndicatorPaneHeight / model.IndicatorRangeInViewport;
		double relativeY = indicatorValue - model.IndicatorCameraY;
		double screenY = model.IndicatorPaneTop + model.IndicatorPaneHeight / 2 - (relativeY * pixelsPerUnit);

		return new Coordinates(screenX, screenY);
	}

	/// <summary>Converts View coordinates in the indicator pane to time and indicator value</summary>
	public (DateTime time, double value) ViewToIndicator(Coordinates viewCoords)
	{
		// X coordinate uses shared time axis
		double pixelsPerSecond = model.ChartWidth / model.TimeRangeInViewport.TotalSeconds;
		double relativeScreenX = viewCoords.x - model.LeftMargin - model.ChartWidth / 2;
		double worldX = model.CameraPosition.x + (relativeScreenX / pixelsPerSecond);
		DateTime time = model.WorldOriginTime.AddSeconds(worldX);

		// Y coordinate uses indicator pane's own scale
		double pixelsPerUnit = model.IndicatorPaneHeight / model.IndicatorRangeInViewport;
		double relativeScreenY = -(viewCoords.y - model.IndicatorPaneTop - model.IndicatorPaneHeight / 2);
		double value = model.IndicatorCameraY + (relativeScreenY / pixelsPerUnit);

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

		// Помечаем все инструменты технического анализа для перерисовки при изменении viewport
		model.TechnicalAnalysisManager.MarkAllToolsForRedrawing();

		// Уведомляем об изменении viewport
		ViewportChanged?.Invoke();
	}

#endregion
#region === CAMERA CONTROL METHODS ===

	/// <summary>
	/// Инициализация камеры (вызывается один раз при старте)
	/// </summary>
	public void InitializeCamera()
	{
		// Calculate data range
		model.UpdateDataRange();

		// Position camera at center of data in world coordinates
		model.CameraPosition = new Coordinates(0, 0);

		// Set initial zoom to fit all data with some padding
		TimeSpan dataTimeRange = model.CandlestickData.endTime - model.CandlestickData.beginTime;
		model.TimeRangeInViewport = TimeSpan.FromTicks((long)(dataTimeRange.Ticks * 1.5)); // 50% padding

		double dataRangeY = (model.Viewport.maxPrice - model.Viewport.minPrice) * 1.2; // 20% padding
		model.PriceRangeInViewport = dataRangeY;

		// Помечаем все инструменты для перерисовки при первоначальной загрузке
		//model.TechnicalAnalysisManager.MarkAllToolsForRedrawing(); // Скорее всего это не нужно, т.к. по-умолчанию ChartRenderer имеет флаг RedrawAllTechincalTools установленный в true

		// Update viewport based on camera
		UpdateViewportFromCamera();
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
		// Конвертируем пиксельное смещение в мировое смещение (using MainPaneHeight for Y)
		double pixelsPerSecond = model.ChartWidth / model.TimeRangeInViewport.TotalSeconds;
		double pixelsPerPriceUnit = model.MainPaneHeight / model.PriceRangeInViewport;

		double deltaWorldX = -deltaScreenX / pixelsPerSecond; // Отрицательное для естественного перетаскивания
		double deltaWorldY = deltaScreenY / pixelsPerPriceUnit;  // Flip Y

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

		model.PriceRangeInViewport = Math.Clamp(model.PriceRangeInViewport, 0.01, 1000000);

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

		// Вычисляем временной диапазон данных
		TimeSpan dataTimeRange = model.CandlestickData.endTime - model.CandlestickData.beginTime;
		model.TimeRangeInViewport = TimeSpan.FromTicks((long)(dataTimeRange.Ticks * 1.2)); // 20% padding

		// Вычисляем ценовой диапазон данных
		double dataRangeY = (model.Viewport.maxPrice - model.Viewport.minPrice) * 1.2; // 20% padding
		model.PriceRangeInViewport = dataRangeY;

		// Позиционируем камеру так, чтобы последняя свечка была справа
		// Центр камеры должен быть посередине между левым краем viewport и последней свечкой
		DateTime lastCandleTime = model.CandlestickData.endTime;
			
		// Вычисляем время левого края viewport (камера - половина диапазона)
		DateTime leftEdgeTime = lastCandleTime.Subtract(TimeSpan.FromTicks(model.TimeRangeInViewport.Ticks / 2));
			
		// Центр камеры находится посередине между левым краем и последней свечкой
		TimeSpan halfRange = TimeSpan.FromTicks(model.TimeRangeInViewport.Ticks / 2);
		DateTime centerTime = lastCandleTime.Subtract(halfRange);
			
		double centerPrice = (model.Viewport.maxPrice + model.Viewport.minPrice) / 2;
			
		ChartCoordinates centerChart = new ChartCoordinates(centerTime, centerPrice);
		model.CameraPosition = ChartToWorld(centerChart);

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
	/// Позиционирует камеру так, чтобы последняя свечка была видна справа
	/// Центр камеры располагается посередине между левым краем viewport и последней свечкой
	/// </summary>
	public void PositionToLastCandle()
	{
		if (model.CandlestickData.candles == null || model.CandlestickData.candles.Length == 0)
			return;

		DateTime lastCandleTime = model.CandlestickData.endTime;
			
		// Вычисляем позицию центра камеры:
		// Последняя свечка должна быть на расстоянии 1/2 от правого края viewport
		// Это означает, что центр камеры смещен влево на 1/2 от полного диапазона времени
		TimeSpan halfRange = TimeSpan.FromTicks(model.TimeRangeInViewport.Ticks / 2);
		DateTime centerTime = lastCandleTime.Subtract(halfRange);
			
		// Сохраняем текущую позицию по цене
		ChartCoordinates currentCameraChart = WorldToChart(model.CameraPosition);
		double centerPrice = currentCameraChart.price;
			
		ChartCoordinates newCenterChart = new ChartCoordinates(centerTime, centerPrice);
		model.CameraPosition = ChartToWorld(newCenterChart);

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
		TimeSpan timeframeSpan = ParseTimeframe(model.Timeframe);
		return model.CandlestickData.beginTime.Add(TimeSpan.FromTicks(timeframeSpan.Ticks * candleIndex));
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
		TimeSpan timeframeSpan = ParseTimeframe(model.Timeframe);
		double pixelsPerSecond = model.ChartWidth / model.TimeRangeInViewport.TotalSeconds;
			
		// Ширина свечи составляет 60% от доступного пространства для одного timeframe
		double candleWidthPixels = timeframeSpan.TotalSeconds * pixelsPerSecond * 0.6;
			
		// Ограничиваем минимальную и максимальную ширину
		return Math.Clamp(candleWidthPixels, 2, 50);
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
			
		// Целевое количество меток на экране (примерно 5-10)
		int targetTickCount = 10;
		double rawInterval = priceRange / targetTickCount;

		// Округляем до "красивого" числа
		double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawInterval)));
		double normalizedInterval = rawInterval / magnitude;

		if (normalizedInterval <= 1)
			return magnitude;
		else if (normalizedInterval <= 2)
			return 2 * magnitude;
		else if (normalizedInterval <= 5)
			return 5 * magnitude;
		else
			return 10 * magnitude;
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

	/// <summary>
	/// Форматирует подпись цены
	/// </summary>
	public string FormatPriceLabel(double price)
	{
		// Определяем количество знаков после запятой на основе величины цены
		if (Math.Abs(price) >= 1000)
			return price.ToString("F0");
		else if (Math.Abs(price) >= 100)
			return price.ToString("F1");
		else if (Math.Abs(price) >= 10)
			return price.ToString("F2");
		else if (Math.Abs(price) >= 1)
			return price.ToString("F3");
		else
			return price.ToString("F4");
	}
}

/// <summary>
/// Результат обработки мыши для передачи информации обратно в View
/// </summary>
public class MouseInteractionResult
{
	public bool ShouldCaptureMouse { get; set; }
	public Cursor? Cursor { get; set; }
}



