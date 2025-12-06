using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using BotView.Chart;
using BotView.Services;

namespace BotView.Chart.TechnicalAnalysis;

/// <summary>
/// Менеджер для управления коллекцией инструментов технического анализа
/// </summary>
public class TechnicalAnalysisManager
{
	private readonly List<TechnicalAnalysisTool> tools = new List<TechnicalAnalysisTool>();
	private const string DataFolder = "data";

	/// <summary>Текущий символ торговой пары (например, BTC/USDT)</summary>
	public string? CurrentSymbol { get; private set; }

	/// <summary>Добавляет инструмент в коллекцию</summary>
	public void AddTool(TechnicalAnalysisTool tool, TechnicalAnalysisToolType toolType)
	{
		if (tool != null && !tools.Contains(tool))
		{
			tools.Add(tool);
		}
	}

	/// <summary>Генерирует путь к файлу для указанного символа</summary>
	private static string GetFilePath(string symbol)
	{
		// Заменяем "/" на "_" в имени символа для создания валидного имени файла
		string safeFileName = symbol.Replace("/", "_") + ".json";
		return Path.Combine(DataFolder, safeFileName);
	}

	/// <summary>Асинхронно сохраняет все инструменты в JSON-файл для текущего символа</summary>
	public async Task SaveToolsAsync()
	{
		if (string.IsNullOrEmpty(CurrentSymbol))
			return;

		try
		{
			string filePath = GetFilePath(CurrentSymbol);
			var jsonService = new JsonService(filePath);

			var toolsArray = new JArray();
			foreach (var tool in tools)
			{
				toolsArray.Add(tool.toJson());
			}

			await jsonService.SaveArrayAsync(toolsArray);
			Debug.WriteLine($"Saved {tools.Count} tools to {filePath}");
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Error saving tools: {ex.Message}");
		}
	}

	/// <summary>Асинхронно загружает инструменты из JSON-файла для текущего символа</summary>
	public async Task LoadToolsAsync()
	{
		if (string.IsNullOrEmpty(CurrentSymbol))
			return;

		try
		{
			string filePath = GetFilePath(CurrentSymbol);
			var jsonService = new JsonService(filePath);

			var toolsArray = await jsonService.LoadArrayAsync();
			if (toolsArray == null)
			{
				Debug.WriteLine($"No tools file found for {CurrentSymbol}");
				return;
			}

			tools.Clear();

			foreach (var item in toolsArray)
			{
				if (item is JObject toolJson)
				{
					var tool = DeserializeTool(toolJson);
					if (tool != null)
					{
						tools.Add(tool);
					}
				}
			}

			Debug.WriteLine($"Loaded {tools.Count} tools from {filePath}");
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"Error loading tools: {ex.Message}");
		}
	}

	/// <summary>Десериализует инструмент из JSON на основе его типа</summary>
	private static TechnicalAnalysisTool? DeserializeTool(JObject json)
	{
		string? type = json["type"]?.ToString();
		return type switch
		{
			"HorizontalLine" => HorizontalLine.FromJson(json),
			// Добавить другие типы инструментов по мере необходимости
			_ => null
		};
	}

	/// <summary>Переключает на новый символ: сохраняет текущие инструменты, очищает, загружает для нового символа</summary>
	public async Task SetSymbolAsync(string symbol)
	{
		// Сохраняем инструменты для текущего символа (если есть)
		if (!string.IsNullOrEmpty(CurrentSymbol))
		{
			await SaveToolsAsync();
		}

		// Очищаем текущие инструменты
		tools.Clear();

		// Устанавливаем новый символ
		CurrentSymbol = symbol;

		// Загружаем инструменты для нового символа
		await LoadToolsAsync();
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



