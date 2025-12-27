using System;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using BotView.Chart;

namespace BotView.Chart.TechnicalAnalysis;

/// <summary>
/// Трендовый канал - две параллельные линии одинаковой длины и наклона
/// Создаётся по трём кликам: первая и вторая точки первой линии, затем положение параллельной линии
/// </summary>
public class TrendChannel : TechnicalAnalysisTool
{
	/// <summary>Радиус контрольной точки в пикселях</summary>
	public const double ControlPointRadius = 6.0;

	/// <summary>Время начальной точки первой линии</summary>
	public DateTime StartTime { get; set; }

	/// <summary>Цена начальной точки первой линии</summary>
	public double StartPrice { get; set; }

	/// <summary>Время конечной точки первой линии</summary>
	public DateTime EndTime { get; set; }

	/// <summary>Цена конечной точки первой линии</summary>
	public double EndPrice { get; set; }

	/// <summary>Смещение второй линии по цене относительно первой линии (может быть отрицательным)</summary>
	public double ParallelOffset { get; set; }

	/// <summary>Цвет линий</summary>
	public Brush Color { get; set; }

	/// <summary>Толщина линий</summary>
	public double Thickness { get; set; }

	/// <summary>Стиль линий (сплошная, прерывистая, пунктирная)</summary>
	public LineStyle Style { get; set; }

	/// <summary>Поддерживает контрольные точки</summary>
	public override bool SupportsControlPoints => true;

	// === Вычисляемые свойства для второй (параллельной) линии ===

	/// <summary>Цена начальной точки параллельной линии</summary>
	public double ParallelStartPrice => StartPrice + ParallelOffset;

	/// <summary>Цена конечной точки параллельной линии</summary>
	public double ParallelEndPrice => EndPrice + ParallelOffset;

	/// <summary>Конструктор трендового канала</summary>
	public TrendChannel(DateTime startTime, double startPrice, DateTime endTime, double endPrice,
		double parallelOffset, Brush color, double thickness = 2.0, LineStyle style = LineStyle.Solid)
	{
		StartTime = startTime;
		StartPrice = startPrice;
		EndTime = endTime;
		EndPrice = endPrice;
		ParallelOffset = parallelOffset;
		Color = color;
		Thickness = thickness;
		Style = style;
		IsVisible = true;
	}

	/// <summary>Отрисовка трендового канала (двух параллельных линий)</summary>
	public override void Draw(
		DrawingContext drawingContext,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		ViewportClippingCoords viewport)
	{
		// Конвертируем точки первой линии в View Coordinates
		var startChart1 = new ChartCoordinates(StartTime, StartPrice);
		var endChart1 = new ChartCoordinates(EndTime, EndPrice);

		Coordinates startView1 = chartToViewConverter(startChart1);
		Coordinates endView1 = chartToViewConverter(endChart1);

		// Конвертируем точки параллельной линии в View Coordinates
		var startChart2 = new ChartCoordinates(StartTime, ParallelStartPrice);
		var endChart2 = new ChartCoordinates(EndTime, ParallelEndPrice);

		Coordinates startView2 = chartToViewConverter(startChart2);
		Coordinates endView2 = chartToViewConverter(endChart2);

		// Проверяем валидность координат
		if (!AreCoordinatesValid(startView1, endView1, startView2, endView2))
			return;

		// Создаём перо с нужным стилем
		Pen linePen = CreateStyledPen();

		// Отрисовываем первую линию
		drawingContext.DrawLine(linePen,
			new System.Windows.Point(startView1.x, startView1.y),
			new System.Windows.Point(endView1.x, endView1.y));

		// Отрисовываем параллельную линию
		drawingContext.DrawLine(linePen,
			new System.Windows.Point(startView2.x, startView2.y),
			new System.Windows.Point(endView2.x, endView2.y));

		// Отрисовываем контрольные точки если канал в режиме редактирования
		if (IsBeingEdited)
		{
			DrawControlPoints(drawingContext, startView1, endView1, startView2, endView2);
		}
	}

