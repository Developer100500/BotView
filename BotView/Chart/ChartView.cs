using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BotView.Chart.TechnicalAnalysis;
using BotView.Chart.IndicatorPane;

namespace BotView.Chart;
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
/// Универсальная структура для хранения координат
/// Используется для различных систем координат в графике:
/// 
/// 1. View Coordinates (Координаты окна):
///    - Пиксели от верхнего левого угла компонента ChartView
///    - Используется для позиционирования UI элементов и обработки мыши
/// 
/// 2. World Coordinates (Мировые координаты):
///    - x: секунды от базового времени (worldOriginTime)
///    - y: единицы цены от базовой цены (worldOriginPrice)
///    - Используется для позиционирования графика, операций zoom/pan
/// </summary>
public struct Coordinates
{
	public double x;
	public double y;

	public Coordinates(double x, double y)
	{
		this.x = x;
		this.y = y;
	}

	// Конструктор для целочисленных координат
	public Coordinates(int x, int y)
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
	// === MVC COMPONENTS ===
	private readonly ChartModel model;
	private readonly ChartController controller;
	private readonly ChartRenderer renderer;

	// === TOOL CREATION PREVIEW ===
	/// <summary>Текущая позиция мыши для превью создаваемого инструмента</summary>
	private ChartCoordinates? currentMouseChartCoords = null;

	// === RENDER TIME COUNTER ===
	/// <summary>Время последней отрисовки в миллисекундах</summary>
	public double LastRenderTimeMs { get; private set; }

	public ChartView() : base()
	{
		// Инициализируем модель (она создаст тестовые данные и инструменты)
		model = new ChartModel();
		
		// Инициализируем контроллер
		controller = new ChartController(model);
		
		// Инициализируем рендерер
		renderer = new ChartRenderer(model, controller);
		
		// Подписываемся на изменение viewport для перерисовки
		controller.ViewportChanged += () => InvalidateVisual();
	}

	protected override void OnRender(DrawingContext drawingContext)
	{
		// Начинаем измерение времени
		Stopwatch stopwatch = Stopwatch.StartNew();

		base.OnRender(drawingContext);

		// Initialize camera only once
		if (!model.IsInitialized && model.ChartWidth > 0 && model.ChartHeight > 0)
		{
			controller.InitializeCamera();
			model.IsInitialized = true;
		}

		renderer.Render(drawingContext); // Делегируем отрисовку в renderer

		// Отрисовка превью создаваемого инструмента
		DrawToolCreationPreview(drawingContext);

		// Завершаем измерение времени и сохраняем результат в миллисекундах
		stopwatch.Stop();
		LastRenderTimeMs = stopwatch.Elapsed.TotalMilliseconds;
	}

	/// <summary>Отрисовка превью создаваемого инструмента</summary>
	private void DrawToolCreationPreview(DrawingContext drawingContext)
	{
		// Проверяем, нужно ли рисовать превью
		if (!TechnicalAnalysisTool.IsCreatingTool || 
			TechnicalAnalysisTool.CreationStep != 1 || 
			!TechnicalAnalysisTool.FirstPointCoords.HasValue ||
			!currentMouseChartCoords.HasValue)
			return;

		var firstPoint = TechnicalAnalysisTool.FirstPointCoords.Value;
		var currentPoint = currentMouseChartCoords.Value;

		// Конвертируем в View координаты
		var startView = controller.ChartToView(firstPoint);
		var endView = controller.ChartToView(currentPoint);

		// Проверяем валидность координат
		if (double.IsNaN(startView.x) || double.IsNaN(startView.y) ||
			double.IsNaN(endView.x) || double.IsNaN(endView.y))
			return;

		// Рисуем превью в зависимости от типа инструмента
		if (TechnicalAnalysisTool.CreatingToolType == TechnicalAnalysisToolType.TrendLine)
		{
			// Полупрозрачная синяя линия для превью
			var previewPen = new Pen(new SolidColorBrush(Color.FromArgb(128, 0, 120, 255)), 2.0);
			previewPen.DashStyle = DashStyles.Dash;
			
			drawingContext.DrawLine(previewPen,
				new Point(startView.x, startView.y),
				new Point(endView.x, endView.y));
		}
	}

	protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
	{
		base.OnRenderSizeChanged(sizeInfo);

		UpdateChartDimensions();

		// При изменении размера окна обновляем viewport и запрашиваем перерисовку всех инструментов
		if (model.IsInitialized)
		{
			controller.UpdateViewportFromCamera();
			renderer.RequestRedrawAllTechnicalTools();
		}
	}

	// === MOUSE INTERACTION ===

	protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
	{
		base.OnMouseLeftButtonDown(e);
		
		// Проверяем, находимся ли мы в режиме создания инструмента теханализа
		if (TechnicalAnalysisTool.IsCreatingTool)
		{
			HandleToolCreation(e);
			return;
		}

		Point mousePos = e.GetPosition(this);
		var viewCoords = new Coordinates(mousePos.X, mousePos.Y);

		// Если TrendLine в режиме редактирования — проверяем клик на контрольную точку
		if (TechnicalAnalysisTool.EditingTool is TrendLine editingTrendLine && editingTrendLine.IsBeingEdited)
		{
			int controlPointIndex = editingTrendLine.GetControlPointIndex(viewCoords, controller.ChartToView);
			if (controlPointIndex >= 0)
			{
				// Начинаем перетаскивание контрольной точки
				TechnicalAnalysisTool.EditingControlPointIndex = controlPointIndex;
				this.CaptureMouse();
				this.Cursor = Cursors.Cross;
				e.Handled = true;
				return;
			}
			else
			{
				// Клик вне контрольных точек — выходим из режима редактирования
				TechnicalAnalysisTool.StopEditing();
				InvalidateVisual();
			}
		}
		
		// Проверяем, кликнули ли на инструмент
		var toolAtPoint = model.TechnicalAnalysisManager.GetToolAtPoint(
			viewCoords,
			controller.ChartToView,
			model.Viewport,
			tolerance: 5.0
		);

		if (toolAtPoint != null)
		{
			if (toolAtPoint is TrendLine trendLine)
			{
				// Для TrendLine: активируем режим редактирования и показываем контрольные точки
				trendLine.IsBeingEdited = true;
				TechnicalAnalysisTool.StartEditing(trendLine, -1);
				InvalidateVisual();
				e.Handled = true;
				return;
			}
			else if (toolAtPoint is HorizontalLine)
			{
				// Для HorizontalLine: начинаем редактирование сразу
				TechnicalAnalysisTool.StartEditing(toolAtPoint);
				this.CaptureMouse();
				this.Cursor = Cursors.SizeNS;
				e.Handled = true;
				return;
			}
		}
			
		var result = controller.HandleMouseLeftButtonDown(mousePos);
			
		if (result.ShouldCaptureMouse)
		{
			this.CaptureMouse();
		}
			
		if (result.Cursor != null)
		{
			this.Cursor = result.Cursor;
		}
	}

	/// <summary>Обработка клика для создания инструмента технического анализа</summary>
	private void HandleToolCreation(MouseButtonEventArgs e)
	{
		Point mousePos = e.GetPosition(this);
		var chartCoords = controller.ViewToChart(new Coordinates(mousePos.X, mousePos.Y));

		// Обработка многоточечных инструментов (например, TrendLine)
		if (TechnicalAnalysisTool.CreatingToolType == TechnicalAnalysisToolType.TrendLine)
		{
			HandleTrendLineCreation(chartCoords);
			e.Handled = true;
			return;
		}

		// Обработка одноточечных инструментов (например, HorizontalLine)
		TechnicalAnalysisTool? newTool = TechnicalAnalysisTool.CreatingToolType switch
		{
			TechnicalAnalysisToolType.HorizontalLine => new HorizontalLine(
				price: chartCoords.price,
				color: Brushes.Red,
				thickness: 2.0
			),
			_ => null
		};

		if (newTool != null)
		{
			model.TechnicalAnalysisManager.AddTool(newTool, TechnicalAnalysisTool.CreatingToolType);
		}
		
		TechnicalAnalysisTool.StopCreating();
		this.Cursor = Cursors.Arrow;
		InvalidateVisual();
		e.Handled = true;
	}

