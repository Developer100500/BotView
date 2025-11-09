using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BotView.Chart
{
	/**
	 * A structure for storing candlestick data.
	 */
	public struct OHLCV
	{
		public long timestamp;    // UTC timestamp in milliseconds (added for CCXT compatibility)
		public double open;
		public double high;
		public double low;
		public double close;
		public double volume;

		// Existing constructor (for backward compatibility)
		public OHLCV(double open, double high, double low, double close, double volume = -1)
		{
			this.timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // Current time by default
			this.open = open;
			this.high = high;
			this.low = low;
			this.close = close;
			this.volume = volume;
		}
		
		// New constructor with timestamp (for CCXT compatibility)
		public OHLCV(long timestamp, double open, double high, double low, double close, double volume = -1)
		{
			this.timestamp = timestamp;
			this.open = open;
			this.high = high;
			this.low = low;
			this.close = close;
			this.volume = volume;
		}
		
		// Convert timestamp to DateTime
		public DateTime GetDateTime() => DateTimeOffset.FromUnixTimeMilliseconds(timestamp).DateTime;
	}

	public struct CandlestickData
	{
		public string timeframe; // chart's candle timeframe
		public DateTime beginTime; // the date of the first candle
		public DateTime endTime; // last candle
		public OHLCV[] candles;

		public CandlestickData(string timeframe, DateTime beginDateTime, DateTime endDateTime, OHLCV[] candles)
		{
			this.timeframe = timeframe.Trim();
			this.beginTime = beginDateTime;
			this.endTime = endDateTime;
			this.candles = candles;
		}
	}

	/// <summary>
	/// Координаты внутри окна компонента (View Coordinates)
	/// Отсчет от верхнего левого угла в пикселях
	/// </summary>
	public struct ViewCoordinates
	{
		public int x;
		public int y;

		public ViewCoordinates(int x, int y)
		{
			this.x = x;
			this.y = y;
		}
	}

	/// <summary>
	/// Мировые координаты (World Coordinates)
	/// Пиксели с привязкой к единой точке отсчета (центр изначального экрана)
	/// </summary>
	public struct WorldCoordinates
	{
		public double x;
		public double y;

		public WorldCoordinates(double x, double y)
		{
			this.x = x;
			this.y = y;
		}
	}

	/// <summary>
	/// Координаты в виде цены и времени (Chart Coordinates)
	/// Используются для позиционирования свечек согласно OHLCV данным
	/// </summary>
	public struct ChartCoordinates
	{
		public DateTime time;
		public double price;

		public ChartCoordinates(DateTime time, double price)
		{
			this.time = time;
			this.price = price;
		}
	}

	/// <summary>
	/// Viewport/Camera - определяет какую часть мирового пространства мы видим
	/// </summary>
	public struct ViewportClippingCoords
	{
		public double minPrice; // минимальная цена в нашем текующем viewport (низ нашего порта)
		public double maxPrice;
		public DateTime minTime; // точка времени на которую сейчас приходится левый край нешего viewport
		public DateTime maxTime;

		public ViewportClippingCoords(double minPrice, double maxPrice, DateTime minTime, DateTime maxTime)
		{
			this.minPrice = minPrice;
			this.maxPrice = maxPrice;
			this.minTime = minTime;
			this.maxTime = maxTime;
		}
	}

	public class ChartView : FrameworkElement
	{
		string timeframe = string.Empty;
		CandlestickData candlestickData;
		OHLCV[] candlesticks;

		ViewportClippingCoords viewport;

		// Data range (will be calculated from candlestick data)
		//private double minPrice = double.MaxValue;
		//private double maxPrice = double.MinValue;

		// Viewport settings (in pixels)
		private double leftMargin = 10;
		private double rightMargin = 80;  // Увеличено для шкалы цены
		private double topMargin = 20;
		private double bottomMargin = 40; // Увеличено для шкалы времени

		// Chart area dimensions (calculated)
		private double chartWidth = 0;
		private double chartHeight = 0;

		// === CAMERA SYSTEM (World Space) ===
		// Camera position in world coordinates (центр камеры в мировых координатах)
		private WorldCoordinates cameraPosition = new WorldCoordinates(0, 0);

		// Camera zoom level - сколько времени и цены помещается в viewport
		private TimeSpan timeRangeInViewport = TimeSpan.FromDays(30);  // 30 дней видимо в viewport
		private double priceRangeInViewport = 1000;                    // диапазон цен в viewport

		// Initialization flag
		private bool isInitialized = false;

		// === COORDINATE CONVERSION SYSTEM ===
		// Базовая точка отсчета для мировых координат (центр изначального экрана)
		private DateTime worldOriginTime;
		private double worldOriginPrice;

		public ChartView() : base()
		{
			this.timeframe = "1d";

			// Создаем тестовые данные с реальными временными метками
			DateTime baseTime = DateTime.Now;
			this.candlesticks = [
				new OHLCV (100, 114, 93, 105, 1000),
				new OHLCV (105, 111, 100, 106, 800)
			];

			this.candlestickData = new CandlestickData(timeframe, baseTime, DateTime.Now, candlesticks);

			// Инициализируем мировую систему координат
			worldOriginTime = baseTime;
			worldOriginPrice = 100; // базовая цена для отсчета

			this.viewport = new ViewportClippingCoords(
				minPrice: 90,
				maxPrice: 120,
				minTime: baseTime.AddDays(-3),
				maxTime: baseTime
			);
		}

		protected override void OnRender(DrawingContext drawingContext)
		{
			base.OnRender(drawingContext);

			// Update chart dimensions (these can change with window resize)
			UpdateChartDimensions();

			// Initialize camera only once
			if (!isInitialized && chartWidth > 0 && chartHeight > 0)
			{
				InitializeCamera();
				isInitialized = true;
			}

			// Draw background and borders for visualization
			DrawChartArea(drawingContext);

			// Draw grid lines
			DrawGrid(drawingContext);

			// Draw scales (axes)
			DrawTimeScale(drawingContext);
			DrawPriceScale(drawingContext);

			// Draw the candlestick
			DrawCandlesticks(drawingContext, candlesticks);
		}

		protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
		{
			base.OnRenderSizeChanged(sizeInfo);
			
			// При изменении размера окна обновляем viewport
			if (isInitialized)
			{
				UpdateViewportFromCamera();
			}
		}

		// === MOUSE INTERACTION ===
		private bool isDragging = false;
		private bool isScaleZooming = false;
		private ScaleZoomMode scaleZoomMode = ScaleZoomMode.None;
		private Point lastMousePosition;

		/// <summary>
		/// Режимы масштабирования через шкалы
		/// </summary>
		private enum ScaleZoomMode
		{
			None,
			TimeScale,   // Масштабирование по времени (горизонтальная ось)
			PriceScale   // Масштабирование по цене (вертикальная ось)
		}

		protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
		{
			base.OnMouseLeftButtonDown(e);
			
			Point mousePos = e.GetPosition(this);
			lastMousePosition = mousePos;
			
			// Проверяем, кликнул ли пользователь в области шкал
			ScaleZoomMode detectedMode = DetectScaleArea(mousePos);
			
			if (detectedMode != ScaleZoomMode.None)
			{
				// Начинаем масштабирование через шкалы
				isScaleZooming = true;
				scaleZoomMode = detectedMode;
				this.Cursor = detectedMode == ScaleZoomMode.TimeScale ? Cursors.SizeWE : Cursors.SizeNS;
			}
			else
			{
				// Обычное перетаскивание графика
				isDragging = true;
			}
			
			this.CaptureMouse();
		}

		protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
		{
			base.OnMouseLeftButtonUp(e);
			
			isDragging = false;
			isScaleZooming = false;
			scaleZoomMode = ScaleZoomMode.None;
			this.Cursor = Cursors.Arrow;
			this.ReleaseMouseCapture();
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			
			Point currentPosition = e.GetPosition(this);
			
			if (isScaleZooming)
			{
				// Масштабирование через шкалы
				double deltaX = currentPosition.X - lastMousePosition.X;
				double deltaY = currentPosition.Y - lastMousePosition.Y;
				
				HandleScaleZoom(deltaX, deltaY);
				
				lastMousePosition = currentPosition;
			}
			else if (isDragging)
			{
				// Обычное перетаскивание графика
				double deltaX = currentPosition.X - lastMousePosition.X;
				double deltaY = currentPosition.Y - lastMousePosition.Y;
				
				PanByPixels(deltaX, deltaY);
				
				lastMousePosition = currentPosition;
			}
			else
			{
				// Изменяем курсор при наведении на шкалы
				ScaleZoomMode hoverMode = DetectScaleArea(currentPosition);
				this.Cursor = hoverMode switch
				{
					ScaleZoomMode.TimeScale => Cursors.SizeWE,
					ScaleZoomMode.PriceScale => Cursors.SizeNS,
					_ => Cursors.Arrow
				};
			}
		}

		protected override void OnMouseWheel(MouseWheelEventArgs e)
		{
			base.OnMouseWheel(e);
			
			// Масштабирование к позиции курсора
			Point mousePosition = e.GetPosition(this);
			double zoomFactor = e.Delta > 0 ? 0.9 : 1.1; // Zoom in/out
			
			ZoomAtScreenPoint(mousePosition.X, mousePosition.Y, zoomFactor);
		}

		/// <summary>
		/// Update chart dimensions (called on every render to handle window resize)
		/// </summary>
		private void UpdateChartDimensions()
		{
			chartWidth = Math.Max(0, this.ActualWidth - leftMargin - rightMargin);
			chartHeight = Math.Max(0, this.ActualHeight - topMargin - bottomMargin);
		}

		/// <summary>
		/// Initialize camera position and zoom (called only once at startup)
		/// </summary>
		private void InitializeCamera()
		{
			// Calculate data range
			UpdateDataRange();

			// Position camera at center of data in world coordinates
			cameraPosition = new WorldCoordinates(0, 0);

			// Set initial zoom to fit all data with some padding
			TimeSpan dataTimeRange = candlestickData.endTime - candlestickData.beginTime;
			timeRangeInViewport = TimeSpan.FromTicks((long)(dataTimeRange.Ticks * 1.5)); // 50% padding

			double dataRangeY = (viewport.maxPrice - viewport.minPrice) * 1.2; // 20% padding
			priceRangeInViewport = dataRangeY;

			// Update viewport based on camera
			UpdateViewportFromCamera();
		}

		/// <summary>
		/// Calculate the min/max price range from the data
		/// </summary>
		private void UpdateDataRange()
		{
			if (candlesticks == null || candlesticks.Length == 0)
			{
				viewport.minPrice = 0;
				viewport.maxPrice = 100;
				return;
			}

			// Находим минимальную и максимальную цены среди всех свечей
			viewport.minPrice = double.MaxValue;
			viewport.maxPrice = double.MinValue;

			foreach (var candle in candlesticks)
			{
				viewport.minPrice = Math.Min(viewport.minPrice, candle.low);
				viewport.maxPrice = Math.Max(viewport.maxPrice, candle.high);
			}

			// Добавляем отступы к ценовому диапазону (10% с каждой стороны)
			double priceRange = viewport.maxPrice - viewport.minPrice;
			double padding = priceRange * 0.1;
			viewport.minPrice -= padding;
			viewport.maxPrice += padding;
		}

		// === COORDINATE CONVERSION METHODS ===

		/// <summary>
		/// Конвертация из Chart координат (время/цена) в World координаты
		/// </summary>
		private WorldCoordinates ChartToWorld(ChartCoordinates chartCoords)
		{
			// Время конвертируем в секунды от базовой точки
			double timeOffsetSeconds = (chartCoords.time - worldOriginTime).TotalSeconds;
			
			// Цену конвертируем относительно базовой цены
			double priceOffset = chartCoords.price - worldOriginPrice;

			return new WorldCoordinates(timeOffsetSeconds, priceOffset);
		}

		/// <summary>
		/// Конвертация из World координат в Chart координаты (время/цена)
		/// </summary>
		private ChartCoordinates WorldToChart(WorldCoordinates worldCoords)
		{
			// Конвертируем секунды обратно во время
			DateTime time = worldOriginTime.AddSeconds(worldCoords.x);
			
			// Конвертируем смещение цены обратно в абсолютную цену
			double price = worldOriginPrice + worldCoords.y;

			return new ChartCoordinates(time, price);
		}

		/// <summary>
		/// Конвертация из World координат в View координаты (пиксели на экране)
		/// </summary>
		private ViewCoordinates WorldToView(WorldCoordinates worldCoords)
		{
			// Вычисляем сколько пикселей на единицу мирового пространства
			double pixelsPerSecond = chartWidth / timeRangeInViewport.TotalSeconds;
			double pixelsPerPriceUnit = chartHeight / priceRangeInViewport;

			// Вычисляем позицию относительно камеры
			double relativeX = worldCoords.x - cameraPosition.x;
			double relativeY = worldCoords.y - cameraPosition.y;

			// Конвертируем в экранные координаты (центрируем в viewport)
			int screenX = (int)(leftMargin + chartWidth / 2 + (relativeX * pixelsPerSecond));
			int screenY = (int)(topMargin + chartHeight / 2 - (relativeY * pixelsPerPriceUnit)); // Flip Y

			return new ViewCoordinates(screenX, screenY);
		}

		/// <summary>
		/// Конвертация из View координат (пиксели) в World координаты
		/// Полезно для обработки мыши
		/// </summary>
		private WorldCoordinates ViewToWorld(ViewCoordinates viewCoords)
		{
			// Вычисляем сколько пикселей на единицу мирового пространства
			double pixelsPerSecond = chartWidth / timeRangeInViewport.TotalSeconds;
			double pixelsPerPriceUnit = chartHeight / priceRangeInViewport;

			// Конвертируем экранную позицию в относительную позицию в viewport
			double relativeScreenX = viewCoords.x - leftMargin - chartWidth / 2;
			double relativeScreenY = -(viewCoords.y - topMargin - chartHeight / 2); // Flip Y

			// Конвертируем в мировое пространство
			double worldX = cameraPosition.x + (relativeScreenX / pixelsPerSecond);
			double worldY = cameraPosition.y + (relativeScreenY / pixelsPerPriceUnit);

			return new WorldCoordinates(worldX, worldY);
		}

		/// <summary>
		/// Прямая конвертация из Chart координат в View координаты
		/// </summary>
		private ViewCoordinates ChartToView(ChartCoordinates chartCoords)
		{
			WorldCoordinates worldCoords = ChartToWorld(chartCoords);
			return WorldToView(worldCoords);
		}

		/// <summary>
		/// Прямая конвертация из View координат в Chart координаты
		/// </summary>
		private ChartCoordinates ViewToChart(ViewCoordinates viewCoords)
		{
			WorldCoordinates worldCoords = ViewToWorld(viewCoords);
			return WorldToChart(worldCoords);
		}

		/// <summary>
		/// Обновляет viewport на основе текущей позиции камеры
		/// </summary>
		private void UpdateViewportFromCamera()
		{
			// Вычисляем границы viewport в chart координатах
			WorldCoordinates topLeft = new WorldCoordinates(
				cameraPosition.x - timeRangeInViewport.TotalSeconds / 2,
				cameraPosition.y + priceRangeInViewport / 2
			);
			
			WorldCoordinates bottomRight = new WorldCoordinates(
				cameraPosition.x + timeRangeInViewport.TotalSeconds / 2,
				cameraPosition.y - priceRangeInViewport / 2
			);

			ChartCoordinates topLeftChart = WorldToChart(topLeft);
			ChartCoordinates bottomRightChart = WorldToChart(bottomRight);

			viewport = new ViewportClippingCoords(
				bottomRightChart.price,  // minPrice
				topLeftChart.price,      // maxPrice
				topLeftChart.time,       // minTime
				bottomRightChart.time    // maxTime
			);
		}

		// === CAMERA CONTROL METHODS ===

		/// <summary>
		/// Перемещение камеры в мировых координатах
		/// </summary>
		/// <param name="deltaWorldX">Изменение по X в мировых координатах (секунды)</param>
		/// <param name="deltaWorldY">Изменение по Y в мировых координатах (единицы цены)</param>
		public void Pan(double deltaWorldX, double deltaWorldY)
		{
			cameraPosition = new WorldCoordinates(
				cameraPosition.x + deltaWorldX,
				cameraPosition.y + deltaWorldY
			);
			
			UpdateViewportFromCamera();
			InvalidateVisual();
		}

		/// <summary>
		/// Перемещение камеры на основе движения пикселей экрана
		/// Полезно для перетаскивания мышью
		/// </summary>
		/// <param name="deltaScreenX">Изменение по X экрана (пиксели)</param>
		/// <param name="deltaScreenY">Изменение по Y экрана (пиксели)</param>
		public void PanByPixels(double deltaScreenX, double deltaScreenY)
		{
			// Конвертируем пиксельное смещение в мировое смещение
			double pixelsPerSecond = chartWidth / timeRangeInViewport.TotalSeconds;
			double pixelsPerPriceUnit = chartHeight / priceRangeInViewport;

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
			double focusX = worldFocusX ?? cameraPosition.x;
			double focusY = worldFocusY ?? cameraPosition.y;

			// Вычисляем смещение от камеры до точки фокуса
			double offsetX = focusX - cameraPosition.x;
			double offsetY = focusY - cameraPosition.y;

			// Применяем масштабирование
			timeRangeInViewport = TimeSpan.FromTicks((long)(timeRangeInViewport.Ticks * zoomFactorX));
			priceRangeInViewport *= zoomFactorY;

			// Ограничиваем уровни масштабирования разумными значениями
			if (timeRangeInViewport.TotalSeconds < 60) // минимум 1 минута
				timeRangeInViewport = TimeSpan.FromMinutes(1);
			if (timeRangeInViewport.TotalDays > 3650) // максимум 10 лет
				timeRangeInViewport = TimeSpan.FromDays(3650);

			priceRangeInViewport = Math.Clamp(priceRangeInViewport, 0.01, 1000000);

			// Корректируем позицию камеры чтобы точка фокуса осталась в той же экранной позиции
			cameraPosition = new WorldCoordinates(
				focusX - offsetX * zoomFactorX,
				focusY - offsetY * zoomFactorY
			);

			UpdateViewportFromCamera();
			InvalidateVisual();
		}

		/// <summary>
		/// Масштабирование к определенной точке экрана (полезно для колеса мыши)
		/// </summary>
		public void ZoomAtScreenPoint(double screenX, double screenY, double zoomFactor)
		{
			ViewCoordinates viewCoords = new ViewCoordinates((int)screenX, (int)screenY);
			WorldCoordinates worldPoint = ViewToWorld(viewCoords);
			Zoom(zoomFactor, zoomFactor, worldPoint.x, worldPoint.y);
		}

		/// <summary>
		/// Масштабирование к определенному времени и цене (полезно для навигации к конкретной свече)
		/// </summary>
		public void ZoomToChartPoint(DateTime time, double price, double zoomFactor)
		{
			ChartCoordinates chartCoords = new ChartCoordinates(time, price);
			WorldCoordinates worldPoint = ChartToWorld(chartCoords);
			Zoom(zoomFactor, zoomFactor, worldPoint.x, worldPoint.y);
		}

		// === PUBLIC API METHODS ===

		/// <summary>
		/// Устанавливает новые данные свечей
		/// </summary>
		public void SetCandlestickData(CandlestickData newData)
		{
			this.candlestickData = newData;
			this.candlesticks = newData.candles;
			this.timeframe = newData.timeframe;
			
			// Пересчитываем диапазон данных
			UpdateDataRange();
			
			// Если камера еще не инициализирована, инициализируем её
			if (!isInitialized && chartWidth > 0 && chartHeight > 0)
			{
				InitializeCamera();
				isInitialized = true;
			}
			
			InvalidateVisual();
		}

		/// <summary>
		/// Центрирует график на определенном времени
		/// </summary>
		public void CenterOnTime(DateTime time)
		{
			ChartCoordinates chartCoords = new ChartCoordinates(time, (viewport.maxPrice + viewport.minPrice) / 2);
			WorldCoordinates worldCoords = ChartToWorld(chartCoords);
			
			cameraPosition = new WorldCoordinates(worldCoords.x, cameraPosition.y);
			UpdateViewportFromCamera();
			InvalidateVisual();
		}

		/// <summary>
		/// Центрирует график на определенной цене
		/// </summary>
		public void CenterOnPrice(double price)
		{
			ChartCoordinates chartCoords = new ChartCoordinates(DateTime.Now, price);
			WorldCoordinates worldCoords = ChartToWorld(chartCoords);
			
			cameraPosition = new WorldCoordinates(cameraPosition.x, worldCoords.y);
			UpdateViewportFromCamera();
			InvalidateVisual();
		}

		/// <summary>
		/// Подгоняет график чтобы показать все данные
		/// </summary>
		public void FitToData()
		{
			if (candlesticks == null || candlesticks.Length == 0)
				return;

			// Вычисляем временной диапазон данных
			TimeSpan dataTimeRange = candlestickData.endTime - candlestickData.beginTime;
			timeRangeInViewport = TimeSpan.FromTicks((long)(dataTimeRange.Ticks * 1.2)); // 20% padding

			// Вычисляем ценовой диапазон данных
			double dataRangeY = (viewport.maxPrice - viewport.minPrice) * 1.2; // 20% padding
			priceRangeInViewport = dataRangeY;

			// Позиционируем камеру так, чтобы последняя свечка была справа
			// Центр камеры должен быть посередине между левым краем viewport и последней свечкой
			DateTime lastCandleTime = candlestickData.endTime;
			
			// Вычисляем время левого края viewport (камера - половина диапазона)
			DateTime leftEdgeTime = lastCandleTime.Subtract(TimeSpan.FromTicks(timeRangeInViewport.Ticks / 2));
			
			// Центр камеры находится посередине между левым краем и последней свечкой
			TimeSpan halfRange = TimeSpan.FromTicks(timeRangeInViewport.Ticks / 2);
			DateTime centerTime = lastCandleTime.Subtract(halfRange);
			
			double centerPrice = (viewport.maxPrice + viewport.minPrice) / 2;
			
			ChartCoordinates centerChart = new ChartCoordinates(centerTime, centerPrice);
			cameraPosition = ChartToWorld(centerChart);

			UpdateViewportFromCamera();
			InvalidateVisual();
		}

		/// <summary>
		/// Получает текущий viewport в chart координатах
		/// </summary>
		public ViewportClippingCoords GetCurrentViewport()
		{
			return viewport;
		}

		/// <summary>
		/// Получает позицию камеры в chart координатах
		/// </summary>
		public ChartCoordinates GetCameraPosition()
		{
			return WorldToChart(cameraPosition);
		}

		/// <summary>
		/// Позиционирует камеру так, чтобы последняя свечка была видна справа
		/// Центр камеры располагается посередине между левым краем viewport и последней свечкой
		/// </summary>
		public void PositionToLastCandle()
		{
			if (candlesticks == null || candlesticks.Length == 0)
				return;

			DateTime lastCandleTime = candlestickData.endTime;
			
			// Вычисляем позицию центра камеры:
			// Последняя свечка должна быть на расстоянии 1/2 от правого края viewport
			// Это означает, что центр камеры смещен влево на 1/2 от полного диапазона времени
			TimeSpan halfRange = TimeSpan.FromTicks(timeRangeInViewport.Ticks / 2);
			DateTime centerTime = lastCandleTime.Subtract(halfRange);
			
			// Сохраняем текущую позицию по цене
			ChartCoordinates currentCameraChart = WorldToChart(cameraPosition);
			double centerPrice = currentCameraChart.price;
			
			ChartCoordinates newCenterChart = new ChartCoordinates(centerTime, centerPrice);
			cameraPosition = ChartToWorld(newCenterChart);

			UpdateViewportFromCamera();
			InvalidateVisual();
		}

		/// <summary>
		/// Draw chart area background and borders for visualization
		/// </summary>
		private void DrawChartArea(DrawingContext drawingContext)
		{
			// Draw background
			Rect chartRect = new Rect(leftMargin, topMargin, chartWidth, chartHeight);
			drawingContext.DrawRectangle(Brushes.White, new Pen(Brushes.Gray, 1), chartRect);
		}

		private void DrawCandlesticks(DrawingContext context, OHLCV[] candles)
		{
			for (int i = 0; i < candles.Length; i++)
			{
				if (candles[i].high > viewport.maxPrice || candles[i].low < viewport.minPrice)
					continue; // skip if the candle is outside of min or max price of the chart

				DateTime candleTime = GetCandleTime(i);
				if (candleTime < viewport.minTime || candleTime > viewport.maxTime)
					continue;

				DrawCandlestick(context, candles[i], i);
			}
		}

		/// <summary>
		/// Отрисовка одной свечи на основе времени и цены
		/// </summary>
		private void DrawCandlestick(DrawingContext drawingContext, OHLCV candlestick, int candleIndex)
		{
			// Вычисляем время свечи на основе индекса и timeframe
			DateTime candleTime = GetCandleTime(candleIndex);

			// Создаем chart координаты для различных точек свечи
			ChartCoordinates centerChart = new ChartCoordinates(candleTime, (candlestick.high + candlestick.low) / 2);
			ChartCoordinates highChart = new ChartCoordinates(candleTime, candlestick.high);
			ChartCoordinates lowChart = new ChartCoordinates(candleTime, candlestick.low);
			ChartCoordinates openChart = new ChartCoordinates(candleTime, candlestick.open);
			ChartCoordinates closeChart = new ChartCoordinates(candleTime, candlestick.close);

			// Конвертируем в экранные координаты
			ViewCoordinates centerView = ChartToView(centerChart);
			ViewCoordinates highView = ChartToView(highChart);
			ViewCoordinates lowView = ChartToView(lowChart);
			ViewCoordinates openView = ChartToView(openChart);
			ViewCoordinates closeView = ChartToView(closeChart);

			// Пропускаем отрисовку если свеча за пределами viewport
			if (centerView.x < leftMargin - 50 || centerView.x > leftMargin + chartWidth + 50)
				return;

			// Вычисляем ширину свечи в пикселях на основе timeframe и масштаба
			double candleWidthPixels = GetCandleWidthPixels();

			// Определяем тип свечи (бычья или медвежья)
			bool isBullish = candlestick.close > candlestick.open;

			// Устанавливаем цвета в зависимости от типа свечи
			Brush bodyBrush = isBullish ? Brushes.LightGreen : Brushes.LightCoral;
			Pen bodyPen = new Pen(isBullish ? Brushes.Green : Brushes.Red, 2);
			Pen wickPen = new Pen(Brushes.Black, 1.5);

			// Отрисовываем фитиль (тонкая вертикальная линия от high до low)
			Point highPoint = new Point(highView.x, highView.y);
			Point lowPoint = new Point(lowView.x, lowView.y);
			drawingContext.DrawLine(wickPen, highPoint, lowPoint);

			// Отрисовываем тело свечи (прямоугольник от open до close)
			double bodyTop = Math.Min(openView.y, closeView.y);
			double bodyHeight = Math.Abs(closeView.y - openView.y);

			// Обрабатываем случай doji (open == close)
			if (bodyHeight < 1)
				bodyHeight = 1;

			Rect bodyRect = new Rect(
				centerView.x - candleWidthPixels / 2,
				bodyTop,
				candleWidthPixels,
				bodyHeight
			);
			drawingContext.DrawRectangle(bodyBrush, bodyPen, bodyRect);
		}

		/// <summary>
		/// Получает время свечи на основе индекса и timeframe
		/// Uses timestamp from OHLCV data when available, falls back to calculation for backward compatibility
		/// </summary>
		private DateTime GetCandleTime(int candleIndex)
		{
			// Check if we have valid candles data and the index is within bounds
			if (candlesticks != null && candleIndex >= 0 && candleIndex < candlesticks.Length)
			{
				// Use timestamp from OHLCV data if it's not the default value (0)
				if (candlesticks[candleIndex].timestamp > 0)
				{
					return candlesticks[candleIndex].GetDateTime();
				}
			}
			
			// Fallback to calculation based on timeframe for backward compatibility
			TimeSpan timeframeSpan = ParseTimeframe(timeframe);
			return candlestickData.beginTime.Add(TimeSpan.FromTicks(timeframeSpan.Ticks * candleIndex));
		}

		/// <summary>
		/// Парсит строку timeframe в TimeSpan
		/// </summary>
		private TimeSpan ParseTimeframe(string timeframe)
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
		private double GetCandleWidthPixels()
		{
			TimeSpan timeframeSpan = ParseTimeframe(timeframe);
			double pixelsPerSecond = chartWidth / timeRangeInViewport.TotalSeconds;
			
			// Ширина свечи составляет 60% от доступного пространства для одного timeframe
			double candleWidthPixels = timeframeSpan.TotalSeconds * pixelsPerSecond * 0.6;
			
			// Ограничиваем минимальную и максимальную ширину
			return Math.Clamp(candleWidthPixels, 2, 50);
		}

		// === SCALE DRAWING METHODS ===

		/// <summary>
		/// Отрисовка шкалы времени (горизонтальная ось внизу)
		/// </summary>
		private void DrawTimeScale(DrawingContext drawingContext)
		{
			if (viewport.minTime >= viewport.maxTime)
				return;

			// Настройки отрисовки
			Pen scalePen = new Pen(Brushes.Gray, 1);
			Pen tickPen = new Pen(Brushes.DarkGray, 1);
			Brush textBrush = Brushes.Black;
			double tickHeight = 5;
			double textOffset = 3;

			// Определяем оптимальный интервал для меток времени
			TimeSpan timeInterval = CalculateOptimalTimeInterval();
			
			// Находим первую метку времени (округляем вниз до ближайшего интервала)
			DateTime firstTick = RoundDownToInterval(viewport.minTime, timeInterval);
			
			// Отрисовываем основную линию шкалы
			double scaleY = topMargin + chartHeight;
			drawingContext.DrawLine(scalePen, 
				new Point(leftMargin, scaleY), 
				new Point(leftMargin + chartWidth, scaleY));

			// Отрисовываем метки времени
			DateTime currentTime = firstTick;
			while (currentTime <= viewport.maxTime)
			{
				// Конвертируем время в экранные координаты
				ChartCoordinates chartCoords = new ChartCoordinates(currentTime, 0);
				ViewCoordinates viewCoords = ChartToView(chartCoords);

				// Проверяем, что метка находится в пределах видимой области
				if (viewCoords.x >= leftMargin && viewCoords.x <= leftMargin + chartWidth)
				{
					// Отрисовываем риску
					drawingContext.DrawLine(tickPen,
						new Point(viewCoords.x, scaleY),
						new Point(viewCoords.x, scaleY + tickHeight));

					// Отрисовываем подпись времени
					string timeText = FormatTimeLabel(currentTime, timeInterval);
					FormattedText formattedText = new FormattedText(
						timeText,
						System.Globalization.CultureInfo.CurrentCulture,
						FlowDirection.LeftToRight,
						new Typeface("Arial"),
						10,
						textBrush,
						VisualTreeHelper.GetDpi(this).PixelsPerDip);

					// Центрируем текст относительно риски
					double textX = viewCoords.x - formattedText.Width / 2;
					double textY = scaleY + tickHeight + textOffset;

					drawingContext.DrawText(formattedText, new Point(textX, textY));
				}

				currentTime = currentTime.Add(timeInterval);
			}
		}

		/// <summary>
		/// Отрисовка шкалы цены (вертикальная ось справа)
		/// </summary>
		private void DrawPriceScale(DrawingContext drawingContext)
		{
			if (viewport.minPrice >= viewport.maxPrice)
				return;

			// Настройки отрисовки
			Pen scalePen = new Pen(Brushes.Gray, 1);
			Pen tickPen = new Pen(Brushes.DarkGray, 1);
			Brush textBrush = Brushes.Black;
			double tickWidth = 5;
			double textOffset = 3;

			// Определяем оптимальный интервал для меток цены
			double priceInterval = CalculateOptimalPriceInterval();
			
			// Находим первую метку цены (округляем вниз до ближайшего интервала)
			double firstTick = Math.Floor(viewport.minPrice / priceInterval) * priceInterval;
			
			// Отрисовываем основную линию шкалы
			double scaleX = leftMargin + chartWidth;
			drawingContext.DrawLine(scalePen, 
				new Point(scaleX, topMargin), 
				new Point(scaleX, topMargin + chartHeight));

			// Отрисовываем метки цены
			double currentPrice = firstTick;
			while (currentPrice <= viewport.maxPrice)
			{
				// Конвертируем цену в экранные координаты
				ChartCoordinates chartCoords = new ChartCoordinates(DateTime.Now, currentPrice);
				ViewCoordinates viewCoords = ChartToView(chartCoords);

				// Проверяем, что метка находится в пределах видимой области
				if (viewCoords.y >= topMargin && viewCoords.y <= topMargin + chartHeight)
				{
					// Отрисовываем риску
					drawingContext.DrawLine(tickPen,
						new Point(scaleX, viewCoords.y),
						new Point(scaleX + tickWidth, viewCoords.y));

					// Отрисовываем подпись цены
					string priceText = FormatPriceLabel(currentPrice);
					FormattedText formattedText = new FormattedText(
						priceText,
						System.Globalization.CultureInfo.CurrentCulture,
						FlowDirection.LeftToRight,
						new Typeface("Arial"),
						10,
						textBrush,
						VisualTreeHelper.GetDpi(this).PixelsPerDip);

					// Позиционируем текст справа от риски
					double textX = scaleX + tickWidth + textOffset;
					double textY = viewCoords.y - formattedText.Height / 2;

					drawingContext.DrawText(formattedText, new Point(textX, textY));
				}

				currentPrice += priceInterval;
			}
		}

		/// <summary>
		/// Вычисляет оптимальный интервал для меток времени
		/// </summary>
		private TimeSpan CalculateOptimalTimeInterval()
		{
			TimeSpan viewportRange = viewport.maxTime - viewport.minTime;
			double totalSeconds = viewportRange.TotalSeconds;

			// Целевое количество меток на экране (примерно 5-10)
			int targetTickCount = 8;
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
		private double CalculateOptimalPriceInterval()
		{
			double priceRange = viewport.maxPrice - viewport.minPrice;
			
			// Целевое количество меток на экране (примерно 5-10)
			int targetTickCount = 8;
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
		private DateTime RoundDownToInterval(DateTime time, TimeSpan interval)
		{
			long ticks = time.Ticks;
			long intervalTicks = interval.Ticks;
			long roundedTicks = (ticks / intervalTicks) * intervalTicks;
			return new DateTime(roundedTicks);
		}

		/// <summary>
		/// Форматирует подпись времени в зависимости от интервала
		/// </summary>
		private string FormatTimeLabel(DateTime time, TimeSpan interval)
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
		private string FormatPriceLabel(double price)
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

		/// <summary>
		/// Отрисовка сетки для лучшей читаемости графика
		/// </summary>
		private void DrawGrid(DrawingContext drawingContext)
		{
			if (viewport.minPrice >= viewport.maxPrice || viewport.minTime >= viewport.maxTime)
				return;

			// Настройки сетки
			Pen gridPen = new Pen(Brushes.LightGray, 0.5);
			gridPen.DashStyle = new DashStyle(new double[] { 2, 4 }, 0);

			// Горизонтальные линии сетки (по ценам)
			double priceInterval = CalculateOptimalPriceInterval();
			double firstPriceTick = Math.Floor(viewport.minPrice / priceInterval) * priceInterval;
			
			double currentPrice = firstPriceTick;
			while (currentPrice <= viewport.maxPrice)
			{
				ChartCoordinates chartCoords = new ChartCoordinates(DateTime.Now, currentPrice);
				ViewCoordinates viewCoords = ChartToView(chartCoords);

				if (viewCoords.y >= topMargin && viewCoords.y <= topMargin + chartHeight)
				{
					drawingContext.DrawLine(gridPen,
						new Point(leftMargin, viewCoords.y),
						new Point(leftMargin + chartWidth, viewCoords.y));
				}

				currentPrice += priceInterval;
			}

			// Вертикальные линии сетки (по времени)
			TimeSpan timeInterval = CalculateOptimalTimeInterval();
			DateTime firstTimeTick = RoundDownToInterval(viewport.minTime, timeInterval);
			
			DateTime currentTime = firstTimeTick;
			while (currentTime <= viewport.maxTime)
			{
				ChartCoordinates chartCoords = new ChartCoordinates(currentTime, 0);
				ViewCoordinates viewCoords = ChartToView(chartCoords);

				if (viewCoords.x >= leftMargin && viewCoords.x <= leftMargin + chartWidth)
				{
					drawingContext.DrawLine(gridPen,
						new Point(viewCoords.x, topMargin),
						new Point(viewCoords.x, topMargin + chartHeight));
				}

				currentTime = currentTime.Add(timeInterval);
			}
		}

		// === SCALE INTERACTION METHODS ===

		/// <summary>
		/// Определяет, находится ли точка в области шкал
		/// </summary>
		/// <param name="point">Точка для проверки</param>
		/// <returns>Режим масштабирования или None</returns>
		private ScaleZoomMode DetectScaleArea(Point point)
		{
			const double tolerance = 15; // Погрешность в пикселях

			// Проверяем область шкалы времени (внизу)
			double timeScaleY = topMargin + chartHeight;
			if (point.Y >= timeScaleY - tolerance && point.Y <= timeScaleY + bottomMargin &&
				point.X >= leftMargin && point.X <= leftMargin + chartWidth)
			{
				return ScaleZoomMode.TimeScale;
			}

			// Проверяем область шкалы цены (справа)
			double priceScaleX = leftMargin + chartWidth;
			if (point.X >= priceScaleX - tolerance && point.X <= priceScaleX + rightMargin &&
				point.Y >= topMargin && point.Y <= topMargin + chartHeight)
			{
				return ScaleZoomMode.PriceScale;
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
					// Масштабирование по цене (вверх-вниз)
					// Положительное deltaY = движение вниз = увеличение масштаба (zoom out)
					// Отрицательное deltaY = движение вверх = уменьшение масштаба (zoom in)
					double priceZoomFactor = 1.0 + (deltaY * sensitivity);
					priceZoomFactor = Math.Clamp(priceZoomFactor, 0.5, 2.0); // Ограничиваем скорость
					
					// Масштабируем только по цене, время оставляем без изменений
					ZoomAxis(1.0, priceZoomFactor);
					break;
			}
		}

		/// <summary>
		/// Масштабирование только по одной оси с сохранением фокуса на центре экрана
		/// </summary>
		/// <param name="timeZoomFactor">Фактор масштабирования по времени</param>
		/// <param name="priceZoomFactor">Фактор масштабирования по цене</param>
		public void ZoomAxis(double timeZoomFactor, double priceZoomFactor)
		{
			// Используем центр экрана как точку фокуса
			double centerScreenX = leftMargin + chartWidth / 2;
			double centerScreenY = topMargin + chartHeight / 2;
			
			ViewCoordinates centerView = new ViewCoordinates((int)centerScreenX, (int)centerScreenY);
			WorldCoordinates centerWorld = ViewToWorld(centerView);
			
			Zoom(timeZoomFactor, priceZoomFactor, centerWorld.x, centerWorld.y);
		}
	}
}
