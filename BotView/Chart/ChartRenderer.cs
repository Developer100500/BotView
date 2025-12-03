using System;
using System.Windows;
using System.Windows.Media;
using BotView.Chart.TechnicalAnalysis;

namespace BotView.Chart;

/// <summary>
/// Рендерер графика - содержит всю логику отрисовки
/// </summary>
public class ChartRenderer
{
	private readonly ChartModel model;
	private readonly ChartController controller;

	public bool RedrawAllTechnicalTools { get; set; } = false;


	public ChartRenderer(ChartModel model, ChartController controller)
	{
		this.model = model ?? throw new ArgumentNullException(nameof(model));
		this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
	}

	/// <summary>
	/// Главный метод отрисовки графика
	/// </summary>
	/// <param name="drawingContext">Контекст отрисовки WPF</param>
	public void Render(DrawingContext drawingContext)
	{
		// Draw background and borders for visualization
		DrawChartArea(drawingContext);

		// Draw grid lines
		DrawGrid(drawingContext);

		// Draw scales (axes)
		DrawTimeScale(drawingContext);
		DrawPriceScale(drawingContext);

		// Draw the candlestick
		DrawCandlesticks(drawingContext, model.CandlestickData.candles);

		// Draw technical analysis tools
		DrawTechnicalAnalysisTools(drawingContext);
	}

	/// <summary>
	/// Draw chart area background and borders for visualization
	/// </summary>
	private void DrawChartArea(DrawingContext drawingContext)
	{
		// Draw background
		Rect chartRect = new Rect(model.LeftMargin, model.TopMargin, model.ChartWidth, model.ChartHeight);
		drawingContext.DrawRectangle(Brushes.White, new Pen(Brushes.Gray, 1), chartRect);
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

		// Создаем clipping region для ограничения отрисовки видимой областью
		// Это позволяет рисовать частично видимые свечи без "рваных" краев
		RectangleGeometry clipGeometry = new RectangleGeometry(new Rect(
			viewportLeft,
			model.TopMargin,
			model.ChartWidth,
			model.ChartHeight
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

	/// <summary>
	/// Отрисовка шкалы времени (горизонтальная ось внизу)
	/// </summary>
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
			
		// Отрисовываем основную линию шкалы
		double scaleY = model.TopMargin + model.ChartHeight;
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

	/// <summary>
	/// Отрисовка шкалы цены (вертикальная ось справа)
	/// </summary>
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
			
		// Отрисовываем основную линию шкалы
		double scaleX = model.LeftMargin + model.ChartWidth;
		drawingContext.DrawLine(scalePen, 
			new Point(scaleX, model.TopMargin), 
			new Point(scaleX, model.TopMargin + model.ChartHeight));

		// Отрисовываем метки цены
		double currentPrice = firstTick;
		while (currentPrice <= model.Viewport.maxPrice)
		{
			// Конвертируем цену в экранные координаты
			ChartCoordinates chartCoords = new ChartCoordinates(DateTime.Now, currentPrice);
			Coordinates viewCoords = controller.ChartToView(chartCoords);

			// Проверяем, что метка находится в пределах видимой области
			if (viewCoords.y >= model.TopMargin && viewCoords.y <= model.TopMargin + model.ChartHeight)
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

	/// <summary>
	/// Отрисовка сетки для лучшей читаемости графика
	/// </summary>
	private void DrawGrid(DrawingContext drawingContext)
	{
		if (model.Viewport.minPrice >= model.Viewport.maxPrice || model.Viewport.minTime >= model.Viewport.maxTime)
			return;

		// Настройки сетки
		Pen gridPen = new Pen(Brushes.LightGray, 0.5);
		gridPen.DashStyle = new DashStyle(new double[] { 2, 4 }, 0);

		// Горизонтальные линии сетки (по ценам)
		double priceInterval = controller.CalculateOptimalPriceInterval();
		double firstPriceTick = Math.Floor(model.Viewport.minPrice / priceInterval) * priceInterval;
			
		double currentPrice = firstPriceTick;
		while (currentPrice <= model.Viewport.maxPrice)
		{
			ChartCoordinates chartCoords = new ChartCoordinates(DateTime.Now, currentPrice);
			Coordinates viewCoords = controller.ChartToView(chartCoords);

			if (viewCoords.y >= model.TopMargin && viewCoords.y <= model.TopMargin + model.ChartHeight)
			{
				drawingContext.DrawLine(gridPen,
					new Point(model.LeftMargin, viewCoords.y),
					new Point(model.LeftMargin + model.ChartWidth, viewCoords.y));
			}

			currentPrice += priceInterval;
		}

		// Вертикальные линии сетки (по времени)
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
					new Point(viewCoords.x, model.TopMargin + model.ChartHeight));
			}

			currentTime = currentTime.Add(timeInterval);
		}
	}

	private void DrawTechnicalAnalysisTools(DrawingContext drawingContext)
	{
		if (model.TechnicalAnalysisManager == null)
			return;

		// Проверяем, что размеры графика установлены и камера инициализирована
		if (model.ChartWidth <= 0 || model.ChartHeight <= 0 || !model.IsInitialized)
			return;

		// Получаем текущий viewport (он должен быть обновлен после инициализации камеры)
		ViewportClippingCoords currentViewport = model.Viewport;

		// Создаем clipping region для ограничения отрисовки видимой областью графика
		RectangleGeometry clipGeometry = new RectangleGeometry(new Rect(
			model.LeftMargin,
			model.TopMargin,
			model.ChartWidth,
			model.ChartHeight
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
			// Убираем clipping
			drawingContext.Pop();
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

