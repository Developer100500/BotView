using System.Windows;
using BotView.Chart.IndicatorPane;
using BotView.Chart.TechnicalAnalysis;
using BotView.Services;

namespace BotView.Chart;

/// <summary>Captured geometry and overlays plus one stable candle snapshot for a render pass.</summary>
public sealed class ChartRenderFrame
{
	public CandleSeriesSnapshot Candles { get; }
	public ChartTransform Transform { get; }
	public AxisLayout Axes { get; }
	public ViewportClippingCoords Viewport { get; }
	public IndicatorViewport IndicatorViewport { get; }
	public IReadOnlyList<TechnicalAnalysisTool> Tools { get; }
	public IReadOnlyList<Indicator> Indicators { get; }
	public bool IsInitialized { get; }
	public bool ShowCandleOutlines { get; }
	public double Left { get; }
	public double Top { get; }
	public double LeftMargin => Left;
	public double TopMargin => Top;
	public double RightMargin { get; }
	public double BottomMargin { get; }
	public double ChartWidth { get; }
	public double MainPaneHeight { get; }
	public double IndicatorPaneTop { get; }
	public double IndicatorPaneHeight { get; }
	public double DividerY { get; }
	public double DividerHeight { get; }
	public double ScaleX => Left + ChartWidth;
	public double TimeScaleY => IndicatorPaneTop + IndicatorPaneHeight;
	public double CandleWidthPixels => Transform.CandleWidthPixels;
	public double PriceInterval => Axes.PriceInterval;
	public TimeSpan TimeInterval => Axes.TimeInterval;
	public double IndicatorValueInterval => Axes.IndicatorValueInterval;
	public Rect MainPaneRect { get; }
	public Rect DividerRect { get; }
	public Rect IndicatorPaneRect { get; }
	public bool CrosshairVisible { get; }
	public Point CrosshairViewPosition { get; }
	public ChartPane CrosshairPane { get; }
	public TechnicalAnalysisTool? SelectedTool { get; }
	public TechnicalAnalysisToolType ActiveToolType { get; }
	public bool IsCreatingTool => ActiveToolType != TechnicalAnalysisToolType.None;
	public int CreationStep { get; }
	public IReadOnlyList<ChartCoordinates?> CreationPoints { get; }
	public TechnicalAnalysisTool? CreatingToolInstance { get; }
	public ChartCoordinates? CurrentMouseChartCoords { get; }
	private readonly TimeSpan timeframe;

	internal ChartRenderFrame(ChartState state, ChartTransform transform, AxisLayout axes,
		CandleSeriesSnapshot candles, TimeSpan timeframe)
	{
		Candles = candles;
		Transform = transform;
		Axes = axes;
		this.timeframe = timeframe;
		Viewport = state.Viewport;
		IndicatorViewport = state.IndicatorViewport;
		Tools = Array.AsReadOnly(state.TechnicalAnalysisManager.GetTools().ToArray());
		Indicators = Array.AsReadOnly(state.Indicators.ToArray());
		IsInitialized = state.IsInitialized;
		ShowCandleOutlines = state.ShowCandleOutlines;
		Left = state.LeftMargin;
		Top = state.TopMargin;
		RightMargin = state.RightMargin;
		BottomMargin = state.BottomMargin;
		ChartWidth = state.ChartWidth;
		MainPaneHeight = state.MainPaneHeight;
		IndicatorPaneTop = state.IndicatorPaneTop;
		IndicatorPaneHeight = state.IndicatorPaneHeight;
		DividerY = state.DividerY;
		DividerHeight = state.DividerHeight;
		MainPaneRect = new(Left, Top, ChartWidth, MainPaneHeight);
		DividerRect = new(Left, DividerY, ChartWidth, DividerHeight);
		IndicatorPaneRect = new(Left, IndicatorPaneTop, ChartWidth, IndicatorPaneHeight);
		var input = state.Interaction;
		CrosshairVisible = input.CrosshairVisible;
		CrosshairViewPosition = input.CrosshairViewPosition;
		CrosshairPane = input.CrosshairPane;
		SelectedTool = input.SelectedTool;
		ActiveToolType = input.ActiveToolType;
		CreationStep = input.CreationStep;
		CreationPoints = Array.AsReadOnly(input.CreationPoints.ToArray());
		CreatingToolInstance = input.CreatingToolInstance;
		CurrentMouseChartCoords = input.CurrentMouseChartCoords;
	}

	public DateTime GetCandleTime(int index) => CandleTime.Get(Candles, index, timeframe);
}
