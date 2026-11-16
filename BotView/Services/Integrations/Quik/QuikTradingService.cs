using BotView.Configuration;
using System.Threading.Channels;
using System.Globalization;
using QuikSharp.DataStructures;
using QuikSharp.DataStructures.Transaction;

namespace BotView.Services.Integrations.Quik;

/// <summary>Adapts QUIK account reads and explicitly enabled transaction execution.</summary>
public sealed class QuikTradingService : ITradingService
{
    private readonly TradingAccountProfile _profile;
    private readonly IQuikReadClient _client;
    private readonly IQuikTransactionClient? _transactions;
    private int _disposed;
    private readonly Dictionary<InstrumentKey, Instrument> _instruments = [];
    private readonly Dictionary<TradingStream, Channel<bool>> _signals = Enum.GetValues<TradingStream>().ToDictionary(s => s,
        _ => Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest }));
    public TradingAccountContext Account { get; }
    public bool CanExecuteTrades => _transactions is not null;

    /// <summary>Creates lazy leases; execution is disabled by default and is never enabled by the application factory.</summary>
    public QuikTradingService(TradingAccountProfile profile, bool enableTrading = false)
        : this(profile, CreateClient(profile), enableTrading
            ? new QuikTransactionClient(new QuikConnectionOptions { Host = profile.Quik!.Host, Port = profile.Quik.Port })
            : null) { }

    /// <summary>Validates routing before acquiring a terminal lease.</summary>
    private static IQuikReadClient CreateClient(TradingAccountProfile profile)
    {
        if (profile.Exchange != "quik" || profile.Quik is null || profile.Market is not (TradingMarket.MoexShares or TradingMarket.MoexFutures))
            throw new ArgumentException("A QUIK shares/futures profile is required.");
        return new QuikReadClient(new QuikConnectionOptions
        {
            Host = profile.Quik.Host,
            Port = profile.Quik.Port
        });
    }

    /// <summary>Injects account reads and an optional execution boundary for deterministic offline tests.</summary>
    internal QuikTradingService(TradingAccountProfile profile, IQuikReadClient client, IQuikTransactionClient? transactions = null)
    {
        if (profile.Exchange != "quik" || profile.Quik is null || profile.Market is not (TradingMarket.MoexShares or TradingMarket.MoexFutures))
            throw new ArgumentException("A QUIK shares/futures profile is required.");
        _profile = profile;
        _client = client;
        _transactions = transactions;
        Account = QuikReadClient.Context(profile);
        _client.AccountChanged += OnAccountChanged;
    }

    /// <summary>Loads instrument metadata once for the session's calculation and display needs.</summary>
    public async Task<Instrument> FetchInstrumentAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        ValidateInstrument(instrument);
        var value = await _client.InstrumentAsync(instrument, ct).ConfigureAwait(false);
        lock (_instruments) _instruments[instrument] = value;
        return value;
    }

    /// <summary>Formats native quantities as whole lots or contracts, without sending a request.</summary>
    public decimal AmountToPrecision(InstrumentKey instrument, decimal quantity) => decimal.Floor(quantity);

    /// <summary>Formats a preview price using known instrument metadata only.</summary>
    public decimal PriceToPrecision(InstrumentKey instrument, decimal price)
    {
        lock (_instruments)
        {
            var step = _instruments[instrument].PriceStep!.Value;
            return decimal.Floor(price / step) * step;
        }
    }

    /// <summary>Returns all open regular and conditional orders belonging to the bound account.</summary>
    public async Task<IReadOnlyList<OrderSnapshot>> FetchOpenOrdersAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        ValidateInstrument(instrument);
        return (await _client.OrdersAsync(_profile, instrument, ct).ConfigureAwait(false)).Where(o => !TradingAccountSession.Terminal(o.State)).ToArray();
    }

    /// <summary>Looks up an order using both its category and exchange identifier.</summary>
    public async Task<OrderSnapshot> FetchOrderAsync(OrderReference order, CancellationToken ct = default)
    {
        ValidateInstrument(order.Instrument);
        return (await _client.OrdersAsync(_profile, order.Instrument, ct).ConfigureAwait(false)).Single(o => o.Reference == order);
    }

    /// <summary>Reads account executions without creating any local trades.</summary>
    public Task<IReadOnlyList<ExecutionSnapshot>> FetchMyTradesAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        ValidateInstrument(instrument);
        return _client.TradesAsync(_profile, instrument, ct);
    }

    /// <summary>Reads balances and collateral from the selected terminal routing tuple.</summary>
    public Task<AccountSnapshot> FetchBalanceAsync(CancellationToken ct = default) => _client.BalanceAsync(_profile, ct);

    /// <summary>Reads native holdings without calculating a synthetic BotView position.</summary>
    public Task<IReadOnlyList<PositionSnapshot>> FetchPositionsAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        ValidateInstrument(instrument);
        return _client.PositionsAsync(_profile, instrument, ct);
    }

    /// <summary>Rejects listing/account market mismatches before exposing terminal data.</summary>
    private void ValidateInstrument(InstrumentKey instrument)
    {
        var board = _profile.Market == TradingMarket.MoexFutures ? "SPBFUT" : "TQBR";
        if (instrument.Venue != "MOEX" || instrument.Board != board || QuikReadClient.Key(board, instrument.SecurityId) != instrument)
            throw new ArgumentException("The listing does not belong to this account market.");
    }

    /// <summary>MOEX positions are net; the adapter never changes an account's position mode.</summary>
    public Task<bool?> FetchPositionModeAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateInstrument(instrument);
        return Task.FromResult<bool?>(false);
    }

    /// <summary>The shared session performs read-only snapshot reconciliation on its polling interval.</summary>
    public bool SupportsStream(TradingStream stream) => true;

    /// <summary>Coalesces callbacks without accumulating work during volatile markets.</summary>
    private void OnAccountChanged(TradingStream stream) => _signals[stream].Writer.TryWrite(true);

    /// <summary>Uses callbacks for fast refreshes and a heartbeat to detect broker disconnects.</summary>
    private async Task WaitForUpdateAsync(TradingStream stream, CancellationToken ct)
    {
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        heartbeat.CancelAfter(TimeSpan.FromSeconds(2));
        try { await _signals[stream].Reader.ReadAsync(heartbeat.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        ct.ThrowIfCancellationRequested();
        if (!await _client.ConnectedAsync(ct).ConfigureAwait(false)) throw new InvalidOperationException("QUIK не подключён к брокеру.");
    }

    /// <summary>Sends one limit transaction in native lots/contracts and waits for its exchange acknowledgement.</summary>
    public async Task<OrderSnapshot> CreateLimitOrderAsync(LimitOrderRequest request, CancellationToken ct = default)
    {
        var transactions = RequireTrading(ct);
        ValidateOrder(request.Instrument, request.Side, request.Quantity, request.PostOnly, request.ReduceOnly, request.PositionSide, request.Hedged);
        var instrument = await FetchInstrumentAsync(request.Instrument, ct).ConfigureAwait(false);
        if (request.Price <= 0 && !instrument.AllowNonPositivePrices)
            throw new ArgumentException("A positive limit price is required.");

        var price = PriceToPrecision(request.Instrument, request.Price);
        if (price <= 0 && !instrument.AllowNonPositivePrices)
            throw new ArgumentException("Price rounds to zero.");

        return await CreateOrderAsync(transactions, request.Instrument, request.Side, request.Quantity, price, OrderType.Limit, ct).ConfigureAwait(false);
    }

    /// <summary>Uses zero for stock market orders and the terminal's direction-specific price bound on FORTS.</summary>
    public async Task<OrderSnapshot> CreateMarketOrderAsync(MarketOrderRequest request, CancellationToken ct = default)
    {
        var transactions = RequireTrading(ct);
        ValidateOrder(request.Instrument, request.Side, request.Quantity, false, request.ReduceOnly, request.PositionSide, request.Hedged);
        var price = _profile.Market == TradingMarket.MoexFutures
            ? await transactions.MarketPriceAsync(request.Instrument, request.Side, ct).ConfigureAwait(false)
            : 0m;

        return await CreateOrderAsync(transactions, request.Instrument, request.Side, request.Quantity, price, OrderType.Market, ct).ConfigureAwait(false);
    }

    /// <summary>Verifies account ownership and cancels a regular or conditional order once without inventing final state.</summary>
    public async Task<OrderSnapshot> CancelOrderAsync(OrderReference order, CancellationToken ct = default)
    {
        var transactions = RequireTrading(ct);
        ValidateInstrument(order.Instrument);
        if (!Enum.IsDefined(order.Category) || !long.TryParse(order.ExchangeOrderId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new ArgumentException("A valid native order reference is required.");

        var existing = await FetchOrderAsync(order, ct).ConfigureAwait(false);
        if (TradingAccountSession.Terminal(existing.State))
            throw new InvalidOperationException("The order is already terminal.");

        var transaction = CreateTransaction(order.Instrument);
        transaction.ACTION = order.Category == OrderCategory.Conditional ? TransactionAction.KILL_STOP_ORDER : TransactionAction.KILL_ORDER;
        if (order.Category == OrderCategory.Conditional) transaction.STOP_ORDER_KEY = order.ExchangeOrderId;
        else transaction.ORDER_KEY = order.ExchangeOrderId;

        await EnsureAccountAsync(ct).ConfigureAwait(false);
        var reply = await transactions.SendAsync(transaction, ct).ConfigureAwait(false);
        ValidateReply(reply);
        return existing with { StatusReason = "Cancellation acknowledged; final order state awaits terminal data." };
    }

    /// <summary>QUIK has no per-instrument leverage setter; broker limits and FORTS collateral are not leverage settings.</summary>
    public Task SetLeverageAsync(InstrumentKey instrument, int leverage, CancellationToken ct = default)
    {
        RequireTrading(ct);
        ValidateInstrument(instrument);
        if (leverage < 1) throw new ArgumentOutOfRangeException(nameof(leverage));
        throw new NotSupportedException("QUIK does not support setting per-instrument leverage.");
    }

    /// <summary>Requires explicit execution opt-in; application-created sessions remain read-only.</summary>
    private IQuikTransactionClient RequireTrading(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _transactions ?? throw new NotSupportedException("QUIK transaction execution is disabled for this connection.");
    }

    /// <summary>Rejects invalid native units and unsupported guarantees before any transaction is sent.</summary>
    private void ValidateOrder(InstrumentKey instrument, OrderSide side, decimal quantity, bool postOnly, bool reduceOnly, PositionSide? positionSide, bool? hedged)
    {
        ValidateInstrument(instrument);
        if (!Enum.IsDefined(side) || quantity < 1 || decimal.Floor(quantity) != quantity || quantity > int.MaxValue)
            throw new ArgumentException("A valid direction and positive whole lots/contracts are required.");

        if (postOnly || reduceOnly || positionSide is not null || hedged == true)
            throw new NotSupportedException("QUIK net orders do not support Post-only, Reduce-only or hedge-side guarantees.");
    }

    /// <summary>Keeps the selected firm, client, account and listing explicit in every native command.</summary>
    private Transaction CreateTransaction(InstrumentKey instrument)
    {
        var route = _profile.Quik!;
        if (string.IsNullOrWhiteSpace(route.FirmId) || string.IsNullOrWhiteSpace(route.TradeAccount))
            throw new ArgumentException("A firm and trading account are required.");

        return new Transaction
        {
            FIRM_ID = route.FirmId, ACCOUNT = route.TradeAccount, CLIENT_CODE = route.ClientCode,
            CLASSCODE = instrument.Board, SECCODE = instrument.SecurityId
        };
    }

    /// <summary>Confirms broker connectivity and the bound account's routing before dispatch.</summary>
    private async Task EnsureAccountAsync(CancellationToken ct)
    {
        if (!await _client.ConnectedAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("QUIK is not connected to the broker.");

        await _client.BalanceAsync(_profile, ct).ConfigureAwait(false);
        RequireTrading(ct);
    }

    /// <summary>Preserves one dispatch and reports only a real native order identity as a pending acknowledgement.</summary>
    private async Task<OrderSnapshot> CreateOrderAsync(IQuikTransactionClient transactions, InstrumentKey instrument, OrderSide side,
        decimal quantity, decimal price, OrderType type, CancellationToken ct)
    {
        var transaction = CreateTransaction(instrument);
        transaction.ACTION = TransactionAction.NEW_ORDER;
        transaction.TYPE = type == OrderType.Limit ? TransactionType.L : TransactionType.M;
        transaction.OPERATION = side == OrderSide.Buy ? TransactionOperation.B : TransactionOperation.S;
        transaction.QUANTITY = (int)quantity;
        transaction.PRICE = price;
        transaction.EXECUTION_CONDITION = ExecutionCondition.PUT_IN_QUEUE;

        await EnsureAccountAsync(ct).ConfigureAwait(false);
        var reply = await transactions.SendAsync(transaction, ct).ConfigureAwait(false);
        ValidateReply(reply);
        if (reply.OrderNum is not > 0)
            throw new TradingServiceException(new TradingError("OutcomeUnknown", "QUIK acknowledged the transaction without an order number. Check terminal orders."));

        return new OrderSnapshot(new OrderReference(instrument, reply.OrderNum.Value.ToString(CultureInfo.InvariantCulture)),
            reply.TransID.ToString(CultureInfo.InvariantCulture), side, type, OrderState.Pending,
            quantity, null, null, type == OrderType.Limit ? price : null, null, null, DateTimeOffset.UtcNow);
    }

    /// <summary>Distinguishes exchange rejection from timeout without exposing native response text.</summary>
    private static void ValidateReply(TransactionReply reply)
    {
        if (reply.Status == 3) return;
        var unknown = reply.Status is 0 or 1 or 12;
        throw new TradingServiceException(new TradingError(unknown ? "OutcomeUnknown" : "TransactionRejected",
            unknown ? "QUIK transaction outcome is unknown. Check terminal orders." : $"QUIK rejected the transaction (status {reply.Status})."));
    }

    /// <summary>Polling is used instead of a synthetic private order stream.</summary>
    public async Task<IReadOnlyList<OrderSnapshot>> WatchOrdersAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        ValidateInstrument(instrument);
        await WaitForUpdateAsync(TradingStream.Orders, ct).ConfigureAwait(false);
        return await _client.OrdersAsync(_profile, instrument, ct).ConfigureAwait(false);
    }

    /// <summary>Polling is used instead of a synthetic private execution stream.</summary>
    public async Task<IReadOnlyList<ExecutionSnapshot>> WatchMyTradesAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        await WaitForUpdateAsync(TradingStream.MyTrades, ct).ConfigureAwait(false);
        return await FetchMyTradesAsync(instrument, ct).ConfigureAwait(false);
    }

    /// <summary>Polling is used instead of a synthetic private balance stream.</summary>
    public async Task<AccountSnapshot> WatchBalanceAsync(CancellationToken ct = default)
    {
        await WaitForUpdateAsync(TradingStream.Balance, ct).ConfigureAwait(false);
        return await FetchBalanceAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Releases this account reader without stopping other terminal consumers.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _client.AccountChanged -= OnAccountChanged;
        try
        {
            if (_transactions is not null) await _transactions.DisposeAsync().ConfigureAwait(false);
        }
        finally { await _client.DisposeAsync().ConfigureAwait(false); }
    }
}
