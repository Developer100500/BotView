using System;
using BotView.Models;

public sealed class MarketDataSubscription : IMarketDataSubscription
{
    private readonly Action<CandleCacheKey>? _onDispose;
    private bool _isDisposed;

    public CandleCacheKey Key { get; }
    public CandleStore Store { get; }

    public event Action<OHLCV>? LiveCandleTicked;
    public event Action<OHLCV, OHLCV>? CandleClosed;
    public event Action<OHLCV[]>? OlderCandlesLoaded;

    /// <summary> Creates subscription wrapper over shared candle store. </summary>
    public MarketDataSubscription(CandleCacheKey key, CandleStore store, Action<CandleCacheKey>? onDispose = null)
    {
        Key = key;
        Store = store ?? throw new ArgumentNullException(nameof(store));
        _onDispose = onDispose;
    }

    /// <summary> Notifies listeners that current live candle was updated. </summary>
    internal void RaiseLiveCandleTicked(OHLCV candle)
    {
        if (_isDisposed)
        {
            return;
        }

        LiveCandleTicked?.Invoke(candle);
    }

    /// <summary> Notifies listeners that one candle closed and a new one opened. </summary>
    internal void RaiseCandleClosed(OHLCV closed, OHLCV newOpen)
    {
        if (_isDisposed)
        {
            return;
        }

        CandleClosed?.Invoke(closed, newOpen);
    }

    /// <summary> Notifies listeners about newly loaded older candles. </summary>
    internal void RaiseOlderCandlesLoaded(OHLCV[] candles)
    {
        if (_isDisposed || candles == null || candles.Length == 0)
        {
            return;
        }

        OlderCandlesLoaded?.Invoke(candles);
    }

    /// <summary> Disposes subscription and removes it from service registry. </summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _onDispose?.Invoke(Key);
    }
}
