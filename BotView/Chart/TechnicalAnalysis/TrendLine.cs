using System;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using BotView.Chart;

namespace BotView.Chart.TechnicalAnalysis;

/// <summary>Стиль линии</summary>
public enum LineStyle
{
	Solid,
	Dashed,
	Dotted
}

/// <summary>
/// Трендовая (наклонная) линия для обозначения трендов на графике
/// Создается по двум точкам: начальной и конечной
/// </summary>
public class TrendLine : TechnicalAnalysisTool
{
	/// <summary>Радиус контрольной точки в пикселях</summary>
	public const double ControlPointRadius = 6.0;

	/// <summary>Время начальной точки</summary>
	public DateTime StartTime { get; set; }

	/// <summary>Цена начальной точки</summary>
	public double StartPrice { get; set; }

	/// <summary>Время конечной точки</summary>
	public DateTime EndTime { get; set; }

	/// <summary>Цена конечной точки</summary>
	public double EndPrice { get; set; }

	/// <summary>Цвет линии</summary>
	public Brush Color { get; set; }

	/// <summary>Толщина линии</summary>
	public double Thickness { get; set; }

	/// <summary>Стиль линии (сплошная, прерывистая, пунктирная)</summary>
	public LineStyle Style { get; set; }

	/// <summary>Поддерживает контрольные точки</summary>
	public override bool SupportsControlPoints => true;

	/// <summary>Конструктор трендовой линии</summary>
	public TrendLine(DateTime startTime, double startPrice, DateTime endTime, double endPrice, 
		Brush color, double thickness = 2.0, LineStyle style = LineStyle.Solid)
	{
		StartTime = startTime;
		StartPrice = startPrice;
		EndTime = endTime;
		EndPrice = endPrice;
		Color = color;
		Thickness = thickness;
		Style = style;
		IsVisible = true;
	}

	/// <summary>Отрисовка трендовой линии</summary>
	public override void Draw(
		DrawingContext drawingContext,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		ViewportClippingCoords viewport)
	{
		// Конвертируем точки в View Coordinates
		var startChart = new ChartCoordinates(StartTime, StartPrice);
		var endChart = new ChartCoordinates(EndTime, EndPrice);

		Coordinates startView = chartToViewConverter(startChart);
		Coordinates endView = chartToViewConverter(endChart);

		// Проверяем валидность координат
		if (double.IsNaN(startView.x) || double.IsNaN(startView.y) ||
			double.IsNaN(endView.x) || double.IsNaN(endView.y) ||
			double.IsInfinity(startView.x) || double.IsInfinity(startView.y) ||
			double.IsInfinity(endView.x) || double.IsInfinity(endView.y))
			return;

		// Создаем перо с нужным стилем
		Pen linePen = CreateStyledPen();

		// Отрисовываем линию
		drawingContext.DrawLine(linePen, 
			new System.Windows.Point(startView.x, startView.y), 
			new System.Windows.Point(endView.x, endView.y));

		// Отрисовываем контрольные точки если линия в режиме редактирования
		if (IsBeingEdited)
		{
			DrawControlPoints(drawingContext, startView, endView);
		}
	}

