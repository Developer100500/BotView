using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BotView.Interfaces;
using BotView.Models;
using BotView.Services;

public sealed class MarketDataService : IMarketDataService
{
    private readonly IExchangeService _exchangeService;
    private readonly CandleStore _store;
    private readonly Dictionary<string, ExchangeService> _exchangeInstances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CandleCacheKey, MarketDataSubscription> _subscriptions = new();
    private readonly Dictionary<CandleCacheKey, SemaphoreSlim> _keyLocks = new();
    private readonly object _sync = new();
    private bool _isDisposed;

    /// <summary> Creates market data service backed by exchange service and shared candle store. </summary>
    public MarketDataService(IExchangeService exchangeService, CandleStore? store = null)
    {
        _exchangeService = exchangeService ?? throw new ArgumentNullException(nameof(exchangeService));
        _store = store ?? new CandleStore();
    }

    /// <summary> Subscribes to key and guarantees initial history is loaded. </summary>
    public async Task<IMarketDataSubscription> SubscribeAsync(
        CandleCacheKey key,
        int initialHistory = 500,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ValidateKey(key);

        if (initialHistory <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialHistory), "Initial history size must be greater than zero.");
        }

        await EnsureInitialHistoryAsync(key, initialHistory, ct);

        lock (_sync)
        {
            ThrowIfDisposed();

            if (_subscriptions.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var created = new MarketDataSubscription(key, _store, RemoveSubscription);
            _subscriptions[key] = created;
            return created;
        }
    }

    /// <summary> Loads candles older than current left edge for the specified stream key. </summary>
    public async Task<int> LoadOlderAsync(CandleCacheKey key, int count, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ValidateKey(key);

        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be greater than zero.");
        }

        var keyLock = GetOrCreateKeyLock(key);
        await keyLock.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();

            var oldestTimestamp = _store.GetOldestTimestamp(key);
            if (!oldestTimestamp.HasValue)
            {
                await EnsureInitialHistoryCoreAsync(key, count, ct);
                return _store.GetCount(key);
            }

            var timeframeMs = GetTimeframeMilliseconds(key.Timeframe);
            var since = Math.Max(0, oldestTimestamp.Value - (timeframeMs * count));

            var fetched = await _exchangeService.FetchOHLCVAsync(
                key.Exchange,
                key.Symbol,
                key.Timeframe,
                since,
                count);

            if (fetched == null || fetched.Count == 0)
            {
                return 0;
            }

            var converted = CandlestickDataConverter.ConvertFromCCXT(fetched, key.Timeframe);
            var olderOnly = converted.candles
                .Where(c => c.timestamp < oldestTimestamp.Value)
                .OrderBy(c => c.timestamp)
                .ToArray();

            if (olderOnly.Length == 0)
            {
                return 0;
            }

            var added = _store.PrependHistory(key, olderOnly);
            if (added <= 0)
            {
                return 0;
            }

            var addedBatch = olderOnly.TakeLast(added).ToArray();
            RaiseOlderCandlesLoaded(key, addedBatch);
            return added;
        }
        finally
        {
            keyLock.Release();
        }
    }

    /// <summary> Disposes service and releases internal synchronization resources. </summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_isDisposed)
            {
                return ValueTask.CompletedTask;
            }

            _isDisposed = true;

            foreach (var subscription in _subscriptions.Values)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();

            foreach (var gate in _keyLocks.Values)
            {
                gate.Dispose();
            }

            _keyLocks.Clear();
            _exchangeInstances.Clear();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary> Removes subscription from registry when it gets disposed. </summary>
    private void RemoveSubscription(CandleCacheKey key)
    {
        lock (_sync)
        {
            _subscriptions.Remove(key);
        }
    }

    /// <summary> Ensures a key has initial historical candles in store. </summary>
    private async Task EnsureInitialHistoryAsync(CandleCacheKey key, int initialHistory, CancellationToken ct)
    {
        if (_store.GetCount(key) > 0)
        {
            return;
        }

        var keyLock = GetOrCreateKeyLock(key);
        await keyLock.WaitAsync(ct);
        try
        {
            await EnsureInitialHistoryCoreAsync(key, initialHistory, ct);
        }
        finally
        {
            keyLock.Release();
        }
    }

    /// <summary> Loads initial batch from exchange if series is empty. </summary>
    private async Task EnsureInitialHistoryCoreAsync(CandleCacheKey key, int initialHistory, CancellationToken ct)
    {
        if (_store.GetCount(key) > 0)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        var initial = await _exchangeService.GetCandlestickDataAsync(key.Exchange, key.Symbol, key.Timeframe, initialHistory);
        if (initial.candles == null || initial.candles.Length == 0)
        {
            return;
        }

        _store.AppendClosed(key, initial.candles);
    }

    /// <summary> Returns existing semaphore for key or creates a new one. </summary>
    private SemaphoreSlim GetOrCreateKeyLock(CandleCacheKey key)
    {
        lock (_sync)
        {
            if (_keyLocks.TryGetValue(key, out var gate))
            {
                return gate;
            }

            gate = new SemaphoreSlim(1, 1);
            _keyLocks[key] = gate;
            return gate;
        }
    }

    /// <summary> Raises OlderCandlesLoaded event for active subscription when available. </summary>
    private void RaiseOlderCandlesLoaded(CandleCacheKey key, OHLCV[] candles)
    {
        MarketDataSubscription? subscription;
        lock (_sync)
        {
            _subscriptions.TryGetValue(key, out subscription);
        }

        subscription?.RaiseOlderCandlesLoaded(candles);
    }

    /// <summary> Validates stream key for required values. </summary>
    private static void ValidateKey(CandleCacheKey key)
    {
        if (string.IsNullOrWhiteSpace(key.Exchange))
        {
            throw new ArgumentException("Exchange cannot be empty.", nameof(key));
        }

        if (string.IsNullOrWhiteSpace(key.Symbol))
        {
            throw new ArgumentException("Symbol cannot be empty.", nameof(key));
        }

        if (string.IsNullOrWhiteSpace(key.Timeframe))
        {
            throw new ArgumentException("Timeframe cannot be empty.", nameof(key));
        }
    }

    /// <summary> Throws if service was already disposed. </summary>
    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(MarketDataService));
        }
    }

    /// <summary> Converts timeframe string into milliseconds duration. </summary>
    private static long GetTimeframeMilliseconds(string timeframe)
    {
        return timeframe switch
        {
            "1m" => 60_000L,
            "5m" => 300_000L,
            "15m" => 900_000L,
            "30m" => 1_800_000L,
            "1h" => 3_600_000L,
            "4h" => 14_400_000L,
            "1d" => 86_400_000L,
            "1w" => 604_800_000L,
            _ => throw new NotSupportedException($"Unsupported timeframe '{timeframe}'.")
        };
    }
}
