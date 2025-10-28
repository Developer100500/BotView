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

	struct CandlestickData
	{
		public string	timeframe; // chart's candle timeframe
		public DateTime beginTime; // the date of the first candle
		public DateTime endTime; // last candle
		public OHLCV[]	candles;

		public CandlestickData(string timeframe, DateTime beginDateTime, DateTime endDateTime, OHLCV[] candles)
		{
			this.timeframe = timeframe.Trim();
			this.beginTime = beginDateTime;
			this.endTime = endDateTime;
			this.candles = candles;
		}
	}

	struct ViewportClippingCoords
	{
		public double minPrice;
		public double maxPrice;
		public DateTime minTime;
		public DateTime maxTime;
	}

	public class ChartView : FrameworkElement
	{
		string timeframe = string.Empty;
		CandlestickData candlestickData;
		OHLCV[] candlesticks;

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

	// === CAMERA SYSTEM (World Space) ===
	// Camera position in world coordinates
	private double cameraWorldX = 0;      // World X position (candle index)
	private double cameraWorldY = 50;     // World Y position (price)
	
	// Camera zoom level (how many world units fit in the viewport)
	private double worldUnitsPerViewportWidth = 20;   // How many candles fit horizontally
	private double worldUnitsPerViewportHeight = 100; // How much price range fits vertically

	// Initialization flag
	private bool isInitialized = false;

		public ChartView() : base()
		{
			timeframe = "1d";

			candlesticks = [
					new OHLCV (1, 14, -7, 5, 100),
					new OHLCV (11, 1, 3, 6, 60)
			];

			candlestickData = new CandlestickData (timeframe,
				DateTime.Parse("2025/10/20 12:00:00"), DateTime.Now, candlesticks);

			ViewportClippingCoords viewport = new()
			{
				minPrice = 0,
				maxPrice = 0,
				minTime = DateTime.Now.AddDays(-3),
				maxTime = DateTime.Now
			};
		}

		protected override void OnRender(DrawingContext drawingContext)
		{
			base.OnRender(drawingContext);

			// Update chart dimensions (these can change with window resize)
			UpdateChartDimensions();

		// Initialize camera only once
		if (!isInitialized && chartWidth > 0 && chartHeight > 0)
		{
			InitializeCamera();
			isInitialized = true;
		}

			// Draw background and borders for visualization
			DrawChartArea(drawingContext);

			// Draw the candlestick
			DrawCandlesticks(drawingContext, candlesticks);
		}

		protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
		{
			base.OnRenderSizeChanged(sizeInfo);
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
	/// Initialize camera position and zoom (called only once at startup)
	/// </summary>
	private void InitializeCamera()
	{
		// Calculate data range
		UpdateDataRange();

		// Position camera at center of data
		cameraWorldX = (minIndex + maxIndex) / 2.0;
		cameraWorldY = (minPrice + maxPrice) / 2.0;

		// Set initial zoom to fit all data with some padding
		double dataRangeX = maxIndex - minIndex + 10; // +10 for padding
		double dataRangeY = (maxPrice - minPrice) * 1.2; // 20% padding

		worldUnitsPerViewportWidth = dataRangeX;
		worldUnitsPerViewportHeight = dataRangeY;
	}

		/// <summary>
		/// Calculate the min/max price range from the data
		/// </summary>
		private void UpdateDataRange()
		{
			// For now, just use the single candlestick
			// Later, this will iterate through all candlesticks
			minPrice = -100;
			maxPrice = 100;
			minIndex = 0;
			maxIndex = 0;

			// Add some padding to the price range (10% on each side)
			double priceRange = maxPrice - minPrice;
			double padding = priceRange * 0.1;
			minPrice -= padding;
			maxPrice += padding;
		}

	/// <summary>
	/// Convert world coordinates to screen coordinates
	/// This is the KEY function that handles all coordinate transformation
	/// </summary>
	private Point WorldToScreen(double worldX, double worldY)
	{
		// Calculate how many pixels per world unit
		double pixelsPerWorldUnitX = chartWidth / worldUnitsPerViewportWidth;
		double pixelsPerWorldUnitY = chartHeight / worldUnitsPerViewportHeight;

		// Calculate world position relative to camera
		double relativeX = worldX - cameraWorldX;
		double relativeY = worldY - cameraWorldY;

		// Convert to screen space (centered in viewport)
		double screenX = leftMargin + chartWidth / 2 + (relativeX * pixelsPerWorldUnitX);
		double screenY = topMargin + chartHeight / 2 - (relativeY * pixelsPerWorldUnitY); // Flip Y

		return new Point(screenX, screenY);
	}

	/// <summary>
	/// Convert screen coordinates back to world coordinates
	/// Useful for mouse interaction
	/// </summary>
	private Point ScreenToWorld(double screenX, double screenY)
	{
		// Calculate how many pixels per world unit
		double pixelsPerWorldUnitX = chartWidth / worldUnitsPerViewportWidth;
		double pixelsPerWorldUnitY = chartHeight / worldUnitsPerViewportHeight;

		// Convert screen position to relative position in viewport
		double relativeScreenX = screenX - leftMargin - chartWidth / 2;
		double relativeScreenY = -(screenY - topMargin - chartHeight / 2); // Flip Y

		// Convert to world space
		double worldX = cameraWorldX + (relativeScreenX / pixelsPerWorldUnitX);
		double worldY = cameraWorldY + (relativeScreenY / pixelsPerWorldUnitY);

		return new Point(worldX, worldY);
	}

	/// <summary>
	/// Pan the camera in world coordinates
	/// </summary>
	/// <param name="deltaWorldX">Change in world X (candle indices)</param>
	/// <param name="deltaWorldY">Change in world Y (price units)</param>
	public void Pan(double deltaWorldX, double deltaWorldY)
	{
		cameraWorldX += deltaWorldX;
		cameraWorldY += deltaWorldY;
		InvalidateVisual();
	}

	/// <summary>
	/// Pan the camera based on screen pixel movement
	/// This is useful for mouse drag panning
	/// </summary>
	/// <param name="deltaScreenX">Change in screen X (pixels)</param>
	/// <param name="deltaScreenY">Change in screen Y (pixels)</param>
	public void PanByPixels(double deltaScreenX, double deltaScreenY)
	{
		// Convert pixel delta to world delta
		double pixelsPerWorldUnitX = chartWidth / worldUnitsPerViewportWidth;
		double pixelsPerWorldUnitY = chartHeight / worldUnitsPerViewportHeight;

		double deltaWorldX = -deltaScreenX / pixelsPerWorldUnitX; // Negative for natural panning
		double deltaWorldY = deltaScreenY / pixelsPerWorldUnitY;  // Flip Y

		Pan(deltaWorldX, deltaWorldY);
	}

	/// <summary>
	/// Zoom the camera (changes how many world units are visible)
	/// </summary>
	/// <param name="zoomFactorX">Zoom factor for X axis (1.0 = no change, 2.0 = zoom out 2x, 0.5 = zoom in 2x)</param>
	/// <param name="zoomFactorY">Zoom factor for Y axis</param>
	/// <param name="worldFocusX">World X coordinate to zoom towards (optional, defaults to camera center)</param>
	/// <param name="worldFocusY">World Y coordinate to zoom towards (optional, defaults to camera center)</param>
	public void Zoom(double zoomFactorX, double zoomFactorY, double? worldFocusX = null, double? worldFocusY = null)
	{
		// Use camera position as default focus point
		double focusX = worldFocusX ?? cameraWorldX;
		double focusY = worldFocusY ?? cameraWorldY;

		// Calculate offset from camera to focus point
		double offsetX = focusX - cameraWorldX;
		double offsetY = focusY - cameraWorldY;

		// Apply zoom
		worldUnitsPerViewportWidth *= zoomFactorX;
		worldUnitsPerViewportHeight *= zoomFactorY;

		// Clamp zoom levels to reasonable values
		worldUnitsPerViewportWidth = Math.Clamp(worldUnitsPerViewportWidth, 1, 10000);
		worldUnitsPerViewportHeight = Math.Clamp(worldUnitsPerViewportHeight, 1, 100000);

		// Adjust camera position to keep focus point in the same screen position
		cameraWorldX = focusX - offsetX * zoomFactorX;
		cameraWorldY = focusY - offsetY * zoomFactorY;

		InvalidateVisual();
	}

	/// <summary>
	/// Zoom towards a specific screen point (useful for mouse wheel zoom)
	/// </summary>
	public void ZoomAtScreenPoint(double screenX, double screenY, double zoomFactor)
	{
		Point worldPoint = ScreenToWorld(screenX, screenY);
		Zoom(zoomFactor, zoomFactor, worldPoint.X, worldPoint.Y);
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

		private void DrawCandlesticks(DrawingContext context, OHLCV[] candles)
		{
			for (int i = 0; i < candles.Length; i++)
			{
				DrawCandlestick(context, candles[i], i);
			}
		}

	/// <summary>
	/// Draw a single candlestick at the specified index
	/// </summary>
	private void DrawCandlestick(DrawingContext drawingContext, OHLCV candlestick, int candleIndex)
	{
		// Calculate candle width in world units (e.g., 0.6 of one candle unit)
		double candleWidthWorldUnits = 0.6;
		
		// Convert to screen space
		double pixelsPerWorldUnitX = chartWidth / worldUnitsPerViewportWidth;
		double candleWidthPixels = candleWidthWorldUnits * pixelsPerWorldUnitX;

		// Get screen positions using world coordinates
		Point centerPos = WorldToScreen(candleIndex, (candlestick.high + candlestick.low) / 2);
		Point highPos = WorldToScreen(candleIndex, candlestick.high);
		Point lowPos = WorldToScreen(candleIndex, candlestick.low);
		Point openPos = WorldToScreen(candleIndex, candlestick.open);
		Point closePos = WorldToScreen(candleIndex, candlestick.close);

		// Skip drawing if candle is outside viewport
		if (centerPos.X < leftMargin - 50 || centerPos.X > leftMargin + chartWidth + 50)
			return;

		// Determine if bullish (close > open) or bearish
		bool isBullish = candlestick.close > candlestick.open;

		// Set colors based on candle type
		Brush bodyBrush = isBullish ? Brushes.LightGreen : Brushes.LightCoral;
		Pen bodyPen = new Pen(isBullish ? Brushes.Green : Brushes.Red, 2);
		Pen wickPen = new Pen(Brushes.Black, 1.5);

		// Draw the wick (thin vertical line from high to low)
		drawingContext.DrawLine(wickPen, highPos, lowPos);

		// Draw the body (rectangle from open to close)
		double bodyTop = Math.Min(openPos.Y, closePos.Y);
		double bodyHeight = Math.Abs(closePos.Y - openPos.Y);

		// Handle doji case (open == close)
		if (bodyHeight < 1)
			bodyHeight = 1;

		Rect bodyRect = new Rect(
			centerPos.X - candleWidthPixels / 2,
			bodyTop,
			candleWidthPixels,
			bodyHeight
		);
		drawingContext.DrawRectangle(bodyBrush, bodyPen, bodyRect);
	}
	}
}