	/// <summary>Отрисовывает контрольные точки на концах линии</summary>
	private void DrawControlPoints(DrawingContext drawingContext, Coordinates startView, Coordinates endView)
	{
		var controlPointBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255));
		var controlPointPen = new Pen(Color, 2.0);

		// Начальная контрольная точка
		drawingContext.DrawEllipse(
			controlPointBrush, 
			controlPointPen,
			new System.Windows.Point(startView.x, startView.y),
			ControlPointRadius, 
			ControlPointRadius);

		// Конечная контрольная точка
		drawingContext.DrawEllipse(
			controlPointBrush, 
			controlPointPen,
			new System.Windows.Point(endView.x, endView.y),
			ControlPointRadius, 
			ControlPointRadius);
	}

	/// <summary>Создаёт перо с заданным стилем линии</summary>
	private Pen CreateStyledPen()
	{
		var pen = new Pen(Color, Thickness);

		switch (Style)
		{
			case LineStyle.Dashed:
				pen.DashStyle = new DashStyle(new double[] { 4, 2 }, 0);
				break;
			case LineStyle.Dotted:
				pen.DashStyle = new DashStyle(new double[] { 1, 2 }, 0);
				break;
			case LineStyle.Solid:
			default:
				// Сплошная линия по умолчанию
				break;
		}

		return pen;
	}

	/// <summary>Проверяет, попадает ли точка на трендовую линию</summary>
	public override bool HitTest(
		Coordinates viewCoords,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		ViewportClippingCoords viewport,
		double tolerance = 5.0)
	{
		// Конвертируем точки линии в View Coordinates
		var startChart = new ChartCoordinates(StartTime, StartPrice);
		var endChart = new ChartCoordinates(EndTime, EndPrice);

		Coordinates startView = chartToViewConverter(startChart);
		Coordinates endView = chartToViewConverter(endChart);

		// Проверяем валидность координат
		if (double.IsNaN(startView.x) || double.IsNaN(startView.y) ||
			double.IsNaN(endView.x) || double.IsNaN(endView.y))
			return false;

		// Вычисляем расстояние от точки до отрезка
		double distance = DistancePointToSegment(
			viewCoords.x, viewCoords.y,
			startView.x, startView.y,
			endView.x, endView.y);

		return distance <= tolerance;
	}

	/// <summary>Вычисляет расстояние от точки до отрезка</summary>
	private static double DistancePointToSegment(double px, double py, double x1, double y1, double x2, double y2)
	{
		double dx = x2 - x1;
		double dy = y2 - y1;
		double lengthSquared = dx * dx + dy * dy;

		if (lengthSquared < 0.0001)
		{
			// Отрезок вырожден в точку
			return Math.Sqrt((px - x1) * (px - x1) + (py - y1) * (py - y1));
		}

		// Параметр t определяет проекцию точки на линию
		double t = Math.Max(0, Math.Min(1, ((px - x1) * dx + (py - y1) * dy) / lengthSquared));

		// Ближайшая точка на отрезке
		double closestX = x1 + t * dx;
		double closestY = y1 + t * dy;

		return Math.Sqrt((px - closestX) * (px - closestX) + (py - closestY) * (py - closestY));
	}

	/// <summary>Обновляет позицию трендовой линии (перемещает обе точки)</summary>
	public override void UpdatePosition(ChartCoordinates chartCoords)
	{
		// Вычисляем смещение от старой позиции средней точки к новой
		DateTime midTime = StartTime.AddTicks((EndTime - StartTime).Ticks / 2);
		double midPrice = (StartPrice + EndPrice) / 2;

		TimeSpan timeDelta = chartCoords.time - midTime;
		double priceDelta = chartCoords.price - midPrice;

		// Перемещаем обе точки на одинаковое смещение
		StartTime = StartTime.Add(timeDelta);
		EndTime = EndTime.Add(timeDelta);
		StartPrice += priceDelta;
		EndPrice += priceDelta;
	}

	/// <summary>Определяет индекс контрольной точки под курсором (0=начало, 1=конец, -1=не найдено)</summary>
	public override int GetControlPointIndex(
		Coordinates viewCoords,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		double tolerance = -1)
	{
		if (tolerance < 0) tolerance = ControlPointRadius + 3;

		var startView = chartToViewConverter(new ChartCoordinates(StartTime, StartPrice));
		var endView = chartToViewConverter(new ChartCoordinates(EndTime, EndPrice));

		// Проверяем расстояние до начальной точки
		double distToStart = Math.Sqrt(
			Math.Pow(viewCoords.x - startView.x, 2) + 
			Math.Pow(viewCoords.y - startView.y, 2));
		
		if (distToStart <= tolerance)
			return 0;

		// Проверяем расстояние до конечной точки
		double distToEnd = Math.Sqrt(
			Math.Pow(viewCoords.x - endView.x, 2) + 
			Math.Pow(viewCoords.y - endView.y, 2));
		
		if (distToEnd <= tolerance)
			return 1;

		return -1;
	}

	/// <summary>Обновляет позицию указанной контрольной точки</summary>
	/// <param name="controlPointIndex">0 = начальная точка, 1 = конечная точка</param>
	/// <param name="chartCoords">Новые координаты точки</param>
	public override void UpdateControlPoint(int controlPointIndex, ChartCoordinates chartCoords)
	{
		if (controlPointIndex == 0)
		{
			StartTime = chartCoords.time;
			StartPrice = chartCoords.price;
		}
		else if (controlPointIndex == 1)
		{
			EndTime = chartCoords.time;
			EndPrice = chartCoords.price;
		}
	}

	/// <summary>Сериализует трендовую линию в JObject</summary>
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
			["type"] = "TrendLine",
			["startTime"] = StartTime.ToString("o"),
			["startPrice"] = StartPrice,
			["endTime"] = EndTime.ToString("o"),
			["endPrice"] = EndPrice,
			["color"] = colorString,
			["thickness"] = Thickness,
			["style"] = Style.ToString(),
			["isVisible"] = IsVisible
		};
	}

	/// <summary>Сериализует трендовую линию в JSON строку</summary>
	public override string toJsonString()
	{
		return toJson().ToString(Newtonsoft.Json.Formatting.None);
	}

	/// <summary>Создаёт трендовую линию из JObject</summary>
	public static TrendLine? FromJson(JObject json)
	{
		if (json == null)
			return null;

		string? type = json["type"]?.ToString();
		if (type != "TrendLine")
			return null;

		// Извлекаем параметры
		DateTime startTime = DateTime.Parse(json["startTime"]?.ToString() ?? DateTime.Now.ToString("o"));
		double startPrice = json["startPrice"]?.Value<double>() ?? 0;
		DateTime endTime = DateTime.Parse(json["endTime"]?.ToString() ?? DateTime.Now.ToString("o"));
		double endPrice = json["endPrice"]?.Value<double>() ?? 0;
		double thickness = json["thickness"]?.Value<double>() ?? 2.0;
		bool isVisible = json["isVisible"]?.Value<bool>() ?? true;

		// Парсим стиль линии
		LineStyle style = LineStyle.Solid;
		string? styleString = json["style"]?.ToString();
		if (!string.IsNullOrEmpty(styleString))
		{
			Enum.TryParse(styleString, out style);
		}

		// Парсим цвет
		Brush color = Brushes.Blue; // значение по умолчанию
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
				brush.Freeze(); // Делаем потокобезопасным
				color = brush;
			}
			catch
			{
				// Используем значение по умолчанию
			}
		}

		var line = new TrendLine(startTime, startPrice, endTime, endPrice, color, thickness, style)
		{
			IsVisible = isVisible
		};

		return line;
	}
}

