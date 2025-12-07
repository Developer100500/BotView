using System;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using BotView.Chart;

namespace BotView.Chart.TechnicalAnalysis;

/// <summary>
/// Горизонтальная линия для обозначения уровней поддержки и сопротивления
/// Использует Chart Coordinates (цена) для позиционирования
/// </summary>
public class HorizontalLine : TechnicalAnalysisTool
{
	public double Price { get; set; }

	public Brush Color { get; set; }
 
	public double Thickness { get; set; }

	/// <summary>
	/// Конструктор горизонтальной линии
	/// </summary>
	/// <param name="price">Цена линии</param>
	/// <param name="color">Цвет линии</param>
	/// <param name="thickness">Толщина линии</param>
	public HorizontalLine(double price, Brush color, double thickness = 2.0)
	{
		Price = price;
		Color = color;
		Thickness = thickness;
		IsVisible = true;
	}

	/// <summary>Отрисовка горизонтальной линии через весь график</summary>
	public override void Draw(
		DrawingContext drawingContext,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		ViewportClippingCoords viewport
	)
	{
		// Проверяем, находится ли линия в видимом диапазоне цен
		double minVisiblePrice = Math.Min(viewport.minPrice, viewport.maxPrice);
		double maxVisiblePrice = Math.Max(viewport.minPrice, viewport.maxPrice);
		
		// Если линия полностью вне видимой области, не рисуем её
		if (Price < minVisiblePrice || Price > maxVisiblePrice)
			return;

		// Создаем точки начала и конца линии в Chart Coordinates
		// Линия проходит от minTime до maxTime на уровне заданной цены
		ChartCoordinates startPoint = new ChartCoordinates(viewport.minTime, Price);
		ChartCoordinates endPoint = new ChartCoordinates(viewport.maxTime, Price);

		// Конвертируем в View Coordinates
		Coordinates startView = chartToViewConverter(startPoint);
		Coordinates endView = chartToViewConverter(endPoint);

		// Проверяем, что координаты валидны (не NaN и не Infinity)
		if (double.IsNaN(startView.x) || double.IsNaN(startView.y) || 
		    double.IsNaN(endView.x) || double.IsNaN(endView.y) ||
		    double.IsInfinity(startView.x) || double.IsInfinity(startView.y) ||
		    double.IsInfinity(endView.x) || double.IsInfinity(endView.y))
			return;

		// Создаем перо для отрисовки
		Pen linePen = new Pen(Color, Thickness);

		// Отрисовываем линию
		drawingContext.DrawLine(linePen, new System.Windows.Point(startView.x, startView.y), new System.Windows.Point(endView.x, endView.y));
	}

	/// <summary>
	/// Проверяет, попадает ли точка на горизонтальную линию
	/// </summary>
	public override bool HitTest(
		Coordinates viewCoords,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		ViewportClippingCoords viewport,
		double tolerance = 5.0)
	{
		// Конвертируем цену линии в Y-координату View
		ChartCoordinates linePoint = new ChartCoordinates(viewport.minTime, Price);
		Coordinates lineView = chartToViewConverter(linePoint);

		// Проверяем валидность координат
		if (double.IsNaN(lineView.y) || double.IsInfinity(lineView.y))
			return false;

		// Проверяем, находится ли Y-координата мыши в пределах tolerance от линии
		double distance = Math.Abs(viewCoords.y - lineView.y);
		return distance <= tolerance;
	}

	/// <summary>
	/// Обновляет позицию горизонтальной линии (цену)
	/// </summary>
	public override void UpdatePosition(ChartCoordinates chartCoords)
	{
		Price = chartCoords.price;
	}

	/// <summary>Сериализует горизонтальную линию в JObject</summary>
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
			["type"] = "HorizontalLine",
			["price"] = Price,
			["color"] = colorString,
			["thickness"] = Thickness,
			["isVisible"] = IsVisible
		};
	}

	/// <summary>Сериализует горизонтальную линию в JSON строку</summary>
	public override string toJsonString()
	{
		return toJson().ToString(Newtonsoft.Json.Formatting.None);
	}

	/// <summary>Создаёт горизонтальную линию из JObject</summary>
	public static HorizontalLine? FromJson(JObject json)
	{
		if (json == null)
			return null;

		// Проверяем тип инструмента
		string? type = json["type"]?.ToString();
		if (type != "HorizontalLine")
			return null;

		// Извлекаем параметры
		double price = json["price"]?.Value<double>() ?? 0;
		double thickness = json["thickness"]?.Value<double>() ?? 2.0;
		bool isVisible = json["isVisible"]?.Value<bool>() ?? true;

		// Парсим цвет из строки формата #AARRGGBB
		Brush color = Brushes.Red; // значение по умолчанию (уже заморожено)
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
				// Если парсинг не удался, используем значение по умолчанию
			}
		}

		var line = new HorizontalLine(price, color, thickness)
		{
			IsVisible = isVisible
		};

		return line;
	}
}

