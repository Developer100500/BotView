using System;
using System.Windows.Media;
using BotView.Chart;

namespace BotView.Chart.TechnicalAnalysis;

/// <summary>
/// Абстрактный базовый класс для всех инструментов технического анализа
/// Инструменты используют Chart Coordinates (время и цена) для позиционирования
/// </summary>
public abstract class TechnicalAnalysisTool
{
	/// <summary>
	/// Видимость инструмента на графике
	/// </summary>
	public bool IsVisible { get; set; } = true;

	/// <summary>
	/// Оптимизация для отрисовки только тех элементов, которые были смещены или изменены.
	/// После каждого Draw() ставится в false
	/// </summary>
	public bool NeedsRedrawing { get; set; } = true;

	/// <summary>
	/// Абстрактный метод для отрисовки инструмента
	/// </summary>
	/// <param name="drawingContext">Контекст отрисовки WPF</param>
	/// <param name="chartToViewConverter">Функция конвертации из Chart Coordinates в View Coordinates</param>
	/// <param name="viewport">Текущий viewport для определения видимой области</param>
	public abstract void Draw(
		DrawingContext drawingContext,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		ViewportClippingCoords viewport
	);
}

