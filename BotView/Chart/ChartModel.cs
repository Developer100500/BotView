using System;
using BotView.Chart.TechnicalAnalysis;

namespace BotView.Chart;

/// <summary>
/// Модель состояния графика - содержит все данные и состояние компонента
/// </summary>
public class ChartModel
{
	// === DATA ===
	public CandlestickData CandlestickData { get; set; }
	public string Timeframe { get; set; } = string.Empty;

	// === CAMERA STATE ===
	public Coordinates CameraPosition { get; set; } = new Coordinates(0, 0);
	public TimeSpan TimeRangeInViewport { get; set; } = TimeSpan.FromDays(30);
	public double PriceRangeInViewport { get; set; } = 1000;
	public DateTime WorldOriginTime { get; set; }
	public double WorldOriginPrice { get; set; }
	public bool IsInitialized { get; set; } = false;

	// === VIEWPORT ===
	public ViewportClippingCoords Viewport { get; set; }

	// === DIMENSIONS AND MARGINS ===
	public double ChartWidth { get; set; } = 0;
	public double ChartHeight { get; set; } = 0;

	// Margins are constants
	public double LeftMargin { get; } = 10;
	public double RightMargin { get; } = 80;  // Увеличено для шкалы цены
	public double TopMargin { get; } = 20;
	public double BottomMargin { get; } = 40; // Увеличено для шкалы времени

	// === TECHNICAL ANALYSIS TOOLS ===
	public TechnicalAnalysisManager TechnicalAnalysisManager { get; }

	/// <summary>
	/// Конструктор ChartModel с инициализацией значений по умолчанию
	/// </summary>
	public ChartModel()
	{
		TechnicalAnalysisManager = new TechnicalAnalysisManager();

		// Инициализация тестовых данных
		Timeframe = "1d";
		DateTime baseTime = DateTime.Now;
		OHLCV[] candles = [
			new OHLCV(100, 114, 93, 105, 1000),
			new OHLCV(105, 111, 100, 106, 800)
		];

		CandlestickData = new CandlestickData(Timeframe, baseTime, DateTime.Now, candles);

		// Инициализируем мировую систему координат
		WorldOriginTime = baseTime;
		WorldOriginPrice = 100; // базовая цена для отсчета

		Viewport = new ViewportClippingCoords(
			minPrice: 0,
			maxPrice: 120,
			minTime: baseTime.AddDays(-3),
			maxTime: baseTime
		);
	}

	/// <summary>
	/// Вычисляет диапазон цен из данных свечей и обновляет viewport
	/// </summary>
	public void UpdateDataRange()
	{
		if (CandlestickData.candles == null || CandlestickData.candles.Length == 0)
		{
			Viewport = new ViewportClippingCoords(
				Viewport.minPrice,
				Viewport.maxPrice,
				Viewport.minTime,
				Viewport.maxTime
			);
			// Если нет данных, оставляем текущие значения цен
			return;
		}

		// Находим минимальную и максимальную цены среди всех свечей
		double minPrice = double.MaxValue;
		double maxPrice = double.MinValue;

		foreach (var candle in CandlestickData.candles)
		{
			minPrice = Math.Min(minPrice, candle.low);
			maxPrice = Math.Max(maxPrice, candle.high);
		}

		// Добавляем отступы к ценовому диапазону (10% с каждой стороны)
		double priceRange = maxPrice - minPrice;
		double padding = priceRange * 0.1;
		minPrice -= padding;
		maxPrice += padding;

		// Обновляем viewport с новыми ценами, сохраняя временные границы
		Viewport = new ViewportClippingCoords(
			minPrice,
			maxPrice,
			Viewport.minTime,
			Viewport.maxTime
		);
	}
}



