using System;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using BotView.Chart;

namespace BotView.Chart.TechnicalAnalysis;

/// <summary>
/// Прямоугольник для обозначения зон на графике
/// Стороны параллельны осям графика
/// Создаётся по двум диагональным углам
/// </summary>
public class Rectangle : TechnicalAnalysisTool
{
	/// <summary>Радиус контрольной точки в пикселях</summary>
	public const double ControlPointRadius = 6.0;

	/// <summary>Время первого угла</summary>
	public DateTime StartTime { get; set; }

	/// <summary>Цена первого угла</summary>
	public double StartPrice { get; set; }

	/// <summary>Время второго угла (по диагонали)</summary>
	public DateTime EndTime { get; set; }

	/// <summary>Цена второго угла (по диагонали)</summary>
	public double EndPrice { get; set; }

	/// <summary>Цвет линий прямоугольника</summary>
	public Brush Color { get; set; }

	/// <summary>Цвет заливки прямоугольника</summary>
	private Brush _fillColor = DefaultFillColor;
	public Brush FillColor 
	{ 
		get => _fillColor;
		set
		{
			if (value == null)
			{
				_fillColor = DefaultFillColor;
				return;
			}
			
			// Если кисть не заморожена, клонируем и замораживаем
			if (!value.IsFrozen)
			{
				var clone = value.Clone();
				clone.Freeze();
				_fillColor = clone;
			}
			else
			{
				_fillColor = value;
			}
		}
	}

	/// <summary>Толщина линий</summary>
	public double Thickness { get; set; }

	/// <summary>Стиль линий</summary>
	public LineStyle Style { get; set; }

	/// <summary>Поддерживает контрольные точки</summary>
	public override bool SupportsControlPoints => true;

	// === Вычисляемые свойства для корректной работы при перестановке углов ===

	/// <summary>Минимальное время (левый край)</summary>
	public DateTime MinTime => StartTime < EndTime ? StartTime : EndTime;

	/// <summary>Максимальное время (правый край)</summary>
	public DateTime MaxTime => StartTime > EndTime ? StartTime : EndTime;

	/// <summary>Минимальная цена (нижний край)</summary>
	public double MinPrice => Math.Min(StartPrice, EndPrice);

	/// <summary>Максимальная цена (верхний край)</summary>
	public double MaxPrice => Math.Max(StartPrice, EndPrice);

	/// <summary>Светло-зеленый цвет заливки по умолчанию (полупрозрачный)</summary>
	public static Brush DefaultFillColor { get; } = CreateDefaultFillColor();