	/// <summary>Обработка создания трендовой линии (два клика)</summary>
	private void HandleTrendLineCreation(ChartCoordinates chartCoords)
	{
		if (TechnicalAnalysisTool.CreationStep == 0)
		{
			// Первый клик: сохраняем первую точку
			TechnicalAnalysisTool.SetFirstPoint(chartCoords);
			InvalidateVisual();
		}
		else if (TechnicalAnalysisTool.CreationStep == 1 && TechnicalAnalysisTool.FirstPointCoords.HasValue)
		{
			// Второй клик: создаём линию
			var firstPoint = TechnicalAnalysisTool.FirstPointCoords.Value;
			var newTool = new TrendLine(
				startTime: firstPoint.time,
				startPrice: firstPoint.price,
				endTime: chartCoords.time,
				endPrice: chartCoords.price,
				color: Brushes.Blue,
				thickness: 2.0,
				style: LineStyle.Solid
			);

			model.TechnicalAnalysisManager.AddTool(newTool, TechnicalAnalysisToolType.TrendLine);
			TechnicalAnalysisTool.StopCreating();
			this.Cursor = Cursors.Arrow;
			InvalidateVisual();
		}
	}

	protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
	{
		base.OnMouseLeftButtonUp(e);

		// Если перетаскивали контрольную точку TrendLine — завершаем перетаскивание, но остаёмся в режиме редактирования
		if (TechnicalAnalysisTool.IsEditingTool && TechnicalAnalysisTool.EditingControlPointIndex >= 0)
		{
			TechnicalAnalysisTool.EditingControlPointIndex = -1;
			this.Cursor = Cursors.Arrow;
			this.ReleaseMouseCapture();
			InvalidateVisual();
			return;
		}

		// Если редактировали HorizontalLine — завершаем редактирование
		if (TechnicalAnalysisTool.IsEditingTool && TechnicalAnalysisTool.EditingTool is HorizontalLine)
		{
			TechnicalAnalysisTool.StopEditing();
			this.Cursor = Cursors.Arrow;
			this.ReleaseMouseCapture();
			return;
		}
			
		controller.HandleMouseLeftButtonUp();
		this.Cursor = Cursors.Arrow;
		this.ReleaseMouseCapture();
	}

	protected override void OnMouseMove(MouseEventArgs e)
	{
		base.OnMouseMove(e);
			
		Point currentPosition = e.GetPosition(this);
		var viewCoords = new Coordinates(currentPosition.X, currentPosition.Y);

		// Обновляем позицию мыши для превью во время создания инструмента
		if (TechnicalAnalysisTool.IsCreatingTool && TechnicalAnalysisTool.CreationStep == 1)
		{
			currentMouseChartCoords = controller.ViewToChart(viewCoords);
			InvalidateVisual();
			return;
		}

		// Если перетаскиваем контрольную точку TrendLine
		if (TechnicalAnalysisTool.IsEditingTool && 
			TechnicalAnalysisTool.EditingTool is TrendLine trendLine && 
			TechnicalAnalysisTool.EditingControlPointIndex >= 0)
		{
			var chartCoords = controller.ViewToChart(viewCoords);
			trendLine.UpdateControlPoint(TechnicalAnalysisTool.EditingControlPointIndex, chartCoords);
			InvalidateVisual();
			return;
		}

		// Если редактируем HorizontalLine — обновляем его позицию
		if (TechnicalAnalysisTool.IsEditingTool && TechnicalAnalysisTool.EditingTool is HorizontalLine)
		{
			var chartCoords = controller.ViewToChart(viewCoords);
			TechnicalAnalysisTool.EditingTool.UpdatePosition(chartCoords);
			InvalidateVisual();
			return;
		}

		// Если не перетаскиваем график — проверяем наведение на инструмент
		if (e.LeftButton != MouseButtonState.Pressed)
		{
			// Если TrendLine в режиме редактирования — проверяем наведение на контрольные точки
			if (TechnicalAnalysisTool.EditingTool is TrendLine editingTrendLine && editingTrendLine.IsBeingEdited)
			{
				int controlPointIndex = editingTrendLine.GetControlPointIndex(viewCoords, controller.ChartToView);
				if (controlPointIndex >= 0)
				{
					this.Cursor = Cursors.Cross;
					return;
				}
			}

			var toolAtPoint = model.TechnicalAnalysisManager.GetToolAtPoint(
				viewCoords,
				controller.ChartToView,
				model.Viewport,
				tolerance: 5.0
			);

			if (toolAtPoint != null)
			{
				// Для горизонтальной линии показываем вертикальную стрелку
				if (toolAtPoint is HorizontalLine)
				{
					this.Cursor = Cursors.SizeNS;
				}
				else if (toolAtPoint is TrendLine)
				{
					this.Cursor = Cursors.Hand;
				}
				return;
			}
		}

		var cursor = controller.HandleMouseMove(currentPosition);
			
		if (cursor != null)
		{
			this.Cursor = cursor;
		}
	}

