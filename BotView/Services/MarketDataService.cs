using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BotView.Interfaces;
using BotView.Models;
using BotView.Configuration;
using BotView.Services;

public sealed class MarketDataService : IMarketDataService
{
    private readonly IExchangeService _exchangeService;
    private readonly CandleStore _store;
    private readonly TimeSpan _pollInterval;
    private readonly Dictionary<CandleCacheKey, HashSet<MarketDataSubscription>> _subscriptions = new();
    private readonly Dictionary<CandleCacheKey, MarketDataSubscription[]> _subscriberSnapshots = new();
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
        ct.ThrowIfCancellationRequested();

        if (initialHistory <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialHistory), "Initial history size must be greater than zero.");
        }

        var keyLock = GetOrCreateKeyLock(key);
        await keyLock.WaitAsync(ct);
        try
        {
            lock (_sync)
                ThrowIfDisposed();

            bool alreadyStreaming;
            lock (_sync)
                alreadyStreaming = _subscriptions.ContainsKey(key);
            if (!alreadyStreaming)
                await EnsureInitialHistoryCoreOrCatchUpAsync(key, initialHistory, ct);

            lock (_sync)
            {
                ThrowIfDisposed();
                ct.ThrowIfCancellationRequested();

                if (!_subscriptions.TryGetValue(key, out var subscribers))
                {
                    subscribers = new HashSet<MarketDataSubscription>();
                    _subscriptions[key] = subscribers;
                    var loopCts = new CancellationTokenSource();
                    _realtimeLoops[key] = loopCts;
                    _ = Task.Run(() => RunRealtimeLoopAsync(key, loopCts.Token), loopCts.Token);
                }

                var created = new MarketDataSubscription(key, _store, RemoveSubscription);
                subscribers.Add(created);
                _subscriberSnapshots[key] = subscribers.ToArray();

                return created;
            }
        }
        catch
        {
            EvictIfUnused(key);
            throw;
        }
        finally
        {
            keyLock.Release();
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
        OHLCV[] addedBatch = Array.Empty<OHLCV>();
        int added = 0;
        try
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ThrowIfDisposed();
                if (!_subscriptions.ContainsKey(key))
                    return 0;
            }

            var oldestTimestamp = _store.GetOldestTimestamp(key);
            if (!oldestTimestamp.HasValue)
            {
                await EnsureInitialHistoryCoreAsync(key, count, ct);
                addedBatch = _store.GetSeries(key).GetSnapshot()
                    .Enumerate(long.MinValue, long.MaxValue)
                    .Select(item => item.Candle).ToArray();
                added = addedBatch.Length;
            }
            else
            {
                var timeframeMs = GetTimeframeMilliseconds(key.Timeframe);
                var since = Math.Max(0, oldestTimestamp.Value - (timeframeMs * count));

                var fetched = await _exchangeService.FetchOHLCVAsync(
                    key.Exchange,
                    key.Symbol,
                    key.Timeframe,
                    since,
                    count,
                    ct);
                ct.ThrowIfCancellationRequested();

                if (fetched != null && fetched.Count > 0)
                {
                    lock (_sync)
                    {
                        ThrowIfDisposed();
                        if (!_subscriptions.ContainsKey(key))
                            return 0;
                    }
                    var converted = CandlestickDataConverter.ConvertFromCCXT(fetched, key.Timeframe);
                    var olderOnly = converted.candles
                        .Where(c => c.timestamp < oldestTimestamp.Value)
                        .OrderBy(c => c.timestamp)
                        .ToArray();

                    if (olderOnly.Length > 0)
                        added = _store.PrependHistory(key, olderOnly, out addedBatch);
                }
            }
        }
        finally
        {
            keyLock.Release();
        }

        if (added > 0)
            RaiseOlderCandlesLoaded(key, addedBatch);
        return added;
    }

    /// <summary>Returns the shared series without flattening its candle blocks.</summary>
    public ICandleSeriesReader GetSeries(CandleCacheKey key) => _store.GetSeries(key);

    /// <summary> Stops streams and releases retained candle histories. </summary>
    public async ValueTask DisposeAsync()
    {
        CandleCacheKey[] keys;
        lock (_sync)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            foreach (var cts in _realtimeLoops.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _realtimeLoops.Clear();

            foreach (var subscription in _subscriptions.Values.SelectMany(set => set))
                subscription.Detach();
            _subscriptions.Clear();
            _subscriberSnapshots.Clear();

            keys = _keyLocks.Keys.ToArray();
        }

        // Wait for in-flight mutations before dropping the final store references.
        await Task.WhenAll(keys.Select(EvictWhenIdleAsync)).ConfigureAwait(false);
    }

    /// <summary> Removes subscription from registry when it gets disposed. </summary>
    private void RemoveSubscription(MarketDataSubscription subscription)
    {
        lock (_sync)
        {
            var key = subscription.Key;
            if (!_subscriptions.TryGetValue(key, out var subscribers) || !subscribers.Remove(subscription))
                return;
            if (subscribers.Count > 0)
            {
                _subscriberSnapshots[key] = subscribers.ToArray();
                return;
            }
            _subscriptions.Remove(key);
            _subscriberSnapshots.Remove(key);

            if (_realtimeLoops.TryGetValue(key, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _realtimeLoops.Remove(key);
            }
            // Eviction waits for any in-flight history mutation to finish.
            _ = EvictWhenIdleAsync(key);
        }
    }

    private async Task EvictWhenIdleAsync(CandleCacheKey key)
    {
        var gate = GetOrCreateKeyLock(key);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EvictIfUnused(key);
        }
        finally
        {
            gate.Release();
        }
    }

    private void EvictIfUnused(CandleCacheKey key)
    {
        lock (_sync)
        {
            if (_subscriptions.ContainsKey(key))
                return;
            _store.Clear(key);
            _exchangeService.EvictCandlestickData(key.Exchange, key.Symbol, key.Timeframe, key.Limit);
        }
    }

    /// <summary> Polls exchange for latest candles and raises subscription events. </summary>
    private async Task RunRealtimeLoopAsync(CandleCacheKey key, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Fetch a small window so the previous live candle can receive its final values.
                var fetched = await _exchangeService.FetchOHLCVAsync(
                    key.Exchange,
                    key.Symbol,
                    key.Timeframe,
                    since: null,
                    limit: 3,
                    ct: ct).ConfigureAwait(false);

                if (fetched != null && fetched.Count > 0)
                {
                    var recent = CandlestickDataConverter.ConvertFromCCXT(fetched, key.Timeframe)
                        .candles
                        .OrderBy(c => c.timestamp)
                        .ToArray();
                    var notifications = new List<(LiveCandleChange Change, OHLCV Closed, OHLCV Current)>();
                    var keyLock = GetOrCreateKeyLock(key);
                    await keyLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var beforeCatchUp = _store.GetSeries(key).GetSnapshot();
                        var currentLive = beforeCatchUp.Live;
                        if (currentLive.HasValue && recent.Length > 0 &&
                            recent[^1].timestamp - currentLive.Value.timestamp >
                            GetTimeframeMilliseconds(key.Timeframe) * recent.Length)
                        {
                            // Fill a gap larger than the recent polling window before publishing ticks.
                            await EnsureRightEdgeCoreAsync(key, ct).ConfigureAwait(false);
                            var afterCatchUp = _store.GetSeries(key).GetSnapshot();
                            if (afterCatchUp.ClosedCount > beforeCatchUp.ClosedCount && afterCatchUp.Live.HasValue)
                                notifications.Add((LiveCandleChange.Closed,
                                    afterCatchUp[afterCatchUp.ClosedCount - 1], afterCatchUp.Live.Value));
                        }
                        foreach (var candle in recent)
                        {
                            ct.ThrowIfCancellationRequested();
                            var change = _store.UpdateLive(key, candle, out var closed);
                            if (change != LiveCandleChange.Stale)
                                notifications.Add((change, closed, candle));
                        }
                    }
                    finally
                    {
                        keyLock.Release();
                    }


                    // Subscriber callbacks run only after the series and key locks are released.
                    foreach (var (change, closed, current) in notifications)
                    {
                        if (change == LiveCandleChange.Closed)
                            RaiseCandleClosed(key, closed, current);
                        else
                            RaiseLiveCandleTicked(key, current);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Keep polling after a transient fetch or conversion failure.
                Debug.WriteLine($"Realtime loop error for {key.Symbol}: {ex.Message}");
            }

            try
            {
                // Pace requests and let cancellation stop the loop during the wait.
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary> Ensures a key has initial historical candles in store. </summary>
    private async Task EnsureInitialHistoryCoreOrCatchUpAsync(CandleCacheKey key, int initialHistory, CancellationToken ct)
    {
        if (_store.GetSeries(key).GetSnapshot().Count == 0)
        {
            await EnsureInitialHistoryCoreAsync(key, initialHistory, ct);
        }
        else
        {
            await EnsureRightEdgeCoreAsync(key, ct);
        }
    }

    /// <summary> Loads initial batch from exchange if series is empty. </summary>
    private async Task EnsureInitialHistoryCoreAsync(CandleCacheKey key, int initialHistory, CancellationToken ct)
    {
        if (_store.GetSeries(key).GetSnapshot().Count > 0)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        var initial = await _exchangeService.GetCandlestickDataAsync(
            key.Exchange,
            key.Symbol,
            key.Timeframe,
            initialHistory,
            ct);
        ct.ThrowIfCancellationRequested();

        if (initial.candles == null || initial.candles.Length == 0)
        {
            return;
        }

        _store.LoadInitial(key, initial.candles);
    }

    /// <summary> Catches cached history up to the newest closed candle and current live candle. </summary>
    private async Task EnsureRightEdgeCoreAsync(CandleCacheKey key, CancellationToken ct)
    {
        var snapshot = _store.GetSeries(key).GetSnapshot();
        if (snapshot.Count == 0)
            return;

        ct.ThrowIfCancellationRequested();
        var latestFetched = await _exchangeService.FetchOHLCVAsync(
            key.Exchange,
            key.Symbol,
            key.Timeframe,
            since: null,
            limit: 3,
            ct: ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (latestFetched == null || latestFetched.Count == 0)
        {
            return;
        }

        var latestCandles = CandlestickDataConverter.ConvertFromCCXT(latestFetched, key.Timeframe)
            .candles.OrderBy(c => c.timestamp).ToArray();
        var latest = latestCandles[^1];
        if (snapshot.Live.HasValue && snapshot.Live.Value.timestamp >= latest.timestamp)
        {
            _store.UpdateLive(key, latest, out _);
            return;
        }

        var timeframeMs = GetTimeframeMilliseconds(key.Timeframe);
        var pageLimit = Math.Max(2, key.Limit);
        var since = snapshot.Live?.timestamp
            ?? (snapshot.NewestClosedTimestamp ?? 0) + timeframeMs;

        while (!ct.IsCancellationRequested)
        {
            var fetched = await _exchangeService.FetchOHLCVAsync(
                key.Exchange,
                key.Symbol,
                key.Timeframe,
                since,
                pageLimit,
                ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (fetched == null || fetched.Count == 0)
            {
                break;
            }

            var candles = CandlestickDataConverter.ConvertFromCCXT(fetched, key.Timeframe)
                .candles
                .Where(c => c.timestamp >= since && c.timestamp <= latest.timestamp)
                .OrderBy(c => c.timestamp)
                .ToArray();

            if (candles.Length == 0)
                break;

            foreach (var candle in candles)
                _store.UpdateLive(key, candle, out _);

            var last = candles[^1];
            if (last.timestamp >= latest.timestamp)
                break;
            since = last.timestamp + timeframeMs;

            if (candles.Length < pageLimit)
                break;
        }

        foreach (var candle in latestCandles)
            _store.UpdateLive(key, candle, out _);
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
    private MarketDataSubscription[] GetSubscriptions(CandleCacheKey key)
    {
        lock (_sync)
        {
            return _subscriberSnapshots.TryGetValue(key, out var subscribers)
                ? subscribers
                : Array.Empty<MarketDataSubscription>();
        }
    }

    /// <summary> Raises OlderCandlesLoaded event for active subscription when available. </summary>
    private void RaiseOlderCandlesLoaded(CandleCacheKey key, OHLCV[] candles)
    {
        foreach (var subscriber in GetSubscriptions(key))
        {
            try
            {
                subscriber.RaiseOlderCandlesLoaded(candles);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Subscriber callback error for {key.Symbol}: {ex.Message}");
            }
        }
    }

    private void RaiseLiveCandleTicked(CandleCacheKey key, OHLCV candle)
    {
        foreach (var subscriber in GetSubscriptions(key))
        {
            try
            {
                subscriber.RaiseLiveCandleTicked(candle);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Subscriber callback error for {key.Symbol}: {ex.Message}");
            }
        }
    }

    private void RaiseCandleClosed(CandleCacheKey key, OHLCV closed, OHLCV next)
    {
        foreach (var subscriber in GetSubscriptions(key))
        {
            try
            {
                subscriber.RaiseCandleClosed(closed, next);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Subscriber callback error for {key.Symbol}: {ex.Message}");
            }
        }
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
        return (long)MarketCatalog.GetTimeframe(timeframe).Duration.TotalMilliseconds;
    }
}
