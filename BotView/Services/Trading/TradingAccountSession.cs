using BotView.Models;

namespace BotView.Services.Trading;

/// <summary>Application-owned display state and private subscriptions for one profile. No trading recovery policy.</summary>
public sealed class TradingAccountSession : IAsyncDisposable
{
    private readonly ITradingService _service;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _tracking = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<InstrumentKey, OrderInputCalculator> _orderInputs = new();
    private readonly Dictionary<OrderReference, OrderSnapshot> _orders = new();
    private readonly Dictionary<(OrderReference Order, string Id), ExecutionSnapshot> _executions = new();
    private readonly Dictionary<InstrumentKey, IReadOnlyList<PositionSnapshot>> _positions = new();
    private readonly Dictionary<InstrumentKey, bool?> _positionModes = new();
    private readonly List<Task> _workers = new();
    private readonly Dictionary<(TradingStream Stream, InstrumentKey? Key), Resource> _resources = new();
    private readonly TimeSpan _pollInterval;
    private AccountSnapshot? _balance;
    private string _message = "Connecting account…";
    private bool _connected;
    private bool _disposed;
    public TradingAccountContext Account => _service.Account;

    /// <summary>Owns the adapter; an injectable polling interval keeps session tests deterministic and fast.</summary>
    public TradingAccountSession(ITradingService service, TimeSpan? pollInterval = null)
    {
        _service = service;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Copies the shared display collections without exposing mutable caches.</summary>
    public TradingAccountState Current
    {
        get
        {
            lock (_sync)
            {
                var balance = _balance is null ? null : _balance with { Positions = _positions.Values.SelectMany(p => p).ToArray() };
                var message = _resources.Values.Select(r => r.Error).FirstOrDefault(e => e is not null) ?? _message;
                return new(message, _orders.Values.ToArray(), _executions.Values.OrderByDescending(e => e.ExecutedAt).Take(100).ToArray(),
                    balance, _connected && !_stop.IsCancellationRequested);
            }
        }
    }

    /// <summary>Loads precision metadata and starts each private resource only once per tracked instrument.</summary>
    public async Task TrackAsync(InstrumentKey key, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await _tracking.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_sync) if (_orderInputs.ContainsKey(key)) return;
            var instrument = await _service.FetchInstrumentAsync(key, linked.Token).ConfigureAwait(false);
            // Authentication is independent of order/execution endpoint availability.
            var balance = await _service.FetchBalanceAsync(linked.Token).ConfigureAwait(false);
            bool? hedged = null;
            if (Account.Market == TradingMarket.UsdtFutures)
            {
                try
                {
                    hedged = await _service.FetchPositionModeAsync(key, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.Token.IsCancellationRequested) { throw; }
                catch (Exception ex) { Report(ex); }
            }
            lock (_sync)
            {
                var first = _orderInputs.Count == 0;
                _orderInputs[key] = new OrderInputCalculator(_service, instrument);
                _balance = balance;
                _connected = true;
                if (Account.Market == TradingMarket.UsdtFutures) _positionModes[key] = hedged;
                if (_message == "Connecting account…") _message = "Connected";
                StartResource(TradingStream.Orders, key);
                StartResource(TradingStream.MyTrades, key);
                if (first) StartResource(TradingStream.Balance, key);
                if (Account.Market == TradingMarket.UsdtFutures) _workers.Add(Task.Run(() => PollPositionsAsync(key)));
            }
        }
        finally { _tracking.Release(); }
    }

    /// <summary>Starts independent watchers and fallback polling for a single display resource.</summary>
    private void StartResource(TradingStream stream, InstrumentKey key)
    {
        var resource = new Resource();
        _resources[(stream, stream == TradingStream.Balance ? null : key)] = resource;
        _workers.Add(Task.Run(() => PollAsync(stream, key, resource)));
        if (_service.SupportsStream(stream)) _workers.Add(Task.Run(() => WatchAsync(stream, key, resource)));
    }

    /// <summary>Tracks stream health and prevents a slow REST snapshot from overwriting a newer stream batch.</summary>
    private sealed class Resource
    {
        public bool Healthy;
        public long Version;
        public string? Error;
    }

