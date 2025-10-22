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
		string timeframe = string.Empty;
		OHLCV singleCandleStick;

		// Data range (will be calculated from candlestick data)
		private double minPrice = double.MaxValue;
		private double maxPrice = double.MinValue;
		private int minIndex = 0;
		private int maxIndex = 0;

		// Viewport settings (in pixels)
		private double leftMargin = 50;
		private double rightMargin = 50;
		private double topMargin = 30;
		private double bottomMargin = 30;

	// Chart area dimensions (calculated)
	private double chartWidth = 0;
	private double chartHeight = 0;

	// Scaling factors (fixed after initialization)
	private double xScale = 10.0;  // pixels per candle index (default)
	private double yScale = 10.0;  // pixels per price unit (default)

	// Pan/Zoom positions
	private double startTimePosition = 0;   // leftmost candle index visible in viewport
	private double startPricePosition = 0;  // bottom price visible in viewport

	// Initialization flag
	private bool isInitialized = false;

	public ChartView() : base()
	{
		singleCandleStick = new OHLCV(1, 14, -7, 5, 100);
	}

	protected override void OnRender(DrawingContext drawingContext)
	{
		base.OnRender(drawingContext);
		
		// Update chart dimensions (these can change with window resize)
		UpdateChartDimensions();
		
		// Initialize scaling and positions only once
		if (!isInitialized && chartWidth > 0 && chartHeight > 0)
		{
			InitializeScaling();
			isInitialized = true;
		}
		
		// Draw background and borders for visualization
		DrawChartArea(drawingContext);
		
		// Draw the candlestick
		DrawCandlestick(drawingContext, singleCandleStick, 0);
	}

	/// <summary>
	/// Update chart dimensions (called on every render to handle window resize)
	/// </summary>
	private void UpdateChartDimensions()
	{
		chartWidth = Math.Max(0, this.ActualWidth - leftMargin - rightMargin);
		chartHeight = Math.Max(0, this.ActualHeight - topMargin - bottomMargin);
	}

	/// <summary>
	/// Initialize scaling factors and start positions (called only once at startup)
	/// </summary>
	private void InitializeScaling()
	{
		// Calculate data range
		UpdateDataRange();
		
		// Calculate initial scaling to fit data in viewport
		double priceRange = maxPrice - minPrice;
		if (priceRange > 0)
		{
			yScale = chartHeight / priceRange;  // pixels per price unit
		}
		
		// For X scale, use a default value (e.g., 50 pixels per candle)
		// This will be adjustable via zoom later
		xScale = 50.0;  // pixels per candle
		
		// Set initial positions to center the data
		startTimePosition = minIndex - 5;  // Start a bit before first candle
		startPricePosition = minPrice;     // Start at minimum price
	}

	/// <summary>
	/// Calculate the min/max price range from the data
	/// </summary>
	private void UpdateDataRange()
	{
		// For now, just use the single candlestick
		// Later, this will iterate through all candlesticks
		minPrice = singleCandleStick.low;
		maxPrice = singleCandleStick.high;
		minIndex = 0;
		maxIndex = 0;
		
		// Add some padding to the price range (10% on each side)
		double priceRange = maxPrice - minPrice;
		double padding = priceRange * 0.1;
		minPrice -= padding;
		maxPrice += padding;
	}

	/// <summary>
	/// Convert data coordinates (candle index, price) to screen coordinates
	/// </summary>
	private Point DataToScreen(double candleIndex, double price)
	{
		double x = leftMargin + (candleIndex - startTimePosition) * xScale;
		// Flip Y axis: higher prices should be higher on screen
		double y = topMargin + chartHeight - ((price - startPricePosition) * yScale);
		return new Point(x, y);
	}

	/// <summary>
	/// Convert price value to screen Y coordinate
	/// </summary>
	private double PriceToScreenY(double price)
	{
		return topMargin + chartHeight - ((price - startPricePosition) * yScale);
	}

	/// <summary>
	/// Convert candle index to screen X coordinate
	/// </summary>
	private double IndexToScreenX(double candleIndex)
	{
		return leftMargin + (candleIndex - startTimePosition) * xScale;
	}

	/// <summary>
	/// Pan the chart by moving the start positions
	/// </summary>
	/// <param name="deltaTime">Change in time/index position</param>
	/// <param name="deltaPrice">Change in price position</param>
	public void Pan(double deltaTime, double deltaPrice)
	{
		startTimePosition += deltaTime;
		startPricePosition += deltaPrice;
		InvalidateVisual();  // Request redraw
	}

	/// <summary>
	/// Set zoom level (scaling factors)
	/// </summary>
	/// <param name="newXScale">New X scale (pixels per candle)</param>
	/// <param name="newYScale">New Y scale (pixels per price unit)</param>
	public void SetZoom(double newXScale, double newYScale)
	{
		xScale = Math.Max(1, newXScale);  // Minimum 1 pixel per candle
		yScale = Math.Max(0.1, newYScale); // Minimum scale
		InvalidateVisual();  // Request redraw
	}

		/// <summary>
		/// Draw chart area background and borders for visualization
		/// </summary>
		private void DrawChartArea(DrawingContext drawingContext)
		{
			// Draw background
			Rect chartRect = new Rect(leftMargin, topMargin, chartWidth, chartHeight);
			drawingContext.DrawRectangle(Brushes.White, new Pen(Brushes.Gray, 1), chartRect);
			
			// Draw center line for reference
			Pen centerLinePen = new Pen(Brushes.LightGray, 1);
			centerLinePen.DashStyle = DashStyles.Dash;
			double centerY = topMargin + chartHeight / 2;
			drawingContext.DrawLine(centerLinePen, 
				new Point(leftMargin, centerY), 
				new Point(leftMargin + chartWidth, centerY));
		}

	/// <summary>
	/// Draw a single candlestick at the specified index
	/// </summary>
	/// <param name="drawingContext">Drawing context</param>
	/// <param name="candlestick">Candlestick data</param>
	/// <param name="candleIndex">Index position of the candle</param>
	private void DrawCandlestick(DrawingContext drawingContext, OHLCV candlestick, int candleIndex)
	{
		// Candle width (in pixels) - can be adjusted based on available space
		double candleWidth = Math.Min(20, chartWidth * 0.6);  // Max 20px or 60% of chart width
		
		// Calculate X position (center of the candle)
		double xPosition = IndexToScreenX(candleIndex);
		
		// Convert prices to Y coordinates using the coordinate system
		double highY = PriceToScreenY(candlestick.high);
		double lowY = PriceToScreenY(candlestick.low);
		double openY = PriceToScreenY(candlestick.open);
		double closeY = PriceToScreenY(candlestick.close);
		
		// Determine if bullish (close > open) or bearish
		bool isBullish = candlestick.close > candlestick.open;
		
		// Set colors based on candle type
		Brush bodyBrush = isBullish ? Brushes.LightGreen : Brushes.LightCoral;
		Pen bodyPen = new Pen(isBullish ? Brushes.Green : Brushes.Red, 2);
		Pen wickPen = new Pen(Brushes.Black, 1.5);
		
		// Draw the wick (thin vertical line from high to low)
		Point wickTop = new Point(xPosition, highY);
		Point wickBottom = new Point(xPosition, lowY);
		drawingContext.DrawLine(wickPen, wickTop, wickBottom);
		
		// Draw the body (rectangle from open to close)
		double bodyTop = Math.Min(openY, closeY);      // Top of body
		double bodyHeight = Math.Abs(closeY - openY);  // Height of body
		
		// Handle doji case (open == close)
		if (bodyHeight < 1)
			bodyHeight = 1;  // Minimum 1 pixel height
		
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
