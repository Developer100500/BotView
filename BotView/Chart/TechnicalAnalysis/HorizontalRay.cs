using System;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using BotView.Chart;

namespace BotView.Chart.TechnicalAnalysis;

/// <summary>
/// Горизонтальный луч: начинается в точке клика и уходит бесконечно вправо на уровне цены
/// </summary>
public class HorizontalRay : TechnicalAnalysisTool
{
	public const double ControlPointRadius = 6.0;

	/// <summary>Время начала луча</summary>
	public DateTime StartTime { get; set; }

	/// <summary>Цена луча</summary>
	public double Price { get; set; }

	public Brush Color { get; set; }

	public double Thickness { get; set; }

	public override bool SupportsControlPoints => true;

	public override System.Windows.Input.Cursor GetHoverCursor()
	{
		return System.Windows.Input.Cursors.SizeNS;
	}

	public override System.Windows.Input.Cursor GetEditCursor()
	{
		return System.Windows.Input.Cursors.SizeAll;
	}

	public override System.Windows.Input.Cursor GetControlPointCursor(int controlPointIndex)
	{
		return System.Windows.Input.Cursors.SizeAll;
	}

	public override bool TryGetPriceScaleAnchor(out double price1, out double price2)
	{
		price1 = Price;
		price2 = 0;
		return true;
	}

	public HorizontalRay(DateTime startTime, double price, Brush color, double thickness = 2.0)
	{
		StartTime = startTime;
		Price = price;
		Color = color;
		Thickness = thickness;
		IsVisible = true;
	}

	public override void Draw(
		DrawingContext drawingContext,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		ViewportClippingCoords viewport)
	{
		double minVisiblePrice = Math.Min(viewport.minPrice, viewport.maxPrice);
		double maxVisiblePrice = Math.Max(viewport.minPrice, viewport.maxPrice);
		if (Price < minVisiblePrice || Price > maxVisiblePrice)
			return;

		if (StartTime > viewport.maxTime)
			return;

		DateTime visibleStart = StartTime > viewport.minTime ? StartTime : viewport.minTime;
		var startChart = new ChartCoordinates(visibleStart, Price);
		var endChart = new ChartCoordinates(viewport.maxTime, Price);

		Coordinates startView = chartToViewConverter(startChart);
		Coordinates endView = chartToViewConverter(endChart);

		if (!AreCoordinatesValid(startView, endView))
			return;

		Pen linePen = new Pen(Color, Thickness);
		drawingContext.DrawLine(
			linePen,
			new System.Windows.Point(startView.x, startView.y),
			new System.Windows.Point(endView.x, endView.y));

		if (IsBeingEdited)
			DrawStartControlPoint(drawingContext, chartToViewConverter);
	}

	public override bool HitTest(
		Coordinates viewCoords,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		ViewportClippingCoords viewport,
		double tolerance = 5.0)
	{
		var originView = chartToViewConverter(new ChartCoordinates(StartTime, Price));
		if (double.IsNaN(originView.y) || double.IsInfinity(originView.y) ||
			double.IsNaN(originView.x) || double.IsInfinity(originView.x))
			return false;

		if (viewCoords.x < originView.x - tolerance)
			return false;

		return Math.Abs(viewCoords.y - originView.y) <= tolerance;
	}

	public override void UpdatePosition(ChartCoordinates chartCoords)
	{
		StartTime = chartCoords.time;
		Price = chartCoords.price;
	}

	public override void Translate(TimeSpan timeDelta, double priceDelta)
	{
		StartTime = StartTime.Add(timeDelta);
		Price += priceDelta;
	}

	public override int GetControlPointIndex(
		Coordinates viewCoords,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		double tolerance = -1)
	{
		if (tolerance < 0) tolerance = ControlPointRadius + 3;

		var originView = chartToViewConverter(new ChartCoordinates(StartTime, Price));
		double dist = Math.Sqrt(
			Math.Pow(viewCoords.x - originView.x, 2) +
			Math.Pow(viewCoords.y - originView.y, 2));

		return dist <= tolerance ? 0 : -1;
	}

	public override void UpdateControlPoint(int controlPointIndex, ChartCoordinates chartCoords)
	{
		if (controlPointIndex != 0)
			return;

		StartTime = chartCoords.time;
		Price = chartCoords.price;
	}

	public override JObject toJson()
	{
		string colorString = "#FFFFFF";
		if (Color is SolidColorBrush solidBrush)
		{
			var color = solidBrush.Color;
			colorString = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
		}

		return new JObject
		{
			["type"] = "HorizontalRay",
			["startTime"] = StartTime.ToString("o"),
			["price"] = Price,
			["color"] = colorString,
			["thickness"] = Thickness,
			["isVisible"] = IsVisible
		};
	}

	public override string toJsonString()
	{
		return toJson().ToString(Newtonsoft.Json.Formatting.None);
	}

	public static HorizontalRay? FromJson(JObject json)
	{
		if (json == null)
			return null;

		string? type = json["type"]?.ToString();
		if (type != "HorizontalRay")
			return null;

		if (!DateTime.TryParse(json["startTime"]?.ToString(), out DateTime startTime))
			startTime = DateTime.Now;

		double price = json["price"]?.Value<double>() ?? 0;
		double thickness = json["thickness"]?.Value<double>() ?? 2.0;
		bool isVisible = json["isVisible"]?.Value<bool>() ?? true;

		Brush color = Brushes.OrangeRed;
		string? colorString = json["color"]?.ToString();
		if (!string.IsNullOrEmpty(colorString) && colorString.StartsWith("#") && colorString.Length == 9)
		{
			try
			{
				byte a = Convert.ToByte(colorString.Substring(1, 2), 16);
				byte r = Convert.ToByte(colorString.Substring(3, 2), 16);
				byte g = Convert.ToByte(colorString.Substring(5, 2), 16);
				byte b = Convert.ToByte(colorString.Substring(7, 2), 16);
				var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
				brush.Freeze();
				color = brush;
			}
			catch
			{
			}
		}

		return new HorizontalRay(startTime, price, color, thickness)
		{
			IsVisible = isVisible
		};
	}

	private void DrawStartControlPoint(
		DrawingContext drawingContext,
		Func<ChartCoordinates, Coordinates> chartToViewConverter)
	{
		var originView = chartToViewConverter(new ChartCoordinates(StartTime, Price));
		if (!AreCoordinatesValid(originView, originView))
			return;

		var controlPointBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255));
		var controlPointPen = new Pen(Color, 2.0);
		drawingContext.DrawEllipse(
			controlPointBrush,
			controlPointPen,
			new System.Windows.Point(originView.x, originView.y),
			ControlPointRadius,
			ControlPointRadius);
	}

	private static bool AreCoordinatesValid(Coordinates a, Coordinates b)
	{
		return !double.IsNaN(a.x) && !double.IsNaN(a.y) &&
			!double.IsNaN(b.x) && !double.IsNaN(b.y) &&
			!double.IsInfinity(a.x) && !double.IsInfinity(a.y) &&
			!double.IsInfinity(b.x) && !double.IsInfinity(b.y);
	}
}
