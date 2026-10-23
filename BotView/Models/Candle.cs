namespace BotView.Models;

/// <summary>A provider-independent OHLCV value. Timestamp is the bar start in UTC Unix milliseconds.
/// Numeric values use the chart's double precision; unavailable values must be handled by the adapter.</summary>
public readonly record struct Candle
{
    public long Timestamp { get; init; }
    public double Open { get; init; }
    public double High { get; init; }
    public double Low { get; init; }
    public double Close { get; init; }
    public double Volume { get; init; }

    public DateTimeOffset OpenTimeUtc => DateTimeOffset.FromUnixTimeMilliseconds(Timestamp);
    public DateTime GetDateTime() => OpenTimeUtc.UtcDateTime;
}

/// <summary>A full candle snapshot, never a price or volume delta.</summary>
public sealed record CandleUpdate(Candle Candle, bool IsClosed);
