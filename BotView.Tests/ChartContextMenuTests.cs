using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BotView.Chart.ChartViews;
using BotView.Chart.TechnicalAnalysis;
using Newtonsoft.Json.Linq;

namespace BotView.Tests;

public sealed class ChartContextMenuTests
{
	[Fact]
	public void NestedWidgetsMeasureArrangeAndHitTestAcrossOrientations()
	{
		var first = new MenuButtonWidget(new Size(30, 20), () => { }, "A");
		var second = new MenuButtonWidget(new Size(40, 20), () => { }, "B");
		var last = new MenuButtonWidget(new Size(75, 25), () => { }, "C");
		var row = new StackMenuWidget(Orientation.Horizontal, 5).Add(first).Add(second);
		var root = new StackMenuWidget(Orientation.Vertical, 6, new Thickness(10)).Add(row).Add(last);

		Assert.Equal(new Size(95, 71), root.Measure());
		root.Arrange(new Rect(new Point(100, 200), root.Measure()));
		Assert.Same(first, root.HitTest(new Point(115, 215)));
		Assert.Same(second, root.HitTest(new Point(150, 215)));
		Assert.Same(last, root.HitTest(new Point(115, 245)));
		Assert.Null(root.HitTest(new Point(100, 200)));
	}

	[Fact]
	public void MenuPositionIsClampedAtWindowEdges()
	{
		Assert.Equal(new Point(8, 8), ChartContextMenu.ClampToWindow(
			new Point(-20, -30), new Size(200, 100), new Size(500, 400)));
		Assert.Equal(new Point(292, 292), ChartContextMenu.ClampToWindow(
			new Point(490, 390), new Size(200, 100), new Size(500, 400)));
	}

	[Theory]
	[InlineData("#112233", 255, 17, 34, 51)]
	[InlineData("#80112233", 128, 17, 34, 51)]
	[InlineData(" #abcdef ", 255, 171, 205, 239)]
	public void HexInputAcceptsRgbAndArgb(string text, byte a, byte r, byte g, byte b)
	{
		Assert.True(ChartContextMenu.TryParseHex(text, out var color));
		Assert.Equal(Color.FromArgb(a, r, g, b), color);
		Assert.True(ChartContextMenu.TryParseHex(ChartContextMenu.FormatHex(color), out var roundTrip));
		Assert.Equal(color, roundTrip);
	}

	[Theory]
	[InlineData("#12345")]
	[InlineData("#GG1122")]
	[InlineData("112233")]
	[InlineData("")]
	public void HexInputRejectsInvalidText(string text)
	{
		Assert.False(ChartContextMenu.TryParseHex(text, out _));
	}

	[Fact]
	public void OutlineSettingsRoundTripForAllExistingTools()
	{
		DateTime time = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
		TechnicalAnalysisTool[] tools =
		{
			new HorizontalLine(100, Brushes.Red),
			new HorizontalRay(time, 100, Brushes.Red),
			new TrendLine(time, 100, time.AddDays(1), 110, Brushes.Red),
			new TrendChannel(time, 100, time.AddDays(1), 110, 5, Brushes.Red),
			new BotView.Chart.TechnicalAnalysis.Rectangle(time, 100, time.AddDays(1), 110, Brushes.Red)
		};
		foreach (var tool in tools)
		{
			var outline = (IStrokeStyleTool)tool;
			outline.Style = LineStyle.Dotted;
			outline.Thickness = 4;
			outline.Color = Brushes.Blue;
			JObject json = tool.toJson();
			TechnicalAnalysisTool restored = tool switch
			{
				HorizontalLine => HorizontalLine.FromJson(json)!,
				HorizontalRay => HorizontalRay.FromJson(json)!,
				TrendLine => TrendLine.FromJson(json)!,
				TrendChannel => TrendChannel.FromJson(json)!,
				BotView.Chart.TechnicalAnalysis.Rectangle => BotView.Chart.TechnicalAnalysis.Rectangle.FromJson(json)!,
				_ => throw new InvalidOperationException()
			};
			var result = (IStrokeStyleTool)restored;
			Assert.Equal(LineStyle.Dotted, result.Style);
			Assert.Equal(4, result.Thickness);
			Assert.Equal(Colors.Blue, ((SolidColorBrush)result.Color).Color);
		}
	}

	[Fact]
	public void OldHorizontalToolJsonDefaultsToSolid()
	{
		var lineJson = new HorizontalLine(100, Brushes.Red).toJson();
		lineJson.Remove("style");
		var rayJson = new HorizontalRay(DateTime.UtcNow, 100, Brushes.Red).toJson();
		rayJson.Remove("style");
		Assert.Equal(LineStyle.Solid, HorizontalLine.FromJson(lineJson)!.Style);
		Assert.Equal(LineStyle.Solid, HorizontalRay.FromJson(rayJson)!.Style);
	}

	[Theory]
	[InlineData(LineStyle.Solid)]
	[InlineData(LineStyle.Dashed)]
	[InlineData(LineStyle.Dotted)]
	public void SharedPenFactoryUsesRequestedStyle(LineStyle style)
	{
		var tool = new HorizontalLine(100, Brushes.Red, 3, style);
		Pen pen = StrokePenFactory.Create(tool);
		Assert.Equal(3, pen.Thickness);
		Assert.Same(style switch
		{
			LineStyle.Dashed => DashStyles.Dash,
			LineStyle.Dotted => DashStyles.Dot,
			_ => DashStyles.Solid
		}, pen.DashStyle);
		if (style == LineStyle.Dotted)
			Assert.Equal(PenLineCap.Round, pen.DashCap);
	}
}
