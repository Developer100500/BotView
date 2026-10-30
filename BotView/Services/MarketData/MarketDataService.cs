using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BotView.Models;
using BotView.Configuration;
using BotView.Services;

namespace BotView.Services.MarketData;

public sealed class MarketDataService : IMarketDataService
{
    private readonly IExchangeService _exchangeService;
    private readonly CandleStore _store;
    private readonly TimeSpan _reconnectDelay;
    private readonly Dictionary<CandleCacheKey, HashSet<MarketDataSubscription>> _subscriptions = new();
    private readonly Dictionary<CandleCacheKey, MarketDataSubscription[]> _subscriberSnapshots = new();
    private readonly Dictionary<CandleCacheKey, SemaphoreSlim> _keyLocks = new();
    private readonly Dictionary<CandleCacheKey, CancellationTokenSource> _realtimeLoops = new();
    private readonly HashSet<Task> _workers = new();
    private readonly object _sync = new();
    private bool _isDisposed;

    /// <summary> Creates market data service backed by exchange service and shared candle store. </summary>
    public MarketDataService(IExchangeService exchangeService, CandleStore? store = null, TimeSpan? reconnectDelay = null)
    {
        _exchangeService = exchangeService ?? throw new ArgumentNullException(nameof(exchangeService));
        _store = store ?? new CandleStore();
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(5);
        if (_reconnectDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(reconnectDelay));
    }

    /// <summary>The first subscriber loads initial history; later subscribers share that series and stream.</summary>
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
                    var worker = Task.Run(() => RunRealtimeLoopAsync(key, loopCts.Token));
                    _workers.Add(worker);
                    _ = worker.ContinueWith(completed =>
                    {
                        loopCts.Dispose();
                        lock (_sync) _workers.Remove(completed);
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
        Candle[] addedBatch = Array.Empty<Candle>();
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

                var fetched = await _exchangeService.GetCandlesAsync(
                    key.Exchange,
                    key.Symbol,
                    key.Timeframe,
                    DateTimeOffset.FromUnixTimeMilliseconds(oldestTimestamp.Value),
                    count,
                    ct);
                ct.ThrowIfCancellationRequested();

                if (fetched != null && fetched.Length > 0)
                {
                    lock (_sync)
                    {
                        ThrowIfDisposed();
                        if (!_subscriptions.ContainsKey(key))
                            return 0;
                    }
                    var olderOnly = fetched
                        .Where(c => c.Timestamp < oldestTimestamp.Value)
                        .OrderBy(c => c.Timestamp)
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
        Task[] workers;
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
            }

            _realtimeLoops.Clear();

            foreach (var subscription in _subscriptions.Values.SelectMany(set => set))
                subscription.Detach();
            _subscriptions.Clear();
            _subscriberSnapshots.Clear();

            keys = _keyLocks.Keys.ToArray();
            workers = _workers.ToArray();
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
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
            _exchangeService.EvictCandlestickData(key.Exchange, key.Symbol, key.Timeframe);
        }
    }

