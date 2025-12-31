using System;
using System.Windows;
using System.Windows.Media;

namespace BotView.Chart.IndicatorPane;

/// <summary>Renders indicator data in the indicator pane</summary>
public class IndicatorRenderer
{
	private readonly ChartModel model;
	private readonly ChartController controller;

	public IndicatorRenderer(ChartModel model, ChartController controller)
	{
		this.model = model ?? throw new ArgumentNullException(nameof(model));
		this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
	}

	/// <summary>Main render method for all indicators</summary>
	public void Render(DrawingContext drawingContext)
	{
		if (model.Indicators.Count == 0)
			return;

		// Create clipping region for indicator pane
		RectangleGeometry clipGeometry = new RectangleGeometry(new Rect(
			model.LeftMargin,
			model.IndicatorPaneTop,
			model.ChartWidth,
			model.IndicatorPaneHeight
		));

		drawingContext.PushClip(clipGeometry);

		try
		{
			// Create draw context for indicators
			var context = new IndicatorDrawContext(drawingContext, model, controller);

			foreach (var indicator in model.Indicators)
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
