using System;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using BotView.Chart;

namespace BotView.Chart.TechnicalAnalysis;

/// <summary>Тип инструмента технического анализа</summary>
public enum TechnicalAnalysisToolType
{
	None,
	HorizontalLine,
	HorizontalRay,
	VerticalLine,
	TrendLine,
	TrendChannel,
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
	
	/// <summary>Флаг, указывающий что сейчас активен режим создания инструмента на графике</summary>
	public static bool IsCreatingTool { get; set; } = false;

	/// <summary>Тип инструмента, который сейчас создаётся</summary>
	public static TechnicalAnalysisToolType CreatingToolType { get; set; } = TechnicalAnalysisToolType.None;

	/// <summary>Шаг создания для многоточечных инструментов
	/// (0 = не начато, 1 = первая точка размещена, 2 = вторая точка размещена для 3-точечных)
	/// </summary>
	public static int CreationStep { get; set; } = 0;

	/// <summary>Точки при создании многоточечного инструмента (индекс соответствует CreationStep)</summary>
	public static ChartCoordinates?[] CreationPoints { get; set; } = new ChartCoordinates?[3];

	/// <summary>Временный инструмент, создаваемый в процессе (для 3-точечных инструментов)</summary>
	public static TechnicalAnalysisTool? CreatingToolInstance { get; set; } = null;

	/// <summary>Начинает режим создания инструмента указанного типа</summary>
	public static void StartCreating(TechnicalAnalysisToolType toolType)
	{
		IsCreatingTool = true;
		CreatingToolType = toolType;
		CreationStep = 0;
		CreationPoints = new ChartCoordinates?[3];
		CreatingToolInstance = null;
	}


	/// <summary>Завершает режим создания инструмента</summary>
	public static void StopCreating()
	{
		IsCreatingTool = false;
		CreatingToolType = TechnicalAnalysisToolType.None;
		CreationStep = 0;
		CreationPoints = new ChartCoordinates?[3];
		CreatingToolInstance = null;
	}

	// === СТАТИЧЕСКИЕ СВОЙСТВА ДЛЯ РЕЖИМА РЕДАКТИРОВАНИЯ ===

	/// <summary>Флаг, указывающий что сейчас активен режим редактирования инструмента</summary>
	public static bool IsEditingTool { get; private set; } = false;

	/// <summary>Ссылка на инструмент, который сейчас редактируется</summary>
	public static TechnicalAnalysisTool? EditingTool { get; private set; } = null;

	/// <summary>Индекс редактируемой контрольной точки (-1 = не редактируется точка, 0+ = индекс точки)</summary>
	public static int EditingControlPointIndex { get; set; } = -1;

	/// <summary>Начинает режим редактирования указанного инструмента</summary>
	public static void StartEditing(TechnicalAnalysisTool tool, int controlPointIndex = -1)
	{
		IsEditingTool = true;
		EditingTool = tool;
		EditingControlPointIndex = controlPointIndex;
	}

	/// <summary>Завершает режим редактирования инструмента</summary>
	public static void StopEditing()
	{
		// Сбрасываем флаг редактирования на инструменте
		EditingTool?.SetEditMode(false);
		
		IsEditingTool = false;
		EditingTool = null;
		EditingControlPointIndex = -1;
	}

	// === СВОЙСТВА ЭКЗЕМПЛЯРА ===

	/// <summary>Видимость инструмента на графике</summary>
	public bool IsVisible { get; set; } = true;

	/// <summary>
	/// Оптимизация для отрисовки только тех элементов, которые были смещены или изменены.
	/// После каждого Draw() ставится в false
	/// </summary>
	public bool NeedsRedrawing { get; set; } = true;

	// === ВИРТУАЛЬНЫЕ МЕТОДЫ ДЛЯ РЕДАКТИРОВАНИЯ ===

	/// <summary>Поддерживает ли инструмент контрольные точки для редактирования</summary>
	public virtual bool SupportsControlPoints => true;

	/// <summary>Находится ли инструмент в режиме редактирования</summary>
	public virtual bool IsBeingEdited { get; set; } = false;

	/// <summary>Устанавливает режим редактирования инструмента</summary>
	public virtual void SetEditMode(bool editing)
	{
		IsBeingEdited = editing;
	}

	/// <summary>
	/// Получает индекс контрольной точки под курсором
	/// </summary>
	/// <returns>Индекс точки или -1 если не найдено</returns>
	public virtual int GetControlPointIndex(
		Coordinates viewCoords,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		double tolerance = -1)
	{
		return -1; // По умолчанию нет контрольных точек
	}

	/// <summary>Обновляет позицию указанной контрольной точки</summary>
	public virtual void UpdateControlPoint(int controlPointIndex, ChartCoordinates chartCoords)
	{
		// По умолчанию ничего не делает
	}

	/// <summary>Получает тип курсора для наведения на инструмент</summary>
	public virtual System.Windows.Input.Cursor GetHoverCursor()
	{
		return System.Windows.Input.Cursors.Hand;
	}

	/// <summary>Получает тип курсора для редактирования инструмента</summary>
	public virtual System.Windows.Input.Cursor GetEditCursor()
	{
		return System.Windows.Input.Cursors.SizeAll;
	}

	/// <summary>Получает тип курсора для конкретной контрольной точки</summary>
	public virtual System.Windows.Input.Cursor GetControlPointCursor(int controlPointIndex)
	{
		return System.Windows.Input.Cursors.Cross;
	}

	/// <summary>
	/// Для плоских одноценовых инструментов (горизонтальная линия, луч и т.п.)
	/// возвращает уровень цены и цвет метки на ценовой шкале.
	/// </summary>
	public virtual bool TryGetPriceScaleAnchor(out double price1, out double price2)
	{
		price1 = 0;
		price2 = 0;
		return false;
	}

	/// <summary>Абстрактный метод для отрисовки инструмента</summary>
	/// <param name="drawingContext">Контекст отрисовки WPF</param>
	/// <param name="chartToViewConverter">Функция конвертации из Chart Coordinates в View Coordinates</param>
	/// <param name="viewport">Текущий viewport для определения видимой области</param>
	public abstract void Draw(
		DrawingContext drawingContext,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		ViewportClippingCoords viewport
	);

	/// <summary>Проверяет, попадает ли точка (координаты мыши) на инструмент</summary>
	/// <param name="viewCoords">Координаты точки в View Coordinates (пиксели)</param>
	/// <param name="chartToViewConverter">Функция конвертации из Chart Coordinates в View Coordinates</param>
	/// <param name="viewport">Текущий viewport для определения видимой области</param>
	/// <param name="tolerance">Допустимое отклонение в пикселях</param>
	/// <returns>true, если точка попадает на инструмент</returns>
	public abstract bool HitTest(
		Coordinates viewCoords,
		Func<ChartCoordinates, Coordinates> chartToViewConverter,
		ViewportClippingCoords viewport,
		double tolerance = 5.0
	);

	/// <summary>Обновляет позицию инструмента при перетаскивании</summary>
	/// <param name="chartCoords">Новые координаты в Chart Coordinates (время и цена)</param>
	public abstract void UpdatePosition(ChartCoordinates chartCoords);

	/// <summary>Смещает инструмент целиком на заданную дельту времени и цены</summary>
	public abstract void Translate(TimeSpan timeDelta, double priceDelta);

	/// <summary>Сериализует инструмент в JObject</summary>
	public abstract JObject toJson();
	/// <summary>Сериализует инструмент в JSON строку</summary>
	public abstract string toJsonString();
}

