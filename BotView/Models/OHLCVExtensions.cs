namespace BotView.Models;

/// <summary>Chart time helpers for the CCXT candle type.</summary>
public static class OHLCVExtensions
{
    public static DateTime GetDateTime(this ccxt.OHLCV candle) =>
        DateTimeOffset.FromUnixTimeMilliseconds(candle.timestamp.GetValueOrDefault()).DateTime;
}
