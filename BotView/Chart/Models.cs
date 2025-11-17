namespace BotView.Chart.Models
{
	/**
	 * A structure for storing candlestick data.
	 */
	public struct OHLCV
	{
		public long timestamp;    // UTC timestamp in milliseconds (added for CCXT compatibility)
		public double open;
		public double high;
		public double low;
		public double close;
		public double volume;

		public OHLCV(double open, double high, double low, double close, double volume = -1)
		{
			this.timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // Current time by default
			this.open = open;
			this.high = high;
			this.low = low;
			this.close = close;
			this.volume = volume;
		}

		// New constructor with timestamp (for CCXT compatibility)
		public OHLCV(long timestamp, double open, double high, double low, double close, double volume = -1)
		{
			this.timestamp = timestamp;
			this.open = open;
			this.high = high;
			this.low = low;
			this.close = close;
			this.volume = volume;
		}

		// Convert timestamp to DateTime
		public DateTime GetDateTime() => DateTimeOffset.FromUnixTimeMilliseconds(timestamp).DateTime;
	}

	public struct CandlestickData
	{
		public string timeframe; // chart's candle timeframe
		public DateTime beginTime; // the date of the first candle
		public DateTime endTime; // last candle
		public OHLCV[] candles;

		public CandlestickData(string timeframe, DateTime beginDateTime, DateTime endDateTime, OHLCV[] candles)
		{
			this.timeframe = timeframe.Trim();
			this.beginTime = beginDateTime;
			this.endTime = endDateTime;
			this.candles = candles;
		}
	}

	/// <summary>
	/// Универсальная структура для хранения координат
	/// Используется для различных систем координат в графике:
	/// 
	/// 1. View Coordinates (Координаты окна):
	///    - Пиксели от верхнего левого угла компонента ChartView
	///    - Используется для позиционирования UI элементов и обработки мыши
	/// 
	/// 2. World Coordinates (Мировые координаты):
	///    - x: секунды от базового времени (worldOriginTime)
	///    - y: единицы цены от базовой цены (worldOriginPrice)
	///    - Используется для позиционирования графика, операций zoom/pan
	/// </summary>
	public struct Coordinates
	{
		public double x;
		public double y;

		public Coordinates(double x, double y)
		{
			this.x = x;
			this.y = y;
		}

		// Конструктор для целочисленных координат
		public Coordinates(int x, int y)
		{
			this.x = x;
			this.y = y;
		}
	}

	/// <summary>
	/// Координаты в виде цены и времени (Chart Coordinates)
	/// Используются для позиционирования свечек согласно OHLCV данным
	/// </summary>
	public struct ChartCoordinates
	{
		public DateTime time;
		public double price;

		public ChartCoordinates(DateTime time, double price)
		{
			this.time = time;
			this.price = price;
		}
	}

	/// <summary>
	/// Viewport/Camera - определяет какую часть мирового пространства мы видим
	/// </summary>
	public struct ViewportClippingCoords
	{
		public double minPrice; // минимальная цена в нашем текующем viewport (низ нашего порта)
		public double maxPrice;
		public DateTime minTime; // точка времени на которую сейчас приходится левый край нешего viewport
		public DateTime maxTime;

		public ViewportClippingCoords(double minPrice, double maxPrice, DateTime minTime, DateTime maxTime)
		{
			this.minPrice = minPrice;
			this.maxPrice = maxPrice;
			this.minTime = minTime;
			this.maxTime = maxTime;
		}
	}
}