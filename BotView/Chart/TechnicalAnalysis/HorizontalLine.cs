using System;
using System.Windows.Media;
using BotView.Chart;

namespace BotView.Chart.TechnicalAnalysis;

/// <summary>
/// Горизонтальная линия для обозначения уровней поддержки и сопротивления
/// Использует Chart Coordinates (цена) для позиционирования
/// </summary>
public class HorizontalLine : TechnicalAnalysisTool
{
	/// <summary>
	/// Цена линии в Chart Coordinates
	/// </summary>
	public double Price { get; set; }

	/// <summary>
	/// Цвет линии
	/// </summary>
	public Brush Color { get; set; }

	/// <summary>
	/// Толщина линии
	/// </summary>
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

	/// <summary>
	/// Отрисовка горизонтальной линии через весь график
	/// </summary>
	public override void Draw(
		DrawingContext drawingContext,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		ViewportClippingCoords viewport
	)
	{
		if (!IsVisible)
			return;

		// Создаем точки начала и конца линии в Chart Coordinates
		// Линия проходит от minTime до maxTime на уровне заданной цены
		ChartCoordinates startPoint = new ChartCoordinates(viewport.minTime, Price);
		ChartCoordinates endPoint = new ChartCoordinates(viewport.maxTime, Price);

		// Проверяем, находится ли линия в видимом диапазоне цен
		// Определяем границы видимой области (с учетом возможного переворота)
		double minVisiblePrice = Math.Min(viewport.minPrice, viewport.maxPrice);
		double maxVisiblePrice = Math.Max(viewport.minPrice, viewport.maxPrice);
		
		// Если линия полностью вне видимой области, не рисуем её
		if (Price < minVisiblePrice || Price > maxVisiblePrice)
		{
			IsVisible = false;
			return;
		}

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
}

