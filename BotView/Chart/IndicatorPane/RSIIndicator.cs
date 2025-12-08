using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using TALib;

namespace BotView.Chart.IndicatorPane;

/// <summary>
/// RSI (Relative Strength Index) Indicator using TA-Lib
/// Measures the speed and magnitude of price changes on a scale of 0 to 100
/// </summary>
public class RSIIndicator : Indicator
{
	/// <summary>RSI calculation period (default 14)</summary>
	public int Period { get; set; } = 14;

	/// <summary>Overbought level (default 70)</summary>
	public double OverboughtLevel { get; set; } = 70;

	/// <summary>Oversold level (default 30)</summary>
	public double OversoldLevel { get; set; } = 30;

	/// <summary>Middle line level (default 50)</summary>
	public double MiddleLevel { get; set; } = 50;

	/// <summary>Whether to show the middle line</summary>
	public bool ShowMiddleLine { get; set; } = true;

	public RSIIndicator() : base()
	{
		Id = "rsi";
		Name = "RSI";
		Color = Brushes.Purple;
		Thickness = 1.5;
		SetupReferenceLines();
	}

	public RSIIndicator(int period) : this()
	{
		Period = period;
		Name = $"RSI({period})";
	}

	public RSIIndicator(int period, Brush color) : this(period)
	{
		Color = color;
	}

	/// <summary>Sets up default reference lines for overbought/oversold levels</summary>
	private void SetupReferenceLines()
	{
		ReferenceLines.Clear();
		ReferenceLines.Add(OversoldLevel);
		if (ShowMiddleLine)
			ReferenceLines.Add(MiddleLevel);
		ReferenceLines.Add(OverboughtLevel);
		ReferenceLinesColor = new SolidColorBrush(System.Windows.Media.Color.FromArgb(128, 128, 128, 128));
	}

	/// <summary>Calculates RSI values from candlestick data using TA-Lib</summary>
	/// <param name="candles">OHLCV candlestick data array</param>
	/// <param name="getTime">Function to get DateTime for each candle index</param>
	public void Calculate(OHLCV[] candles, Func<int, DateTime> getTime)
	{
		Points.Clear();

		if (candles == null || candles.Length < Period + 1)
			return;

		// Extract close prices for TA-Lib
		double[] closePrices = new double[candles.Length];
		for (int i = 0; i < candles.Length; i++)
		{
			closePrices[i] = candles[i].close;
		}

		// Prepare output array
		double[] rsiOutput = new double[candles.Length];

		// Calculate RSI using TA-Lib
		var result = Functions.Rsi(
			inReal: new ReadOnlySpan<double>(closePrices),
			inRange: Range.All,
			outReal: new Span<double>(rsiOutput),
			outRange: out Range outRange,
			optInTimePeriod: Period
		);

		if (result != Core.RetCode.Success)
			return;

		// Get the valid output range
		int outBegIdx = outRange.Start.Value;
		int outEndIdx = outRange.End.Value;

		// Convert TA-Lib output to indicator points
		for (int i = outBegIdx; i < outEndIdx; i++)
		{
			int candleIndex = i;
			DateTime time = getTime(candleIndex);
			Points.Add(new IndicatorPoint(time, rsiOutput[i - outBegIdx]));
		}
	}

	/// <summary>Calculates RSI from CandlestickData structure</summary>
	public void Calculate(CandlestickData data, ChartController controller)
	{
		if (data.candles == null || data.candles.Length == 0)
			return;

		Calculate(data.candles, (index) => controller.GetCandleTime(index));
	}

	/// <summary>Draws the RSI line</summary>
	public override void Draw(IndicatorDrawContext context)
	{
		if (Points.Count < 2)
			return;

		Pen linePen = new Pen(Color, Thickness);
		linePen.Freeze();

		DateTime minTime = context.Model.Viewport.minTime;
		DateTime maxTime = context.Model.Viewport.maxTime;

		Point? lastPoint = null;

		for (int i = 0; i < Points.Count; i++)
		{
			var point = Points[i];

			// Skip points outside visible time range (with margin)
			if (point.Time < minTime.AddMinutes(-30) || point.Time > maxTime.AddMinutes(30))
			{
				lastPoint = null;
				continue;
			}

			Coordinates viewCoords = context.Controller.IndicatorToView(point.Time, point.Value);

			if (!context.IsValidCoordinate(viewCoords))
			{
				lastPoint = null;
				continue;
			}

			Point currentPoint = new Point(viewCoords.x, viewCoords.y);

			if (lastPoint.HasValue)
			{
				context.DrawingContext.DrawLine(linePen, lastPoint.Value, currentPoint);
			}

			lastPoint = currentPoint;
		}
	}

	/// <summary>Override to draw colored zones for overbought/oversold</summary>
	public override void DrawReferenceLines(IndicatorDrawContext context)
	{
		// Draw overbought zone (light red background above 70)
		DrawZone(context, OverboughtLevel, 100, System.Windows.Media.Color.FromArgb(30, 255, 0, 0));

		// Draw oversold zone (light green background below 30)
		DrawZone(context, 0, OversoldLevel, System.Windows.Media.Color.FromArgb(30, 0, 255, 0));

		// Draw reference lines
		base.DrawReferenceLines(context);
	}

	/// <summary>Draws a colored zone between two levels</summary>
	private void DrawZone(IndicatorDrawContext context, double minLevel, double maxLevel, System.Windows.Media.Color zoneColor)
	{
		Coordinates topLeft = context.Controller.IndicatorToView(context.Model.Viewport.minTime, maxLevel);
		Coordinates bottomRight = context.Controller.IndicatorToView(context.Model.Viewport.maxTime, minLevel);

		if (!context.IsValidCoordinate(topLeft) || !context.IsValidCoordinate(bottomRight))
			return;

		// Clamp to indicator pane bounds
		double top = Math.Max(topLeft.y, context.Model.IndicatorPaneTop);
		double bottom = Math.Min(bottomRight.y, context.Model.IndicatorPaneTop + context.Model.IndicatorPaneHeight);

		if (bottom <= top)
			return;

		Rect zoneRect = new Rect(
			context.Model.LeftMargin,
			top,
			context.Model.ChartWidth,
			bottom - top
		);

		Brush zoneBrush = new SolidColorBrush(zoneColor);
		context.DrawingContext.DrawRectangle(zoneBrush, null, zoneRect);
	}

	/// <summary>Gets RSI-specific value range (always 0-100)</summary>
	public new (double min, double max) GetValueRange()
	{
		// RSI is always 0-100, with some padding
		return (0, 100);
	}

	/// <summary>Returns current RSI value (last calculated point)</summary>
	public double? CurrentValue => Points.Count > 0 ? Points[Points.Count - 1].Value : null;

	/// <summary>Returns whether current RSI is in overbought zone</summary>
	public bool IsOverbought => CurrentValue.HasValue && CurrentValue.Value >= OverboughtLevel;

	/// <summary>Returns whether current RSI is in oversold zone</summary>
	public bool IsOversold => CurrentValue.HasValue && CurrentValue.Value <= OversoldLevel;
}
