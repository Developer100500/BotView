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
	private static readonly Pen ComparisonPen = CreateFrozenPen(Brushes.MediumPurple, 2.5);
	private static readonly Pen GridPen = CreateFrozenDashedPen(Brushes.LightGray, 0.5, new DashStyle(new double[] { 2, 4 }, 0));
	private static readonly Brush DividerBrush = CreateFrozenBrush(Color.FromRgb(180, 180, 180));
	private static readonly Brush IndicatorBgBrush = CreateFrozenBrush(Color.FromRgb(250, 250, 252));
	private static readonly Brush CrosshairLabelBrush = CreateFrozenBrush(Color.FromRgb(96, 96, 96));

	/// <summary>Draws the captured frame into drawingContext, including comparison and measurement overlays.</summary>
	public void Render(DrawingContext drawingContext, ChartRenderFrame frame)
	{
		var candles = frame.Candles;
		if (frame.ChartWidth <= 0) return;

		DrawMainPaneArea(drawingContext, frame);
		DrawMainPaneGrid(drawingContext, frame);
		DrawPriceScale(drawingContext, frame);
		DrawCandlesticks(drawingContext, frame, candles);
		DrawComparison(drawingContext, frame);
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
		DrawMeasurement(drawingContext, frame, candles);
	}

	/// <summary>Draws the captured ruler into context; frame supplies geometry and candles supplies actual bar counts.</summary>
	private void DrawMeasurement(DrawingContext context, ChartRenderFrame frame, CandleSeriesSnapshot candles)
	{
		if (!frame.IsInitialized || frame.ChartWidth <= 0 || frame.MainPaneHeight <= 0 ||
			frame.MeasurementStart is not { } start || frame.MeasurementEnd is not { } end) return;
		var a = frame.Transform.ChartToView(start);
		var b = frame.Transform.ChartToView(end);
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
			frame.RightMargin, frame.MainPaneHeight)));
		try
		{
			double top = Math.Max(rectangle.Top, frame.MainPaneRect.Top);
			double bottom = Math.Min(rectangle.Bottom, frame.MainPaneRect.Bottom);
			context.DrawRectangle(fill, null, new Rect(frame.ScaleX, top, frame.RightMargin, Math.Max(0, bottom - top)));
			DrawMeasurementPriceLabel(context, frame, start.price, brush);
			if (start.price != end.price) DrawMeasurementPriceLabel(context, frame, end.price, brush);
		}
		finally { context.Pop(); }
	}

	/// <summary>Draws an absolute price label into context with brush using the captured frame geometry.</summary>
	private void DrawMeasurementPriceLabel(DrawingContext context, ChartRenderFrame frame, double price, Brush brush)
	{
		if (!IsPriceVisible(frame, price)) return;
		// Measurements always report primary prices, including in comparison mode.
		var text = CreateLabelText(price.ToString("G10", System.Globalization.CultureInfo.CurrentCulture), Brushes.White, 10);
		var bounds = GetPriceScaleLabelRect(frame, price, text);
		context.DrawRectangle(brush, null, bounds);
		context.DrawText(text, new Point(bounds.X + 4, bounds.Y + (bounds.Height - text.Height) / 2));
	}

	/// <summary>Fits a card of size inside pane near measurement, placing it below falling measurements and above rising ones.</summary>
	internal static Rect GetMeasurementCardRect(Rect measurement, Rect pane, Size size, bool falling)
	{
		double width = Math.Min(size.Width, pane.Width), height = Math.Min(size.Height, pane.Height);
		double x = Math.Clamp(measurement.X + (measurement.Width - width) / 2, pane.Left, pane.Right - width);
		double y = Math.Clamp(falling ? measurement.Bottom + 8 : measurement.Top - height - 8, pane.Top, pane.Bottom - height);
		return new Rect(x, y, width, height);
	}

	/// <summary>Draws an arrow from start to end into context using pen for its shaft and head.</summary>
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

	/// <summary>Draws the normalized comparison and legend into context using only candles and geometry captured in frame.</summary>
	private void DrawComparison(DrawingContext context, ChartRenderFrame frame)
	{
		if (frame.ComparisonCandles is not { } snapshot || frame.ComparisonAnchor is not { } anchor)
			return;
		long end = ComparisonSeriesMath.ToUnixMilliseconds(frame.Viewport.maxTime);
		TimeSpan timeframe = frame.Timeframe;
		var points = new List<Point>();
		long previousTimestamp = 0;
		double lastVisibleClose = double.NaN;
		context.PushClip(new RectangleGeometry(frame.MainPaneRect));
		try
		{
			foreach (var (_, candle) in snapshot.Enumerate(anchor.Timestamp, end))
			{
				double projected = ComparisonSeriesMath.ToPrimaryPrice(candle.Close, anchor);
				if (!double.IsFinite(projected) || projected <= 0)
				{
					DrawSmoothSegment(context, points);
					points.Clear();
					previousTimestamp = 0;
					continue;
				}
				if (previousTimestamp != 0 &&
					!ComparisonSeriesMath.IsContinuous(previousTimestamp, candle.Timestamp, timeframe))
				{
					DrawSmoothSegment(context, points);
					points.Clear();
				}
				var time = candle.GetDateTime();
				points.Add(new Point(frame.Transform.TimeToViewX(time), frame.Transform.PriceToViewY(projected)));
				previousTimestamp = candle.Timestamp;
				lastVisibleClose = candle.Close;
			}
			DrawSmoothSegment(context, points);
		}
		finally { context.Pop(); }

		string value = double.IsFinite(lastVisibleClose)
			? $"  {ComparisonSeriesMath.ToPercent(lastVisibleClose, anchor.ComparisonClose):+0.##;-0.##;0}%"
			: string.Empty;
		var legend = CreateLabelText($"Compare · {frame.ComparisonSymbol}{value}", Brushes.MediumPurple, 11);
		context.DrawText(legend, new Point(frame.Left + 8, frame.Top + 5));
		if (snapshot.Count > 0 && snapshot[snapshot.Count - 1].Timestamp >= anchor.Timestamp &&
			snapshot[snapshot.Count - 1].Timestamp <= end)
		{
			double last = ComparisonSeriesMath.ToPrimaryPrice(snapshot[snapshot.Count - 1].Close, anchor);
			if (double.IsFinite(last) && last >= frame.Viewport.minPrice && last <= frame.Viewport.maxPrice)
				DrawPriceScaleLabel(context, frame, last, Brushes.MediumPurple);
		}
	}

	/// <summary>Draws the ordered view-coordinate points into context with smooth tangents that preserve turning points.</summary>
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
				if (candle.Low > viewportMaxPrice || candle.High < viewportMinPrice)
					continue;

				DateTime candleTime = candle.Timestamp > 0 ? candle.GetDateTime() : frame.GetCandleTime(i);
				if (candleTime < viewportMinTime || candleTime > viewportMaxTime)
					continue;

				double centerX = frame.Transform.TimeToViewX(candleTime);
				double candleLeft = centerX - halfWidth;
				double candleRight = centerX + halfWidth;
				if (candleRight < viewportLeft || candleLeft > viewportRight)
					continue;

				double highY = frame.Transform.PriceToViewY(candle.High);
				double lowY = frame.Transform.PriceToViewY(candle.Low);
				double openY = frame.Transform.PriceToViewY(candle.Open);
				double closeY = frame.Transform.PriceToViewY(candle.Close);

				bool isBullish = candle.Close > candle.Open;
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
		Candle currentCandle = candles[lastIndex];
		double currentPrice = currentCandle.Close;
		if (!double.IsFinite(currentPrice) ||
			currentPrice < frame.Viewport.minPrice ||
			currentPrice > frame.Viewport.maxPrice)
		{
			return;
		}

		DateTime candleTime = currentCandle.Timestamp > 0
			? currentCandle.GetDateTime()
			: frame.GetCandleTime(lastIndex);
		double priceY = frame.Transform.PriceToViewY(currentPrice);
		double candleX = frame.Transform.TimeToViewX(candleTime);
		double scaleX = frame.ScaleX;
		double candleRight = candleX + frame.CandleWidthPixels / 2;
		double lineStartX = Math.Max(frame.Left, candleRight);

		Brush indicatorBrush = currentCandle.Close >= currentCandle.Open
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
		if (candles.Count > 0 && IsPriceVisible(frame, candles[candles.Count - 1].Close))
		{
			labelRect = GetPriceScaleLabelRect(frame, candles[candles.Count - 1].Close);
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
