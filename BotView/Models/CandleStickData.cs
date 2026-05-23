namespace BotView.Models;

/// <summary> A structure for storing candlestick data. </summary>
public struct OHLCV
{
	public long timestamp;    // UTC timestamp in milliseconds (added for CCXT compatibility)
	public double open;
	public double high;
	public double low;
	public double close;
	public double volume;

	// Existing constructor (for backward compatibility)
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
