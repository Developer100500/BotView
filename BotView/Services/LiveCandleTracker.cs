using System.Collections.Generic;
using BotView.Models;

public sealed class LiveCandleTracker
{
    private readonly Dictionary<CandleCacheKey, OHLCV> _liveCandles = new();
    private readonly object _sync = new();

    /// <summary> Updates live candle for the specified key. </summary>
    public void Update(CandleCacheKey key, OHLCV candle)
    {
        lock (_sync)
        {
            _liveCandles[key] = candle;
        }
    }

    /// <summary> Returns current live candle for the specified key. </summary>
    public OHLCV? Get(CandleCacheKey key)
    {
        lock (_sync)
        {
            return _liveCandles.TryGetValue(key, out var candle) ? candle : null;
        }
    }

    /// <summary> Closes current live candle when a newer timestamp arrives. </summary>
    public bool TryClose(CandleCacheKey key, OHLCV newCandle, out OHLCV closed)
    {
        lock (_sync)
        {
            if (_liveCandles.TryGetValue(key, out var current) && newCandle.timestamp > current.timestamp)
            {
                closed = current;
                _liveCandles[key] = newCandle;
                return true;
            }

            closed = default;
            return false;
        }
    }

    /// <summary> Removes live candle state for the specified key. </summary>
    public void Remove(CandleCacheKey key)
    {
        lock (_sync)
        {
            _liveCandles.Remove(key);
        }
    }
}