	/// <summary>Отрисовка превью второй линии пунктиром при создании канала</summary>
	public void DrawPreviewParallelLine(
		DrawingContext drawingContext,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		double previewOffset)
	{
		// Конвертируем точки превью линии в View Coordinates
		var startChart = new ChartCoordinates(StartTime, StartPrice + previewOffset);
		var endChart = new ChartCoordinates(EndTime, EndPrice + previewOffset);

		Coordinates startView = chartToViewConverter(startChart);
		Coordinates endView = chartToViewConverter(endChart);

		// Проверяем валидность координат
		if (double.IsNaN(startView.x) || double.IsNaN(startView.y) ||
			double.IsNaN(endView.x) || double.IsNaN(endView.y) ||
			double.IsInfinity(startView.x) || double.IsInfinity(startView.y) ||
			double.IsInfinity(endView.x) || double.IsInfinity(endView.y))
			return;

		// Создаём пунктирное перо для превью
		var previewPen = new Pen(Color, Thickness);
		previewPen.DashStyle = new DashStyle(new double[] { 4, 4 }, 0);

		// Отрисовываем превью линию
		drawingContext.DrawLine(previewPen,
			new System.Windows.Point(startView.x, startView.y),
			new System.Windows.Point(endView.x, endView.y));
	}

	/// <summary>Проверяет валидность всех координат</summary>
	private static bool AreCoordinatesValid(Coordinates c1, Coordinates c2, Coordinates c3, Coordinates c4)
	{
		return !double.IsNaN(c1.x) && !double.IsNaN(c1.y) &&
			   !double.IsNaN(c2.x) && !double.IsNaN(c2.y) &&
			   !double.IsNaN(c3.x) && !double.IsNaN(c3.y) &&
			   !double.IsNaN(c4.x) && !double.IsNaN(c4.y) &&
			   !double.IsInfinity(c1.x) && !double.IsInfinity(c1.y) &&
			   !double.IsInfinity(c2.x) && !double.IsInfinity(c2.y) &&
			   !double.IsInfinity(c3.x) && !double.IsInfinity(c3.y) &&
			   !double.IsInfinity(c4.x) && !double.IsInfinity(c4.y);
	}

