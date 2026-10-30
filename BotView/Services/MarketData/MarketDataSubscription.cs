using System;
using BotView.Models;
using BotView.Services;

namespace BotView.Services.MarketData;

public sealed class MarketDataSubscription : IMarketDataSubscription
{
    private readonly Action<MarketDataSubscription>? _onDispose;
    private bool _isDisposed;

    public CandleCacheKey Key { get; }
    public ICandleSeriesReader Series { get; }

    public event Action<Candle>? LiveCandleTicked;
    public event Action<Candle>? CandleClosed;
    public event Action<Candle[]>? OlderCandlesLoaded;

    /// <summary> Creates subscription wrapper over shared candle store. </summary>
    public MarketDataSubscription(CandleCacheKey key, CandleStore store, Action<MarketDataSubscription>? onDispose = null)
    {
        Key = key;
        ArgumentNullException.ThrowIfNull(store);
        Series = store.GetSeries(key);
        _onDispose = onDispose;
    }

    /// <summary> Notifies listeners that current live candle was updated. </summary>
    internal void RaiseLiveCandleTicked(Candle candle)
    {
        if (_isDisposed)
        {
            return;
        }

        LiveCandleTicked?.Invoke(candle);
    }

    /// <summary>Notifies listeners that a candle closed, including a close before a trading break.</summary>
    internal void RaiseCandleClosed(Candle closed)
    {
        if (_isDisposed)
        {
            return;
        }

        CandleClosed?.Invoke(closed);
    }

    /// <summary> Notifies listeners about newly loaded older candles. </summary>
    internal void RaiseOlderCandlesLoaded(Candle[] candles)
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
        _onDispose?.Invoke(this);
    }

    internal void Detach() => _isDisposed = true;
}
