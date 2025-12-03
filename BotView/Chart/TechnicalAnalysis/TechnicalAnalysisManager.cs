using System;
using System.Collections.Generic;
using System.Linq;
using BotView.Chart;

namespace BotView.Chart.TechnicalAnalysis;

/// <summary>
/// Менеджер для управления коллекцией инструментов технического анализа
/// </summary>
public class TechnicalAnalysisManager
{
	private readonly List<TechnicalAnalysisTool> tools = new List<TechnicalAnalysisTool>();

	/// <summary>
	/// Добавляет инструмент в коллекцию
	/// </summary>
	/// <param name="tool">Инструмент для добавления</param>
	public void AddTool(TechnicalAnalysisTool tool)
	{
		if (tool != null && !tools.Contains(tool))
		{
			tools.Add(tool);
		}
	}

	/// <summary>
	/// Удаляет инструмент из коллекции
	/// </summary>
	/// <param name="tool">Инструмент для удаления</param>
	public void RemoveTool(TechnicalAnalysisTool tool)
	{
		if (tool != null)
		{
			tools.Remove(tool);
		}
	}

	/// <summary>
	/// Получает все инструменты из коллекции
	/// </summary>
	/// <returns>Коллекция всех инструментов</returns>
	public IEnumerable<TechnicalAnalysisTool> GetTools()
	{
		return tools.ToList(); // Возвращаем копию для безопасности
	}

	/// <summary>
	/// Очищает все инструменты из коллекции
	/// </summary>
	public void Clear()
	{
		tools.Clear();
	}

	/// <summary>
	/// Получает количество инструментов в коллекции
	/// </summary>
	public int Count => tools.Count;

	/// <summary>
	/// Устанавливает флаг NeedsRedrawing = true для всех инструментов
	/// Вызывается при изменении viewport (pan/zoom), смене данных или других операциях, требующих перерисовки
	/// </summary>
	public void MarkAllToolsForRedrawing()
	{
		foreach (var tool in tools)
		{
			tool.NeedsRedrawing = true;
		}
	}

	/// <summary>
	/// Находит инструмент под указанной точкой (hit testing)
	/// </summary>
	/// <param name="viewCoords">Координаты точки в View Coordinates (пиксели)</param>
	/// <param name="chartToViewConverter">Функция конвертации из Chart Coordinates в View Coordinates</param>
	/// <param name="viewport">Текущий viewport для определения видимой области</param>
	/// <param name="tolerance">Допустимое отклонение в пикселях (по умолчанию 5)</param>
	/// <returns>Инструмент под курсором или null, если ничего не найдено</returns>
	public TechnicalAnalysisTool? GetToolAtPoint(
		Coordinates viewCoords,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		ViewportClippingCoords viewport,
		double tolerance = 5.0)
	{
		// Перебираем инструменты в обратном порядке (последний добавленный проверяется первым)
		for (int i = tools.Count - 1; i >= 0; i--)
		{
			var tool = tools[i];
			if (tool.IsVisible && tool.HitTest(viewCoords, chartToViewConverter, viewport, tolerance))
			{
				return tool;
			}
		}
		return null;
	}
}