	protected override void OnMouseWheel(MouseWheelEventArgs e)
	{
		base.OnMouseWheel(e);
			
		// Масштабирование к позиции курсора
		Point mousePosition = e.GetPosition(this);
		controller.HandleMouseWheel(mousePosition, e.Delta);
	}

	/// <summary>Update chart dimensions (called on every render to handle window resize)</summary>
	private void UpdateChartDimensions()
	{
		model.ChartWidth = Math.Max(0, this.ActualWidth - model.LeftMargin - model.RightMargin);
		model.ChartHeight = Math.Max(0, this.ActualHeight - model.TopMargin - model.BottomMargin);
	}

	// === COORDINATE CONVERSION METHODS (delegated to controller) ===

	/// <summary>
	/// Прямая конвертация из Chart координат в View координаты
	/// Возвращает экземпляр Coordinates для View Coordinates
	/// </summary>
	internal Coordinates ChartToView(ChartCoordinates chartCoords)
	{
		return controller.ChartToView(chartCoords);
	}

	// === PUBLIC API METHODS (delegated to controller) ===

	/// <summary>
	/// Устанавливает новые данные свечей
	/// </summary>
	public void SetCandlestickData(CandlestickData newData)
	{
		controller.SetCandlestickData(newData);
		
		// Recalculate all indicators with new data
		RecalculateIndicators();
	}

	/// <summary>
	/// Центрирует график на определенном времени
	/// </summary>
	public void CenterOnTime(DateTime time)
	{
		controller.CenterOnTime(time);
		InvalidateVisual();
	}

	/// <summary>
	/// Центрирует график на определенной цене
	/// </summary>
	public void CenterOnPrice(double price)
	{
		controller.CenterOnPrice(price);
		InvalidateVisual();
	}

	/// <summary>
	/// Подгоняет график чтобы показать все данные
	/// </summary>
	public void FitToData()
	{
		controller.FitToData();
		InvalidateVisual();
	}

	/// <summary>
	/// Получает текущий viewport в chart координатах
	/// </summary>
	public ViewportClippingCoords GetCurrentViewport()
	{
		return controller.GetCurrentViewport();
	}

	/// <summary>
	/// Получает позицию камеры в chart координатах
	/// </summary>
	public ChartCoordinates GetCameraPosition()
	{
		return controller.GetCameraPosition();
	}

	/// <summary>
	/// Позиционирует камеру так, чтобы последняя свечка была видна справа
	/// Центр камеры располагается посередине между левым краем viewport и последней свечкой
	/// </summary>
	public void PositionToLastCandle()
	{
		controller.PositionToLastCandle();
		InvalidateVisual();
	}

	/// <summary>
	/// Масштабирование только по одной оси с сохранением фокуса на центре экрана
	/// </summary>
	/// <param name="timeZoomFactor">Фактор масштабирования по времени</param>
	/// <param name="priceZoomFactor">Фактор масштабирования по цене</param>
	public void ZoomAxis(double timeZoomFactor, double priceZoomFactor)
	{
		controller.ZoomAxis(timeZoomFactor, priceZoomFactor);
		InvalidateVisual();
	}

