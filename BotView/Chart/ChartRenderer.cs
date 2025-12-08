using System;
using System.Windows;
using System.Windows.Media;
using BotView.Chart.TechnicalAnalysis;
using BotView.Chart.IndicatorPane;

namespace BotView.Chart;

/// <summary>
/// Рендерер графика - содержит всю логику отрисовки
/// </summary>
public class ChartRenderer
{
	private readonly ChartModel model;
	private readonly ChartController controller;
	private IndicatorRenderer? indicatorRenderer;

	public bool RedrawAllTechnicalTools { get; set; } = false;


	public ChartRenderer(ChartModel model, ChartController controller)
	{
		this.model = model ?? throw new ArgumentNullException(nameof(model));
		this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
		this.indicatorRenderer = new IndicatorRenderer(model, controller);
	}

	/// <summary>Главный метод отрисовки графика</summary>
	/// <param name="drawingContext">Контекст отрисовки WPF</param>
	public void Render(DrawingContext drawingContext)
	{
		// Draw main pane background
		DrawMainPaneArea(drawingContext);

		// Draw main pane grid lines
		DrawMainPaneGrid(drawingContext);

		// Draw main pane price scale (Y-axis on right)
		DrawPriceScale(drawingContext);

		// Draw the candlesticks in main pane
		DrawCandlesticks(drawingContext, model.CandlestickData.candles);

		// Draw technical analysis tools in main pane
		DrawTechnicalAnalysisTools(drawingContext);

		// Draw divider between panes
		DrawDivider(drawingContext);

		// Draw indicator pane
		DrawIndicatorPaneArea(drawingContext);
		DrawIndicatorPaneGrid(drawingContext);
		DrawIndicatorScale(drawingContext);

		// Draw indicators using the indicator renderer
		indicatorRenderer?.Render(drawingContext);

		// Draw shared time scale (at bottom of indicator pane)
		DrawTimeScale(drawingContext);
	}

	/// <summary>Draw main pane background and border</summary>
	private void DrawMainPaneArea(DrawingContext drawingContext)
	{
		Rect mainRect = new Rect(model.LeftMargin, model.TopMargin, model.ChartWidth, model.MainPaneHeight);
		drawingContext.DrawRectangle(Brushes.White, new Pen(Brushes.Gray, 1), mainRect);
	}

