using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BotView.Models;

namespace BotView.Services;

/// <summary>One account's long-lived spot session. Owns recovery, validation and serialized mutations.</summary>
public sealed class CcxtTradingService : ITradingService
{
    private static readonly TradingCapabilities Capabilities = new(new HashSet<OrderType> { OrderType.Limit },
        new HashSet<OrderTimeInForce> { OrderTimeInForce.Gtc }, SupportsPostOnly: true,
        SupportsLookupByClientOrderId: true, SupportsOrderHistory: true, SupportsExecutions: true);
    private readonly ISpotTradingClient _client;
    private readonly TradingJournal _journal;
    private readonly string _accountKey;
    private readonly bool? _canTrade;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<InstrumentKey, byte> _tracked = new();
    private readonly ConcurrentDictionary<InstrumentKey, bool> _ready = new();
    private readonly ConcurrentDictionary<InstrumentKey, SpotMarket> _markets = new();
    private readonly ConcurrentDictionary<InstrumentKey, DateTimeOffset> _marketTimes = new();
    private readonly Dictionary<OrderReference, OrderSnapshot> _orders = new();
    private readonly Dictionary<(InstrumentKey, string), ExecutionSnapshot> _fills = new();
    private readonly Dictionary<string, TradingOperation> _operations = new();
    private readonly ConcurrentDictionary<Guid, Channel<TradingUpdate>> _subscribers = new();
    private readonly List<Task> _streams = new();
    private readonly Task _worker;
    private AccountSnapshot? _balance;
    private string _message = "Synchronizing account…";
    private TradingConnectionState _connection = TradingConnectionState.Connecting;
    private bool _balanceDirty = true;
    private bool _disposed;
    private Task? _disposeTask;
    private int _resyncGeneration;

    public TradingAccountContext Account { get; }
    internal bool? KeyCanTrade => _canTrade;

    /// <summary>Immediately prevents further mutations when account credentials change.</summary>
    internal void Suspend() { _disposed = true; _stop.Cancel(); }

    /// <summary>Checks pair readiness independently of other windows' tracked markets.</summary>
    public bool IsReady(InstrumentKey key) => !_disposed && _ready.GetValueOrDefault(key) && Current.Ready;

    /// <summary>Validates cached limits and precision for immediate input feedback without network requests.</summary>
    public OrderValidationResult ValidateInput(PlaceOrderRequest request)
    {
        if (!_markets.TryGetValue(request.Instrument, out var market)) return new([new("MetadataRequired", "Wait for trading metadata.")]);
        var errors = TradingOrderValidator.Validate(request, market.Instrument, Capabilities, Account, DateTimeOffset.UtcNow).Errors.ToList();
        try
        {
            if (request.Quantity > 0 && _client.NormalizeAmount(market.Instrument.Symbol, request.Quantity) != request.Quantity) errors.Add(new("QuantityPrecision", "Quantity does not match exchange precision."));
            if (request.Price is > 0 && _client.NormalizePrice(market.Instrument.Symbol, request.Price.Value) != request.Price) errors.Add(new("PricePrecision", "Price does not match exchange precision."));
        }
        catch (Exception) { errors.Add(new("Precision", "The entered values cannot be represented at the exchange precision.")); }
        if (_canTrade == false) errors.Add(new("TradingDenied", "Trading is denied for this API key."));
        return new(errors);
    }

