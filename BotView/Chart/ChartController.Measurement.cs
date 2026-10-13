using System.Windows;
using System.Windows.Input;
using BotView.Chart.TechnicalAnalysis;

namespace BotView.Chart;

public partial class ChartController
{
	/// <summary>Clears the ruler and its active tool mode, then requests updated UI effects.</summary>
	public void ClearMeasurement()
	{
		interaction.Measurement.Clear();
		if (interaction.ActiveToolType == TechnicalAnalysisToolType.Measure) StopCreating();
		NotifyInteractionChanged();
		ViewActionRequested?.Invoke(new(Cursor: Cursors.Arrow));
	}

	/// <summary>Consumes a ruler click at the view position point; completed rulers allow scale navigation.</summary>
	internal InteractionResult HandleMeasurementClick(Point point)
	{
		var measurement = interaction.Measurement;
		if (measurement.Phase == MeasurementPhase.Idle) return new();
		bool inMainPane = DetectPane(point) == ChartPane.Main;
		if (inMainPane)
		{
			measurement.Click(ViewToChart(new(point.X, point.Y)), state.Series.GetSnapshot());
			if (!measurement.IsActive) interaction.ActiveToolType = TechnicalAnalysisToolType.None;
			NotifyInteractionChanged();
		}
		return new(measurement.Phase != MeasurementPhase.Completed || inMainPane,
			measurement.IsActive ? Cursors.Cross : Cursors.Arrow, RequestFocus: inMainPane);
	}

	/// <summary>Updates the pending ruler endpoint at point in view pixels without initiating navigation.</summary>
	internal InteractionResult HandleMeasurementMove(Point point)
	{
		var measurement = interaction.Measurement;
		if (!measurement.IsActive) return new();
		if (DetectPane(point) == ChartPane.Main)
			measurement.Move(ViewToChart(new(point.X, point.Y)), state.Series.GetSnapshot());
		RequestRender();
		return new(true, Cursors.Cross);
	}
}
