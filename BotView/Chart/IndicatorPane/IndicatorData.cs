using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace BotView.Chart.IndicatorPane;

/// <summary>Viewport for indicator pane Y-axis (min/max indicator values)</summary>
public struct IndicatorViewport
{
	public double MinValue { get; set; }
	public double MaxValue { get; set; }

	public IndicatorViewport(double minValue, double maxValue)
	{
		MinValue = minValue;
		MaxValue = maxValue;
	}

	/// <summary>Returns the range of values in this viewport</summary>
	public double Range => MaxValue - MinValue;
}

/// <summary>Single data point for an indicator (timestamp + value)</summary>
public struct IndicatorPoint
{
	public DateTime Time { get; set; }
	public double Value { get; set; }

	public IndicatorPoint(DateTime time, double value)
	{
		Time = time;
		Value = value;
	}
}

/// <summary>Context passed to indicator Draw method containing all rendering dependencies</summary>
public class IndicatorDrawContext
{
	public DrawingContext DrawingContext { get; }
	public ChartModel Model { get; }
	public ChartController Controller { get; }

	public IndicatorDrawContext(DrawingContext drawingContext, ChartModel model, ChartController controller)
	{
		DrawingContext = drawingContext;
		Model = model;
		Controller = controller;
	}

	/// <summary>Check if coordinate is valid (not NaN or Infinity)</summary>
	public bool IsValidCoordinate(Coordinates coords)
	{
		return !double.IsNaN(coords.x) && !double.IsNaN(coords.y) &&
			   !double.IsInfinity(coords.x) && !double.IsInfinity(coords.y);
	}
}

/// <summary>Abstract base class for all indicators</summary>
public abstract class Indicator
{
	/// <summary>Unique identifier for this indicator</summary>
	public string Id { get; set; } = string.Empty;

	/// <summary>Display name of the indicator</summary>
	public string Name { get; set; } = string.Empty;

	/// <summary>Data points for this indicator</summary>
	public List<IndicatorPoint> Points { get; set; } = new List<IndicatorPoint>();

	/// <summary>Color for rendering this indicator</summary>
	public Brush Color { get; set; } = Brushes.Blue;

	/// <summary>Line thickness for rendering</summary>
	public double Thickness { get; set; } = 1.5;

	/// <summary>Whether this indicator is visible</summary>
	public bool IsVisible { get; set; } = true;

	/// <summary>Optional reference lines (e.g., overbought/oversold levels)</summary>
	public List<double> ReferenceLines { get; set; } = new List<double>();

	/// <summary>Color for reference lines</summary>
	public Brush ReferenceLinesColor { get; set; } = Brushes.Gray;

	protected Indicator() { }

	protected Indicator(string id, string name, Brush color)
	{
		Id = id;
		Name = name;
		Color = color;
	}

	/// <summary>Draws the indicator on the chart</summary>
	public abstract void Draw(IndicatorDrawContext context);

	/// <summary>Draws reference lines (e.g., overbought/oversold levels)</summary>
	public virtual void DrawReferenceLines(IndicatorDrawContext context)
	{
		if (ReferenceLines.Count == 0)
			return;

		Pen refPen = new Pen(ReferenceLinesColor, 1);
		refPen.DashStyle = DashStyles.Dash;

		foreach (double level in ReferenceLines)
		{
			Coordinates startView = context.Controller.IndicatorToView(context.Model.Viewport.minTime, level);
			Coordinates endView = context.Controller.IndicatorToView(context.Model.Viewport.maxTime, level);

			if (context.IsValidCoordinate(startView) && context.IsValidCoordinate(endView))
			{
				context.DrawingContext.DrawLine(refPen,
					new Point(startView.x, startView.y),
					new Point(endView.x, endView.y));
			}
		}
	}

	/// <summary>Gets the min and max values from the data points</summary>
	public (double min, double max) GetValueRange()
	{
		if (Points.Count == 0)
			return (0, 100);

		double min = double.MaxValue;
		double max = double.MinValue;

		foreach (var point in Points)
		{
			if (point.Value < min) min = point.Value;
			if (point.Value > max) max = point.Value;
		}

		return (min, max);
	}

	/// <summary>Gets the value at a specific time (or interpolated)</summary>
	public double? GetValueAtTime(DateTime time)
	{
		if (Points.Count == 0)
			return null;

		for (int i = 0; i < Points.Count; i++)
		{
			if (Points[i].Time >= time)
			{
				if (i == 0)
					return Points[0].Value;
				
				return Points[i - 1].Value;
			}
		}

		return Points[Points.Count - 1].Value;
	}
}