    /// <summary>Restores durable state only after the provider has authenticated this exact account/network.</summary>
    internal CcxtTradingService(TradingAccountProfile profile, ISpotTradingClient client, TradingJournal journal, string identity, bool? canTrade)
    {
        _client = client;
        _journal = journal;
        _accountKey = $"{profile.Exchange}:{profile.Environment}:{identity}";
        Account = new(profile.Id.ToString(), identity, profile.Exchange, profile.Environment);
        _canTrade = canTrade;
        foreach (var order in journal.Orders(_accountKey)) _orders[order.Reference] = order;
        foreach (var fill in journal.Executions(_accountKey)) _fills[(fill.Order.Instrument, fill.ExecutionId)] = fill;
        foreach (var operation in journal.Operations(_accountKey))
        {
            var restored = operation.Phase == TradingOperationPhase.Dispatching ? operation with { Phase = TradingOperationPhase.Unknown } : operation;
            _operations[operation.Id] = restored;
            if (restored.Phase == TradingOperationPhase.Unknown) _tracked.TryAdd(restored.Request.Instrument, 0);
        }
        foreach (var order in _orders.Values.Where(o => !Terminal(o.State))) _tracked.TryAdd(order.Reference.Instrument, 0);
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Returns a detached state; views can coalesce updates on their existing 50 ms timer.</summary>
    public SpotTradingState Current
    {
        get
        {
            lock (_sync) return new(_connection, _message, _orders.Values.ToArray(), _fills.Values.OrderByDescending(f => f.ExecutedAt).ToArray(),
                _balance, _operations.Values.Where(Pending).ToArray(), !_disposed && !_balanceDirty && _connection == TradingConnectionState.Connected);
        }
    }

    /// <summary>Adds a pair for ongoing supervision; window closure deliberately does not untrack live orders.</summary>
    public async Task TrackAsync(InstrumentKey key, CancellationToken ct = default)
    {
        _tracked.TryAdd(key, 0);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await MarketCoreAsync(key, ct).ConfigureAwait(false);
            StartStreams(key);
            await ReconcileCoreAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { MarkDisconnected(ex); throw Safe(ex); }
        finally { _gate.Release(); }
    }

    /// <summary>Converts quote input through the exchange's quantity precision without rounding the price.</summary>
    public decimal ConvertQuantity(InstrumentKey key, decimal input, decimal price, OrderQuantityCurrency currency) =>
        SpotOrderMath.Quantity(input, price, currency, value => _client.NormalizeAmount(key.SecurityId, value));

    /// <summary>Loads market metadata within the account request gate.</summary>
    public async Task<Instrument> GetTradingInstrumentAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return (await MarketCoreAsync(instrument, ct).ConfigureAwait(false)).Instrument; }
        catch (Exception ex) { throw Safe(ex); }
        finally { _gate.Release(); }
    }

    /// <summary>Advertises only the spot limit functionality implemented by this adapter.</summary>
    public async Task<TradingCapabilities> GetCapabilitiesAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        await GetTradingInstrumentAsync(instrument, ct).ConfigureAwait(false);
        return Capabilities;
    }

    /// <summary>Runs a preview check; placement repeats it under the same account mutation gate.</summary>
    public async Task<OrderValidationResult> ValidateOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ValidateCoreAsync(request, ct).ConfigureAwait(false); }
        catch (Exception ex) { throw Safe(ex); }
        finally { _gate.Release(); }
    }

    /// <summary>Records a unique intent durably, then dispatches once; uncertainty never causes automatic resubmission.</summary>
    public async Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        ct = lifetime.Token;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        TradingOperation? operation = null;
        var dispatched = false;
        var responded = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_sync) if (_operations.Values.Any(o => o.Request.ClientOrderId == request.ClientOrderId))
                return new(PlaceOrderStatus.Rejected, request.ClientOrderId, request.Instrument, Error: new("DuplicateIntent", "This client order ID has already been used."));
            var validation = await ValidateCoreAsync(request, ct).ConfigureAwait(false);
            if (!validation.IsValid) return new(PlaceOrderStatus.Rejected, request.ClientOrderId, request.Instrument, Error: validation.Errors[0]);
            operation = new(request.ClientOrderId, _accountKey, request, TradingOperationPhase.Prepared, DateTimeOffset.UtcNow);
            Save(operation);
            ct.ThrowIfCancellationRequested();
            operation = operation with { Phase = TradingOperationPhase.Dispatching };
            Save(operation);
            lock (_sync) _balanceDirty = true;
            dispatched = true;
            var order = await _client.PlaceAsync(request, ct).ConfigureAwait(false);
            responded = true;
            if (!string.IsNullOrWhiteSpace(order.Reference.ExchangeOrderId)) Merge(order);
            operation = operation with { ExchangeOrderId = order.Reference.ExchangeOrderId,
                Phase = order.State is OrderState.Open || Terminal(order.State) ? TradingOperationPhase.Resolved : TradingOperationPhase.Unknown };
            Save(operation);
            // Only an authoritative balance fetched after the order observation can release the local spend barrier.
            try { await RefreshBalanceCoreAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception) { MarkDisconnected(null); }
            return new(order.State == OrderState.Rejected ? PlaceOrderStatus.Rejected : operation.Phase == TradingOperationPhase.Resolved ? PlaceOrderStatus.Accepted : PlaceOrderStatus.Submitted,
                request.ClientOrderId, request.Instrument, order.Reference.ExchangeOrderId, Order: order);
        }
        catch (Exception ex)
        {
            if (dispatched && operation is not null)
            {
                var rejected = !responded && DefiniteRejection(ex);
                RecordFailure(operation, rejected);
                return new(rejected ? PlaceOrderStatus.Rejected : PlaceOrderStatus.Unknown, request.ClientOrderId, request.Instrument,
                    Error: rejected ? Error(ex) : new("ResultUnknown", "No definitive order outcome. Reconciling the original client ID; no automatic resend."));
            }
            if (ex is OperationCanceledException) throw;
            return new(PlaceOrderStatus.Rejected, request.ClientOrderId, request.Instrument, Error: Error(ex));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Persists cancellation uncertainty independently of the order's partial executions.</summary>
    public async Task<CancelOrderResult> CancelOrderAsync(OrderReference order, CancellationToken ct = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        ct = lifetime.Token;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        TradingOperation? operation = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await MarketCoreAsync(order.Instrument, ct).ConfigureAwait(false);
            if (_canTrade == false) return new(CancelOrderStatus.Rejected, order, Error: new("TradingDenied", "This API key cannot cancel orders."));
            var known = await _client.OrderAsync(order, ct).ConfigureAwait(false);
            if (known is not null)
            {
                Merge(known);
                if (Terminal(known.State)) return new(CancelOrderStatus.AlreadyClosed, order, Order: known);
            }
            lock (_sync) if (_operations.Values.Any(o => Pending(o) && o.CancelTarget == order))
                return new(CancelOrderStatus.Unknown, order, Error: new("CancelPending", "An earlier cancellation is still being reconciled."));
            var request = new PlaceOrderRequest(order.Instrument, known?.Side ?? OrderSide.Buy, OrderType.Limit,
                known?.Quantity ?? 0, known?.ClientOrderId ?? "", known?.Price);
            operation = new("cancel:" + Guid.NewGuid().ToString("N"), _accountKey, request, TradingOperationPhase.Prepared, DateTimeOffset.UtcNow, order);
            Save(operation);
            ct.ThrowIfCancellationRequested();
            operation = operation with { Phase = TradingOperationPhase.Dispatching };
            Save(operation);
            var result = await _client.CancelAsync(order, ct).ConfigureAwait(false);
            if (result is not null && result.Quantity > 0) Merge(result);
            var final = result is not null && Terminal(result.State);
            Save(operation with { Phase = final ? TradingOperationPhase.Resolved : TradingOperationPhase.Unknown });
            lock (_sync) _balanceDirty = true;
            return new(final ? result!.State == OrderState.Filled ? CancelOrderStatus.AlreadyClosed : CancelOrderStatus.Canceled : CancelOrderStatus.Submitted, order, Order: result);
        }
        catch (Exception ex)
        {
            if (operation?.Phase == TradingOperationPhase.Dispatching)
            {
                if (ex is ccxt.PermissionDenied or ccxt.AuthenticationError)
                {
                    RecordFailure(operation, true);
                    return new(CancelOrderStatus.Rejected, order, Error: Error(ex));
                }
                RecordFailure(operation, false);
                return new(CancelOrderStatus.Unknown, order, Error: new("CancelUnknown", "Cancellation outcome is unknown; orders and executions will be reconciled."));
            }
            if (ex is OperationCanceledException) throw;
            return new(CancelOrderStatus.Rejected, order, Error: Error(ex));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Captures every current order ID before the view requests confirmation.</summary>
    public async Task<CancelBatch> PrepareCancelAllAsync(InstrumentKey key, CancellationToken ct = default)
    {
        var orders = new List<OrderSnapshot>();
        string? cursor = null;
        var cursors = new HashSet<string>();
        do
        {
            var page = await GetOpenOrdersAsync(new(key, Cursor: cursor), ct).ConfigureAwait(false);
            orders.AddRange(page.Items);
            cursor = page.NextCursor;
            if (cursor is not null && !cursors.Add(cursor)) throw new TradingServiceException(new("Pagination", "Could not capture a complete cancellation snapshot."));
        } while (cursor is not null);
        if (orders.Any(o => string.IsNullOrWhiteSpace(o.Reference.ExchangeOrderId)))
            throw new TradingServiceException(new("OrderIdentityMissing", "An exchange order has no usable identity; cancellation snapshot is incomplete."));
        return new(Account, key, orders.Where(o => !Terminal(o.State)).Select(o => o.Reference).Distinct().ToArray());
    }

    /// <summary>Cancels only confirmed snapshot IDs and returns every individual outcome, including partial failure.</summary>
    public async Task<IReadOnlyList<CancelOrderResult>> CancelBatchAsync(CancelBatch batch, CancellationToken ct = default)
    {
        if (batch.Account != Account || batch.Orders.Any(o => o.Instrument != batch.Instrument)) throw new ArgumentException("Cancel batch account mismatch.");
        var results = new List<CancelOrderResult>();
        foreach (var order in batch.Orders) results.Add(await CancelOrderAsync(order, ct).ConfigureAwait(false));
        return results;
    }

    /// <summary>Reads an authoritative order without leaking provider exceptions.</summary>
    public Task<OrderSnapshot?> GetOrderAsync(OrderReference order, CancellationToken ct = default) => ReadInstrumentAsync(order.Instrument, () => _client.OrderAsync(order, ct), ct);

    /// <summary>Missing history is not used as proof that an uncertain placement failed.</summary>
    public Task<OrderSnapshot?> FindOrderByClientIdAsync(InstrumentKey instrument, string clientOrderId, CancellationToken ct = default) => ReadInstrumentAsync(instrument, () => _client.FindAsync(instrument, clientOrderId, ct), ct);

    /// <summary>Exposes provider continuation rather than silently truncating open orders.</summary>
    public Task<TradingPage<OrderSnapshot>> GetOpenOrdersAsync(TradingQuery query, CancellationToken ct = default) => ReadInstrumentAsync(query.Instrument, () => _client.OrdersAsync(query, true, ct), ct);

    /// <summary>Exposes paged order history in application models.</summary>
    public Task<TradingPage<OrderSnapshot>> GetOrderHistoryAsync(TradingQuery query, CancellationToken ct = default) => ReadInstrumentAsync(query.Instrument, () => _client.OrdersAsync(query, false, ct), ct);

    /// <summary>Exposes paged executions with stable fill identities.</summary>
    public Task<TradingPage<ExecutionSnapshot>> GetExecutionsAsync(TradingQuery query, CancellationToken ct = default) => ReadInstrumentAsync(query.Instrument, () => _client.ExecutionsAsync(query, ct), ct);

    /// <summary>Returns a fresh account snapshot rather than substituting an unavailable section with zeroes.</summary>
    public Task<AccountSnapshot> GetAccountStateAsync(CancellationToken ct = default) => ReadAsync(() => _client.BalanceAsync(Account, ct), ct);

    /// <summary>Spot holdings are balances; derivative position operations are not supported.</summary>
    public Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(InstrumentKey? instrument = null, CancellationToken ct = default) => throw new NotSupportedException("Use spot account balances.");

    /// <summary>Broadcasts independent bounded streams; a slow reader is instructed to resynchronize after overflow.</summary>
    public async IAsyncEnumerable<TradingUpdate> WatchUpdatesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<TradingUpdate>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.Wait });
        _subscribers[id] = channel;
        channel.Writer.TryWrite(new TradingConnectionChanged(Current.Connection, DateTimeOffset.UtcNow, true));
        try { await foreach (var update in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return update; }
        finally { _subscribers.TryRemove(id, out _); }
    }

    /// <summary>Serializes REST operations while allowing independent private-stream waits.</summary>
    private async Task<T> ReadAsync<T>(Func<Task<T>> read, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); return await read().ConfigureAwait(false); }
        catch (Exception ex) { throw Safe(ex); }
        finally { _gate.Release(); }
    }

    /// <summary>Ensures listing metadata exists even when a contract method is used before a book is opened.</summary>
    private Task<T> ReadInstrumentAsync<T>(InstrumentKey? key, Func<Task<T>> read, CancellationToken ct) => ReadAsync(async () =>
    {
        if (key is null) throw new NotSupportedException("A spot instrument is required.");
        await MarketCoreAsync(key, ct).ConfigureAwait(false);
        return await read().ConfigureAwait(false);
    }, ct);

    /// <summary>Caches spot metadata after successful loading under the account gate.</summary>
    private async Task<SpotMarket> MarketCoreAsync(InstrumentKey key, CancellationToken ct)
    {
        if (_markets.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - _marketTimes[key] < TimeSpan.FromMinutes(5)) return cached;
        var market = await _client.MarketAsync(key, ct).ConfigureAwait(false);
        _markets[key] = market;
        _marketTimes[key] = DateTimeOffset.UtcNow;
        return market;
    }

    /// <summary>Checks limits, precision, permission and fresh funds immediately before dispatch.</summary>
    private async Task<OrderValidationResult> ValidateCoreAsync(PlaceOrderRequest request, CancellationToken ct)
    {
        var market = await MarketCoreAsync(request.Instrument, ct).ConfigureAwait(false);
        var errors = ValidateInput(request).Errors.ToList();
        if (request.ClientOrderId.Length > 36 || request.ClientOrderId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) errors.Add(new("ClientOrderId", "Use a unique client ID of at most 36 ASCII characters."));
        if (!_ready.GetValueOrDefault(request.Instrument) || _disposed) errors.Add(new("Synchronizing", "Wait for the account and this pair to synchronize."));
        TradingOperation[] pending;
        lock (_sync) pending = _operations.Values.Where(Pending).ToArray();
        if (pending.Any(o => o.Request.Instrument == request.Instrument)) errors.Add(new("UnknownOperation", "An operation on this pair is still being reconciled; it will not be resent."));
        var spendCurrency = request.Side == OrderSide.Buy ? market.Quote : market.Base;
        // When an unknown order may already be reflected in free balance, subtraction could count it twice.
        // Block competing spending in its currency until order+balance reconciliation establishes the truth.
        if (pending.Any(o => _markets.TryGetValue(o.Request.Instrument, out var m) &&
            (o.Request.Side == OrderSide.Buy ? m.Quote : m.Base) == spendCurrency))
            errors.Add(new("FundsUncertain", "Funds in this currency are awaiting reconciliation of an earlier operation."));
        if (errors.Count > 0) return new(errors);
        errors.AddRange(await _client.ValidateMarketAsync(request, ct).ConfigureAwait(false));
        if (errors.Count > 0) return new(errors);
        if (_balanceDirty || _balance is null || DateTimeOffset.UtcNow - _balance.AsOf > TimeSpan.FromSeconds(5))
            await RefreshBalanceCoreAsync(ct).ConfigureAwait(false);
        if (market.FeeRate is null) errors.Add(new("FeeUnavailable", "Trading fees could not be established."));
        else
        {
            // Conservative coverage permits commissions in either input asset without assuming discounts or borrowing.
            var feeInSpend = market.FeeSide switch { "get" => false, "base" => request.Side == OrderSide.Sell,
                "quote" => request.Side == OrderSide.Buy, _ => true };
            var required = (request.Side == OrderSide.Buy ? request.Quantity * request.Price!.Value : request.Quantity) * (1 + (feeInSpend ? market.FeeRate.Value : 0));
            var available = _balance?.Balances?.FirstOrDefault(b => b.Currency == spendCurrency)?.Available;
            if (available is null) errors.Add(new("BalanceUnavailable", $"Available {spendCurrency} balance is unknown."));
            else if (available < required) errors.Add(new("InsufficientFunds", $"Insufficient available {spendCurrency}, including commission coverage."));
        }
        return new(errors);
    }

    /// <summary>Refreshes all tracked pairs after stream disruption, with capped exponential reconnect delay.</summary>
    private async Task RunAsync()
    {
        var retry = 1;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                foreach (var key in _tracked.Keys)
                {
                    await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
                    try
                    {
                        await MarketCoreAsync(key, _stop.Token).ConfigureAwait(false);
                        StartStreams(key);
                        await ReconcileCoreAsync(key, _stop.Token).ConfigureAwait(false);
                    }
                    finally { _gate.Release(); }
                }
                retry = 1;
                await Task.Delay(2000, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                MarkDisconnected(ex);
                try { await Task.Delay(TimeSpan.FromSeconds(retry), _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                retry = Math.Min(30, retry * 2);
            }
        }
    }

    private readonly HashSet<(string, InstrumentKey?)> _started = new();

    /// <summary>Starts private watchers before REST snapshots; reducers safely merge buffered and later events.</summary>
    private void StartStreams(InstrumentKey key)
    {
        foreach (var kind in new[] { "watchOrders", "watchMyTrades", "watchBalance" })
        {
            var identity = (kind, kind == "watchBalance" ? null : key);
            if (!_client.SupportsStream(kind) || !_started.Add(identity)) continue;
            _streams.Add(Task.Run(() => WatchCoreAsync(kind, key)));
        }
    }

    /// <summary>Retries failed streams while REST reconciliation remains the authority for readiness.</summary>
    private async Task WatchCoreAsync(string kind, InstrumentKey key)
    {
        var retry = 1;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                foreach (var update in await _client.WatchAsync(kind, key, Account, _stop.Token).ConfigureAwait(false))
                {
                    if (update is OrderChanged order) Merge(order.Order);
                    else if (update is ExecutionReceived fill) Merge(fill.Execution);
                    else if (update is AccountChanged balance)
                    {
                        lock (_sync)
                        {
                            if (_balance is null || balance.AsOf >= _balance.AsOf) _balance = balance.Account;
                            _balanceDirty = true;
                        }
                        Publish(update);
                    }
                }
                retry = 1;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (ccxt.NotSupported) { break; }
            catch (Exception ex)
            {
                MarkDisconnected(ex);
                try { await Task.Delay(TimeSpan.FromSeconds(retry), _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                retry = Math.Min(30, retry * 2);
            }
        }
    }

    /// <summary>Combines complete open-order pages, point lookups, unresolved intents, executions and a final balance.</summary>
    private async Task ReconcileCoreAsync(InstrumentKey key, CancellationToken ct)
    {
        var generation = Volatile.Read(ref _resyncGeneration);
        string? cursor = null;
        var seen = new HashSet<string>();
        var observedOpen = new HashSet<OrderReference>();
        do
        {
            var page = await _client.OrdersAsync(new(key, Limit: 1000, Cursor: cursor), true, ct).ConfigureAwait(false);
            foreach (var order in page.Items) { observedOpen.Add(order.Reference); Merge(order); }
            cursor = page.NextCursor;
            if (cursor is not null && !seen.Add(cursor)) throw new InvalidOperationException("Repeated history cursor.");
        } while (cursor is not null);
        OrderSnapshot[] known;
        TradingOperation[] pending;
        lock (_sync)
        {
            known = _orders.Values.Where(o => o.Reference.Instrument == key && !Terminal(o.State)).ToArray();
            pending = _operations.Values.Where(o => Pending(o) && o.Request.Instrument == key).ToArray();
        }
        foreach (var order in known.Where(o => !observedOpen.Contains(o.Reference)))
        {
            var current = await _client.OrderAsync(order.Reference, ct).ConfigureAwait(false);
            if (current is not null) Merge(current);
        }
        foreach (var operation in pending)
        {
            var order = operation.CancelTarget is { } target ? await _client.OrderAsync(target, ct).ConfigureAwait(false)
                : await _client.FindAsync(key, operation.Request.ClientOrderId, ct).ConfigureAwait(false);
            if (order is null && operation.CancelTarget is null && operation.CreatedAt < DateTimeOffset.UtcNow.AddDays(-6))
                order = await FindInHistoryCoreAsync(operation, ct).ConfigureAwait(false);
            if (order is not null)
            {
                Merge(order);
                if (operation.CancelTarget is null && order.State is not (OrderState.Pending or OrderState.Unknown) || operation.CancelTarget is not null && Terminal(order.State))
                    Save(operation with { Phase = TradingOperationPhase.Resolved, ExchangeOrderId = order.Reference.ExchangeOrderId });
            }
        }
        DateTimeOffset since;
        var recoveryStart = known.Where(o => o.CreatedAt.HasValue).Select(o => o.CreatedAt!.Value)
            .Concat(pending.Select(o => o.CreatedAt)).DefaultIfEmpty(DateTimeOffset.UtcNow.AddDays(-1)).Min();
        lock (_sync) since = _fills.Values.Where(f => f.Order.Instrument == key).Select(f => f.ExecutedAt).DefaultIfEmpty(recoveryStart).Max().AddMinutes(-5);
        var until = DateTimeOffset.UtcNow;
        while (since < until)
        {
            var end = since.AddDays(7) < until ? since.AddDays(7) : until;
            cursor = null; seen.Clear();
            do
            {
                var page = await _client.ExecutionsAsync(new(key, Since: since, Until: end, Limit: 1000, Cursor: cursor), ct).ConfigureAwait(false);
                foreach (var fill in page.Items) Merge(fill);
                cursor = page.NextCursor;
                if (cursor is not null && !seen.Add(cursor)) throw new InvalidOperationException("Repeated execution cursor.");
            } while (cursor is not null);
            since = end;
        }
        await RefreshBalanceCoreAsync(ct).ConfigureAwait(false);
        if (generation != Volatile.Read(ref _resyncGeneration)) return;
        _ready[key] = true;
        lock (_sync)
        {
            _connection = TradingConnectionState.Connected;
            _message = _canTrade is null ? "Connected · trading permissions unknown" : _canTrade == false ? "Connected · trading denied" : "Connected";
        }
        Publish(new TradingConnectionChanged(TradingConnectionState.Connected, DateTimeOffset.UtcNow));
    }

    /// <summary>Searches older unresolved client IDs in bounded history windows without guessing or resending.</summary>
    private async Task<OrderSnapshot?> FindInHistoryCoreAsync(TradingOperation operation, CancellationToken ct)
    {
        var since = operation.CreatedAt.AddMinutes(-5);
        var until = DateTimeOffset.UtcNow;
        while (since < until)
        {
            var end = since.AddDays(7) < until ? since.AddDays(7) : until;
            string? cursor = null;
            var seen = new HashSet<string>();
            do
            {
                var page = await _client.OrdersAsync(new(operation.Request.Instrument, since, end, 1000, cursor), false, ct).ConfigureAwait(false);
                var found = page.Items.FirstOrDefault(o => o.ClientOrderId == operation.Request.ClientOrderId);
                if (found is not null) return found;
                cursor = page.NextCursor;
                if (cursor is not null && !seen.Add(cursor)) throw new InvalidOperationException("Repeated order history cursor.");
            } while (cursor is not null);
            since = end;
        }
        return null;
    }

    /// <summary>Fetches authoritative free balances after order state, avoiding double local reservations.</summary>
    private async Task RefreshBalanceCoreAsync(CancellationToken ct)
    {
        var balance = await _client.BalanceAsync(Account, ct).ConfigureAwait(false);
        lock (_sync) { _balance = balance; _balanceDirty = false; }
        Publish(new AccountChanged(balance));
    }

    /// <summary>Reduces duplicate/out-of-order snapshots without losing fills or reopening terminal orders.</summary>
    internal void Merge(OrderSnapshot order)
    {
        if (string.IsNullOrWhiteSpace(order.Reference.ExchangeOrderId)) return;
        lock (_sync)
        {
            if (_orders.TryGetValue(order.Reference, out var old)) order = Reduce(old, order);
            _orders[order.Reference] = order;
            _journal.Save(_accountKey, order);
            foreach (var operation in _operations.Values.Where(o => Pending(o) &&
                (o.CancelTarget == order.Reference || o.CancelTarget is null && o.Request.Instrument == order.Reference.Instrument && o.Request.ClientOrderId == order.ClientOrderId)).ToArray())
                if (operation.CancelTarget is null && order.State is not (OrderState.Pending or OrderState.Unknown) || operation.CancelTarget is not null && Terminal(order.State))
                    Save(operation with { Phase = TradingOperationPhase.Resolved, ExchangeOrderId = order.Reference.ExchangeOrderId });
        }
        Publish(new OrderChanged(order));
    }

    /// <summary>Preserves monotonic filled quantity and terminal state under delayed stream messages.</summary>
    internal static OrderSnapshot Reduce(OrderSnapshot old, OrderSnapshot next)
    {
        var filled = old.FilledQuantity.HasValue && next.FilledQuantity.HasValue ? Math.Max(old.FilledQuantity.Value, next.FilledQuantity.Value) : next.FilledQuantity ?? old.FilledQuantity;
        var newer = next.UpdatedAt >= old.UpdatedAt || (next.FilledQuantity ?? 0) > (old.FilledQuantity ?? 0);
        var result = newer ? next : old;
        if (Terminal(old.State) && !Terminal(result.State)) result = result with { State = old.State };
        if (old.State == OrderState.Filled) result = result with { State = OrderState.Filled };
        var quantity = result.Quantity > 0 ? result.Quantity : old.Quantity;
        return result with { Quantity = quantity, FilledQuantity = filled,
            RemainingQuantity = filled.HasValue ? Math.Max(0, quantity - filled.Value) : result.RemainingQuantity ?? old.RemainingQuantity,
            Price = result.Price ?? old.Price, ClientOrderId = result.ClientOrderId ?? old.ClientOrderId,
            StatusReason = result.StatusReason ?? old.StatusReason,
            AverageFillPrice = result.AverageFillPrice ?? old.AverageFillPrice, CreatedAt = result.CreatedAt ?? old.CreatedAt,
            UpdatedAt = next.UpdatedAt > old.UpdatedAt ? next.UpdatedAt : old.UpdatedAt };
    }

    /// <summary>Persists a fill once, allowing a later event to enrich a previously unknown commission.</summary>
    private void Merge(ExecutionSnapshot fill)
    {
        if (string.IsNullOrEmpty(fill.ExecutionId)) return;
        lock (_sync)
        {
            var key = (fill.Order.Instrument, fill.ExecutionId);
            if (_fills.TryGetValue(key, out var old) && (old.Fee is not null || fill.Fee is null)) return;
            _fills[key] = fill;
            _journal.Save(_accountKey, fill);
        }
        Publish(new ExecutionReceived(fill));
    }

    /// <summary>Updates durable and in-memory intent state together before returning to a caller.</summary>
    private void Save(TradingOperation operation)
    {
        lock (_sync)
        {
            if (_operations.TryGetValue(operation.Id, out var old) && old.Phase == TradingOperationPhase.Resolved && operation.Phase != TradingOperationPhase.Resolved) return;
            _journal.Save(operation); _operations[operation.Id] = operation;
        }
    }

    /// <summary>Retains uncertainty in RAM even if storage fails after dispatch; the earlier durable Dispatching remains recoverable.</summary>
    private void RecordFailure(TradingOperation operation, bool rejected)
    {
        var updated = operation with { Phase = rejected ? TradingOperationPhase.Resolved : TradingOperationPhase.Unknown };
        try { Save(updated); }
        catch (Exception)
        {
            lock (_sync)
            {
                if (!_operations.TryGetValue(updated.Id, out var old) || old.Phase != TradingOperationPhase.Resolved) _operations[updated.Id] = updated;
                _balanceDirty = true;
            }
        }
    }

    /// <summary>Invalidates placement readiness on any stream/REST gap until reconciliation succeeds.</summary>
    private void MarkDisconnected(Exception? ex)
    {
        Interlocked.Increment(ref _resyncGeneration);
        foreach (var key in _tracked.Keys) _ready[key] = false;
        lock (_sync) { _connection = TradingConnectionState.Reconnecting; _balanceDirty = true; _message = ex is null ? "Balance synchronization required" : Error(ex).Message; }
        Publish(new TradingConnectionChanged(TradingConnectionState.Reconnecting, DateTimeOffset.UtcNow, true, ex is null ? null : Error(ex)));
    }

    /// <summary>Broadcasts detached events and marks an overflowed subscriber as requiring a full resync.</summary>
    private void Publish(TradingUpdate update)
    {
        foreach (var channel in _subscribers.Values)
            if (!channel.Writer.TryWrite(update))
            {
                while (channel.Reader.TryRead(out _)) { }
                channel.Writer.TryWrite(new TradingConnectionChanged(TradingConnectionState.Reconnecting, DateTimeOffset.UtcNow, true));
            }
    }

    /// <summary>Identifies only states whose exchange lifecycle has ended.</summary>
    internal static bool Terminal(OrderState state) => state is OrderState.Filled or OrderState.Canceled or OrderState.Expired or OrderState.Rejected;

    /// <summary>Prepared intents have not crossed the network boundary and must never be replayed.</summary>
    private static bool Pending(TradingOperation operation) => operation.Phase is TradingOperationPhase.Dispatching or TradingOperationPhase.Unknown;

    /// <summary>Only explicit business rejections prove that placement failed; timeouts and duplicates remain uncertain.</summary>
    private static bool DefiniteRejection(Exception ex) => ex is ccxt.InsufficientFunds or ccxt.PermissionDenied or ccxt.AuthenticationError ||
        ex is ccxt.InvalidOrder && ex is not ccxt.OrderNotFound && !ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);

    /// <summary>Produces stable user-facing errors without provider payloads or signed URLs.</summary>
    private static TradingError Error(Exception ex) => ex is TradingServiceException service ? service.Error : ex switch
    {
        ccxt.InsufficientFunds => new("InsufficientFunds", "The exchange rejected the order: insufficient funds."),
        ccxt.InvalidOrder => new("InvalidOrder", "The exchange rejected the order parameters or Post-only condition."),
        OperationCanceledException or ccxt.NetworkError => new("ConnectionLost", "Connection interrupted. Any dispatched operation will be reconciled, not resent."),
        NotSupportedException => new("Unsupported", "This operation or spot instrument is unavailable."),
        _ => new("ExchangeFailure", AccountConnectionVerifier.SafeError(ex))
    };

    /// <summary>Normalizes provider read failures while preserving caller cancellation.</summary>
    private static Exception Safe(Exception ex) => ex is OperationCanceledException or NotSupportedException ? ex : new TradingServiceException(Error(ex));

    /// <summary>Closes transports and workers without issuing exchange cancellation requests.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync) return new(_disposeTask ??= DisposeCoreAsync());
    }

    /// <summary>Stops stream waits, drains the current bounded request and retains uncertain journal entries.</summary>
    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        await _stop.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _client.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
        await _worker.ConfigureAwait(false);
        await Task.WhenAll(_streams).ConfigureAwait(false);
        foreach (var channel in _subscribers.Values) channel.Writer.TryComplete();
        lock (_sync) { _connection = TradingConnectionState.Disconnected; _message = "Disconnected"; }
    }
}