    /// <summary>Refreshes only when the corresponding stream is unsupported or disconnected.</summary>
    private async Task PollAsync(TradingStream stream, InstrumentKey key, Resource resource)
    {
        var initial = true;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                bool poll;
                lock (_sync) poll = initial || !resource.Healthy;
                if (poll) await SnapshotAsync(stream, key, resource).ConfigureAwait(false);
                initial = false;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Report(ex); }
            try { await Task.Delay(_pollInterval, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Reads one REST resource and publishes it unless a newer private event already arrived.</summary>
    private async Task SnapshotAsync(TradingStream stream, InstrumentKey key, Resource resource)
    {
        long version;
        lock (_sync) version = resource.Version;
        object result = stream switch
        {
            TradingStream.Orders => await _service.FetchOpenOrdersAsync(key, _stop.Token).ConfigureAwait(false),
            TradingStream.MyTrades => await _service.FetchMyTradesAsync(key, _stop.Token).ConfigureAwait(false),
            _ => await _service.FetchBalanceAsync(_stop.Token).ConfigureAwait(false)
        };
        lock (_sync)
        {
            if (version != resource.Version) return;
            Apply(stream, key, result, snapshot: true);
            resource.Version++;
            _message = "Connected";
        }
    }

    /// <summary>Restarts read-only streams and refreshes their snapshots after reconnection.</summary>
    private async Task WatchAsync(TradingStream stream, InstrumentKey key, Resource resource)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                object result = stream switch
                {
                    TradingStream.Orders => await _service.WatchOrdersAsync(key, _stop.Token).ConfigureAwait(false),
                    TradingStream.MyTrades => await _service.WatchMyTradesAsync(key, _stop.Token).ConfigureAwait(false),
                    _ => await _service.WatchBalanceAsync(_stop.Token).ConfigureAwait(false)
                };
                bool recovered;
                lock (_sync)
                {
                    recovered = !resource.Healthy;
                    resource.Healthy = true;
                    resource.Error = null;
                    resource.Version++;
                    Apply(stream, key, result, snapshot: false);
                }
                if (recovered) await SnapshotAsync(stream, key, resource).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    resource.Healthy = false;
                    resource.Error = $"{stream} stream unavailable; updating via REST. {TradingErrorText.FromException(ex)}";
                }
                Report(ex);
                try { await Task.Delay(_pollInterval, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Merges detached display data; terminal private events cannot reopen an order from an older batch.</summary>
    private void Apply(TradingStream stream, InstrumentKey key, object value, bool snapshot)
    {
        if (stream == TradingStream.Orders)
        {
            var orders = (IReadOnlyList<OrderSnapshot>)value;
            if (snapshot)
            {
                var ids = orders.Select(o => o.Reference).ToHashSet();
                foreach (var id in _orders.Where(p => p.Key.Instrument == key && !Terminal(p.Value.State) && !ids.Contains(p.Key))
                    .Select(p => p.Key).ToArray()) _orders.Remove(id);
            }
            foreach (var order in orders)
            {
                if (_orders.TryGetValue(order.Reference, out var old) &&
                    (old.UpdatedAt > order.UpdatedAt || Terminal(old.State) && !Terminal(order.State))) continue;
                _orders[order.Reference] = order;
            }
            foreach (var id in _orders.Values.Where(o => Terminal(o.State)).OrderByDescending(o => o.UpdatedAt)
                .Skip(500).Select(o => o.Reference).ToArray()) _orders.Remove(id);
        }
        else if (stream == TradingStream.MyTrades)
        {
            foreach (var fill in (IReadOnlyList<ExecutionSnapshot>)value) _executions[(fill.Order, fill.ExecutionId)] = fill;
            foreach (var id in _executions.OrderByDescending(p => p.Value.ExecutedAt).Skip(500).Select(p => p.Key).ToArray())
                _executions.Remove(id);
        }
        else _balance = (AccountSnapshot)value;
    }

    /// <summary>Refreshes positions independently when there is no dedicated private position stream.</summary>
    private async Task PollPositionsAsync(InstrumentKey key)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var hedged = await _service.FetchPositionModeAsync(key, _stop.Token).ConfigureAwait(false);
                var positions = await _service.FetchPositionsAsync(key, _stop.Token).ConfigureAwait(false);
                lock (_sync)
                {
                    _positionModes[key] = hedged;
                    _positions[key] = positions;
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Report(ex); }
            try { await Task.Delay(_pollInterval, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Publishes a safe display error while allowing supported operations to remain usable.</summary>
    private void Report(Exception error)
    {
        lock (_sync) _message = TradingErrorText.FromException(error);
    }

    /// <summary>Returns the last exchange position mode for this instrument. Null means it was not reported.</summary>
    public bool? GetHedged(InstrumentKey key)
    {
        lock (_sync)
        {
            return _positionModes.TryGetValue(key, out var hedged) ? hedged : null;
        }
    }

    /// <summary>Returns the input calculator created from the tracked instrument metadata.</summary>
    public OrderInputCalculator GetOrderInputCalculator(InstrumentKey key)
    {
        lock (_sync)
        {
            return _orderInputs[key];
        }
    }

    /// <summary>Sends one explicit limit request and schedules a read-only refresh.</summary>
    public async Task<OrderSnapshot> CreateLimitOrderAsync(LimitOrderRequest request, CancellationToken ct = default)
    {
        _stop.Token.ThrowIfCancellationRequested();
        var order = await _service.CreateLimitOrderAsync(request, ct).ConfigureAwait(false);
        lock (_sync) _orders[order.Reference] = order;
        await RefreshAfterActionAsync(request.Instrument, ct).ConfigureAwait(false);
        return order;
    }

    /// <summary>Sends one explicit market request and schedules a read-only refresh.</summary>
    public async Task<OrderSnapshot> CreateMarketOrderAsync(MarketOrderRequest request, CancellationToken ct = default)
    {
        _stop.Token.ThrowIfCancellationRequested();
        var order = await _service.CreateMarketOrderAsync(request, ct).ConfigureAwait(false);
        lock (_sync) _orders[order.Reference] = order;
        await RefreshAfterActionAsync(request.Instrument, ct).ConfigureAwait(false);
        return order;
    }

    /// <summary>Requests cancellation once, then refreshes exchange data without assuming final cancellation.</summary>
    public async Task<OrderSnapshot> CancelOrderAsync(OrderReference reference, CancellationToken ct = default)
    {
        _stop.Token.ThrowIfCancellationRequested();
        var order = await _service.CancelOrderAsync(reference, ct).ConfigureAwait(false);
        lock (_sync) _orders[order.Reference] = order;
        await RefreshAfterActionAsync(reference.Instrument, ct).ConfigureAwait(false);
        return order;
    }

    /// <summary>Changes futures leverage once and refreshes the account data after acknowledgement.</summary>
    public async Task SetLeverageAsync(InstrumentKey key, int leverage, CancellationToken ct = default)
    {
        _stop.Token.ThrowIfCancellationRequested();
        await _service.SetLeverageAsync(key, leverage, ct).ConfigureAwait(false);
        await RefreshAfterActionAsync(key, ct).ConfigureAwait(false);
    }

    /// <summary>Refreshes display state after a successful action; a refresh error does not change the action's result.</summary>
    private async Task RefreshAfterActionAsync(InstrumentKey key, CancellationToken ct)
    {
        try
        {
            foreach (var stream in Enum.GetValues<TradingStream>())
            {
                Resource? resource;
                lock (_sync) _resources.TryGetValue((stream, stream == TradingStream.Balance ? null : key), out resource);
                if (resource is not null) await SnapshotAsync(stream, key, resource).ConfigureAwait(false);
            }
            if (Account.Market == TradingMarket.UsdtFutures)
            {
                var hedged = await _service.FetchPositionModeAsync(key, ct).ConfigureAwait(false);
                var positions = await _service.FetchPositionsAsync(key, ct).ConfigureAwait(false);
                lock (_sync)
                {
                    _positionModes[key] = hedged;
                    _positions[key] = positions;
                }
            }
        }
        catch (Exception ex) { Report(ex); }
    }

    /// <summary>Fetches and freezes the current list before the UI asks the user to confirm cancellation.</summary>
    public async Task<CancelBatch> PrepareCancelAllAsync(InstrumentKey key, CancellationToken ct = default) =>
        new(Account, key, (await _service.FetchOpenOrdersAsync(key, ct).ConfigureAwait(false)).Select(o => o.Reference).ToArray());

    /// <summary>Attempts each confirmed cancellation once and reports its independent result.</summary>
    public async Task<IReadOnlyList<string>> CancelBatchAsync(CancelBatch batch, CancellationToken ct = default)
    {
        if (batch.Account != Account) throw new ArgumentException("Cancellation batch belongs to another account.");
        var results = new List<string>();
        foreach (var order in batch.Orders)
        {
            ct.ThrowIfCancellationRequested();
            try { results.Add($"{order.ExchangeOrderId}: {(await CancelOrderAsync(order, ct).ConfigureAwait(false)).State}"); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { results.Add($"{order.ExchangeOrderId}: {TradingErrorText.FromException(ex)}"); }
        }
        return results;
    }

    /// <summary>Recognizes final exchange statuses without depending on a provider adapter.</summary>
    public static bool Terminal(OrderState state) => state is OrderState.Filled or OrderState.Canceled or OrderState.Expired or OrderState.Rejected;

    /// <summary>Stops further actions immediately when keys or profile settings change.</summary>
    public void Suspend() => _stop.Cancel();

    /// <summary>Stops subscriptions and releases the transport without canceling any live exchange orders.</summary>
    public async ValueTask DisposeAsync()
    {
        await _tracking.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await _stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(_workers).ConfigureAwait(false);
            await _service.DisposeAsync().ConfigureAwait(false);
        }
        finally { _tracking.Release(); }
    }
}
