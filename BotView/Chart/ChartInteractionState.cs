using System.Windows;
using BotView.Chart.TechnicalAnalysis;

namespace BotView.Chart;

/// <summary>Input and overlay state owned by one chart, never shared statically.</summary>
public sealed class ChartInteractionState
{
	internal ChartMeasurement Measurement { get; } = new();
	public TechnicalAnalysisTool? SelectedTool { get; internal set; }
	public TechnicalAnalysisToolType ActiveToolType { get; internal set; }
	public int CreationStep { get; internal set; }
	public TechnicalAnalysisTool? CreatingToolInstance { get; internal set; }
	public int EditingControlPointIndex { get; internal set; } = -1;
	public bool CrosshairVisible { get; internal set; }
	public Point CrosshairViewPosition { get; internal set; }
	public ChartPane CrosshairPane { get; internal set; }
	public ChartCoordinates? CurrentMouseChartCoords { get; internal set; }
	internal ChartCoordinates?[] CreationPoints { get; } = new ChartCoordinates?[3];
	internal bool IsCreatingTool => ActiveToolType is not (TechnicalAnalysisToolType.None or TechnicalAnalysisToolType.Measure);
	internal bool IsDragging { get; set; }
	internal bool IsScaleZooming { get; set; }
	internal bool IsDraggingDivider { get; set; }
	internal ScaleZoomMode ScaleZoomMode { get; set; }
	internal ChartPane ActivePane { get; set; }
	internal Point LastMousePosition { get; set; }
	internal TechnicalAnalysisTool? PendingTool { get; set; }
	internal Point PendingStartView { get; set; }
	internal ChartCoordinates PendingLastChart { get; set; }
	internal bool IsDraggingToolBody { get; set; }
}

internal enum ScaleZoomMode { None, TimeScale, PriceScale, IndicatorScale }

public enum MouseCaptureAction { None, Capture, Release }
public enum ContextMenuAction { None, Open, Close }

/// <summary>UI effects applied by ChartView after a controller operation.</summary>
public sealed record InteractionResult(
	bool Handled = false,
	System.Windows.Input.Cursor? Cursor = null,
	MouseCaptureAction Capture = MouseCaptureAction.None,
	bool RequestFocus = false,
	ContextMenuAction Menu = ContextMenuAction.None,
	TechnicalAnalysisTool? MenuTool = null,
	Point MenuPosition = default);
