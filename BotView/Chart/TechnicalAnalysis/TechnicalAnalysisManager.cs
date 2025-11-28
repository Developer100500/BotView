using System.Collections.Generic;
using System.Linq;

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
}



