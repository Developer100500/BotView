using System;
using System.Windows.Media;
using BotView.Chart;

namespace BotView.Chart.TechnicalAnalysis;

/// <summary>
/// Тип инструмента технического анализа
/// </summary>
public enum TechnicalAnalysisToolType
{
	None,
	HorizontalLine,
	VerticalLine,
	TrendLine,
	Rectangle,
	// Добавить другие типы по мере необходимости
}

/// <summary>
/// Абстрактный базовый класс для всех инструментов технического анализа
/// Инструменты используют Chart Coordinates (время и цена) для позиционирования
/// </summary>
public abstract class TechnicalAnalysisTool
{
	// === СТАТИЧЕСКИЕ СВОЙСТВА ДЛЯ РЕЖИМА СОЗДАНИЯ ===
	
	/// <summary>
	/// Флаг, указывающий что сейчас активен режим создания инструмента на графике
	/// </summary>
	public static bool IsCreatingTool { get; set; } = false;

	/// <summary>
	/// Тип инструмента, который сейчас создаётся
	/// </summary>
	public static TechnicalAnalysisToolType CreatingToolType { get; set; } = TechnicalAnalysisToolType.None;

	/// <summary>
	/// Начинает режим создания инструмента указанного типа
	/// </summary>
	public static void StartCreating(TechnicalAnalysisToolType toolType)
	{
		IsCreatingTool = true;
		CreatingToolType = toolType;
	}

	/// <summary>
	/// Завершает режим создания инструмента
	/// </summary>
	public static void StopCreating()
	{
		IsCreatingTool = false;
		CreatingToolType = TechnicalAnalysisToolType.None;
	}

	// === СВОЙСТВА ЭКЗЕМПЛЯРА ===

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

