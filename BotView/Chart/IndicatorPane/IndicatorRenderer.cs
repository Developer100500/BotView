using System;
using System.Windows;
using System.Windows.Media;

namespace BotView.Chart.IndicatorPane;

/// <summary>Renders indicator data in the indicator pane</summary>
public class IndicatorRenderer
{
	/// <summary>Main render method for all indicators</summary>
	public void Render(DrawingContext drawingContext, ChartRenderFrame frame)
	{
		if (frame.Indicators.Count == 0)
			return;

		// Create clipping region for indicator pane
		RectangleGeometry clipGeometry = new RectangleGeometry(new Rect(
			frame.LeftMargin,
			frame.IndicatorPaneTop,
			frame.ChartWidth,
			frame.IndicatorPaneHeight
		));

		drawingContext.PushClip(clipGeometry);

		try
		{
			// Create draw context for indicators
			var context = new IndicatorDrawContext(drawingContext, frame);

			foreach (var indicator in frame.Indicators)
			{
				if (!indicator.IsVisible || indicator.Points.Count == 0)
					continue;

				// Draw reference lines first (background)
				indicator.DrawReferenceLines(context);

				// Draw the indicator
				indicator.Draw(context);
			}
		}
		finally
		{
			drawingContext.Pop();
		}
	}
}
