using System;
using System.Collections.Generic;
using BotView.Chart.TechnicalAnalysis;
using BotView.Chart.IndicatorPane;
using BotView.Models;
using BotView.Services;
using BotView.Configuration;

namespace BotView.Chart;

/// <summary>
/// Модель состояния графика - содержит данные и состояние одного экземпляра компонента
/// </summary>
public class ChartState
{
	public ChartInteractionState Interaction { get; } = new();
	public bool ShowCandleOutlines { get; internal set; } = true;
	public bool IsSwitchingSymbol { get; internal set; }
	// === DATA ===
	public ICandleSeriesReader Series { get; internal set; }
	internal ICandleSeriesReader? ComparisonSeries { get; set; }
	internal string? ComparisonSymbol { get; set; }
	internal ComparisonSeriesMath.Anchor? ComparisonAnchor { get; set; }
	internal bool ComparisonAutoFit { get; set; }
	internal double SavedPriceRange { get; set; }
	internal double SavedCameraY { get; set; }
	public string Timeframe { get; internal set; } = string.Empty;
	//public int PollInterval { get; internal set; } = 250; // milliseconds

	// === CAMERA STATE ===
	public Coordinates CameraPosition { get; internal set; } = new Coordinates(0, 0);
	public TimeSpan TimeRangeInViewport { get; internal set; } = TimeSpan.FromDays(30);
	public double PriceRangeInViewport { get; internal set; } = 1000;
	public DateTime WorldOriginTime { get; internal set; }
	public double WorldOriginPrice { get; internal set; }
	public bool IsInitialized { get; internal set; } = false;

	// === VIEWPORT ===
	public ViewportClippingCoords Viewport { get; internal set; }

	// === DIMENSIONS AND MARGINS ===
	public double ChartWidth { get; internal set; } = 0;
	/// <summary>Total height of both panes combined (excluding margins)</summary>
	public double ChartHeight { get; internal set; } = 0;

	// Margins are constants
	public double LeftMargin { get; } = 10;
	public double RightMargin { get; } = 80;  // Увеличено для шкалы цены
	public double TopMargin { get; } = 20;
	public double BottomMargin { get; } = 30; // Увеличено для шкалы времени

	// === INDICATOR PANE STATE ===
	public bool IsIndicatorPaneVisible { get; internal set; }
	/// <summary>Ratio of indicator pane height to total chart height (0.0 to 1.0)</summary>
	public double IndicatorPaneHeightRatio { get; internal set; } = 0.25;

	/// <summary>Height of the divider between panes in pixels</summary>
	public double DividerHeight => IsIndicatorPaneVisible ? 2 : 0;

	/// <summary>Minimum height ratio for indicator pane</summary>
	public double MinIndicatorPaneRatio { get; } = 0.1;

	/// <summary>Maximum height ratio for indicator pane</summary>
	public double MaxIndicatorPaneRatio { get; } = 0.5;

	/// <summary>Height of main candlestick pane in pixels</summary>
	public double MainPaneHeight => !IsIndicatorPaneVisible ? ChartHeight : ChartHeight > DividerHeight
		? (ChartHeight - DividerHeight) * (1.0 - IndicatorPaneHeightRatio) 
		: 0;

	/// <summary>Height of indicator pane in pixels</summary>
	public double IndicatorPaneHeight => IsIndicatorPaneVisible && ChartHeight > DividerHeight
		? (ChartHeight - DividerHeight) * IndicatorPaneHeightRatio 
		: 0;

	/// <summary>Y position of the divider (top edge) in view coordinates</summary>
	public double DividerY => TopMargin + MainPaneHeight;

	/// <summary>Y position where the indicator pane starts</summary>
	public double IndicatorPaneTop => DividerY + DividerHeight;

	/// <summary>Layout shared by the native close button and the indicator pane.</summary>
	public System.Windows.Rect IndicatorCloseButtonBounds
	{
		get
		{
			if (!IsIndicatorPaneVisible || ChartWidth <= 0 || IndicatorPaneHeight <= 0)
				return System.Windows.Rect.Empty;
			double size = Math.Min(22, Math.Min(ChartWidth, IndicatorPaneHeight));
			double insetX = Math.Min(4, ChartWidth - size);
			double insetY = Math.Min(4, IndicatorPaneHeight - size);
			return new(LeftMargin + ChartWidth - size - insetX, IndicatorPaneTop + insetY, size, size);
		}
	}

	/// <summary>Viewport for indicator pane Y-axis (min/max values)</summary>
	public IndicatorViewport IndicatorViewport { get; internal set; } = new IndicatorViewport(0, 100);

	/// <summary>Range of indicator values visible in viewport</summary>
	public double IndicatorRangeInViewport { get; internal set; } = 100;

	/// <summary>Camera Y position for indicator pane (in indicator value units)</summary>
	public double IndicatorCameraY { get; internal set; } = 50;

	/// <summary>Collection of indicators to display</summary>
	internal List<Indicator> MutableIndicators { get; } = new();
	public IReadOnlyList<Indicator> Indicators { get; }
	public double? DataMinPrice { get; internal set; }
	public double? DataMaxPrice { get; internal set; }

	// === TECHNICAL ANALYSIS TOOLS ===
	public TechnicalAnalysisManager TechnicalAnalysisManager { get; }

	/// <summary>
	/// Конструктор ChartState с инициализацией значений по умолчанию
	/// </summary>
	public ChartState(TechnicalAnalysisManager? technicalAnalysisManager = null)
	{
		TechnicalAnalysisManager = technicalAnalysisManager ?? new TechnicalAnalysisManager();
		Indicators = MutableIndicators.AsReadOnly();
		Series = new CandleSeries();
		Timeframe = MarketCatalog.DefaultTimeframeId;
		WorldOriginTime = DateTime.UnixEpoch;
		Viewport = new(-500, 500, WorldOriginTime.AddDays(-15), WorldOriginTime.AddDays(15));
	}

	/// <summary>
	/// Вычисляет диапазон данных независимо от видимой области
	/// </summary>
	internal void UpdateDataRange()
	{
		var snapshot = Series.GetSnapshot();
		if (snapshot.Count == 0)
		{
			DataMinPrice = DataMaxPrice = null;
			return;
		}

		// Находим минимальную и максимальную цены среди всех свечей
		double minPrice = double.MaxValue;
		double maxPrice = double.MinValue;

		foreach (var (_, candle) in snapshot.Enumerate(long.MinValue, long.MaxValue))
		{
			minPrice = Math.Min(minPrice, candle.low);
			maxPrice = Math.Max(maxPrice, candle.high);
		}

		// Добавляем отступы к ценовому диапазону (10% с каждой стороны)
		double priceRange = maxPrice - minPrice;
		double padding = priceRange * 0.1;
		minPrice -= padding;
		maxPrice += padding;

		DataMinPrice = minPrice;
		DataMaxPrice = maxPrice;
	}
}