    /// <summary>Consumes the provider's snapshots; transport and polling belong to the adapter.</summary>
    private async Task RunRealtimeLoopAsync(CandleCacheKey key, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var update in _exchangeService.WatchCandlesAsync(
                    key.Exchange, key.Symbol, key.Timeframe, ct).WithCancellation(ct).ConfigureAwait(false))
                {
                    var notifications = new List<(bool Closed, Candle Candle)>();
                    var gate = GetOrCreateKeyLock(key);
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        var snapshot = _store.GetSeries(key).GetSnapshot();
                        var known = snapshot.Count > 0 ? snapshot[snapshot.Count - 1].Timestamp : (long?)null;
                        var timeframeMs = GetTimeframeMilliseconds(key.Timeframe);
                        if (known.HasValue && update.Candle.Timestamp > known.Value &&
                            (snapshot.Live.HasValue || update.Candle.Timestamp - known.Value > timeframeMs))
                        {
                            var closedBefore = snapshot.ClosedCount;
                            await CatchUpBeforeAsync(key, known.Value, update.Candle.Timestamp, ct).ConfigureAwait(false);
                            var after = _store.GetSeries(key).GetSnapshot();
                            if (after.ClosedCount > closedBefore)
                                notifications.Add((true, after[after.ClosedCount - 1]));
                        }
                        ct.ThrowIfCancellationRequested();
                        var change = _store.UpdateLive(key, update.Candle, out var closed);
                        if (change == LiveCandleChange.Closed) notifications.Add((true, closed));
                        if (change != LiveCandleChange.Stale)
                        {
                            if (update.IsClosed && _store.GetOrCreateSeries(key).FinalizeLive(update.Candle.Timestamp))
                                notifications.Add((true, update.Candle));
                            else notifications.Add((false, update.Candle));
                        }
                    }
                    finally { gate.Release(); }
                    foreach (var (closed, candle) in notifications)
                    {
                        if (closed) RaiseCandleClosed(key, candle);
                        else RaiseLiveCandleTicked(key, candle);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Debug.WriteLine($"Market stream error for {key.Symbol}: {ex.Message}");
            }
            try { await Task.Delay(_reconnectDelay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
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

        if (initial == null || initial.Length == 0)
        {
            return;
        }

        _store.LoadInitial(key, initial);
    }

    /// <summary>Brings a retained series up to date without replacing its existing history.</summary>
    private async Task EnsureRightEdgeCoreAsync(CandleCacheKey key, CancellationToken ct)
    {
        var snapshot = _store.GetSeries(key).GetSnapshot();
        if (snapshot.Count == 0) return;
        var recent = await _exchangeService.GetCandlesAsync(key.Exchange, key.Symbol, key.Timeframe,
            null, 3, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (recent.Length == 0) return;
        var known = snapshot[snapshot.Count - 1].Timestamp;
        await CatchUpBeforeAsync(key, known, recent[^1].Timestamp, ct).ConfigureAwait(false);
        foreach (var candle in recent) _store.UpdateLive(key, candle, out _);
    }

    /// <summary>Walks backwards to the known right edge. Trading breaks are not missing bars.</summary>
    private async Task CatchUpBeforeAsync(CandleCacheKey key, long knownTimestamp, long beforeExclusive, CancellationToken ct)
    {
        if (beforeExclusive <= knownTimestamp) return;
        var collected = new SortedDictionary<long, Candle>();
        var before = beforeExclusive;
        while (before > knownTimestamp)
        {
            var candles = await _exchangeService.GetCandlesAsync(key.Exchange, key.Symbol, key.Timeframe,
                DateTimeOffset.FromUnixTimeMilliseconds(before), CandleSeries.CandleChunkSize, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var earlier = candles.Where(c => c.Timestamp < before).OrderBy(c => c.Timestamp).ToArray();
            if (earlier.Length == 0) break;
            foreach (var candle in earlier.Where(c => c.Timestamp >= knownTimestamp))
                collected[candle.Timestamp] = candle;
            before = earlier[0].Timestamp;
        }
        ct.ThrowIfCancellationRequested();
        foreach (var candle in collected.Values) _store.UpdateLive(key, candle, out _);
    }

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
    private void RaiseOlderCandlesLoaded(CandleCacheKey key, Candle[] candles)
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

    private void RaiseLiveCandleTicked(CandleCacheKey key, Candle candle)
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

    private void RaiseCandleClosed(CandleCacheKey key, Candle closed)
    {
        foreach (var subscriber in GetSubscriptions(key))
        {
            try
            {
                subscriber.RaiseCandleClosed(closed);
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
        return MarketCatalog.TryGetTimeframe(timeframe, out var definition)
            ? (long)definition!.Duration.TotalMilliseconds : 0;
    }
}