	/// <summary>Получает менеджер инструментов технического анализа</summary>
	public TechnicalAnalysisManager GetTechnicalAnalysisManager()
	{
		return model.TechnicalAnalysisManager;
	}

	/// <summary>Конвертирует View координаты (пиксели) в Chart координаты (время и цена)</summary>
	/// <param name="viewCoords">Координаты в пикселях от верхнего левого угла</param>
	/// <returns>Координаты в виде времени и цены</returns>
	public ChartCoordinates ViewToChart(Coordinates viewCoords)
	{
		return controller.ViewToChart(viewCoords);
	}

	// === INDICATOR PANE API ===

	/// <summary>Adds an indicator to the indicator pane</summary>
	public void AddIndicator(Indicator indicator)
	{
		model.Indicators.Add(indicator);
		AutoFitIndicatorViewport();
		InvalidateVisual();
	}

	/// <summary>Removes an indicator by ID</summary>
	public void RemoveIndicator(string indicatorId)
	{
		model.Indicators.RemoveAll(i => i.Id == indicatorId);
		AutoFitIndicatorViewport();
		InvalidateVisual();
	}

	/// <summary>Clears all indicators from the indicator pane</summary>
	public void ClearIndicators()
	{
		model.Indicators.Clear();
		InvalidateVisual();
	}

	/// <summary>Gets all indicators</summary>
	public List<Indicator> GetIndicators()
	{
		return model.Indicators;
	}

	/// <summary>Sets the indicator pane height ratio (0.1 to 0.5)</summary>
	public void SetIndicatorPaneRatio(double ratio)
	{
		model.IndicatorPaneHeightRatio = Math.Clamp(ratio, model.MinIndicatorPaneRatio, model.MaxIndicatorPaneRatio);
		InvalidateVisual();
	}

	/// <summary>Auto-fits the indicator viewport to show all indicator data</summary>
	public void AutoFitIndicatorViewport()
	{
		if (model.Indicators.Count == 0)
		{
			model.IndicatorCameraY = 50;
			model.IndicatorRangeInViewport = 100;
			return;
		}

		double minValue = double.MaxValue;
		double maxValue = double.MinValue;

		foreach (var indicator in model.Indicators)
		{
			if (!indicator.IsVisible || indicator.Points.Count == 0)
				continue;

			var (indicatorMin, indicatorMax) = indicator.GetValueRange();
			minValue = Math.Min(minValue, indicatorMin);
			maxValue = Math.Max(maxValue, indicatorMax);
		}

		if (minValue == double.MaxValue || maxValue == double.MinValue)
		{
			model.IndicatorCameraY = 50;
			model.IndicatorRangeInViewport = 100;
			return;
		}

		// Add 10% padding
		double range = maxValue - minValue;
		double padding = range * 0.1;
		minValue -= padding;
		maxValue += padding;

		model.IndicatorCameraY = (minValue + maxValue) / 2;
		model.IndicatorRangeInViewport = Math.Max(maxValue - minValue, 1);
		
		controller.UpdateIndicatorViewport();
	}

	/// <summary>Adds RSI indicator calculated from current candlestick data</summary>
	/// <param name="period">RSI period (default 14)</param>
	/// <returns>The created RSI indicator</returns>
	public RSIIndicator AddRSI(int period = 14)
	{
		// Remove existing RSI if present
		RemoveIndicator("rsi");

		var rsi = new RSIIndicator(period);
		rsi.Calculate(model.CandlestickData, controller);
		
		model.Indicators.Add(rsi);
		
		// RSI is always 0-100, set viewport accordingly
		model.IndicatorCameraY = 50;
		model.IndicatorRangeInViewport = 110; // Slight padding
		controller.UpdateIndicatorViewport();
		
		InvalidateVisual();
		return rsi;
	}

	/// <summary>Recalculates all indicators (call after candlestick data changes)</summary>
	public void RecalculateIndicators()
	{
		foreach (var indicator in model.Indicators)
		{
			if (indicator is RSIIndicator rsi)
			{
				rsi.Calculate(model.CandlestickData, controller);
			}
			// Add other indicator types here as needed
		}
		
		AutoFitIndicatorViewport();
		InvalidateVisual();
	}

}

