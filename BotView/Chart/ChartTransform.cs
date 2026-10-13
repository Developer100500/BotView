namespace BotView.Chart;

/// <summary>Immutable coordinate transform shared by input and rendering.</summary>
public sealed class ChartTransform
{

	public DateTime WorldOriginTime { get; }
	public double WorldOriginPrice { get; }
	public double PixelsPerSecond { get; }
	public double PixelsPerPriceUnit { get; }
	public double PixelsPerIndicatorUnit { get; }
	public double CandleWidthPixels { get; }
	private readonly double cachedViewCenterX, cachedViewCenterY, cachedIndicatorViewCenterY;
	private readonly double cachedCameraX, cachedCameraY, cachedIndicatorCameraY;

	public ChartTransform(Coordinates camera, TimeSpan timeRange, double priceRange,
		DateTime originTime, double originPrice, double left, double top, double width,
		double mainHeight, double indicatorTop, double indicatorHeight,
		double indicatorCameraY, double indicatorRange, TimeSpan timeframe)
	{
		WorldOriginTime = originTime;
		WorldOriginPrice = originPrice;
		PixelsPerSecond = timeRange.TotalSeconds > 0 ? width / timeRange.TotalSeconds : 0;
		PixelsPerPriceUnit = priceRange > 0 ? mainHeight / priceRange : 0;
		PixelsPerIndicatorUnit = indicatorRange > 0 ? indicatorHeight / indicatorRange : 0;
		cachedViewCenterX = left + width / 2;
		cachedViewCenterY = top + mainHeight / 2;
		cachedIndicatorViewCenterY = indicatorTop + indicatorHeight / 2;
		cachedCameraX = camera.x;
		cachedCameraY = camera.y;
		cachedIndicatorCameraY = indicatorCameraY;
		CandleWidthPixels = Math.Clamp(timeframe.TotalSeconds * PixelsPerSecond * 0.6, 2, 50);
	}
	public Coordinates ChartToWorld(ChartCoordinates chartCoords)
	{
		// Время конвертируем в секунды от базовой точки
		double timeOffsetSeconds = (chartCoords.time - WorldOriginTime).TotalSeconds;
			
		// Цену конвертируем относительно базовой цены
		double priceOffset = chartCoords.price - WorldOriginPrice;

		return new Coordinates(timeOffsetSeconds, priceOffset);
	}

	/// <summary>
	/// Конвертация из World координат в Chart координаты (время/цена)
	/// </summary>
	public ChartCoordinates WorldToChart(Coordinates worldCoords)
	{
		// Конвертируем секунды обратно во время
		DateTime time = WorldOriginTime.AddSeconds(worldCoords.x);
			
		// Конвертируем смещение цены обратно в абсолютную цену
		double price = WorldOriginPrice + worldCoords.y;

		return new ChartCoordinates(time, price);
	}

	/// <summary>
	/// Конвертация из World координат в View координаты (пиксели на экране) для main pane
	/// Принимает экземпляр Coordinates для World Coordinates
	/// Возвращает экземпляр Coordinates для View Coordinates
	/// </summary>
	public Coordinates WorldToView(Coordinates worldCoords)
	{
		double relativeX = worldCoords.x - cachedCameraX;
		double relativeY = worldCoords.y - cachedCameraY;
		double screenX = cachedViewCenterX + (relativeX * PixelsPerSecond);
		double screenY = cachedViewCenterY - (relativeY * PixelsPerPriceUnit); // Flip Y
		return new Coordinates(screenX, screenY);
	}

