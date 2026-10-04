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
	private readonly ChartModel model;
	private readonly ChartController controller;
	private readonly IndicatorRenderer? indicatorRenderer;

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
	private static readonly Pen ComparisonPen = CreateFrozenPen(Brushes.MediumPurple, 2.5);
	private static readonly Brush DividerBrush = CreateFrozenBrush(Color.FromRgb(180, 180, 180));
	private static readonly Brush IndicatorBgBrush = CreateFrozenBrush(Color.FromRgb(250, 250, 252));
	private static readonly Brush CrosshairLabelBrush = CreateFrozenBrush(Color.FromRgb(96, 96, 96));

	public bool RedrawAllTechnicalTools { get; set; } = false;

	/// <summary>Determines whether candlestick bodies are drawn with a border.</summary>
	public bool ShowCandleOutlines { get; set; } = true;

	/// <summary>Текущие координаты мыши для превью инструмента (устанавливается из ChartView)</summary>
	public ChartCoordinates? CurrentMouseChartCoords { get; set; } = null;

	/// <summary>Whether crosshair should be drawn</summary>
	public bool CrosshairVisible { get; set; }

	/// <summary>Mouse position in view coordinates for crosshair</summary>
	public Point CrosshairViewPosition { get; set; }

	/// <summary>Pane under the cursor (affects horizontal line / price label)</summary>
	public ChartPane CrosshairPane { get; set; } = ChartPane.None;

	public ChartRenderer(ChartModel model, ChartController controller)
	{
		this.model = model ?? throw new ArgumentNullException(nameof(model));
		this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
		this.indicatorRenderer = new IndicatorRenderer(model, controller);
	}

	/// <summary>Главный метод отрисовки графика</summary>
	public void Render(DrawingContext drawingContext)
	{
		var candles = model.Series.GetSnapshot();
		controller.RefreshRenderCaches();
		var frameValues = BuildRenderFrame();

		DrawMainPaneArea(drawingContext, frameValues);
		DrawMainPaneGrid(drawingContext, frameValues);
		DrawPriceScale(drawingContext, frameValues);
		DrawCandlesticks(drawingContext, frameValues, candles);
		DrawComparison(drawingContext, frameValues);
		DrawTechnicalAnalysisTools(drawingContext, frameValues);
		DrawToolCreationPreview(drawingContext);
		DrawHorizontalToolPriceLabels(drawingContext, frameValues);
		DrawPriceIndicatorOfSelectedTool(drawingContext, frameValues);
		DrawCurrentPriceIndicator(drawingContext, frameValues, candles);

		DrawDivider(drawingContext, frameValues);
		DrawIndicatorPaneArea(drawingContext, frameValues);
		DrawIndicatorPaneGrid(drawingContext, frameValues);
		DrawIndicatorScale(drawingContext, frameValues);
		indicatorRenderer?.Render(drawingContext);
		DrawTimeScale(drawingContext, frameValues);
		DrawCrosshair(drawingContext, frameValues);
		DrawMeasurement(drawingContext, frameValues, candles);
	}

	private void DrawMeasurement(DrawingContext context, in RenderFrame frame, CandleSeriesSnapshot candles)
	{
		if (!model.IsInitialized || frame.ChartWidth <= 0 || frame.MainPaneHeight <= 0 ||
			model.Measurement.Start is not { } start || model.Measurement.End is not { } end) return;
		var a = controller.ChartToView(start);
		var b = controller.ChartToView(end);
		if (!double.IsFinite(a.x) || !double.IsFinite(a.y) || !double.IsFinite(b.x) || !double.IsFinite(b.y)) return;
		var result = MeasurementMath.Calculate(start, end, candles);
		Color color = result.IsFalling ? Color.FromRgb(242, 54, 69) : Color.FromRgb(41, 98, 255);
		var brush = CreateFrozenBrush(color);
		var fill = CreateFrozenBrush(Color.FromArgb(45, color.R, color.G, color.B));
		var pen = CreateFrozenPen(brush, 1);
		var rectangle = new Rect(new Point(Math.Min(a.x, b.x), Math.Min(a.y, b.y)),
			new Point(Math.Max(a.x, b.x), Math.Max(a.y, b.y)));
		if (!rectangle.IntersectsWith(frame.MainPaneRect)) return;
		context.PushClip(new RectangleGeometry(frame.MainPaneRect));
		try
		{
			context.DrawRectangle(fill, null, rectangle);
			double middleX = (a.x + b.x) / 2, middleY = (a.y + b.y) / 2;
			DrawMeasurementArrow(context, pen, new Point(a.x, middleY), new Point(b.x, middleY));
			DrawMeasurementArrow(context, pen, new Point(middleX, a.y), new Point(middleX, b.y));
			var priceText = CreateLabelText(result.PriceText, Brushes.White, 12);
			var timeText = CreateLabelText(result.TimeText, Brushes.White, 12);
			Rect card = GetMeasurementCardRect(rectangle, frame.MainPaneRect,
				new Size(Math.Max(priceText.Width, timeText.Width) + 16, priceText.Height + timeText.Height + 14), result.IsFalling);
			context.DrawRoundedRectangle(brush, null, card, 3, 3);
			context.DrawText(priceText, new Point(card.X + (card.Width - priceText.Width) / 2, card.Y + 5));
			context.DrawText(timeText, new Point(card.X + (card.Width - timeText.Width) / 2, card.Y + 9 + priceText.Height));
		}
		finally { context.Pop(); }

		// The scale has its own clip so offscreen anchors cannot paint over other panes.
		context.PushClip(new RectangleGeometry(new Rect(frame.ScaleX, frame.MainPaneRect.Top,
			model.RightMargin, frame.MainPaneHeight)));
		try
		{
			double top = Math.Max(rectangle.Top, frame.MainPaneRect.Top);
			double bottom = Math.Min(rectangle.Bottom, frame.MainPaneRect.Bottom);
			context.DrawRectangle(fill, null, new Rect(frame.ScaleX, top, model.RightMargin, Math.Max(0, bottom - top)));
			DrawMeasurementPriceLabel(context, frame, start.price, brush);
			if (start.price != end.price) DrawMeasurementPriceLabel(context, frame, end.price, brush);
		}
		finally { context.Pop(); }
	}

	private void DrawMeasurementPriceLabel(DrawingContext context, in RenderFrame frame, double price, Brush brush)
	{
		if (!IsPriceVisible(price)) return;
		// Measurements always report primary prices, including in comparison mode.
		var text = CreateLabelText(price.ToString("G10", System.Globalization.CultureInfo.CurrentCulture), Brushes.White, 10);
		var bounds = GetPriceScaleLabelRect(frame, price, text);
		context.DrawRectangle(brush, null, bounds);
		context.DrawText(text, new Point(bounds.X + 4, bounds.Y + (bounds.Height - text.Height) / 2));
	}

	internal static Rect GetMeasurementCardRect(Rect measurement, Rect pane, Size size, bool falling)
	{
		double width = Math.Min(size.Width, pane.Width), height = Math.Min(size.Height, pane.Height);
		double x = Math.Clamp(measurement.X + (measurement.Width - width) / 2, pane.Left, pane.Right - width);
		double y = Math.Clamp(falling ? measurement.Bottom + 8 : measurement.Top - height - 8, pane.Top, pane.Bottom - height);
		return new Rect(x, y, width, height);
	}

	private static void DrawMeasurementArrow(DrawingContext context, Pen pen, Point start, Point end)
	{
		context.DrawLine(pen, start, end);
		Vector direction = end - start;
		if (direction.Length < 1) return;
		double length = Math.Min(5, direction.Length / 2);
		direction.Normalize();
		Vector perpendicular = new(-direction.Y, direction.X);
		context.DrawLine(pen, end, end - direction * length + perpendicular * length);
		context.DrawLine(pen, end, end - direction * length - perpendicular * length);
	}

	/// <summary>Собирает layout и шаги шкал один раз на кадр</summary>
	private RenderFrame BuildRenderFrame()
	{
		double left = model.LeftMargin;
		double top = model.TopMargin;
		double chartWidth = model.ChartWidth;
		double mainPaneHeight = model.MainPaneHeight;
		double indicatorPaneTop = model.IndicatorPaneTop;
		double indicatorPaneHeight = model.IndicatorPaneHeight;
		double dividerY = model.DividerY;
		double dividerHeight = model.DividerHeight;
		double scaleX = left + chartWidth;
		double timeScaleY = indicatorPaneTop + indicatorPaneHeight;

		double priceInterval = controller.PriceInterval;
		TimeSpan timeInterval = controller.TimeInterval;
		double indicatorInterval = controller.IndicatorValueInterval;

		double firstPriceTick = priceInterval > 0
			? controller.RoundPriceTickDown(model.Viewport.minPrice, priceInterval)
			: model.Viewport.minPrice;
		DateTime firstTimeTick = timeInterval > TimeSpan.Zero
			? controller.RoundDownToInterval(model.Viewport.minTime, timeInterval)
			: model.Viewport.minTime;

		var indicatorViewport = model.IndicatorViewport;
		double firstIndicatorTick = indicatorInterval > 0
			? Math.Floor(indicatorViewport.MinValue / indicatorInterval) * indicatorInterval
			: indicatorViewport.MinValue;

		return new RenderFrame(
			left,
			top,
			chartWidth,
			mainPaneHeight,
			indicatorPaneTop,
			indicatorPaneHeight,
			dividerY,
			dividerHeight,
			scaleX,
			timeScaleY,
			controller.CandleWidthPixels,
			priceInterval,
			timeInterval,
			firstPriceTick,
			firstTimeTick,
			indicatorInterval,
			firstIndicatorTick,
			indicatorViewport.MaxValue,
			new Rect(left, top, chartWidth, mainPaneHeight),
			new Rect(left, dividerY, chartWidth, dividerHeight),
			new Rect(left, indicatorPaneTop, chartWidth, indicatorPaneHeight));
	}

	private void DrawMainPaneArea(DrawingContext drawingContext, in RenderFrame frame)
	{
		drawingContext.DrawRectangle(Brushes.White, BorderPen, frame.MainPaneRect);
	}

	private void DrawDivider(DrawingContext drawingContext, in RenderFrame frame)
	{
		drawingContext.DrawRectangle(DividerBrush, null, frame.DividerRect);
	}

	private void DrawIndicatorPaneArea(DrawingContext drawingContext, in RenderFrame frame)
	{
		drawingContext.DrawRectangle(IndicatorBgBrush, BorderPen, frame.IndicatorPaneRect);
	}

	/// <summary>Отрисовка всех свечей</summary>
	private void DrawCandlesticks(DrawingContext context, in RenderFrame frame, CandleSeriesSnapshot candles)
	{
		if (candles.Count == 0)
			return;

		var clipGeometry = new RectangleGeometry(frame.MainPaneRect);
		context.PushClip(clipGeometry);

		try
		{
			double viewportMinPrice = model.Viewport.minPrice;
			double viewportMaxPrice = model.Viewport.maxPrice;
			DateTime viewportMinTime = model.Viewport.minTime;
			DateTime viewportMaxTime = model.Viewport.maxTime;
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

				DateTime candleTime = candle.timestamp > 0 ? candle.GetDateTime() : controller.GetCandleTime(i);
				if (candleTime < viewportMinTime || candleTime > viewportMaxTime)
					continue;

				double centerX = controller.TimeToViewX(candleTime);
				double candleLeft = centerX - halfWidth;
				double candleRight = centerX + halfWidth;
				if (candleRight < viewportLeft || candleLeft > viewportRight)
					continue;

				double highY = controller.PriceToViewY(candle.high);
				double lowY = controller.PriceToViewY(candle.low);
				double openY = controller.PriceToViewY(candle.open);
				double closeY = controller.PriceToViewY(candle.close);

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
					ShowCandleOutlines ? bodyPen : null,
					new Rect(candleLeft, bodyTop, candleWidthPixels, bodyHeight));
			}
		}
		finally
		{
			context.Pop();
		}
	}

	private void DrawComparison(DrawingContext context, in RenderFrame frame)
	{
		if (model.ComparisonSeries == null || model.ComparisonAnchor is not { } anchor)
			return;
		var snapshot = model.ComparisonSeries.GetSnapshot();
		long end = ComparisonSeriesMath.ToUnixMilliseconds(model.Viewport.maxTime);
		TimeSpan timeframe = controller.ParseTimeframe(model.Timeframe);
		var points = new List<Point>();
		long previousTimestamp = 0;
		double lastVisibleClose = double.NaN;
		context.PushClip(new RectangleGeometry(frame.MainPaneRect));
		try
		{
			foreach (var (_, candle) in snapshot.Enumerate(anchor.Timestamp, end))
			{
				double projected = ComparisonSeriesMath.ToPrimaryPrice(candle.close, anchor);
				if (!double.IsFinite(projected) || projected <= 0)
				{
					DrawSmoothSegment(context, points);
					points.Clear();
					previousTimestamp = 0;
					continue;
				}
				if (previousTimestamp != 0 &&
					!ComparisonSeriesMath.IsContinuous(previousTimestamp, candle.timestamp, timeframe))
				{
					DrawSmoothSegment(context, points);
					points.Clear();
				}
				var time = candle.GetDateTime();
				points.Add(new Point(controller.TimeToViewX(time), controller.PriceToViewY(projected)));
				previousTimestamp = candle.timestamp;
				lastVisibleClose = candle.close;
			}
			DrawSmoothSegment(context, points);
		}
		finally { context.Pop(); }

		string value = double.IsFinite(lastVisibleClose)
			? $"  {ComparisonSeriesMath.ToPercent(lastVisibleClose, anchor.ComparisonClose):+0.##;-0.##;0}%"
			: string.Empty;
		var legend = CreateLabelText($"Compare · {model.ComparisonSymbol}{value}", Brushes.MediumPurple, 11);
		context.DrawText(legend, new Point(frame.Left + 8, frame.Top + 5));
		if (snapshot.Count > 0 && snapshot[snapshot.Count - 1].timestamp >= anchor.Timestamp &&
			snapshot[snapshot.Count - 1].timestamp <= end)
		{
			double last = ComparisonSeriesMath.ToPrimaryPrice(snapshot[snapshot.Count - 1].close, anchor);
			if (double.IsFinite(last) && last >= model.Viewport.minPrice && last <= model.Viewport.maxPrice)
				DrawPriceScaleLabel(context, frame, last, Brushes.MediumPurple);
		}
	}

	private static void DrawSmoothSegment(DrawingContext context, List<Point> points)
	{
		if (points.Count < 2) return;
		var geometry = new StreamGeometry();
		using (var path = geometry.Open())
		{
			path.BeginFigure(points[0], false, false);
			for (int i = 0; i < points.Count - 1; i++)
			{
				var a = points[i];
				var b = points[i + 1];
				double dx = b.X - a.X;
				if (dx <= 0) continue;
				// Zero tangents at turning points; otherwise average neighboring slopes.
				double slope = (b.Y - a.Y) / dx;
				double before = i > 0 ? (a.Y - points[i - 1].Y) / (a.X - points[i - 1].X) : slope;
				double after = i + 2 < points.Count
					? (points[i + 2].Y - b.Y) / (points[i + 2].X - b.X) : slope;
				double startTangent = before * slope <= 0 ? 0 : Math.Sign(slope) * Math.Min(Math.Abs((before + slope) / 2), Math.Abs(slope) * 3);
				double endTangent = after * slope <= 0 ? 0 : Math.Sign(slope) * Math.Min(Math.Abs((slope + after) / 2), Math.Abs(slope) * 3);
				path.BezierTo(new Point(a.X + dx / 3, a.Y + startTangent * dx / 3),
					new Point(b.X - dx / 3, b.Y - endTangent * dx / 3), b, true, false);
			}
		}
		geometry.Freeze();
		context.DrawGeometry(null, ComparisonPen, geometry);
	}

	/// <summary>
	/// Метки цен видимых горизонтальных линий и лучей на правой шкале.
	/// Метка текущей цены имеет приоритет при пересечении.
	/// </summary>
	private void DrawHorizontalToolPriceLabels(DrawingContext drawingContext, in RenderFrame frame)
	{
		if (!model.IsInitialized || frame.ChartWidth <= 0 || frame.MainPaneHeight <= 0)
			return;

		bool hasCurrentPriceLabel = TryGetCurrentPriceLabelRect(frame, out Rect currentPriceLabelRect);
		foreach (var tool in model.TechnicalAnalysisManager.GetTools())
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
				case HorizontalRay ray when ray.StartTime <= model.Viewport.maxTime:
					price = ray.Price;
					brush = ray.Color;
					break;
				default:
					continue;
			}

			if (!IsPriceVisible(price))
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
	private void DrawPriceIndicatorOfSelectedTool(DrawingContext drawingContext, in RenderFrame frame)
	{
		var tool = TechnicalAnalysisTool.EditingTool;
		if (tool == null || !tool.IsBeingEdited || tool is HorizontalLine or HorizontalRay)
			return;

		if (!tool.TryGetPriceScaleAnchor(out double price1, out double price2))
			return;

		if (!double.IsFinite(price1) ||
			price1 < model.Viewport.minPrice ||
			price1 > model.Viewport.maxPrice)
		{
			return;
		}

		DrawPriceScaleLabel(drawingContext, frame, price1, Brushes.Gray);

		if (!double.IsFinite(price2) ||
			price2 < model.Viewport.minPrice ||
			price2 > model.Viewport.maxPrice)
		{
			return;
		}

		DrawPriceScaleLabel(drawingContext, frame, price2, Brushes.Gray);
	}

	/// <summary>Отрисовывает пунктир от текущей свечи и метку рыночной цены на шкале</summary>
	private void DrawCurrentPriceIndicator(DrawingContext drawingContext, in RenderFrame frame, CandleSeriesSnapshot candles)
	{
		if (candles.Count == 0)
			return;

		int lastIndex = candles.Count - 1;
		OHLCV currentCandle = candles[lastIndex];
		double currentPrice = currentCandle.close;
		if (!double.IsFinite(currentPrice) ||
			currentPrice < model.Viewport.minPrice ||
			currentPrice > model.Viewport.maxPrice)
		{
			return;
		}

		DateTime candleTime = currentCandle.timestamp > 0
			? currentCandle.GetDateTime()
			: controller.GetCandleTime(lastIndex);
		double priceY = controller.PriceToViewY(currentPrice);
		double candleX = controller.TimeToViewX(candleTime);
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
		in RenderFrame frame,
		double price,
		Brush backgroundBrush)
	{
		string priceText = controller.FormatPriceLabel(price, frame.PriceInterval);
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

	private Rect GetPriceScaleLabelRect(in RenderFrame frame, double price)
	{
		string priceText = controller.FormatPriceLabel(price, frame.PriceInterval);
		return GetPriceScaleLabelRect(frame, price, CreateLabelText(priceText, Brushes.White, 10));
	}

	private Rect GetPriceScaleLabelRect(in RenderFrame frame, double price, FormattedText formattedText)
	{
		const double horizontalPadding = 4;
		const double verticalPadding = 2;
		double priceY = controller.PriceToViewY(price);
		double labelWidth = Math.Min(model.RightMargin, formattedText.Width + horizontalPadding * 2);
		double labelHeight = formattedText.Height + verticalPadding * 2;
		return new Rect(frame.ScaleX, priceY - labelHeight / 2, labelWidth, labelHeight);
	}

	private bool TryGetCurrentPriceLabelRect(in RenderFrame frame, out Rect labelRect)
	{
		var candles = model.Series.GetSnapshot();
		if (candles.Count > 0 && IsPriceVisible(candles[candles.Count - 1].close))
		{
			labelRect = GetPriceScaleLabelRect(frame, candles[candles.Count - 1].close);
			return true;
		}

		labelRect = Rect.Empty;
		return false;
	}

	private bool IsPriceVisible(double price) =>
		double.IsFinite(price) && price >= model.Viewport.minPrice && price <= model.Viewport.maxPrice;

	/// <summary>
	/// Crosshair: vertical follows mouse X with continuous time label;
	/// horizontal (main pane only) follows mouse Y with price label.
	/// </summary>
	private void DrawCrosshair(DrawingContext drawingContext, in RenderFrame frame)
	{
		if (!CrosshairVisible || !model.IsInitialized)
			return;

		if (frame.ChartWidth <= 0)
			return;

		Point mouse = CrosshairViewPosition;
		if (mouse.X < frame.Left || mouse.X > frame.ScaleX)
			return;

		ChartCoordinates chartCoords = controller.ViewToChart(new Coordinates(mouse.X, mouse.Y));
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

		bool showPriceCrosshair = CrosshairPane == ChartPane.Main
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
				&& price >= model.Viewport.minPrice
				&& price <= model.Viewport.maxPrice)
			{
				DrawPriceScaleLabel(drawingContext, frame, price, CrosshairLabelBrush);
			}
		}

		DrawTimeScaleLabel(drawingContext, frame, verticalX, chartCoords.time);
	}

	/// <summary>Draws a time label on the bottom time scale centered on vertical crosshair</summary>
	private void DrawTimeScaleLabel(
		DrawingContext drawingContext,
		in RenderFrame frame,
		double centerX,
		DateTime time)
	{
		string timeText = controller.FormatCrosshairTimeLabel(time);
		FormattedText formattedText = CreateLabelText(timeText, Brushes.White, 10);

		const double horizontalPadding = 4;
		const double verticalPadding = 2;
		double labelWidth = formattedText.Width + horizontalPadding * 2;
		double labelHeight = formattedText.Height + verticalPadding * 2;

		double labelLeft = centerX - labelWidth / 2;
		double minLeft = frame.Left;
		double maxLeft = frame.ScaleX + model.RightMargin - labelWidth;
		if (maxLeft < minLeft)
			maxLeft = minLeft;
		labelLeft = Math.Clamp(labelLeft, minLeft, maxLeft);

		double labelTop = frame.TimeScaleY + 2;
		double maxTop = frame.TimeScaleY + model.BottomMargin - labelHeight;
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
	private void DrawTimeScale(DrawingContext drawingContext, in RenderFrame frame)
	{
		if (model.Viewport.minTime >= model.Viewport.maxTime || frame.TimeInterval <= TimeSpan.Zero)
			return;

		double scaleY = frame.TimeScaleY;
		const double tickHeight = 5;
		const double textOffset = 3;

		drawingContext.DrawLine(ScalePen,
			new Point(frame.Left, scaleY),
			new Point(frame.ScaleX, scaleY));

		DateTime currentTime = frame.FirstTimeTick;
		while (currentTime <= model.Viewport.maxTime)
		{
			double x = controller.TimeToViewX(currentTime);
			if (x >= frame.Left && x <= frame.ScaleX)
			{
				drawingContext.DrawLine(TickPen,
					new Point(x, scaleY),
					new Point(x, scaleY + tickHeight));

				string timeText = controller.FormatTimeLabel(currentTime, frame.TimeInterval);
				FormattedText formattedText = CreateLabelText(timeText, Brushes.Black, 10);
				drawingContext.DrawText(
					formattedText,
					new Point(x - formattedText.Width / 2, scaleY + tickHeight + textOffset));
			}

			currentTime = currentTime.Add(frame.TimeInterval);
		}
	}

	/// <summary>Отрисовка шкалы цены (вертикальная ось справа main pane)</summary>
	private void DrawPriceScale(DrawingContext drawingContext, in RenderFrame frame)
	{
		if (model.Viewport.minPrice >= model.Viewport.maxPrice || frame.PriceInterval <= 0)
			return;

		const double tickWidth = 5;
		const double textOffset = 3;
		double scaleX = frame.ScaleX;
		double mainBottom = frame.Top + frame.MainPaneHeight;

		drawingContext.DrawLine(ScalePen,
			new Point(scaleX, frame.Top),
			new Point(scaleX, mainBottom));

		double currentPrice = frame.FirstPriceTick;
		while (currentPrice <= model.Viewport.maxPrice)
		{
			double y = controller.PriceToViewY(currentPrice);
			if (y >= frame.Top && y <= mainBottom)
			{
				drawingContext.DrawLine(TickPen,
					new Point(scaleX, y),
					new Point(scaleX + tickWidth, y));

				string priceText = controller.FormatPriceLabel(currentPrice, frame.PriceInterval);
				FormattedText formattedText = CreateLabelText(priceText, Brushes.Black, 10);
				drawingContext.DrawText(
					formattedText,
					new Point(scaleX + tickWidth + textOffset, y - formattedText.Height / 2));
			}

			currentPrice += frame.PriceInterval;
		}
	}

	/// <summary>Отрисовка шкалы индикатора (вертикальная ось справа indicator pane)</summary>
	private void DrawIndicatorScale(DrawingContext drawingContext, in RenderFrame frame)
	{
		if (frame.FirstIndicatorTick > frame.IndicatorMaxValue || frame.IndicatorValueInterval <= 0)
			return;

		const double tickWidth = 5;
		const double textOffset = 3;
		double scaleX = frame.ScaleX;
		double indicatorBottom = frame.IndicatorPaneTop + frame.IndicatorPaneHeight;

		drawingContext.DrawLine(ScalePen,
			new Point(scaleX, frame.IndicatorPaneTop),
			new Point(scaleX, indicatorBottom));

		double currentValue = frame.FirstIndicatorTick;
		while (currentValue <= frame.IndicatorMaxValue)
		{
			double y = controller.IndicatorValueToViewY(currentValue);
			if (y >= frame.IndicatorPaneTop && y <= indicatorBottom)
			{
				drawingContext.DrawLine(TickPen,
					new Point(scaleX, y),
					new Point(scaleX + tickWidth, y));

				FormattedText formattedText = CreateLabelText(FormatIndicatorValue(currentValue), Brushes.Black, 9);
				drawingContext.DrawText(
					formattedText,
					new Point(scaleX + tickWidth + textOffset, y - formattedText.Height / 2));
			}

			currentValue += frame.IndicatorValueInterval;
		}
	}

	private static string FormatIndicatorValue(double value)
	{
		if (Math.Abs(value) >= 100) return value.ToString("F0");
		if (Math.Abs(value) >= 10) return value.ToString("F1");
		return value.ToString("F2");
	}

	/// <summary>Отрисовка сетки для main pane (candlestick chart)</summary>
	private void DrawMainPaneGrid(DrawingContext drawingContext, in RenderFrame frame)
	{
		if (model.Viewport.minPrice >= model.Viewport.maxPrice || model.Viewport.minTime >= model.Viewport.maxTime)
			return;

		double mainBottom = frame.Top + frame.MainPaneHeight;

		if (frame.PriceInterval > 0)
		{
			double currentPrice = frame.FirstPriceTick;
			while (currentPrice <= model.Viewport.maxPrice)
			{
				double y = controller.PriceToViewY(currentPrice);
				if (y >= frame.Top && y <= mainBottom)
				{
					drawingContext.DrawLine(GridPen,
						new Point(frame.Left, y),
						new Point(frame.ScaleX, y));
				}
				currentPrice += frame.PriceInterval;
			}
		}

		if (frame.TimeInterval > TimeSpan.Zero)
		{
			DateTime currentTime = frame.FirstTimeTick;
			while (currentTime <= model.Viewport.maxTime)
			{
				double x = controller.TimeToViewX(currentTime);
				if (x >= frame.Left && x <= frame.ScaleX)
				{
					drawingContext.DrawLine(GridPen,
						new Point(x, frame.Top),
						new Point(x, mainBottom));
				}
				currentTime = currentTime.Add(frame.TimeInterval);
			}
		}
	}

	/// <summary>Отрисовка сетки для indicator pane</summary>
	private void DrawIndicatorPaneGrid(DrawingContext drawingContext, in RenderFrame frame)
	{
		if (model.Viewport.minTime >= model.Viewport.maxTime)
			return;

		double indicatorBottom = frame.IndicatorPaneTop + frame.IndicatorPaneHeight;

		if (frame.TimeInterval > TimeSpan.Zero)
		{
			DateTime currentTime = frame.FirstTimeTick;
			while (currentTime <= model.Viewport.maxTime)
			{
				double x = controller.TimeToViewX(currentTime);
				if (x >= frame.Left && x <= frame.ScaleX)
				{
					drawingContext.DrawLine(GridPen,
						new Point(x, frame.IndicatorPaneTop),
						new Point(x, indicatorBottom));
				}
				currentTime = currentTime.Add(frame.TimeInterval);
			}
		}

		if (frame.IndicatorValueInterval > 0)
		{
			double currentValue = frame.FirstIndicatorTick;
			while (currentValue <= frame.IndicatorMaxValue)
			{
				double y = controller.IndicatorValueToViewY(currentValue);
				if (y >= frame.IndicatorPaneTop && y <= indicatorBottom)
				{
					drawingContext.DrawLine(GridPen,
						new Point(frame.Left, y),
						new Point(frame.ScaleX, y));
				}
				currentValue += frame.IndicatorValueInterval;
			}
		}
	}

	private void DrawTechnicalAnalysisTools(DrawingContext drawingContext, in RenderFrame frame)
	{
		if (model.TechnicalAnalysisManager == null)
			return;

		if (frame.ChartWidth <= 0 || frame.MainPaneHeight <= 0 || !model.IsInitialized)
			return;

		ViewportClippingCoords currentViewport = model.Viewport;
		var clipGeometry = new RectangleGeometry(frame.MainPaneRect);
		drawingContext.PushClip(clipGeometry);

		try
		{
			foreach (var tool in model.TechnicalAnalysisManager.GetTools())
			{
				if (tool.IsVisible)
					tool.Draw(drawingContext, controller.ChartToView, currentViewport);
			}
		}
		finally
		{
			drawingContext.Pop();
		}
	}

	/// <summary>
	/// Устанавливает флаг принудительной перерисовки всех инструментов технического анализа
	/// </summary>
	public void RequestRedrawAllTechnicalTools()
	{
		RedrawAllTechnicalTools = true;
	}

	/// <summary>Отрисовка превью создаваемого инструмента</summary>
	private void DrawToolCreationPreview(DrawingContext drawingContext)
	{
		if (!TechnicalAnalysisTool.IsCreatingTool || !CurrentMouseChartCoords.HasValue)
			return;

		var currentPoint = CurrentMouseChartCoords.Value;

		if (TechnicalAnalysisTool.CreatingToolType == TechnicalAnalysisToolType.TrendLine &&
			TechnicalAnalysisTool.CreationStep == 1 &&
			TechnicalAnalysisTool.CreationPoints[0].HasValue)
		{
			DrawLinePreview(drawingContext, TechnicalAnalysisTool.CreationPoints[0].Value, currentPoint,
				Color.FromArgb(128, 0, 120, 255));
		}

		if (TechnicalAnalysisTool.CreatingToolType == TechnicalAnalysisToolType.TrendChannel)
		{
			if (TechnicalAnalysisTool.CreationStep == 1 &&
				TechnicalAnalysisTool.CreationPoints[0].HasValue)
			{
				DrawLinePreview(drawingContext, TechnicalAnalysisTool.CreationPoints[0].Value, currentPoint,
					Color.FromArgb(128, 0, 180, 0));
			}

			if (TechnicalAnalysisTool.CreationStep == 2 &&
				TechnicalAnalysisTool.CreatingToolInstance is TrendChannel previewChannel)
			{
				double previewOffset = currentPoint.price - previewChannel.StartPrice;
				previewChannel.DrawPreviewParallelLine(drawingContext, controller.ChartToView, previewOffset);
			}
		}

		if (TechnicalAnalysisTool.CreatingToolType == TechnicalAnalysisToolType.Rectangle &&
			TechnicalAnalysisTool.CreationStep == 1 &&
			TechnicalAnalysisTool.CreationPoints[0].HasValue)
		{
			DrawRectanglePreview(drawingContext, TechnicalAnalysisTool.CreationPoints[0].Value, currentPoint,
				Color.FromArgb(128, 255, 165, 0));
		}
	}

	private void DrawLinePreview(DrawingContext drawingContext, ChartCoordinates start, ChartCoordinates end, Color color)
	{
		var startView = controller.ChartToView(start);
		var endView = controller.ChartToView(end);

		if (double.IsNaN(startView.x) || double.IsNaN(startView.y) ||
			double.IsNaN(endView.x) || double.IsNaN(endView.y))
			return;

		var previewPen = new Pen(new SolidColorBrush(color), 2.0) { DashStyle = DashStyles.Dash };
		drawingContext.DrawLine(previewPen,
			new Point(startView.x, startView.y),
			new Point(endView.x, endView.y));
	}

	private void DrawRectanglePreview(DrawingContext drawingContext, ChartCoordinates corner1, ChartCoordinates corner2, Color color)
	{
		var c1View = controller.ChartToView(corner1);
		var c2View = controller.ChartToView(corner2);

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

	/// <summary>Кэш layout и шагов шкал на один кадр отрисовки</summary>
	private readonly struct RenderFrame
	{
		public readonly double Left;
		public readonly double Top;
		public readonly double ChartWidth;
		public readonly double MainPaneHeight;
		public readonly double IndicatorPaneTop;
		public readonly double IndicatorPaneHeight;
		public readonly double DividerY;
		public readonly double DividerHeight;
		public readonly double ScaleX;
		public readonly double TimeScaleY;
		public readonly double CandleWidthPixels;
		public readonly double PriceInterval;
		public readonly TimeSpan TimeInterval;
		public readonly double FirstPriceTick;
		public readonly DateTime FirstTimeTick;
		public readonly double IndicatorValueInterval;
		public readonly double FirstIndicatorTick;
		public readonly double IndicatorMaxValue;
		public readonly Rect MainPaneRect;
		public readonly Rect DividerRect;
		public readonly Rect IndicatorPaneRect;

		public RenderFrame(
			double left,
			double top,
			double chartWidth,
			double mainPaneHeight,
			double indicatorPaneTop,
			double indicatorPaneHeight,
			double dividerY,
			double dividerHeight,
			double scaleX,
			double timeScaleY,
			double candleWidthPixels,
			double priceInterval,
			TimeSpan timeInterval,
			double firstPriceTick,
			DateTime firstTimeTick,
			double indicatorValueInterval,
			double firstIndicatorTick,
			double indicatorMaxValue,
			Rect mainPaneRect,
			Rect dividerRect,
			Rect indicatorPaneRect)
		{
			Left = left;
			Top = top;
			ChartWidth = chartWidth;
			MainPaneHeight = mainPaneHeight;
			IndicatorPaneTop = indicatorPaneTop;
			IndicatorPaneHeight = indicatorPaneHeight;
			DividerY = dividerY;
			DividerHeight = dividerHeight;
			ScaleX = scaleX;
			TimeScaleY = timeScaleY;
			CandleWidthPixels = candleWidthPixels;
			PriceInterval = priceInterval;
			TimeInterval = timeInterval;
			FirstPriceTick = firstPriceTick;
			FirstTimeTick = firstTimeTick;
			IndicatorValueInterval = indicatorValueInterval;
			FirstIndicatorTick = firstIndicatorTick;
			IndicatorMaxValue = indicatorMaxValue;
			MainPaneRect = mainPaneRect;
			DividerRect = dividerRect;
			IndicatorPaneRect = indicatorPaneRect;
		}
	}
}
