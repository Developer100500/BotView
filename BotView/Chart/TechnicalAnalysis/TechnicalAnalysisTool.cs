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
	Measure,
	// Добавить другие типы по мере необходимости
}

/// <summary>
/// Абстрактный базовый класс для всех инструментов технического анализа
/// Инструменты используют Chart Coordinates (время и цена) для позиционирования
/// </summary>
public abstract class TechnicalAnalysisTool
{
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