	/// <summary>
	/// Конвертация из View координат (пиксели) в World координаты для main pane
	/// Полезно для обработки мыши
	/// Принимает экземпляр Coordinates для View Coordinates
	/// Возвращает экземпляр Coordinates для World Coordinates
	/// </summary>
	public Coordinates ViewToWorld(Coordinates viewCoords)
	{
		double relativeScreenX = viewCoords.x - cachedViewCenterX;
		double relativeScreenY = -(viewCoords.y - cachedViewCenterY); // Flip Y

		double worldX = PixelsPerSecond > 0
			? cachedCameraX + (relativeScreenX / PixelsPerSecond)
			: cachedCameraX;
		double worldY = PixelsPerPriceUnit > 0
			? cachedCameraY + (relativeScreenY / PixelsPerPriceUnit)
			: cachedCameraY;

		return new Coordinates(worldX, worldY);
	}

	/// <summary>
	/// Прямая конвертация из Chart координат в View координаты
	/// Возвращает экземпляр Coordinates для View Coordinates
	/// </summary>
	public Coordinates ChartToView(ChartCoordinates chartCoords)
	{
		double worldX = (chartCoords.time - WorldOriginTime).TotalSeconds;
		double worldY = chartCoords.price - WorldOriginPrice;
		double screenX = cachedViewCenterX + ((worldX - cachedCameraX) * PixelsPerSecond);
		double screenY = cachedViewCenterY - ((worldY - cachedCameraY) * PixelsPerPriceUnit);
		return new Coordinates(screenX, screenY);
	}

	/// <summary>X в View для времени (общий для OHLC одной свечи)</summary>
	public double TimeToViewX(DateTime time)
	{
		double worldX = (time - WorldOriginTime).TotalSeconds;
		return cachedViewCenterX + ((worldX - cachedCameraX) * PixelsPerSecond);
	}

	/// <summary>Y в View для цены</summary>
	public double PriceToViewY(double price)
	{
		double worldY = price - WorldOriginPrice;
		return cachedViewCenterY - ((worldY - cachedCameraY) * PixelsPerPriceUnit);
	}

	/// <summary>
	/// Прямая конвертация из View координат в Chart координаты
	/// Принимает экземпляр Coordinates для View Coordinates
	/// </summary>
	public ChartCoordinates ViewToChart(Coordinates viewCoords)
	{
		Coordinates worldCoords = ViewToWorld(viewCoords);
		return WorldToChart(worldCoords);
	}

	// === INDICATOR PANE COORDINATE METHODS ===

	/// <summary>Converts indicator value and time to View coordinates in the indicator pane</summary>
	public Coordinates IndicatorToView(DateTime time, double indicatorValue)
	{
		double timeOffsetSeconds = (time - WorldOriginTime).TotalSeconds;
		double screenX = cachedViewCenterX + ((timeOffsetSeconds - cachedCameraX) * PixelsPerSecond);
		double relativeY = indicatorValue - cachedIndicatorCameraY;
		double screenY = cachedIndicatorViewCenterY - (relativeY * PixelsPerIndicatorUnit);
		return new Coordinates(screenX, screenY);
	}

	/// <summary>Y в View для значения индикатора</summary>
	public double IndicatorValueToViewY(double indicatorValue)
	{
		double relativeY = indicatorValue - cachedIndicatorCameraY;
		return cachedIndicatorViewCenterY - (relativeY * PixelsPerIndicatorUnit);
	}

	/// <summary>Converts View coordinates in the indicator pane to time and indicator value</summary>
	public (DateTime time, double value) ViewToIndicator(Coordinates viewCoords)
	{
		double relativeScreenX = viewCoords.x - cachedViewCenterX;
		double worldX = PixelsPerSecond > 0
			? cachedCameraX + (relativeScreenX / PixelsPerSecond)
			: cachedCameraX;
		DateTime time = WorldOriginTime.AddSeconds(worldX);

		double relativeScreenY = -(viewCoords.y - cachedIndicatorViewCenterY);
		double value = PixelsPerIndicatorUnit > 0
			? cachedIndicatorCameraY + (relativeScreenY / PixelsPerIndicatorUnit)
			: cachedIndicatorCameraY;

		return (time, value);
	}

}
