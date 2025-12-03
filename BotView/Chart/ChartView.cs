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
	}

	protected override void OnRender(DrawingContext drawingContext)
	{
		// Начинаем измерение времени
		Stopwatch stopwatch = Stopwatch.StartNew();

		base.OnRender(drawingContext);

		// Update chart dimensions (these can change with window resize)
		UpdateChartDimensions();

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

	/// <summary>
	/// Обработка клика для создания инструмента технического анализа
	/// </summary>
	private void HandleToolCreation(MouseButtonEventArgs e)
	{
		// Получаем позицию мыши
		Point mousePos = e.GetPosition(this);
		
		// Конвертируем View координаты в Chart координаты
		var chartCoords = controller.ViewToChart(new Coordinates(mousePos.X, mousePos.Y));
		
		// Создаём инструмент в зависимости от типа
		TechnicalAnalysisTool? newTool = TechnicalAnalysisTool.CreatingToolType switch
		{
			TechnicalAnalysisToolType.HorizontalLine => new HorizontalLine(
				price: chartCoords.price,
				color: Brushes.Red,
				thickness: 2.0
			),
			// Здесь можно добавить другие типы инструментов
			_ => null
		};

		if (newTool != null)
		{
			// Добавляем инструмент в менеджер
			model.TechnicalAnalysisManager.AddTool(newTool);
		}
		
		// Выключаем режим создания и возвращаем курсор
		TechnicalAnalysisTool.StopCreating();
		this.Cursor = Cursors.Arrow;

		// Запрашиваем перерисовку графика
		InvalidateVisual();
		
		// Помечаем событие как обработанное, чтобы не обрабатывать его для pan
		e.Handled = true;
	}

	protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
	{
		base.OnMouseLeftButtonUp(e);
			
		controller.HandleMouseLeftButtonUp();
		this.Cursor = Cursors.Arrow;
		this.ReleaseMouseCapture();
	}

	protected override void OnMouseMove(MouseEventArgs e)
	{
		base.OnMouseMove(e);
			
		Point currentPosition = e.GetPosition(this);
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

