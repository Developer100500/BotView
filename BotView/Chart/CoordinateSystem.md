# Система координат для торгового графика

## Обзор

Реализована трехуровневая система координат для кастомного компонента отображения торговых графиков с возможностью свободного перемещения и масштабирования как в TradingView.

## Типы координат

### 1. Coordinates (Универсальная структура координат)
- **Структура**: `Coordinates`
- **Единицы**: double (поддерживает как пиксели, так и мировые координаты)
- **Использование**: 
  - **View Coordinates**: Координаты окна (пиксели от верхнего левого угла компонента)
  - **World Coordinates**: Мировые координаты (секунды от базового времени, единицы цены от базовой цены)

```csharp
public struct Coordinates
{
    public double x;
    public double y;

    public Coordinates(double x, double y)
    {
        this.x = x;
        this.y = y;
    }

    // Конструктор для целочисленных координат (для обратной совместимости)
    public Coordinates(int x, int y)
    {
        this.x = x;
        this.y = y;
    }
}
```

**Экземпляры для разных целей:**
- **View Coordinates**: Координаты внутри окна компонента ChartView (пиксели)
- **World Coordinates**: Мировые координаты для ориентации в пространстве всего графика

### 2. Chart Coordinates (Координаты графика)
- **Структура**: `ChartCoordinates`
- **Единицы**: Время и цена
- **Использование**: Позиционирование свечей согласно OHLCV данным

```csharp
struct ChartCoordinates
{
    public DateTime time;  // Временная метка
    public double price;   // Цена
}
```

## Система камеры (Viewport)

### ViewportClippingCoords
Определяет видимую область графика в координатах времени и цены:

```csharp
struct ViewportClippingCoords
{
    public double minPrice;   // Нижняя граница цены
    public double maxPrice;   // Верхняя граница цены
    public DateTime minTime;  // Левая граница времени
    public DateTime maxTime;  // Правая граница времени
}
```

### Позиция камеры
- `cameraPosition`: Центр камеры в мировых координатах
- `timeRangeInViewport`: Временной диапазон видимый в viewport
- `priceRangeInViewport`: Ценовой диапазон видимый в viewport

## Методы конвертации координат

### Основные конвертации
- `ChartToWorld()`: Chart → World
- `WorldToChart()`: World → Chart
- `WorldToView()`: World → View
- `ViewToWorld()`: View → World
- `ChartToView()`: Chart → View (прямая)
- `ViewToChart()`: View → Chart (прямая)

### Пример использования
```csharp
// Конвертация времени и цены свечи в экранные координаты
ChartCoordinates candleChart = new ChartCoordinates(candleTime, candlePrice);
Coordinates candleView = ChartToView(candleChart);

// Конвертация позиции мыши в координаты времени/цены
Coordinates mouseView = new Coordinates(mouseX, mouseY);
ChartCoordinates mouseChart = ViewToChart(mouseView);
```

## Управление камерой

### Панорамирование
- `Pan(deltaWorldX, deltaWorldY)`: Перемещение в мировых координатах
- `PanByPixels(deltaScreenX, deltaScreenY)`: Перемещение в пикселях

### Масштабирование
- `Zoom(zoomFactorX, zoomFactorY, focusX?, focusY?)`: Общее масштабирование
- `ZoomAtScreenPoint(screenX, screenY, zoomFactor)`: Масштабирование к точке экрана
- `ZoomToChartPoint(time, price, zoomFactor)`: Масштабирование к времени/цене

### Навигация
- `CenterOnTime(time)`: Центрирование на времени
- `CenterOnPrice(price)`: Центрирование на цене
- `FitToData()`: Подгонка под все данные

## Интерактивность

### Обработка мыши
- **Перетаскивание**: Панорамирование графика
- **Колесо мыши**: Масштабирование к позиции курсора
- **Автоматическое ограничение**: Разумные пределы масштабирования

### Timeframe поддержка
Система автоматически адаптируется к различным timeframe:
- 1m, 5m, 15m, 30m, 1h, 4h, 1d, 1w, 1M
- Автоматический расчет ширины свечей
- Корректное позиционирование по времени

## Производительность

### Оптимизации
- Отсечение свечей вне viewport
- Ленивое обновление viewport
- Ограничение перерисовки при изменениях
- Разумные пределы масштабирования

### Рекомендации
- Используйте `InvalidateVisual()` только при необходимости
- Группируйте операции изменения камеры
- Кэшируйте вычисления координат для больших наборов данных