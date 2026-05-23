using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly LiveCandleTracker _liveTracker = new();
    private readonly TimeSpan _pollInterval;
    private readonly Dictionary<CandleCacheKey, MarketDataSubscription> _subscriptions = new();
    private readonly Dictionary<CandleCacheKey, SemaphoreSlim> _keyLocks = new();
    private readonly Dictionary<CandleCacheKey, CancellationTokenSource> _realtimeLoops = new();
    private readonly object _sync = new();
    private bool _isDisposed;

    /// <summary> Creates market data service backed by exchange service and shared candle store. </summary>
    public MarketDataService(IExchangeService exchangeService, CandleStore? store = null, TimeSpan? pollInterval = null)
    {
        _exchangeService = exchangeService ?? throw new ArgumentNullException(nameof(exchangeService));
        _store = store ?? new CandleStore();
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
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

            var loopCts = new CancellationTokenSource();
            _realtimeLoops[key] = loopCts;
            _ = Task.Run(() => RunRealtimeLoopAsync(key, loopCts.Token), loopCts.Token);

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

    /// <summary> Builds chart snapshot from closed history and current live candle. </summary>
    public CandlestickData BuildChartData(CandleCacheKey key)
    {
        var closed = _store.GetRange(key, 0, long.MaxValue).ToArray();
        var live = _liveTracker.Get(key);

        OHLCV[] all;
        if (live.HasValue && (closed.Length == 0 || live.Value.timestamp > closed[^1].timestamp))
        {
            all = closed.Length == 0
                ? new[] { live.Value }
                : closed.Concat(new[] { live.Value }).ToArray();
        }
        else if (live.HasValue && closed.Length > 0 && live.Value.timestamp == closed[^1].timestamp)
        {
            all = closed.ToArray();
            all[^1] = live.Value;
        }
        else
        {
            all = closed;
        }

        if (all.Length == 0)
        {
            var now = DateTime.UtcNow;
            return new CandlestickData(key.Timeframe, now, now, Array.Empty<OHLCV>());
        }

        return new CandlestickData(
            key.Timeframe,
            all[0].GetDateTime(),
            all[^1].GetDateTime(),
            all);
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

            foreach (var cts in _realtimeLoops.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _realtimeLoops.Clear();

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
        }

        return ValueTask.CompletedTask;
    }

    /// <summary> Removes subscription from registry when it gets disposed. </summary>
    private void RemoveSubscription(CandleCacheKey key)
    {
        lock (_sync)
        {
            _subscriptions.Remove(key);

            if (_realtimeLoops.TryGetValue(key, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _realtimeLoops.Remove(key);
            }

            _liveTracker.Remove(key);
        }
    }

    /// <summary> Polls exchange for latest candles and raises subscription events. </summary>
    private async Task RunRealtimeLoopAsync(CandleCacheKey key, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var fetched = await _exchangeService.FetchOHLCVAsync(
                    key.Exchange,
                    key.Symbol,
                    key.Timeframe,
                    since: null,
                    limit: 3).ConfigureAwait(false);

                if (fetched != null && fetched.Count > 0)
                {
                    var last = CandlestickDataConverter.ConvertFromCCXT(fetched, key.Timeframe)
                        .candles
                        .OrderBy(c => c.timestamp)
                        .Last();
                    var currentLive = _liveTracker.Get(key);

                    if (currentLive.HasValue && last.timestamp < currentLive.Value.timestamp)
                    {
                        Debug.WriteLine($"Skipping stale live candle for {key.Symbol}: {last.timestamp} < {currentLive.Value.timestamp}");
                    }
                    else if (_liveTracker.TryClose(key, last, out var closed))
                    {
                        _store.AppendClosed(key, new[] { closed });
                        GetSubscription(key)?.RaiseCandleClosed(closed, last);
                    }
                    else
                    {
                        _liveTracker.Update(key, last);
                        GetSubscription(key)?.RaiseLiveCandleTicked(last);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Realtime loop error for {key.Symbol}: {ex.Message}");
            }

            try
            {
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary> Ensures a key has initial historical candles in store. </summary>
    private async Task EnsureInitialHistoryAsync(CandleCacheKey key, int initialHistory, CancellationToken ct)
    {
        if (_store.GetCount(key) > 0 && _liveTracker.Get(key).HasValue)
        {
            return;
        }

        var keyLock = GetOrCreateKeyLock(key);
        await keyLock.WaitAsync(ct);
        try
        {
            if (_store.GetCount(key) == 0 && !_liveTracker.Get(key).HasValue)
            {
                await EnsureInitialHistoryCoreAsync(key, initialHistory, ct);
            }
            else
            {
                await EnsureRightEdgeCoreAsync(key, ct);
            }
        }
        finally
        {
            keyLock.Release();
        }
    }

    /// <summary> Loads initial batch from exchange if series is empty. </summary>
    private async Task EnsureInitialHistoryCoreAsync(CandleCacheKey key, int initialHistory, CancellationToken ct)
    {
        if (_store.GetCount(key) > 0 || _liveTracker.Get(key).HasValue)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        var initial = await _exchangeService.GetCandlestickDataAsync(
            key.Exchange,
            key.Symbol,
            key.Timeframe,
            initialHistory);

        if (initial.candles == null || initial.candles.Length == 0)
        {
            return;
        }

        if (initial.candles.Length == 1)
        {
            _liveTracker.Update(key, initial.candles[0]);
            return;
        }

        var closed = initial.candles.Take(initial.candles.Length - 1).ToArray();
        var live = initial.candles[^1];

        _store.AppendClosed(key, closed);
        _liveTracker.Update(key, live);
    }

    /// <summary> Catches cached history up to the newest closed candle and current live candle. </summary>
    private async Task EnsureRightEdgeCoreAsync(CandleCacheKey key, CancellationToken ct)
    {
        var newestClosed = _store.GetNewestTimestamp(key);
        if (!newestClosed.HasValue)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();

        var latestFetched = await _exchangeService.FetchOHLCVAsync(
            key.Exchange,
            key.Symbol,
            key.Timeframe,
            since: null,
            limit: 3).ConfigureAwait(false);

        if (latestFetched == null || latestFetched.Count == 0)
        {
            return;
        }

        var latestCandles = CandlestickDataConverter.ConvertFromCCXT(latestFetched, key.Timeframe)
            .candles
            .OrderBy(c => c.timestamp)
            .ToArray();
        var live = latestCandles[^1];

        if (newestClosed.Value >= live.timestamp)
        {
            _liveTracker.Update(key, live);
            return;
        }

        var timeframeMs = GetTimeframeMilliseconds(key.Timeframe);
        var pageLimit = Math.Max(2, key.Limit);
        var since = newestClosed.Value + timeframeMs;

        while (!ct.IsCancellationRequested)
        {
            var fetched = await _exchangeService.FetchOHLCVAsync(
                key.Exchange,
                key.Symbol,
                key.Timeframe,
                since,
                pageLimit).ConfigureAwait(false);

            if (fetched == null || fetched.Count == 0)
            {
                break;
            }

            var candles = CandlestickDataConverter.ConvertFromCCXT(fetched, key.Timeframe)
                .candles
                .Where(c => c.timestamp > newestClosed.Value && c.timestamp <= live.timestamp)
                .OrderBy(c => c.timestamp)
                .ToArray();

            if (candles.Length == 0)
            {
                break;
            }

            var closed = candles
                .Where(c => c.timestamp < live.timestamp)
                .ToArray();
            if (closed.Length > 0)
            {
                _store.AppendClosed(key, closed);
            }

            var last = candles[^1];
            if (last.timestamp >= live.timestamp)
            {
                _liveTracker.Update(key, live);
                break;
            }

            newestClosed = _store.GetNewestTimestamp(key) ?? last.timestamp;
            since = last.timestamp + timeframeMs;

            if (candles.Length < pageLimit)
            {
                _liveTracker.Update(key, live);
                break;
            }
        }

        if (!_liveTracker.Get(key).HasValue)
        {
            _liveTracker.Update(key, live);
        }
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

    /// <summary> Returns active subscription for the specified key. </summary>
    private MarketDataSubscription? GetSubscription(CandleCacheKey key)
    {
        lock (_sync)
        {
            _subscriptions.TryGetValue(key, out var subscription);
            return subscription;
        }
    }

    /// <summary> Raises OlderCandlesLoaded event for active subscription when available. </summary>
    private void RaiseOlderCandlesLoaded(CandleCacheKey key, OHLCV[] candles)
    {
        GetSubscription(key)?.RaiseOlderCandlesLoaded(candles);
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
