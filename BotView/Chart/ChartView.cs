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

		// Делаем контрол фокусируемым для обработки клавиатуры
		Focusable = true;
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

		// Завершаем измерение времени и сохраняем результат в миллисекундах
		stopwatch.Stop();
		LastRenderTimeMs = stopwatch.Elapsed.TotalMilliseconds;
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

		// Если инструмент в режиме редактирования — проверяем клик на контрольную точку
		var editingTool = TechnicalAnalysisTool.EditingTool;
		if (editingTool != null && editingTool.IsBeingEdited)
		{
			if (HandleEditingToolClick(editingTool, viewCoords, e))
				return;
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
			HandleToolSelection(toolAtPoint, e);
			return;
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

		// Обработка многоточечных инструментов (TrendLine - 2 клика)
		if (TechnicalAnalysisTool.CreatingToolType == TechnicalAnalysisToolType.TrendLine)
		{
			HandleTrendLineCreation(chartCoords);
			e.Handled = true;
			return;
		}

		// Обработка многоточечных инструментов (TrendChannel - 3 клика)
		if (TechnicalAnalysisTool.CreatingToolType == TechnicalAnalysisToolType.TrendChannel)
		{
			HandleTrendChannelCreation(chartCoords);
			e.Handled = true;
			return;
		}

		// Обработка создания прямоугольника (2 клика)
		if (TechnicalAnalysisTool.CreatingToolType == TechnicalAnalysisToolType.Rectangle)
		{
			HandleRectangleCreation(chartCoords);
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

	/// <summary>Обработка создания трендового канала (три клика)</summary>
	private void HandleTrendChannelCreation(ChartCoordinates chartCoords)
	{
		if (TechnicalAnalysisTool.CreationStep == 0)
		{
			// Первый клик: сохраняем первую точку
			TechnicalAnalysisTool.SetFirstPoint(chartCoords);
			InvalidateVisual();
		}
		else if (TechnicalAnalysisTool.CreationStep == 1 && TechnicalAnalysisTool.FirstPointCoords.HasValue)
		{
			// Второй клик: создаём первую линию канала и переходим к шагу 2
			var firstPoint = TechnicalAnalysisTool.FirstPointCoords.Value;
			
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

			TechnicalAnalysisTool.SecondPointCoords = chartCoords;
			TechnicalAnalysisTool.CreatingToolInstance = channel;
			TechnicalAnalysisTool.CreationStep = 2;
			InvalidateVisual();
		}
		else if (TechnicalAnalysisTool.CreationStep == 2 && 
				 TechnicalAnalysisTool.CreatingToolInstance is TrendChannel channel)
		{
			// Третий клик: устанавливаем offset параллельной линии и завершаем создание
			double parallelOffset = chartCoords.price - channel.StartPrice;
			channel.ParallelOffset = parallelOffset;

			model.TechnicalAnalysisManager.AddTool(channel, TechnicalAnalysisToolType.TrendChannel);
			TechnicalAnalysisTool.StopCreating();
			this.Cursor = Cursors.Arrow;
			InvalidateVisual();
		}
	}

	/// <summary>Обработка создания прямоугольника (два клика)</summary>
	private void HandleRectangleCreation(ChartCoordinates chartCoords)
	{
		if (TechnicalAnalysisTool.CreationStep == 0)
		{
			// Первый клик: сохраняем первый угол
			TechnicalAnalysisTool.SetFirstPoint(chartCoords);
			InvalidateVisual();
		}
		else if (TechnicalAnalysisTool.CreationStep == 1 && TechnicalAnalysisTool.FirstPointCoords.HasValue)
		{
			// Второй клик: создаём прямоугольник по диагонали
			var firstPoint = TechnicalAnalysisTool.FirstPointCoords.Value;
			var newTool = new TechnicalAnalysis.Rectangle(
				startTime: firstPoint.time,
				startPrice: firstPoint.price,
				endTime: chartCoords.time,
				endPrice: chartCoords.price,
				color: Brushes.Orange,
				thickness: 2.0,
				style: LineStyle.Solid
			);

			model.TechnicalAnalysisManager.AddTool(newTool, TechnicalAnalysisToolType.Rectangle);
			TechnicalAnalysisTool.StopCreating();
			this.Cursor = Cursors.Arrow;
			InvalidateVisual();
		}
	}

	/// <summary>Обрабатывает клик когда инструмент уже в режиме редактирования</summary>
	/// <returns>true если событие обработано</returns>
	private bool HandleEditingToolClick(TechnicalAnalysisTool tool, Coordinates viewCoords, MouseButtonEventArgs e)
	{
		// Для инструментов с контрольными точками проверяем клик на точку
		if (tool.SupportsControlPoints)
		{
			int controlPointIndex = tool.GetControlPointIndex(viewCoords, controller.ChartToView);
			if (controlPointIndex >= 0)
			{
				// Начинаем перетаскивание контрольной точки
				TechnicalAnalysisTool.EditingControlPointIndex = controlPointIndex;
				this.CaptureMouse();
				this.Cursor = tool.GetControlPointCursor(controlPointIndex);
				e.Handled = true;
				return true;
			}
		}

		// Клик вне контрольных точек — выходим из режима редактирования
		TechnicalAnalysisTool.StopEditing();
		InvalidateVisual();
		return false;
	}

	/// <summary>Обрабатывает выбор инструмента кликом</summary>
	private void HandleToolSelection(TechnicalAnalysisTool tool, MouseButtonEventArgs e)
	{
		// Устанавливаем фокус для обработки клавиатуры (Delete, Escape)
		Focus();

		if (tool.SupportsControlPoints)
		{
			// Для инструментов с контрольными точками: активируем режим редактирования
			tool.SetEditMode(true);
			TechnicalAnalysisTool.StartEditing(tool, -1);
			InvalidateVisual();
		}
		else
		{
			// Для простых инструментов: начинаем редактирование сразу с захватом мыши
			TechnicalAnalysisTool.StartEditing(tool);
			this.CaptureMouse();
			this.Cursor = tool.GetEditCursor();
		}
		e.Handled = true;
	}

	/// <summary>Обрабатывает перетаскивание инструмента или его контрольных точек</summary>
	/// <returns>true если событие обработано</returns>
	private bool HandleToolDragging(Coordinates viewCoords)
	{
		var tool = TechnicalAnalysisTool.EditingTool;
		if (tool == null) return false;

		var chartCoords = controller.ViewToChart(viewCoords);

		// Если перетаскиваем контрольную точку
		if (TechnicalAnalysisTool.EditingControlPointIndex >= 0 && tool.SupportsControlPoints)
		{
			tool.UpdateControlPoint(TechnicalAnalysisTool.EditingControlPointIndex, chartCoords);
			InvalidateVisual();
			return true;
		}

		// Если это простой инструмент без контрольных точек — перемещаем его целиком
		if (!tool.SupportsControlPoints)
		{
			tool.UpdatePosition(chartCoords);
			InvalidateVisual();
			return true;
		}

		return false;
	}

	/// <summary>Обрабатывает наведение мыши на инструменты</summary>
	/// <returns>true если курсор изменён</returns>
	private bool HandleToolHover(Coordinates viewCoords)
	{
		var editingTool = TechnicalAnalysisTool.EditingTool;

		// Если инструмент в режиме редактирования — проверяем наведение на контрольные точки
		if (editingTool != null && editingTool.IsBeingEdited && editingTool.SupportsControlPoints)
		{
			int controlPointIndex = editingTool.GetControlPointIndex(viewCoords, controller.ChartToView);
			if (controlPointIndex >= 0)
			{
				this.Cursor = editingTool.GetControlPointCursor(controlPointIndex);
				return true;
			}
		}

		// Проверяем наведение на любой инструмент
		var toolAtPoint = model.TechnicalAnalysisManager.GetToolAtPoint(
			viewCoords,
			controller.ChartToView,
			model.Viewport,
			tolerance: 5.0
		);

		if (toolAtPoint != null)
		{
			this.Cursor = toolAtPoint.GetHoverCursor();
			return true;
		}

		return false;
	}

	protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
	{
		base.OnMouseLeftButtonUp(e);

		// Если редактировали инструмент — завершаем редактирование
		if (TechnicalAnalysisTool.IsEditingTool)
		{
			var tool = TechnicalAnalysisTool.EditingTool;

			// Если перетаскивали контрольную точку — завершаем перетаскивание, но остаёмся в режиме редактирования
			if (TechnicalAnalysisTool.EditingControlPointIndex >= 0)
			{
				TechnicalAnalysisTool.EditingControlPointIndex = -1;
				this.Cursor = Cursors.Arrow;
				this.ReleaseMouseCapture();
				InvalidateVisual();
				return;
			}

			// Для простых инструментов (без контрольных точек) — завершаем редактирование
			if (tool != null && !tool.SupportsControlPoints)
			{
				TechnicalAnalysisTool.StopEditing();
				this.Cursor = Cursors.Arrow;
				this.ReleaseMouseCapture();
				return;
			}
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
		if (TechnicalAnalysisTool.IsCreatingTool && TechnicalAnalysisTool.CreationStep > 0)
		{
			renderer.CurrentMouseChartCoords = controller.ViewToChart(viewCoords);
			InvalidateVisual();
			return;
		}

		// Обработка перетаскивания при редактировании инструмента
		if (TechnicalAnalysisTool.IsEditingTool && TechnicalAnalysisTool.EditingTool != null)
		{
			if (HandleToolDragging(viewCoords))
				return;
		}

		// Если не перетаскиваем график — проверяем наведение на инструмент
		if (e.LeftButton != MouseButtonState.Pressed)
		{
			if (HandleToolHover(viewCoords))
				return;
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

	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);

		// Удаление инструмента по нажатию Delete
		if (e.Key == Key.Delete && TechnicalAnalysisTool.IsEditingTool && TechnicalAnalysisTool.EditingTool != null)
		{
			DeleteEditingTool();
			e.Handled = true;
			return;
		}

		// Отмена создания инструмента по Escape
		if (e.Key == Key.Escape)
		{
			if (TechnicalAnalysisTool.IsCreatingTool)
			{
				TechnicalAnalysisTool.StopCreating();
				this.Cursor = Cursors.Arrow;
				InvalidateVisual();
				e.Handled = true;
			}
			else if (TechnicalAnalysisTool.IsEditingTool)
			{
				TechnicalAnalysisTool.StopEditing();
				InvalidateVisual();
				e.Handled = true;
			}
		}
	}

	/// <summary>Удаляет инструмент, который сейчас редактируется</summary>
	private void DeleteEditingTool()
	{
		var tool = TechnicalAnalysisTool.EditingTool;
		if (tool == null) return;

		// Удаляем из менеджера
		model.TechnicalAnalysisManager.RemoveTool(tool);

		// Сохраняем изменения в файл
		model.TechnicalAnalysisManager.SaveTools();

		// Выходим из режима редактирования
		TechnicalAnalysisTool.StopEditing();

		// Перерисовываем
		InvalidateVisual();
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
		InvalidateVisual();
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

}