	/// <summary>Draw divider between main pane and indicator pane</summary>
	private void DrawDivider(DrawingContext drawingContext)
	{
		Brush dividerBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180));
		Rect dividerRect = new Rect(model.LeftMargin, model.DividerY, model.ChartWidth, model.DividerHeight);
		drawingContext.DrawRectangle(dividerBrush, null, dividerRect);
	}

	/// <summary>Draw indicator pane background and border</summary>
	private void DrawIndicatorPaneArea(DrawingContext drawingContext)
	{
		Brush indicatorBg = new SolidColorBrush(Color.FromRgb(250, 250, 252));
		Rect indicatorRect = new Rect(model.LeftMargin, model.IndicatorPaneTop, model.ChartWidth, model.IndicatorPaneHeight);
		drawingContext.DrawRectangle(indicatorBg, new Pen(Brushes.Gray, 1), indicatorRect);
	}

	/// <summary>
	/// Отрисовка всех свечей
	/// </summary>
	private void DrawCandlesticks(DrawingContext context, OHLCV[] candles)
	{
		for (int i = 0; i < candles.Length; i++)
		{
			if (candles[i].low > model.Viewport.maxPrice || candles[i].high < model.Viewport.minPrice)
				continue; // skip if the candle is completely outside of price range (low > maxPrice or high < minPrice)

			DateTime candleTime = controller.GetCandleTime(i);
			if (candleTime < model.Viewport.minTime || candleTime > model.Viewport.maxTime)
				continue;

			DrawCandlestick(context, candles[i], i);
		}
	}

	/// <summary>
	/// Отрисовка одной свечи на основе времени и цены
	/// </summary>
	private void DrawCandlestick(DrawingContext drawingContext, OHLCV candlestick, int candleIndex)
	{
		// Вычисляем время свечи на основе индекса и timeframe
		DateTime candleTime = controller.GetCandleTime(candleIndex);

		// Создаем chart координаты для различных точек свечи
		ChartCoordinates centerChart = new ChartCoordinates(candleTime, (candlestick.high + candlestick.low) / 2);
		ChartCoordinates highChart = new ChartCoordinates(candleTime, candlestick.high);
		ChartCoordinates lowChart = new ChartCoordinates(candleTime, candlestick.low);
		ChartCoordinates openChart = new ChartCoordinates(candleTime, candlestick.open);
		ChartCoordinates closeChart = new ChartCoordinates(candleTime, candlestick.close);

		// Конвертируем в экранные координаты
		Coordinates centerView = controller.ChartToView(centerChart);
		Coordinates highView = controller.ChartToView(highChart);
		Coordinates lowView = controller.ChartToView(lowChart);
		Coordinates openView = controller.ChartToView(openChart);
		Coordinates closeView = controller.ChartToView(closeChart);

		// Пропускаем отрисовку если свеча за пределами viewport
		// Вычисляем границы свечи
		double candleWidthPixels = controller.GetCandleWidthPixels();
		double candleLeft = centerView.x - candleWidthPixels / 2;
		double candleRight = centerView.x + candleWidthPixels / 2;

		// Определяем границы видимой области графика
		double viewportLeft = model.LeftMargin;
		double viewportRight = model.LeftMargin + model.ChartWidth;

		// Полностью пропускаем свечи, которые целиком за пределами viewport
		if (candleRight < viewportLeft || candleLeft > viewportRight)
			return;

		// Определяем тип свечи (бычья или медвежья)
		bool isBullish = candlestick.close > candlestick.open;

		// Устанавливаем цвета в зависимости от типа свечи
		Brush bodyBrush = isBullish ? Brushes.LightGreen : Brushes.LightCoral;
		Pen bodyPen = new Pen(isBullish ? Brushes.Green : Brushes.Red, 2);
		Pen wickPen = new Pen(Brushes.Black, 1.5);

		// Создаем clipping region для main pane only
		RectangleGeometry clipGeometry = new RectangleGeometry(new Rect(
			viewportLeft,
			model.TopMargin,
			model.ChartWidth,
			model.MainPaneHeight
		));

		// Применяем clipping
		drawingContext.PushClip(clipGeometry);

		try
		{
			// Отрисовываем фитиль (тонкая вертикальная линия от high до low)
			// Clipping автоматически обрежет части, выходящие за пределы viewport
			Point highPoint = new Point(centerView.x, highView.y);
			Point lowPoint = new Point(centerView.x, lowView.y);
			drawingContext.DrawLine(wickPen, highPoint, lowPoint);

			// Отрисовываем тело свечи (прямоугольник от open до close)
			double bodyTop = Math.Min(openView.y, closeView.y);
			double bodyHeight = Math.Abs(closeView.y - openView.y);

			// Обрабатываем случай doji (open == close)
			if (bodyHeight < 1)
				bodyHeight = 1;

			Rect bodyRect = new Rect(
				candleLeft,
				bodyTop,
				candleWidthPixels,
				bodyHeight
			);
			drawingContext.DrawRectangle(bodyBrush, bodyPen, bodyRect);
		}
		finally
		{
			// Убираем clipping
			drawingContext.Pop();
		}
	}

	/// <summary>Отрисовка шкалы времени (горизонтальная ось внизу indicator pane - shared)</summary>
	private void DrawTimeScale(DrawingContext drawingContext)
	{
		if (model.Viewport.minTime >= model.Viewport.maxTime)
			return;

		// Настройки отрисовки
		Pen scalePen = new Pen(Brushes.Gray, 1);
		Pen tickPen = new Pen(Brushes.DarkGray, 1);
		Brush textBrush = Brushes.Black;
		double tickHeight = 5;
		double textOffset = 3;

		// Определяем оптимальный интервал для меток времени
		TimeSpan timeInterval = controller.CalculateOptimalTimeInterval();
			
		// Находим первую метку времени (округляем вниз до ближайшего интервала)
		DateTime firstTick = controller.RoundDownToInterval(model.Viewport.minTime, timeInterval);
			
		// Time scale is at bottom of indicator pane
		double scaleY = model.IndicatorPaneTop + model.IndicatorPaneHeight;
		drawingContext.DrawLine(scalePen, 
			new Point(model.LeftMargin, scaleY), 
			new Point(model.LeftMargin + model.ChartWidth, scaleY));

		// Отрисовываем метки времени
		DateTime currentTime = firstTick;
		while (currentTime <= model.Viewport.maxTime)
		{
			// Конвертируем время в экранные координаты
			ChartCoordinates chartCoords = new ChartCoordinates(currentTime, 0);
			Coordinates viewCoords = controller.ChartToView(chartCoords);

			// Проверяем, что метка находится в пределах видимой области
			if (viewCoords.x >= model.LeftMargin && viewCoords.x <= model.LeftMargin + model.ChartWidth)
			{
				// Отрисовываем риску
				drawingContext.DrawLine(tickPen,
					new Point(viewCoords.x, scaleY),
					new Point(viewCoords.x, scaleY + tickHeight));

				// Отрисовываем подпись времени
				string timeText = controller.FormatTimeLabel(currentTime, timeInterval);
				FormattedText formattedText = new FormattedText(
					timeText,
					System.Globalization.CultureInfo.CurrentCulture,
					FlowDirection.LeftToRight,
					new Typeface("Arial"),
					10,
					textBrush,
					96.0); // Default DPI

				// Центрируем текст относительно риски
				double textX = viewCoords.x - formattedText.Width / 2;
				double textY = scaleY + tickHeight + textOffset;

				drawingContext.DrawText(formattedText, new Point(textX, textY));
			}

			currentTime = currentTime.Add(timeInterval);
		}
	}

	/// <summary>Отрисовка шкалы цены (вертикальная ось справа main pane)</summary>
	private void DrawPriceScale(DrawingContext drawingContext)
	{
		if (model.Viewport.minPrice >= model.Viewport.maxPrice)
			return;

		// Настройки отрисовки
		Pen scalePen = new Pen(Brushes.Gray, 1);
		Pen tickPen = new Pen(Brushes.DarkGray, 1);
		Brush textBrush = Brushes.Black;
		double tickWidth = 5;
		double textOffset = 3;

		// Определяем оптимальный интервал для меток цены
		double priceInterval = controller.CalculateOptimalPriceInterval();
			
		// Находим первую метку цены (округляем вниз до ближайшего интервала)
		double firstTick = Math.Floor(model.Viewport.minPrice / priceInterval) * priceInterval;
			
		// Price scale is on the right of main pane only
		double scaleX = model.LeftMargin + model.ChartWidth;
		drawingContext.DrawLine(scalePen, 
			new Point(scaleX, model.TopMargin), 
			new Point(scaleX, model.TopMargin + model.MainPaneHeight));

		// Отрисовываем метки цены
		double currentPrice = firstTick;
		while (currentPrice <= model.Viewport.maxPrice)
		{
			// Конвертируем цену в экранные координаты
			ChartCoordinates chartCoords = new ChartCoordinates(DateTime.Now, currentPrice);
			Coordinates viewCoords = controller.ChartToView(chartCoords);

			// Проверяем, что метка находится в пределах main pane
			if (viewCoords.y >= model.TopMargin && viewCoords.y <= model.TopMargin + model.MainPaneHeight)
			{
				// Отрисовываем риску
				drawingContext.DrawLine(tickPen,
					new Point(scaleX, viewCoords.y),
					new Point(scaleX + tickWidth, viewCoords.y));

				// Отрисовываем подпись цены
				string priceText = controller.FormatPriceLabel(currentPrice);
				FormattedText formattedText = new FormattedText(
					priceText,
					System.Globalization.CultureInfo.CurrentCulture,
					FlowDirection.LeftToRight,
					new Typeface("Arial"),
					10,
					textBrush,
					96.0); // Default DPI

				// Позиционируем текст справа от риски
				double textX = scaleX + tickWidth + textOffset;
				double textY = viewCoords.y - formattedText.Height / 2;

				drawingContext.DrawText(formattedText, new Point(textX, textY));
			}

			currentPrice += priceInterval;
		}
	}

	/// <summary>Отрисовка шкалы индикатора (вертикальная ось справа indicator pane)</summary>
	private void DrawIndicatorScale(DrawingContext drawingContext)
	{
		var viewport = model.IndicatorViewport;
		if (viewport.MinValue >= viewport.MaxValue)
			return;

		// Настройки отрисовки
		Pen scalePen = new Pen(Brushes.Gray, 1);
		Pen tickPen = new Pen(Brushes.DarkGray, 1);
		Brush textBrush = Brushes.Black;
		double tickWidth = 5;
		double textOffset = 3;

		// Calculate optimal interval for indicator values
		double valueRange = viewport.MaxValue - viewport.MinValue;
		double rawInterval = valueRange / 5; // Target ~5 ticks
		double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(rawInterval, 0.001))));
		double normalizedInterval = rawInterval / magnitude;
		double valueInterval;
		if (normalizedInterval <= 1) valueInterval = magnitude;
		else if (normalizedInterval <= 2) valueInterval = 2 * magnitude;
		else if (normalizedInterval <= 5) valueInterval = 5 * magnitude;
		else valueInterval = 10 * magnitude;

		double firstTick = Math.Floor(viewport.MinValue / valueInterval) * valueInterval;

		// Scale line on the right of indicator pane
		double scaleX = model.LeftMargin + model.ChartWidth;
		drawingContext.DrawLine(scalePen,
			new Point(scaleX, model.IndicatorPaneTop),
			new Point(scaleX, model.IndicatorPaneTop + model.IndicatorPaneHeight));

		// Draw value labels
		double currentValue = firstTick;
		while (currentValue <= viewport.MaxValue)
		{
			// Convert value to screen coordinates
			Coordinates viewCoords = controller.IndicatorToView(model.Viewport.minTime, currentValue);

			// Check if within indicator pane bounds
			if (viewCoords.y >= model.IndicatorPaneTop && viewCoords.y <= model.IndicatorPaneTop + model.IndicatorPaneHeight)
			{
				// Draw tick
				drawingContext.DrawLine(tickPen,
					new Point(scaleX, viewCoords.y),
					new Point(scaleX + tickWidth, viewCoords.y));

				// Draw value label
				string valueText = FormatIndicatorValue(currentValue);
				FormattedText formattedText = new FormattedText(
					valueText,
					System.Globalization.CultureInfo.CurrentCulture,
					FlowDirection.LeftToRight,
					new Typeface("Arial"),
					9,
					textBrush,
					96.0);

				double textX = scaleX + tickWidth + textOffset;
				double textY = viewCoords.y - formattedText.Height / 2;

				drawingContext.DrawText(formattedText, new Point(textX, textY));
			}

			currentValue += valueInterval;
		}
	}

	/// <summary>Format indicator value for display</summary>
	private string FormatIndicatorValue(double value)
	{
		if (Math.Abs(value) >= 100) return value.ToString("F0");
		else if (Math.Abs(value) >= 10) return value.ToString("F1");
		else return value.ToString("F2");
	}

	/// <summary>Отрисовка сетки для main pane (candlestick chart)</summary>
	private void DrawMainPaneGrid(DrawingContext drawingContext)
	{
		if (model.Viewport.minPrice >= model.Viewport.maxPrice || model.Viewport.minTime >= model.Viewport.maxTime)
			return;

		// Настройки сетки
		Pen gridPen = new Pen(Brushes.LightGray, 0.5);
		gridPen.DashStyle = new DashStyle(new double[] { 2, 4 }, 0);

		// Горизонтальные линии сетки (по ценам) - only in main pane
		double priceInterval = controller.CalculateOptimalPriceInterval();
		double firstPriceTick = Math.Floor(model.Viewport.minPrice / priceInterval) * priceInterval;
			
		double currentPrice = firstPriceTick;
		while (currentPrice <= model.Viewport.maxPrice)
		{
			ChartCoordinates chartCoords = new ChartCoordinates(DateTime.Now, currentPrice);
			Coordinates viewCoords = controller.ChartToView(chartCoords);

			if (viewCoords.y >= model.TopMargin && viewCoords.y <= model.TopMargin + model.MainPaneHeight)
			{
				drawingContext.DrawLine(gridPen,
					new Point(model.LeftMargin, viewCoords.y),
					new Point(model.LeftMargin + model.ChartWidth, viewCoords.y));
			}

			currentPrice += priceInterval;
		}

		// Вертикальные линии сетки (по времени) - only in main pane
		TimeSpan timeInterval = controller.CalculateOptimalTimeInterval();
		DateTime firstTimeTick = controller.RoundDownToInterval(model.Viewport.minTime, timeInterval);
			
		DateTime currentTime = firstTimeTick;
		while (currentTime <= model.Viewport.maxTime)
		{
			ChartCoordinates chartCoords = new ChartCoordinates(currentTime, 0);
			Coordinates viewCoords = controller.ChartToView(chartCoords);

			if (viewCoords.x >= model.LeftMargin && viewCoords.x <= model.LeftMargin + model.ChartWidth)
			{
				drawingContext.DrawLine(gridPen,
					new Point(viewCoords.x, model.TopMargin),
					new Point(viewCoords.x, model.TopMargin + model.MainPaneHeight));
			}

			currentTime = currentTime.Add(timeInterval);
		}
	}

	/// <summary>Отрисовка сетки для indicator pane</summary>
	private void DrawIndicatorPaneGrid(DrawingContext drawingContext)
	{
		if (model.Viewport.minTime >= model.Viewport.maxTime)
			return;

		// Настройки сетки
		Pen gridPen = new Pen(Brushes.LightGray, 0.5);
		gridPen.DashStyle = new DashStyle(new double[] { 2, 4 }, 0);

		// Вертикальные линии сетки (по времени) - same as main pane
		TimeSpan timeInterval = controller.CalculateOptimalTimeInterval();
		DateTime firstTimeTick = controller.RoundDownToInterval(model.Viewport.minTime, timeInterval);
			
		DateTime currentTime = firstTimeTick;
		while (currentTime <= model.Viewport.maxTime)
		{
			ChartCoordinates chartCoords = new ChartCoordinates(currentTime, 0);
			Coordinates viewCoords = controller.ChartToView(chartCoords);

			if (viewCoords.x >= model.LeftMargin && viewCoords.x <= model.LeftMargin + model.ChartWidth)
			{
				drawingContext.DrawLine(gridPen,
					new Point(viewCoords.x, model.IndicatorPaneTop),
					new Point(viewCoords.x, model.IndicatorPaneTop + model.IndicatorPaneHeight));
			}

			currentTime = currentTime.Add(timeInterval);
		}

		// Горизонтальные линии for indicator values
		var viewport = model.IndicatorViewport;
		if (viewport.MinValue >= viewport.MaxValue)
			return;

		double valueRange = viewport.MaxValue - viewport.MinValue;
		double rawInterval = valueRange / 5;
		double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(rawInterval, 0.001))));
		double normalizedInterval = rawInterval / magnitude;
		double valueInterval;
		if (normalizedInterval <= 1) valueInterval = magnitude;
		else if (normalizedInterval <= 2) valueInterval = 2 * magnitude;
		else if (normalizedInterval <= 5) valueInterval = 5 * magnitude;
		else valueInterval = 10 * magnitude;

		double firstValueTick = Math.Floor(viewport.MinValue / valueInterval) * valueInterval;
		double currentValue = firstValueTick;

		while (currentValue <= viewport.MaxValue)
		{
			Coordinates viewCoords = controller.IndicatorToView(model.Viewport.minTime, currentValue);

			if (viewCoords.y >= model.IndicatorPaneTop && viewCoords.y <= model.IndicatorPaneTop + model.IndicatorPaneHeight)
			{
				drawingContext.DrawLine(gridPen,
					new Point(model.LeftMargin, viewCoords.y),
					new Point(model.LeftMargin + model.ChartWidth, viewCoords.y));
			}

			currentValue += valueInterval;
		}
	}

	private void DrawTechnicalAnalysisTools(DrawingContext drawingContext)
	{
		if (model.TechnicalAnalysisManager == null)
			return;

		// Проверяем, что размеры графика установлены и камера инициализирована
		if (model.ChartWidth <= 0 || model.MainPaneHeight <= 0 || !model.IsInitialized)
			return;

		// Получаем текущий viewport (он должен быть обновлен после инициализации камеры)
		ViewportClippingCoords currentViewport = model.Viewport;

		// Создаем clipping region для main pane only
		RectangleGeometry clipGeometry = new RectangleGeometry(new Rect(
			model.LeftMargin,
			model.TopMargin,
			model.ChartWidth,
			model.MainPaneHeight
		));

		// Применяем clipping
		drawingContext.PushClip(clipGeometry);

		try
		{
			// В WPF каждый OnRender должен перерисовать ВСЁ заново,
			// поэтому рисуем все видимые инструменты без проверки NeedsRedrawing
			foreach (var tool in model.TechnicalAnalysisManager.GetTools())
			{
				if (tool.IsVisible)
				{
					tool.Draw(drawingContext, controller.ChartToView, currentViewport);
				}
			}
		}
		finally
		{
			drawingContext.Pop(); // Убираем clipping
		}
	}

	/// <summary>
	/// Устанавливает флаг принудительной перерисовки всех инструментов технического анализа
	/// Вызывается при изменении размера окна, zoom, pan и других операциях, требующих полной перерисовки
	/// </summary>
	public void RequestRedrawAllTechnicalTools()
	{
		RedrawAllTechnicalTools = true;
	}
}

