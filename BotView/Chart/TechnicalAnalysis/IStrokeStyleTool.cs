using System.Windows.Media;

namespace BotView.Chart.TechnicalAnalysis;

/// <summary>Editable outline shared by chart drawing tools.</summary>
public interface IStrokeStyleTool
{
	Brush Color { get; set; }
	double Thickness { get; set; }
	LineStyle Style { get; set; }
}

public static class StrokePenFactory
{
	public static Pen Create(IStrokeStyleTool tool)
	{
		var pen = new Pen(tool.Color, tool.Thickness);
		pen.DashStyle = tool.Style switch
		{
			LineStyle.Dashed => DashStyles.Dash,
			LineStyle.Dotted => DashStyles.Dot,
			_ => DashStyles.Solid
		};
		if (tool.Style == LineStyle.Dotted)
			pen.DashCap = PenLineCap.Round;
		return pen;
	}
}