	/// <summary>Отрисовывает контрольные точки на концах обеих линий и центральные точки</summary>
	private void DrawControlPoints(DrawingContext drawingContext, 
		Coordinates startView1, Coordinates endView1, 
		Coordinates startView2, Coordinates endView2)
	{
		var controlPointBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255));
		var offsetPointBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 255));
		var controlPointPen = new Pen(Color, 2.0);

		// Контрольные точки первой линии (индексы 0 и 1)
		drawingContext.DrawEllipse(
			controlPointBrush,
			controlPointPen,
			new System.Windows.Point(startView1.x, startView1.y),
			ControlPointRadius,
			ControlPointRadius);

		drawingContext.DrawEllipse(
			controlPointBrush,
			controlPointPen,
			new System.Windows.Point(endView1.x, endView1.y),
			ControlPointRadius,
			ControlPointRadius);

		// Центральная контрольная точка первой линии (индекс 5) - для регулировки offset
		double midX1 = (startView1.x + endView1.x) / 2;
		double midY1 = (startView1.y + endView1.y) / 2;
		drawingContext.DrawEllipse(
			offsetPointBrush,
			controlPointPen,
			new System.Windows.Point(midX1, midY1),
			ControlPointRadius,
			ControlPointRadius);

		// Контрольные точки параллельной линии (индексы 2 и 3)
		drawingContext.DrawEllipse(
			controlPointBrush,
			controlPointPen,
			new System.Windows.Point(startView2.x, startView2.y),
			ControlPointRadius,
			ControlPointRadius);

		drawingContext.DrawEllipse(
			controlPointBrush,
			controlPointPen,
			new System.Windows.Point(endView2.x, endView2.y),
			ControlPointRadius,
			ControlPointRadius);

		// Центральная контрольная точка параллельной линии (индекс 4) - для регулировки offset
		double midX2 = (startView2.x + endView2.x) / 2;
		double midY2 = (startView2.y + endView2.y) / 2;
		drawingContext.DrawEllipse(
			offsetPointBrush,
			controlPointPen,
			new System.Windows.Point(midX2, midY2),
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
				break;
		}

		return pen;
	}

	/// <summary>Проверяет, попадает ли точка на трендовый канал (на любую из двух линий)</summary>
	public override bool HitTest(
		Coordinates viewCoords,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		ViewportClippingCoords viewport,
		double tolerance = 5.0)
	{
		// Проверяем попадание на первую линию
		var startChart1 = new ChartCoordinates(StartTime, StartPrice);
		var endChart1 = new ChartCoordinates(EndTime, EndPrice);
		Coordinates startView1 = chartToViewConverter(startChart1);
		Coordinates endView1 = chartToViewConverter(endChart1);

		if (!double.IsNaN(startView1.x) && !double.IsNaN(startView1.y) &&
			!double.IsNaN(endView1.x) && !double.IsNaN(endView1.y))
		{
			double distance1 = DistancePointToSegment(
				viewCoords.x, viewCoords.y,
				startView1.x, startView1.y,
				endView1.x, endView1.y);

			if (distance1 <= tolerance)
				return true;
		}

		// Проверяем попадание на параллельную линию
		var startChart2 = new ChartCoordinates(StartTime, ParallelStartPrice);
		var endChart2 = new ChartCoordinates(EndTime, ParallelEndPrice);
		Coordinates startView2 = chartToViewConverter(startChart2);
		Coordinates endView2 = chartToViewConverter(endChart2);

		if (!double.IsNaN(startView2.x) && !double.IsNaN(startView2.y) &&
			!double.IsNaN(endView2.x) && !double.IsNaN(endView2.y))
		{
			double distance2 = DistancePointToSegment(
				viewCoords.x, viewCoords.y,
				startView2.x, startView2.y,
				endView2.x, endView2.y);

			if (distance2 <= tolerance)
				return true;
		}

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

	/// <summary>Обновляет позицию всего канала (перемещает все точки)</summary>
	public override void UpdatePosition(ChartCoordinates chartCoords)
	{
		// Вычисляем смещение от старой позиции средней точки к новой
		DateTime midTime = StartTime.AddTicks((EndTime - StartTime).Ticks / 2);
		double midPrice = (StartPrice + EndPrice) / 2;

		TimeSpan timeDelta = chartCoords.time - midTime;
		double priceDelta = chartCoords.price - midPrice;

		// Перемещаем обе точки первой линии на одинаковое смещение
		StartTime = StartTime.Add(timeDelta);
		EndTime = EndTime.Add(timeDelta);
		StartPrice += priceDelta;
		EndPrice += priceDelta;
		// ParallelOffset остаётся неизменным, так как вторая линия перемещается вместе с первой
	}

	/// <summary>
	/// Определяет индекс контрольной точки под курсором
	/// 0 = начало первой линии, 1 = конец первой линии
	/// 2 = начало параллельной линии, 3 = конец параллельной линии
	/// 4 = центр параллельной линии (для регулировки offset)
	/// 5 = центр первой линии (для регулировки offset)
	/// -1 = не найдено
	/// </summary>
	public override int GetControlPointIndex(
		Coordinates viewCoords,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		double tolerance = -1)
	{
		if (tolerance < 0) tolerance = ControlPointRadius + 3;

		// Контрольные точки первой линии
		var startView1 = chartToViewConverter(new ChartCoordinates(StartTime, StartPrice));
		var endView1 = chartToViewConverter(new ChartCoordinates(EndTime, EndPrice));

		// Контрольные точки параллельной линии
		var startView2 = chartToViewConverter(new ChartCoordinates(StartTime, ParallelStartPrice));
		var endView2 = chartToViewConverter(new ChartCoordinates(EndTime, ParallelEndPrice));

		// Проверяем центральные точки первыми (приоритет) - индексы 4 и 5
		double midX2 = (startView2.x + endView2.x) / 2;
		double midY2 = (startView2.y + endView2.y) / 2;
		double distToMid2 = Math.Sqrt(
			Math.Pow(viewCoords.x - midX2, 2) +
			Math.Pow(viewCoords.y - midY2, 2));
		if (distToMid2 <= tolerance)
			return 4;

		double midX1 = (startView1.x + endView1.x) / 2;
		double midY1 = (startView1.y + endView1.y) / 2;
		double distToMid1 = Math.Sqrt(
			Math.Pow(viewCoords.x - midX1, 2) +
			Math.Pow(viewCoords.y - midY1, 2));
		if (distToMid1 <= tolerance)
			return 5;

		// Проверяем начальную точку первой линии (индекс 0)
		double distToStart1 = Math.Sqrt(
			Math.Pow(viewCoords.x - startView1.x, 2) +
			Math.Pow(viewCoords.y - startView1.y, 2));
		if (distToStart1 <= tolerance)
			return 0;

		// Проверяем конечную точку первой линии (индекс 1)
		double distToEnd1 = Math.Sqrt(
			Math.Pow(viewCoords.x - endView1.x, 2) +
			Math.Pow(viewCoords.y - endView1.y, 2));
		if (distToEnd1 <= tolerance)
			return 1;

		// Проверяем начальную точку параллельной линии (индекс 2)
		double distToStart2 = Math.Sqrt(
			Math.Pow(viewCoords.x - startView2.x, 2) +
			Math.Pow(viewCoords.y - startView2.y, 2));
		if (distToStart2 <= tolerance)
			return 2;

		// Проверяем конечную точку параллельной линии (индекс 3)
		double distToEnd2 = Math.Sqrt(
			Math.Pow(viewCoords.x - endView2.x, 2) +
			Math.Pow(viewCoords.y - endView2.y, 2));
		if (distToEnd2 <= tolerance)
			return 3;

		return -1;
	}

	/// <summary>
	/// Определяет, на какой линии находится курсор
	/// 0 = первая линия (основная), 1 = параллельная линия, -1 = не на линии
	/// </summary>
	public int GetLineUnderCursor(
		Coordinates viewCoords,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		double tolerance = 5.0)
	{
		// Проверяем первую линию
		var startView1 = chartToViewConverter(new ChartCoordinates(StartTime, StartPrice));
		var endView1 = chartToViewConverter(new ChartCoordinates(EndTime, EndPrice));

		double distance1 = DistancePointToSegment(
			viewCoords.x, viewCoords.y,
			startView1.x, startView1.y,
			endView1.x, endView1.y);

		if (distance1 <= tolerance)
			return 0;

		// Проверяем параллельную линию
		var startView2 = chartToViewConverter(new ChartCoordinates(StartTime, ParallelStartPrice));
		var endView2 = chartToViewConverter(new ChartCoordinates(EndTime, ParallelEndPrice));

		double distance2 = DistancePointToSegment(
			viewCoords.x, viewCoords.y,
			startView2.x, startView2.y,
			endView2.x, endView2.y);

		if (distance2 <= tolerance)
			return 1;

		return -1;
	}

	/// <summary>
	/// Обновляет позицию указанной контрольной точки
	/// Индексы 0-1: точки первой линии (вторая линия следует за счёт offset)
	/// Индексы 2-3: точки параллельной линии (первая линия следует, сохраняя offset)
	/// Индексы 4-5: центры линий (меняют только offset)
	/// </summary>
	public override void UpdateControlPoint(int controlPointIndex, ChartCoordinates chartCoords)
	{
		switch (controlPointIndex)
		{
			case 0: // Начальная точка первой линии
				StartTime = chartCoords.time;
				StartPrice = chartCoords.price;
				break;

			case 1: // Конечная точка первой линии
				EndTime = chartCoords.time;
				EndPrice = chartCoords.price;
				break;

			case 2: // Начальная точка параллельной линии - двигаем начало обеих линий
				// Перемещаем начало первой линии так, чтобы параллельная линия оказалась в нужной позиции
				StartTime = chartCoords.time;
				StartPrice = chartCoords.price - ParallelOffset;
				break;

			case 3: // Конечная точка параллельной линии - двигаем конец обеих линий
				// Перемещаем конец первой линии так, чтобы параллельная линия оказалась в нужной позиции
				EndTime = chartCoords.time;
				EndPrice = chartCoords.price - ParallelOffset;
				break;

			case 4: // Центр параллельной линии - меняем только offset
				{
					// Вычисляем среднюю цену первой линии
					double midPrice = (StartPrice + EndPrice) / 2;
					// Новый offset = цена курсора минус средняя цена первой линии
					ParallelOffset = chartCoords.price - midPrice;
				}
				break;

			case 5: // Центр первой линии - двигаем первую линию, вторая остаётся на месте
				{
					// Вычисляем текущую среднюю цену первой линии
					double currentMidPrice = (StartPrice + EndPrice) / 2;
					// Вычисляем смещение
					double delta = chartCoords.price - currentMidPrice;
					
					// Сдвигаем первую линию
					StartPrice += delta;
					EndPrice += delta;
					
					// Корректируем offset чтобы вторая линия осталась на месте
					ParallelOffset -= delta;
				}
				break;
		}
	}

	/// <summary>
	/// Обновляет смещение параллельной линии (для drag всей параллельной линии)
	/// </summary>
	public void UpdateParallelOffset(double newOffset)
	{
		ParallelOffset = newOffset;
	}

	/// <summary>Получает тип курсора для конкретной контрольной точки</summary>
	public override System.Windows.Input.Cursor GetControlPointCursor(int controlPointIndex)
	{
		// Для центральных точек (индексы 4 и 5) используем курсор NS
		if (controlPointIndex == 4 || controlPointIndex == 5)
			return System.Windows.Input.Cursors.SizeNS;
		
		return System.Windows.Input.Cursors.Cross;
	}

	/// <summary>Сериализует трендовый канал в JObject</summary>
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
			["type"] = "TrendChannel",
			["startTime"] = StartTime.ToString("o"),
			["startPrice"] = StartPrice,
			["endTime"] = EndTime.ToString("o"),
			["endPrice"] = EndPrice,
			["parallelOffset"] = ParallelOffset,
			["color"] = colorString,
			["thickness"] = Thickness,
			["style"] = Style.ToString(),
			["isVisible"] = IsVisible
		};
	}

	/// <summary>Сериализует трендовый канал в JSON строку</summary>
	public override string toJsonString()
	{
		return toJson().ToString(Newtonsoft.Json.Formatting.None);
	}

	/// <summary>Создаёт трендовый канал из JObject</summary>
	public static TrendChannel? FromJson(JObject json)
	{
		if (json == null)
			return null;

		string? type = json["type"]?.ToString();
		if (type != "TrendChannel")
			return null;

		// Извлекаем параметры
		DateTime startTime = DateTime.Parse(json["startTime"]?.ToString() ?? DateTime.Now.ToString("o"));
		double startPrice = json["startPrice"]?.Value<double>() ?? 0;
		DateTime endTime = DateTime.Parse(json["endTime"]?.ToString() ?? DateTime.Now.ToString("o"));
		double endPrice = json["endPrice"]?.Value<double>() ?? 0;
		double parallelOffset = json["parallelOffset"]?.Value<double>() ?? 0;
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
		Brush color = Brushes.Green; // значение по умолчанию для канала
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

		var channel = new TrendChannel(startTime, startPrice, endTime, endPrice, parallelOffset, color, thickness, style)
		{
			IsVisible = isVisible
		};

		return channel;
	}
}

