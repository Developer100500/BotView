using System;
using System.Windows;
using System.Windows.Media;
using BotView.Chart.TechnicalAnalysis;
using BotView.Chart.IndicatorPane;
using BotView.Models;
using BotView.Services;

namespace BotView.Chart;

/// <summary>
/// Рендерер графика - содержит всю логику отрисовки
/// </summary>
public class ChartRenderer
{
	private IndicatorRenderer? indicatorRenderer;

	// === Shared frozen drawing resources (never change) ===
	private static readonly Typeface LabelTypeface = new Typeface("Arial");
	private static readonly Pen BorderPen = CreateFrozenPen(Brushes.Gray, 1);
	private static readonly Pen ScalePen = CreateFrozenPen(Brushes.Gray, 1);
	private static readonly Pen TickPen = CreateFrozenPen(Brushes.DarkGray, 1);
	private static readonly Pen WickPen = CreateFrozenPen(Brushes.Black, 1.5);
	private static readonly Pen BullishBodyPen = CreateFrozenPen(Brushes.Green, 1);
	private static readonly Pen BearishBodyPen = CreateFrozenPen(Brushes.Red, 1);
	private static readonly Pen CurrentPriceDashPen = CreateFrozenDashedPen(Brushes.LightGray, 1, DashStyles.Dot);
	private static readonly Pen CrosshairPen = CreateFrozenDashedPen(Brushes.LightGray, 1, DashStyles.Dash);
	private static readonly Pen GridPen = CreateFrozenDashedPen(Brushes.LightGray, 0.5, new DashStyle(new double[] { 2, 4 }, 0));
	private static readonly Brush DividerBrush = CreateFrozenBrush(Color.FromRgb(180, 180, 180));
	private static readonly Brush IndicatorBgBrush = CreateFrozenBrush(Color.FromRgb(250, 250, 252));
	private static readonly Brush CrosshairLabelBrush = CreateFrozenBrush(Color.FromRgb(96, 96, 96));

	/// <summary>Главный метод отрисовки графика</summary>
	public void Render(DrawingContext drawingContext, ChartRenderFrame frame)
	{
		var candles = frame.Candles;
		if (frame.ChartWidth <= 0) return;

		DrawMainPaneArea(drawingContext, frame);
		DrawMainPaneGrid(drawingContext, frame);
		DrawPriceScale(drawingContext, frame);
		DrawCandlesticks(drawingContext, frame, candles);
		DrawTechnicalAnalysisTools(drawingContext, frame);
		DrawToolCreationPreview(drawingContext, frame);
		DrawHorizontalToolPriceLabels(drawingContext, frame);
		DrawPriceIndicatorOfSelectedTool(drawingContext, frame);
		DrawCurrentPriceIndicator(drawingContext, frame, candles);

		if (frame.IsIndicatorPaneVisible)
		{
			DrawDivider(drawingContext, frame);
			DrawIndicatorPaneArea(drawingContext, frame);
			DrawIndicatorPaneGrid(drawingContext, frame);
			DrawIndicatorScale(drawingContext, frame);
			(indicatorRenderer ??= new()).Render(drawingContext, frame);
		}
		else indicatorRenderer = null;
		DrawTimeScale(drawingContext, frame);
		DrawCrosshair(drawingContext, frame);
	}

	private void DrawMainPaneArea(DrawingContext drawingContext, ChartRenderFrame frame)
	{
		drawingContext.DrawRectangle(Brushes.White, BorderPen, frame.MainPaneRect);
	}

	private void DrawDivider(DrawingContext drawingContext, ChartRenderFrame frame)
	{
		drawingContext.DrawRectangle(DividerBrush, null, frame.DividerRect);
	}

	private void DrawIndicatorPaneArea(DrawingContext drawingContext, ChartRenderFrame frame)
	{
		drawingContext.DrawRectangle(IndicatorBgBrush, BorderPen, frame.IndicatorPaneRect);
	}

