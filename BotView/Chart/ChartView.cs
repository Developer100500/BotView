using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace BotView.Chart
{
	/**
	 * A structure for storing candlestick data.
	 */
	struct OHLCV
	{
		public double open;
		public double high;
		public double low;
		public double close;
		public double volume;

		public OHLCV(double open, double high, double low, double close, double volume = -1)
		{
			this.open = open;
			this.high = high;
			this.low = low;
			this.close = close;
			this.volume = volume;
		}
	}

	public class ChartView : FrameworkElement
	{
		OHLCV singleCandleStick;

		double xScale = 0;	// pixels per time (milliseconds)
		double yScale = 0;	// pixels per price (depends on the price accuracy (how many digits after the decimal point))

		public ChartView() : base()
		{
			singleCandleStick = new OHLCV(1, 14, -7, 5, 100);

		}

		protected override void OnRender(DrawingContext drawingContext)
		{
			base.OnRender(drawingContext);
			
			this.xScale = this.ActualWidth / 1000;
			this.yScale = this.ActualHeight / 10;
			DrawCandlestick(drawingContext, singleCandleStick);
		}

	private void DrawCandlestick(DrawingContext drawingContext, OHLCV candlestick)
	{
		// Position and scale settings for the candlestick
		double xPosition = 100;  // X position on canvas
		double candleWidth = 20; // Width of the candle body
		double yScale = 10;      // Scale factor for price (pixels per unit)
		double yOffset = 200;    // Y offset to flip coordinates (price increases upward)

		// Determine if bullish (close > open) or bearish
		bool isBullish = candlestick.close > candlestick.open;
		
		// Set colors based on candle type
		Brush bodyBrush = isBullish ? Brushes.LightGreen : Brushes.LightCoral;
		Pen bodyPen = new Pen(isBullish ? Brushes.Green : Brushes.Red, 2);
		Pen wickPen = new Pen(Brushes.Black, 1);

		// Convert price to Y coordinates (flip Y axis so higher prices are higher on screen)
		double highY = yOffset - (candlestick.high * yScale);
		double lowY = yOffset - (candlestick.low * yScale);
		double openY = yOffset - (candlestick.open * yScale);
		double closeY = yOffset - (candlestick.close * yScale);

		// Draw the wick (thin vertical line from high to low)
		Point wickTop = new Point(xPosition, highY);
		Point wickBottom = new Point(xPosition, lowY);
		drawingContext.DrawLine(wickPen, wickTop, wickBottom);

		// Draw the body (rectangle from open to close)
		double bodyTop = Math.Min(openY, closeY);    // Top of body
		double bodyHeight = Math.Abs(closeY - openY); // Height of body
		Rect bodyRect = new Rect(
			xPosition - candleWidth / 2,  // X position (centered)
			bodyTop,                       // Y position (top)
			candleWidth,                   // Width
			bodyHeight                     // Height
		);
		drawingContext.DrawRectangle(bodyBrush, bodyPen, bodyRect);
	}
	}
}