	private static Brush CreateDefaultFillColor()
	{
		var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(64, 144, 238, 144));
		brush.Freeze();
		return brush;
	}

	/// <summary>Конструктор прямоугольника</summary>
	public Rectangle(DateTime startTime, double startPrice, DateTime endTime, double endPrice,
		Brush color, double thickness = 2.0, LineStyle style = LineStyle.Solid, Brush? fillColor = null)
	{
		StartTime = startTime;
		StartPrice = startPrice;
		EndTime = endTime;
		EndPrice = endPrice;
		Color = color;
		FillColor = fillColor ?? DefaultFillColor;
		Thickness = thickness;
		Style = style;
		IsVisible = true;
	}

	/// <summary>Отрисовка прямоугольника (4 линии)</summary>
	public override void Draw(
		DrawingContext drawingContext,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		ViewportClippingCoords viewport)
	{
		// Получаем все 4 угла в View координатах
		var corner0 = chartToViewConverter(new ChartCoordinates(StartTime, StartPrice));
		var corner1 = chartToViewConverter(new ChartCoordinates(EndTime, StartPrice));
		var corner2 = chartToViewConverter(new ChartCoordinates(EndTime, EndPrice));
		var corner3 = chartToViewConverter(new ChartCoordinates(StartTime, EndPrice));

		// Проверяем валидность координат
		if (!AreCoordinatesValid(corner0, corner1, corner2, corner3))
			return;

		// Отрисовываем заливку прямоугольника
		var rectGeometry = new RectangleGeometry(new System.Windows.Rect(
			Math.Min(corner0.x, corner2.x),
			Math.Min(corner0.y, corner2.y),
			Math.Abs(corner2.x - corner0.x),
			Math.Abs(corner2.y - corner0.y)
		));
		drawingContext.DrawGeometry(FillColor, null, rectGeometry);

		// Создаём перо с нужным стилем
		Pen linePen = CreateStyledPen();

		// Отрисовываем 4 стороны прямоугольника
		drawingContext.DrawLine(linePen, 
			new System.Windows.Point(corner0.x, corner0.y), 
			new System.Windows.Point(corner1.x, corner1.y)); // Верхняя/нижняя сторона

		drawingContext.DrawLine(linePen, 
			new System.Windows.Point(corner1.x, corner1.y), 
			new System.Windows.Point(corner2.x, corner2.y)); // Правая сторона

		drawingContext.DrawLine(linePen, 
			new System.Windows.Point(corner2.x, corner2.y), 
			new System.Windows.Point(corner3.x, corner3.y)); // Верхняя/нижняя сторона

		drawingContext.DrawLine(linePen, 
			new System.Windows.Point(corner3.x, corner3.y), 
			new System.Windows.Point(corner0.x, corner0.y)); // Левая сторона

		// Отрисовываем контрольные точки если в режиме редактирования
		if (IsBeingEdited)
		{
			DrawControlPoints(drawingContext, corner0, corner1, corner2, corner3);
		}
	}

	/// <summary>Проверяет валидность координат</summary>
	private static bool AreCoordinatesValid(Coordinates c0, Coordinates c1, Coordinates c2, Coordinates c3)
	{
		return !double.IsNaN(c0.x) && !double.IsNaN(c0.y) &&
			   !double.IsNaN(c1.x) && !double.IsNaN(c1.y) &&
			   !double.IsNaN(c2.x) && !double.IsNaN(c2.y) &&
			   !double.IsNaN(c3.x) && !double.IsNaN(c3.y) &&
			   !double.IsInfinity(c0.x) && !double.IsInfinity(c0.y) &&
			   !double.IsInfinity(c1.x) && !double.IsInfinity(c1.y) &&
			   !double.IsInfinity(c2.x) && !double.IsInfinity(c2.y) &&
			   !double.IsInfinity(c3.x) && !double.IsInfinity(c3.y);
	}

	/// <summary>Отрисовывает контрольные точки в углах</summary>
	private void DrawControlPoints(DrawingContext drawingContext,
		Coordinates corner0, Coordinates corner1, Coordinates corner2, Coordinates corner3)
	{
		var controlPointBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255));
		var controlPointPen = new Pen(Color, 2.0);

		// 4 угловые контрольные точки
		drawingContext.DrawEllipse(controlPointBrush, controlPointPen,
			new System.Windows.Point(corner0.x, corner0.y), ControlPointRadius, ControlPointRadius);

		drawingContext.DrawEllipse(controlPointBrush, controlPointPen,
			new System.Windows.Point(corner1.x, corner1.y), ControlPointRadius, ControlPointRadius);

		drawingContext.DrawEllipse(controlPointBrush, controlPointPen,
			new System.Windows.Point(corner2.x, corner2.y), ControlPointRadius, ControlPointRadius);

		drawingContext.DrawEllipse(controlPointBrush, controlPointPen,
			new System.Windows.Point(corner3.x, corner3.y), ControlPointRadius, ControlPointRadius);
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
				break;
		}

		return pen;
	}

	/// <summary>Проверяет, попадает ли точка на любую из 4 сторон прямоугольника</summary>
	public override bool HitTest(
		Coordinates viewCoords,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		ViewportClippingCoords viewport,
		double tolerance = 5.0)
	{
		// Получаем все 4 угла в View координатах
		var corner0 = chartToViewConverter(new ChartCoordinates(StartTime, StartPrice));
		var corner1 = chartToViewConverter(new ChartCoordinates(EndTime, StartPrice));
		var corner2 = chartToViewConverter(new ChartCoordinates(EndTime, EndPrice));
		var corner3 = chartToViewConverter(new ChartCoordinates(StartTime, EndPrice));

		// Проверяем попадание на каждую из 4 сторон
		if (DistancePointToSegment(viewCoords.x, viewCoords.y, corner0.x, corner0.y, corner1.x, corner1.y) <= tolerance)
			return true;
		if (DistancePointToSegment(viewCoords.x, viewCoords.y, corner1.x, corner1.y, corner2.x, corner2.y) <= tolerance)
			return true;
		if (DistancePointToSegment(viewCoords.x, viewCoords.y, corner2.x, corner2.y, corner3.x, corner3.y) <= tolerance)
			return true;
		if (DistancePointToSegment(viewCoords.x, viewCoords.y, corner3.x, corner3.y, corner0.x, corner0.y) <= tolerance)
			return true;

		return false;
	}

	/// <summary>Вычисляет расстояние от точки до отрезка</summary>
	private static double DistancePointToSegment(double px, double py, double x1, double y1, double x2, double y2)
	{
		double dx = x2 - x1;
		double dy = y2 - y1;
		double lengthSquared = dx * dx + dy * dy;

		if (lengthSquared < 0.0001)
		{
			return Math.Sqrt((px - x1) * (px - x1) + (py - y1) * (py - y1));
		}

		double t = Math.Max(0, Math.Min(1, ((px - x1) * dx + (py - y1) * dy) / lengthSquared));

		double closestX = x1 + t * dx;
		double closestY = y1 + t * dy;

		return Math.Sqrt((px - closestX) * (px - closestX) + (py - closestY) * (py - closestY));
	}

	/// <summary>Обновляет позицию всего прямоугольника (перемещает все углы)</summary>
	public override void UpdatePosition(ChartCoordinates chartCoords)
	{
		// Вычисляем центр прямоугольника
		DateTime midTime = StartTime.AddTicks((EndTime - StartTime).Ticks / 2);
		double midPrice = (StartPrice + EndPrice) / 2;

		TimeSpan timeDelta = chartCoords.time - midTime;
		double priceDelta = chartCoords.price - midPrice;

		// Перемещаем оба угла на одинаковое смещение
		StartTime = StartTime.Add(timeDelta);
		EndTime = EndTime.Add(timeDelta);
		StartPrice += priceDelta;
		EndPrice += priceDelta;
	}

	/// <summary>
	/// Определяет индекс контрольной точки под курсором
	/// 0 = угол (StartTime, StartPrice)
	/// 1 = угол (EndTime, StartPrice)
	/// 2 = угол (EndTime, EndPrice)
	/// 3 = угол (StartTime, EndPrice)
	/// -1 = не найдено
	/// </summary>
	public override int GetControlPointIndex(
		Coordinates viewCoords,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		double tolerance = -1)
	{
		if (tolerance < 0) tolerance = ControlPointRadius + 3;

		// Получаем все 4 угла
		var corner0 = chartToViewConverter(new ChartCoordinates(StartTime, StartPrice));
		var corner1 = chartToViewConverter(new ChartCoordinates(EndTime, StartPrice));
		var corner2 = chartToViewConverter(new ChartCoordinates(EndTime, EndPrice));
		var corner3 = chartToViewConverter(new ChartCoordinates(StartTime, EndPrice));

		// Проверяем каждый угол
		if (DistanceToPoint(viewCoords, corner0) <= tolerance) return 0;
		if (DistanceToPoint(viewCoords, corner1) <= tolerance) return 1;
		if (DistanceToPoint(viewCoords, corner2) <= tolerance) return 2;
		if (DistanceToPoint(viewCoords, corner3) <= tolerance) return 3;

		return -1;
	}

	/// <summary>Вычисляет расстояние между двумя точками</summary>
	private static double DistanceToPoint(Coordinates a, Coordinates b)
	{
		return Math.Sqrt(Math.Pow(a.x - b.x, 2) + Math.Pow(a.y - b.y, 2));
	}

	/// <summary>
	/// Обновляет позицию указанной контрольной точки
	/// При перетаскивании угла противоположный по диагонали угол остаётся на месте
	/// </summary>
	public override void UpdateControlPoint(int controlPointIndex, ChartCoordinates chartCoords)
	{
		switch (controlPointIndex)
		{
			case 0: // Угол (StartTime, StartPrice) - противоположный угол (EndTime, EndPrice) остаётся
				StartTime = chartCoords.time;
				StartPrice = chartCoords.price;
				break;

			case 1: // Угол (EndTime, StartPrice) - противоположный угол (StartTime, EndPrice) остаётся
				EndTime = chartCoords.time;
				StartPrice = chartCoords.price;
				break;

			case 2: // Угол (EndTime, EndPrice) - противоположный угол (StartTime, StartPrice) остаётся
				EndTime = chartCoords.time;
				EndPrice = chartCoords.price;
				break;

			case 3: // Угол (StartTime, EndPrice) - противоположный угол (EndTime, StartPrice) остаётся
				StartTime = chartCoords.time;
				EndPrice = chartCoords.price;
				break;
		}
	}

	/// <summary>Сериализует прямоугольник в JObject</summary>
	public override JObject toJson()
	{
		string colorString = "#FFFFFF";
		if (Color is SolidColorBrush solidBrush)
		{
			var color = solidBrush.Color;
			colorString = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
		}

		string fillColorString = "#4090EE90"; // Светло-зеленый по умолчанию
		if (FillColor is SolidColorBrush fillBrush)
		{
			var fillColor = fillBrush.Color;
			fillColorString = $"#{fillColor.A:X2}{fillColor.R:X2}{fillColor.G:X2}{fillColor.B:X2}";
		}

		return new JObject
		{
			["type"] = "Rectangle",
			["startTime"] = StartTime.ToString("o"),
			["startPrice"] = StartPrice,
			["endTime"] = EndTime.ToString("o"),
			["endPrice"] = EndPrice,
			["color"] = colorString,
			["fillColor"] = fillColorString,
			["thickness"] = Thickness,
			["style"] = Style.ToString(),
			["isVisible"] = IsVisible
		};
	}

	/// <summary>Сериализует прямоугольник в JSON строку</summary>
	public override string toJsonString()
	{
		return toJson().ToString(Newtonsoft.Json.Formatting.None);
	}

	/// <summary>Создаёт прямоугольник из JObject</summary>
	public static Rectangle? FromJson(JObject json)
	{
		if (json == null)
			return null;

		string? type = json["type"]?.ToString();
		if (type != "Rectangle")
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

		// Парсим цвет линии
		Brush color = Brushes.Orange; // значение по умолчанию для прямоугольника
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
				// Используем значение по умолчанию
			}
		}

		// Парсим цвет заливки
		Brush? fillColor = null;
		string? fillColorString = json["fillColor"]?.ToString();
		if (!string.IsNullOrEmpty(fillColorString) && fillColorString.StartsWith("#") && fillColorString.Length == 9)
		{
			try
			{
				byte a = Convert.ToByte(fillColorString.Substring(1, 2), 16);
				byte r = Convert.ToByte(fillColorString.Substring(3, 2), 16);
				byte g = Convert.ToByte(fillColorString.Substring(5, 2), 16);
				byte b = Convert.ToByte(fillColorString.Substring(7, 2), 16);
				var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
				brush.Freeze();
				fillColor = brush;
			}
			catch
			{
				// Используем значение по умолчанию
			}
		}

		var rect = new Rectangle(startTime, startPrice, endTime, endPrice, color, thickness, style, fillColor)
		{
			IsVisible = isVisible
		};

		return rect;
	}
}