	/// <summary>Отрисовка всех свечей</summary>
	private void DrawCandlesticks(DrawingContext context, ChartRenderFrame frame, CandleSeriesSnapshot candles)
	{
		if (candles.Count == 0)
			return;

		var clipGeometry = new RectangleGeometry(frame.MainPaneRect);
		context.PushClip(clipGeometry);

		try
		{
			double viewportMinPrice = frame.Viewport.minPrice;
			double viewportMaxPrice = frame.Viewport.maxPrice;
			DateTime viewportMinTime = frame.Viewport.minTime;
			DateTime viewportMaxTime = frame.Viewport.maxTime;
			double candleWidthPixels = frame.CandleWidthPixels;
			double halfWidth = candleWidthPixels / 2;
			double viewportLeft = frame.Left;
			double viewportRight = frame.ScaleX;

			long fromTimestamp = new DateTimeOffset(DateTime.SpecifyKind(viewportMinTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
			long toTimestamp = new DateTimeOffset(DateTime.SpecifyKind(viewportMaxTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
			foreach (var (i, candle) in candles.Enumerate(fromTimestamp, toTimestamp))
			{
				if (candle.low > viewportMaxPrice || candle.high < viewportMinPrice)
					continue;

				DateTime candleTime = candle.timestamp > 0 ? candle.GetDateTime() : frame.GetCandleTime(i);
				if (candleTime < viewportMinTime || candleTime > viewportMaxTime)
					continue;

				double centerX = frame.Transform.TimeToViewX(candleTime);
				double candleLeft = centerX - halfWidth;
				double candleRight = centerX + halfWidth;
				if (candleRight < viewportLeft || candleLeft > viewportRight)
					continue;

				double highY = frame.Transform.PriceToViewY(candle.high);
				double lowY = frame.Transform.PriceToViewY(candle.low);
				double openY = frame.Transform.PriceToViewY(candle.open);
				double closeY = frame.Transform.PriceToViewY(candle.close);

				bool isBullish = candle.close > candle.open;
				Brush bodyBrush = isBullish ? Brushes.LightGreen : Brushes.LightCoral;
				Pen bodyPen = isBullish ? BullishBodyPen : BearishBodyPen;

				context.DrawLine(WickPen, new Point(centerX, highY), new Point(centerX, lowY));

				double bodyTop = Math.Min(openY, closeY);
				double bodyHeight = Math.Abs(closeY - openY);
				if (bodyHeight < 1)
					bodyHeight = 1;

				context.DrawRectangle(
					bodyBrush,
					frame.ShowCandleOutlines ? bodyPen : null,
					new Rect(candleLeft, bodyTop, candleWidthPixels, bodyHeight));
			}
		}
		finally
		{
			context.Pop();
		}
	}

	/// <summary>
	/// Метки цен видимых горизонтальных линий и лучей на правой шкале.
	/// Метка текущей цены имеет приоритет при пересечении.
	/// </summary>
	private void DrawHorizontalToolPriceLabels(DrawingContext drawingContext, ChartRenderFrame frame)
	{
		if (!frame.IsInitialized || frame.ChartWidth <= 0 || frame.MainPaneHeight <= 0)
			return;

		bool hasCurrentPriceLabel = TryGetCurrentPriceLabelRect(frame, out Rect currentPriceLabelRect);
		foreach (var tool in frame.Tools)
		{
			if (!tool.IsVisible)
				continue;

			double price;
			Brush brush;
			switch (tool)
			{
				case HorizontalLine line:
					price = line.Price;
					brush = line.Color;
					break;
				case HorizontalRay ray when ray.StartTime <= frame.Viewport.maxTime:
					price = ray.Price;
					brush = ray.Color;
					break;
				default:
					continue;
			}

			if (!IsPriceVisible(frame, price))
				continue;

			Rect labelRect = GetPriceScaleLabelRect(frame, price);
			if (hasCurrentPriceLabel && labelRect.IntersectsWith(currentPriceLabelRect))
				continue;

			DrawPriceScaleLabel(drawingContext, frame, price, brush);
		}
	}

	/// <summary>
	/// Метка цены на шкале для выбранного плоского инструмента (кроме горизонтальной линии и луча).
	/// </summary>
	private void DrawPriceIndicatorOfSelectedTool(DrawingContext drawingContext, ChartRenderFrame frame)
	{
		var tool = frame.SelectedTool;
		if (tool == null || !tool.IsBeingEdited || tool is HorizontalLine or HorizontalRay)
			return;

		if (!tool.TryGetPriceScaleAnchor(out double price1, out double price2))
			return;

		if (!double.IsFinite(price1) ||
			price1 < frame.Viewport.minPrice ||
			price1 > frame.Viewport.maxPrice)
		{
			return;
		}

		DrawPriceScaleLabel(drawingContext, frame, price1, Brushes.Gray);

		if (!double.IsFinite(price2) ||
			price2 < frame.Viewport.minPrice ||
			price2 > frame.Viewport.maxPrice)
		{
			return;
		}

		DrawPriceScaleLabel(drawingContext, frame, price2, Brushes.Gray);
	}

	/// <summary>Отрисовывает пунктир от текущей свечи и метку рыночной цены на шкале</summary>
	private void DrawCurrentPriceIndicator(DrawingContext drawingContext, ChartRenderFrame frame, CandleSeriesSnapshot candles)
	{
		if (candles.Count == 0)
			return;

		int lastIndex = candles.Count - 1;
		OHLCV currentCandle = candles[lastIndex];
		double currentPrice = currentCandle.close;
		if (!double.IsFinite(currentPrice) ||
			currentPrice < frame.Viewport.minPrice ||
			currentPrice > frame.Viewport.maxPrice)
		{
			return;
		}

		DateTime candleTime = currentCandle.timestamp > 0
			? currentCandle.GetDateTime()
			: frame.GetCandleTime(lastIndex);
		double priceY = frame.Transform.PriceToViewY(currentPrice);
		double candleX = frame.Transform.TimeToViewX(candleTime);
		double scaleX = frame.ScaleX;
		double candleRight = candleX + frame.CandleWidthPixels / 2;
		double lineStartX = Math.Max(frame.Left, candleRight);

		Brush indicatorBrush = currentCandle.close >= currentCandle.open
			? Brushes.Green
			: Brushes.Red;

		if (lineStartX < scaleX)
		{
			drawingContext.DrawLine(
				CurrentPriceDashPen,
				new Point(lineStartX, priceY),
				new Point(scaleX, priceY));
		}

		DrawPriceScaleLabel(drawingContext, frame, currentPrice, indicatorBrush);
	}

	/// <summary>Рисует цветную метку цены на правой шкале</summary>
	private void DrawPriceScaleLabel(
		DrawingContext drawingContext,
		ChartRenderFrame frame,
		double price,
		Brush backgroundBrush)
	{
		string priceText = frame.Axes.FormatPriceLabel(price, frame.PriceInterval);
		FormattedText formattedText = CreateLabelText(priceText, Brushes.White, 10);
		Rect labelRect = GetPriceScaleLabelRect(frame, price, formattedText);

		const double horizontalPadding = 4;
		drawingContext.DrawRectangle(backgroundBrush, null, labelRect);
		drawingContext.DrawText(
			formattedText,
			new Point(
				labelRect.X + horizontalPadding,
				labelRect.Y + (labelRect.Height - formattedText.Height) / 2));
	}

	private Rect GetPriceScaleLabelRect(ChartRenderFrame frame, double price)
	{
		string priceText = frame.Axes.FormatPriceLabel(price, frame.PriceInterval);
		return GetPriceScaleLabelRect(frame, price, CreateLabelText(priceText, Brushes.White, 10));
	}

	private Rect GetPriceScaleLabelRect(ChartRenderFrame frame, double price, FormattedText formattedText)
	{
		const double horizontalPadding = 4;
		const double verticalPadding = 2;
		double priceY = frame.Transform.PriceToViewY(price);
		double labelWidth = Math.Min(frame.RightMargin, formattedText.Width + horizontalPadding * 2);
		double labelHeight = formattedText.Height + verticalPadding * 2;
		return new Rect(frame.ScaleX, priceY - labelHeight / 2, labelWidth, labelHeight);
	}

	private bool TryGetCurrentPriceLabelRect(ChartRenderFrame frame, out Rect labelRect)
	{
		var candles = frame.Candles;
		if (candles.Count > 0 && IsPriceVisible(frame, candles[candles.Count - 1].close))
		{
			labelRect = GetPriceScaleLabelRect(frame, candles[candles.Count - 1].close);
			return true;
		}

		labelRect = Rect.Empty;
		return false;
	}

	private bool IsPriceVisible(ChartRenderFrame frame, double price) =>
		double.IsFinite(price) && price >= frame.Viewport.minPrice && price <= frame.Viewport.maxPrice;

	/// <summary>
	/// Crosshair: vertical follows mouse X with continuous time label;
	/// horizontal (main pane only) follows mouse Y with price label.
	/// </summary>
	private void DrawCrosshair(DrawingContext drawingContext, ChartRenderFrame frame)
	{
		if (!frame.CrosshairVisible || !frame.IsInitialized)
			return;

		if (frame.ChartWidth <= 0)
			return;

		Point mouse = frame.CrosshairViewPosition;
		if (mouse.X < frame.Left || mouse.X > frame.ScaleX)
			return;

		ChartCoordinates chartCoords = frame.Transform.ViewToChart(new Coordinates(mouse.X, mouse.Y));
		double verticalX = mouse.X;
		double mainBottom = frame.Top + frame.MainPaneHeight;
		double indicatorBottom = frame.IndicatorPaneTop + frame.IndicatorPaneHeight;

		drawingContext.DrawLine(
			CrosshairPen,
			new Point(verticalX, frame.Top),
			new Point(verticalX, mainBottom));

		if (frame.IndicatorPaneHeight > 0)
		{
			drawingContext.DrawLine(
				CrosshairPen,
				new Point(verticalX, frame.IndicatorPaneTop),
				new Point(verticalX, indicatorBottom));
		}

		bool showPriceCrosshair = frame.CrosshairPane == ChartPane.Main
			&& mouse.Y >= frame.Top
			&& mouse.Y <= frame.Top + frame.MainPaneHeight;

		if (showPriceCrosshair)
		{
			drawingContext.DrawLine(
				CrosshairPen,
				new Point(frame.Left, mouse.Y),
				new Point(frame.ScaleX, mouse.Y));

			double price = chartCoords.price;
			if (double.IsFinite(price)
				&& price >= frame.Viewport.minPrice
				&& price <= frame.Viewport.maxPrice)
			{
				DrawPriceScaleLabel(drawingContext, frame, price, CrosshairLabelBrush);
			}
		}

		DrawTimeScaleLabel(drawingContext, frame, verticalX, chartCoords.time);
	}

	/// <summary>Draws a time label on the bottom time scale centered on vertical crosshair</summary>
	private void DrawTimeScaleLabel(
		DrawingContext drawingContext,
		ChartRenderFrame frame,
		double centerX,
		DateTime time)
	{
		string timeText = frame.Axes.FormatCrosshairTimeLabel(time);
		FormattedText formattedText = CreateLabelText(timeText, Brushes.White, 10);

		const double horizontalPadding = 4;
		const double verticalPadding = 2;
		double labelWidth = formattedText.Width + horizontalPadding * 2;
		double labelHeight = formattedText.Height + verticalPadding * 2;

		double labelLeft = centerX - labelWidth / 2;
		double minLeft = frame.Left;
		double maxLeft = frame.ScaleX + frame.RightMargin - labelWidth;
		if (maxLeft < minLeft)
			maxLeft = minLeft;
		labelLeft = Math.Clamp(labelLeft, minLeft, maxLeft);

		double labelTop = frame.TimeScaleY + 2;
		double maxTop = frame.TimeScaleY + frame.BottomMargin - labelHeight;
		if (maxTop < frame.TimeScaleY)
			maxTop = frame.TimeScaleY;
		labelTop = Math.Clamp(labelTop, frame.TimeScaleY, maxTop);

		Rect labelRect = new Rect(labelLeft, labelTop, labelWidth, labelHeight);
		drawingContext.DrawRectangle(CrosshairLabelBrush, null, labelRect);
		drawingContext.DrawText(
			formattedText,
			new Point(labelLeft + horizontalPadding, labelTop + verticalPadding));
	}

	/// <summary>Отрисовка шкалы времени (горизонтальная ось внизу indicator pane - shared)</summary>
	private void DrawTimeScale(DrawingContext drawingContext, ChartRenderFrame frame)
	{
		if (frame.Viewport.minTime >= frame.Viewport.maxTime || frame.TimeInterval <= TimeSpan.Zero)
			return;

		double scaleY = frame.TimeScaleY;
		const double tickHeight = 5;
		const double textOffset = 3;

		drawingContext.DrawLine(ScalePen,
			new Point(frame.Left, scaleY),
			new Point(frame.ScaleX, scaleY));

		foreach (var tick in frame.Axes.TimeTicks)
		{
			var currentTime = tick.Value;
			double x = frame.Transform.TimeToViewX(currentTime);
			if (x >= frame.Left && x <= frame.ScaleX)
			{
				drawingContext.DrawLine(TickPen,
					new Point(x, scaleY),
					new Point(x, scaleY + tickHeight));

				string timeText = tick.Label;
				FormattedText formattedText = CreateLabelText(timeText, Brushes.Black, 10);
				drawingContext.DrawText(
					formattedText,
					new Point(x - formattedText.Width / 2, scaleY + tickHeight + textOffset));
			}
		}
	}

	/// <summary>Отрисовка шкалы цены (вертикальная ось справа main pane)</summary>
	private void DrawPriceScale(DrawingContext drawingContext, ChartRenderFrame frame)
	{
		if (frame.Viewport.minPrice >= frame.Viewport.maxPrice || frame.PriceInterval <= 0)
			return;

		const double tickWidth = 5;
		const double textOffset = 3;
		double scaleX = frame.ScaleX;
		double mainBottom = frame.Top + frame.MainPaneHeight;

		drawingContext.DrawLine(ScalePen,
			new Point(scaleX, frame.Top),
			new Point(scaleX, mainBottom));

		foreach (var tick in frame.Axes.PriceTicks)
		{
			var currentPrice = tick.Value;
			double y = frame.Transform.PriceToViewY(currentPrice);
			if (y >= frame.Top && y <= mainBottom)
			{
				drawingContext.DrawLine(TickPen,
					new Point(scaleX, y),
					new Point(scaleX + tickWidth, y));

				string priceText = tick.Label;
				FormattedText formattedText = CreateLabelText(priceText, Brushes.Black, 10);
				drawingContext.DrawText(
					formattedText,
					new Point(scaleX + tickWidth + textOffset, y - formattedText.Height / 2));
			}
		}
	}

	/// <summary>Отрисовка шкалы индикатора (вертикальная ось справа indicator pane)</summary>
	private void DrawIndicatorScale(DrawingContext drawingContext, ChartRenderFrame frame)
	{
		if (frame.Axes.IndicatorTicks.Count == 0 || frame.IndicatorValueInterval <= 0)
			return;

		const double tickWidth = 5;
		const double textOffset = 3;
		double scaleX = frame.ScaleX;
		double indicatorBottom = frame.IndicatorPaneTop + frame.IndicatorPaneHeight;

		drawingContext.DrawLine(ScalePen,
			new Point(scaleX, frame.IndicatorPaneTop),
			new Point(scaleX, indicatorBottom));

		foreach (var tick in frame.Axes.IndicatorTicks)
		{
			var currentValue = tick.Value;
			double y = frame.Transform.IndicatorValueToViewY(currentValue);
			if (y >= frame.IndicatorPaneTop && y <= indicatorBottom)
			{
				drawingContext.DrawLine(TickPen,
					new Point(scaleX, y),
					new Point(scaleX + tickWidth, y));

				FormattedText formattedText = CreateLabelText(tick.Label, Brushes.Black, 9);
				drawingContext.DrawText(
					formattedText,
					new Point(scaleX + tickWidth + textOffset, y - formattedText.Height / 2));
			}
		}
	}

	/// <summary>Отрисовка сетки для main pane (candlestick chart)</summary>
	private void DrawMainPaneGrid(DrawingContext drawingContext, ChartRenderFrame frame)
	{
		if (frame.Viewport.minPrice >= frame.Viewport.maxPrice || frame.Viewport.minTime >= frame.Viewport.maxTime)
			return;

		double mainBottom = frame.Top + frame.MainPaneHeight;

		if (frame.PriceInterval > 0)
		{
			foreach (var tick in frame.Axes.PriceTicks)
			{
				var currentPrice = tick.Value;
				double y = frame.Transform.PriceToViewY(currentPrice);
				if (y >= frame.Top && y <= mainBottom)
				{
					drawingContext.DrawLine(GridPen,
						new Point(frame.Left, y),
						new Point(frame.ScaleX, y));
				}
			}
		}

		if (frame.TimeInterval > TimeSpan.Zero)
		{
			foreach (var tick in frame.Axes.TimeTicks)
			{
				var currentTime = tick.Value;
				double x = frame.Transform.TimeToViewX(currentTime);
				if (x >= frame.Left && x <= frame.ScaleX)
				{
					drawingContext.DrawLine(GridPen,
						new Point(x, frame.Top),
						new Point(x, mainBottom));
				}
			}
		}
	}

	/// <summary>Отрисовка сетки для indicator pane</summary>
	private void DrawIndicatorPaneGrid(DrawingContext drawingContext, ChartRenderFrame frame)
	{
		if (frame.Viewport.minTime >= frame.Viewport.maxTime)
			return;

		double indicatorBottom = frame.IndicatorPaneTop + frame.IndicatorPaneHeight;

		if (frame.TimeInterval > TimeSpan.Zero)
		{
			foreach (var tick in frame.Axes.TimeTicks)
			{
				var currentTime = tick.Value;
				double x = frame.Transform.TimeToViewX(currentTime);
				if (x >= frame.Left && x <= frame.ScaleX)
				{
					drawingContext.DrawLine(GridPen,
						new Point(x, frame.IndicatorPaneTop),
						new Point(x, indicatorBottom));
				}
			}
		}

		if (frame.IndicatorValueInterval > 0)
		{
			foreach (var tick in frame.Axes.IndicatorTicks)
			{
				var currentValue = tick.Value;
				double y = frame.Transform.IndicatorValueToViewY(currentValue);
				if (y >= frame.IndicatorPaneTop && y <= indicatorBottom)
				{
					drawingContext.DrawLine(GridPen,
						new Point(frame.Left, y),
						new Point(frame.ScaleX, y));
				}
			}
		}
	}

	private void DrawTechnicalAnalysisTools(DrawingContext drawingContext, ChartRenderFrame frame)
	{
		if (frame.ChartWidth <= 0 || frame.MainPaneHeight <= 0 || !frame.IsInitialized)
			return;

		ViewportClippingCoords currentViewport = frame.Viewport;
		var clipGeometry = new RectangleGeometry(frame.MainPaneRect);
		drawingContext.PushClip(clipGeometry);

		try
		{
			foreach (var tool in frame.Tools)
			{
				if (tool.IsVisible)
					tool.Draw(drawingContext, frame.Transform.ChartToView, currentViewport);
			}
		}
		finally
		{
			drawingContext.Pop();
		}
	}

	/// <summary>Отрисовка превью создаваемого инструмента</summary>
	private void DrawToolCreationPreview(DrawingContext drawingContext, ChartRenderFrame frame)
	{
		if (!frame.IsCreatingTool || !frame.CurrentMouseChartCoords.HasValue)
			return;

		var currentPoint = frame.CurrentMouseChartCoords.Value;

		if (frame.ActiveToolType == TechnicalAnalysisToolType.TrendLine &&
			frame.CreationStep == 1 &&
			frame.CreationPoints[0].HasValue)
		{
			DrawLinePreview(drawingContext, frame, frame.CreationPoints[0]!.Value, currentPoint,
				Color.FromArgb(128, 0, 120, 255));
		}

		if (frame.ActiveToolType == TechnicalAnalysisToolType.TrendChannel)
		{
			if (frame.CreationStep == 1 &&
				frame.CreationPoints[0].HasValue)
			{
				DrawLinePreview(drawingContext, frame, frame.CreationPoints[0]!.Value, currentPoint,
					Color.FromArgb(128, 0, 180, 0));
			}

			if (frame.CreationStep == 2 &&
				frame.CreatingToolInstance is TrendChannel previewChannel)
			{
				double previewOffset = currentPoint.price - previewChannel.StartPrice;
				previewChannel.DrawPreviewParallelLine(drawingContext, frame.Transform.ChartToView, previewOffset);
			}
		}

		if (frame.ActiveToolType == TechnicalAnalysisToolType.Rectangle &&
			frame.CreationStep == 1 &&
			frame.CreationPoints[0].HasValue)
		{
			DrawRectanglePreview(drawingContext, frame, frame.CreationPoints[0]!.Value, currentPoint,
				Color.FromArgb(128, 255, 165, 0));
		}
	}

	private void DrawLinePreview(DrawingContext drawingContext, ChartRenderFrame frame, ChartCoordinates start, ChartCoordinates end, Color color)
	{
		var startView = frame.Transform.ChartToView(start);
		var endView = frame.Transform.ChartToView(end);

		if (double.IsNaN(startView.x) || double.IsNaN(startView.y) ||
			double.IsNaN(endView.x) || double.IsNaN(endView.y))
			return;

		var previewPen = new Pen(new SolidColorBrush(color), 2.0) { DashStyle = DashStyles.Dash };
		drawingContext.DrawLine(previewPen,
			new Point(startView.x, startView.y),
			new Point(endView.x, endView.y));
	}

	private void DrawRectanglePreview(DrawingContext drawingContext, ChartRenderFrame frame, ChartCoordinates corner1, ChartCoordinates corner2, Color color)
	{
		var c1View = frame.Transform.ChartToView(corner1);
		var c2View = frame.Transform.ChartToView(corner2);

		if (double.IsNaN(c1View.x) || double.IsNaN(c1View.y) ||
			double.IsNaN(c2View.x) || double.IsNaN(c2View.y))
			return;

		var fillBrush = new SolidColorBrush(Color.FromArgb(32, 144, 238, 144));
		fillBrush.Freeze();
		var rectGeometry = new RectangleGeometry(new Rect(
			Math.Min(c1View.x, c2View.x),
			Math.Min(c1View.y, c2View.y),
			Math.Abs(c2View.x - c1View.x),
			Math.Abs(c2View.y - c1View.y)
		));
		drawingContext.DrawGeometry(fillBrush, null, rectGeometry);

		var previewPen = new Pen(new SolidColorBrush(color), 2.0) { DashStyle = DashStyles.Dash };

		drawingContext.DrawLine(previewPen, new Point(c1View.x, c1View.y), new Point(c2View.x, c1View.y));
		drawingContext.DrawLine(previewPen, new Point(c1View.x, c2View.y), new Point(c2View.x, c2View.y));
		drawingContext.DrawLine(previewPen, new Point(c1View.x, c1View.y), new Point(c1View.x, c2View.y));
		drawingContext.DrawLine(previewPen, new Point(c2View.x, c1View.y), new Point(c2View.x, c2View.y));
	}

	private static FormattedText CreateLabelText(string text, Brush brush, double emSize)
	{
		return new FormattedText(
			text,
			System.Globalization.CultureInfo.CurrentCulture,
			FlowDirection.LeftToRight,
			LabelTypeface,
			emSize,
			brush,
			96.0);
	}

	private static Pen CreateFrozenPen(Brush brush, double thickness)
	{
		var pen = new Pen(brush, thickness);
		pen.Freeze();
		return pen;
	}

	private static Pen CreateFrozenDashedPen(Brush brush, double thickness, DashStyle dashStyle)
	{
		if (dashStyle.CanFreeze)
			dashStyle.Freeze();
		var pen = new Pen(brush, thickness) { DashStyle = dashStyle };
		pen.Freeze();
		return pen;
	}

	private static Brush CreateFrozenBrush(Color color)
	{
		var brush = new SolidColorBrush(color);
		brush.Freeze();
		return brush;
	}

}
