using System.Collections.Concurrent;
using System.Globalization;
using BotView.Configuration;
using QuikSharp.DataStructures;
using NativeOrder = QuikSharp.DataStructures.Transaction.Order;
using NativeTrade = QuikSharp.DataStructures.Transaction.Trade;
using AccountBalance = BotView.Models.AccountBalance;

namespace BotView.Services.Integrations.Quik;

public sealed record QuikRoutingChoice(string Firm, string Client, string Account, int LimitKind, string Classes)
{
    public string Label => $"{Firm} · {Account} · client {Client} · limit {LimitKind}";
}

/// <summary>The injectable terminal boundary exposes reads and subscriptions only.</summary>
internal interface IQuikReadClient : IAsyncDisposable
{
    event Action<OrderBookSnapshot>? BookChanged;
    event Action<TradingStream>? AccountChanged;
    Task<bool> ConnectedAsync(CancellationToken ct);
    Task<Instrument> InstrumentAsync(InstrumentKey key, CancellationToken ct);
    Task<OrderBookSnapshot> BookAsync(InstrumentKey key, CancellationToken ct);
    Task ReleaseBookAsync(InstrumentKey key);
    Task<IReadOnlyList<QuikRoutingChoice>> RoutesAsync(CancellationToken ct);
    Task<IReadOnlyList<OrderSnapshot>> OrdersAsync(TradingAccountProfile profile, InstrumentKey key, CancellationToken ct);
    Task<IReadOnlyList<ExecutionSnapshot>> TradesAsync(TradingAccountProfile profile, InstrumentKey key, CancellationToken ct);
    Task<AccountSnapshot> BalanceAsync(TradingAccountProfile profile, CancellationToken ct);
    Task<IReadOnlyList<PositionSnapshot>> PositionsAsync(TradingAccountProfile profile, InstrumentKey key, CancellationToken ct);
}

/// <summary>Uses QUIKSharp only for reads; no transaction, order creation or cancellation API is called.</summary>
internal sealed class QuikReadClient : IQuikReadClient
{
    private readonly QuikConnectionPool.Lease _lease;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<InstrumentKey, Instrument> _instruments = new();
    private readonly ConcurrentDictionary<InstrumentKey, bool> _subscriptions = new();
    // private readonly QuikQuoteBuffer _quotes = new();
    private QuikSharp.Quik? _client;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private bool _disposed;
    private readonly QuikOrderEventBuffer _orderEvents = new();
    public event Action<OrderBookSnapshot>? BookChanged;
    public event Action<TradingStream>? AccountChanged;

    /// <summary>Acquires a shared connection for a configured terminal endpoint.</summary>
    internal QuikReadClient(QuikConnectionOptions? options = null)
    {
        options ??= QuikConnectionOptions.Load();
        _timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        _lease = QuikConnectionPool.Acquire(options);
    }

    /// <summary>Initializes event ownership once, independently of other terminal consumers.</summary>
    private async Task<QuikSharp.Quik> ClientAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _initialization.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is null)
            {
                _client = await _lease.GetAsync(ct).ConfigureAwait(false);
                _client.Events.OnQuote += OnQuote;
                _client.Events.OnOrder += OnOrder;
                _client.Events.OnStopOrder += OnStopOrder;
                _client.Events.OnTrade += OnTrade;
                _client.Events.OnDepoLimit += OnDepoLimit;
                _client.Events.OnMoneyLimit += OnMoneyLimit;
                _client.Events.OnFuturesClientHolding += OnHolding;
                _client.Events.OnFuturesLimitChange += OnFuturesLimit;
            }
            return _client;
        }
        finally { _initialization.Release(); }
    }

    /// <summary>Checks the broker connection as well as transport responsiveness.</summary>
    public async Task<bool> ConnectedAsync(CancellationToken ct) =>
        await (await ClientAsync(ct)).Service.IsConnected((int)_timeout.TotalMilliseconds).WaitAsync(_timeout, ct);

    /// <summary>Loads pricing units without inventing absent monetary metadata.</summary>
    public async Task<Instrument> InstrumentAsync(InstrumentKey key, CancellationToken ct)
    {
        ValidateKey(key);
        if (_instruments.TryGetValue(key, out var cached)) return cached;
        var client = await ClientAsync(ct);
        var info = await client.Class.GetSecurityInfo(key.Board, key.SecurityId).WaitAsync(_timeout, ct);
        if (info is null || info.LotSize <= 0 || info.MinPriceStep <= 0)
            throw new InvalidOperationException("QUIK: отсутствуют размер лота или шаг цены.");

        var currency = await ParameterAsync(client, key, "CURRENCYID", ct);
        var tickValueText = key.Market == "futures" ? await ParameterAsync(client, key, "STEPPRICE", ct) : null;
        decimal? tickValue = decimal.TryParse(tickValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
        var instrument = new Instrument(key, $"{key.Board}:{key.SecurityId}", info.ShortName ?? info.Name ?? key.SecurityId,
            currency, info.LotSize, (decimal)info.MinPriceStep, TradingRules: new(
                key.Market == "futures" ? QuantityUnit.Contracts : QuantityUnit.Lots,
                key.Market == "futures" ? PriceQuotation.Points : PriceQuotation.CurrencyPerUnit,
                TickValue: tickValue, SettlementCurrency: currency));
        _instruments[key] = instrument;
        return instrument;
    }

    /// <summary>Leaves unavailable optional parameters unknown instead of substituting RUB or a multiplier.</summary>
    private async Task<string?> ParameterAsync(QuikSharp.Quik client, InstrumentKey key, string parameter, CancellationToken ct)
    {
        try
        {
            var result = await client.Trading.GetParamEx2(key.Board, key.SecurityId, parameter).WaitAsync(_timeout, ct);
            if (result is null) return null;
            if (parameter == "CURRENCYID")
            {
                var currency = string.IsNullOrWhiteSpace(result.ParamImage) ? result.ParamValue : result.ParamImage;
                return string.IsNullOrWhiteSpace(currency) || currency is "0" or "—" ? null : currency.Trim();
            }
            return string.IsNullOrWhiteSpace(result.ParamValue) ? null : result.ParamValue;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>Restores a subscription and waits for OnQuote; GetQuoteLevel2 RPC emits invalid correlated callbacks in QUIKSharp 2.0.0.</summary>
    public async Task<OrderBookSnapshot> BookAsync(InstrumentKey key, CancellationToken ct)
    {
        await InstrumentAsync(key, ct);
        var client = await ClientAsync(ct);
        var nextQuote = new TaskCompletionSource<OrderBookSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Completes this read from the matching event without retaining a second current snapshot.
        void ReceiveQuote(OrderBookSnapshot snapshot)
        {
            if (snapshot.Market.Instrument?.Key == key) nextQuote.TrySetResult(snapshot);
        }

        BookChanged += ReceiveQuote;
        try
        {
            await _lease.BookGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_subscriptions.TryAdd(key, true)) _lease.RetainBook(key);
                if (!await client.OrderBook.IsSubscribed(key.Board, key.SecurityId).WaitAsync(_timeout, ct))
                {
                    if (!await client.OrderBook.Subscribe(key.Board, key.SecurityId).WaitAsync(_timeout, ct))
                        throw new InvalidOperationException("QUIK отклонил подписку стакана.");
                }
            }
            finally { _lease.BookGate.Release(); }

            // return await _quotes.ReadAsync(key, _timeout, ct).ConfigureAwait(false);
            try { return await nextQuote.Task.WaitAsync(_timeout, ct).ConfigureAwait(false); }
            catch (TimeoutException) { throw new QuikQuoteWaitTimeoutException(); }
        }
        finally { BookChanged -= ReceiveQuote; }
    }

    /// <summary>Forwards only this reader's instruments; snapshots are detached from native arrays.</summary>
    private void OnQuote(QuikSharp.DataStructures.OrderBook book)
    {
        var key = Key(book.class_code, book.sec_code);
        if (_instruments.TryGetValue(key, out var instrument))
        {
            var snapshot = MapBook(book, instrument);
            // _quotes.Publish(key, snapshot);
            BookChanged?.Invoke(snapshot);
        }
    }

    /// <summary>Unsubscribes only after the final listing consumer leaves the shared terminal.</summary>
    public async Task ReleaseBookAsync(InstrumentKey key)
    {
        _instruments.TryRemove(key, out _);
        await _lease.BookGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_subscriptions.TryRemove(key, out _) && _lease.ReleaseBook(key) && _client is { } client)
            {
                try { await client.OrderBook.Unsubscribe(key.Board, key.SecurityId).WaitAsync(_timeout).ConfigureAwait(false); }
                catch (Exception) { /* A disconnected terminal releases subscriptions when its transport closes. */ }
            }
        }
        finally { _lease.BookGate.Release(); }
    }

    /// <summary>Discovers concrete routing tuples rather than guessing a client or settlement limit.</summary>
    public async Task<IReadOnlyList<QuikRoutingChoice>> RoutesAsync(CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var accounts = await client.Class.GetTradeAccounts().WaitAsync(_timeout, ct);
        var deposits = await client.Trading.GetDepoLimits().WaitAsync(_timeout, ct);
        var money = await client.Trading.GetMoneyLimits().WaitAsync(_timeout, ct);
        var futures = await client.Trading.GetFuturesClientLimits().WaitAsync(_timeout, ct);
        var orders = await client.Orders.GetOrders().WaitAsync(_timeout, ct);
        var trades = await client.Trading.GetTrades().WaitAsync(_timeout, ct);
        var result = new List<QuikRoutingChoice>();
        foreach (var account in accounts)
        {
            if (account.ClassCodes.Contains("TQBR", StringComparison.Ordinal))
            {
                foreach (var deposit in deposits.Where(d => d.FirmId == account.Firmid && d.TrdAccId == account.TrdaccId))
                    result.Add(new(account.Firmid, deposit.ClientCode ?? "", account.TrdaccId, (int)deposit.LimitKindInt, "TQBR"));
                // Empty portfolios can still have cash limits; the user explicitly selects an account/client tuple.
                foreach (var cash in money.Where(m => m.FirmId == account.Firmid))
                    result.Add(new(account.Firmid, cash.ClientCode ?? "", account.TrdaccId, cash.LimitKind, "TQBR"));
            }
            if (!account.ClassCodes.Contains("SPBFUT", StringComparison.Ordinal)) continue;
            var clients = orders.Where(o => o.FirmId == account.Firmid && o.Account == account.TrdaccId).Select(o => o.ClientCode)
                .Concat(trades.Where(t => t.FirmId == account.Firmid && t.Account == account.TrdaccId).Select(t => t.ClientCode))
                .Where(c => c is not null).Distinct().ToList();
            if (clients.Count == 0)
            {
                try
                {
                    clients.Add(await client.Trading.GetClientCodeByTrdAcc(account.Firmid, account.TrdaccId).WaitAsync(_timeout, ct) ?? "");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { clients.Add(""); }
            }
            foreach (var limit in futures.Where(f => f.FirmId == account.Firmid && f.TrdAccId == account.TrdaccId))
                foreach (var code in clients)
                    result.Add(new(account.Firmid, code, account.TrdaccId, limit.LimitType, "SPBFUT"));
        }
        return result.Distinct().ToArray();
    }

    /// <summary>Reads normal and conditional orders into separate identities, including external orders.</summary>
    public async Task<IReadOnlyList<OrderSnapshot>> OrdersAsync(TradingAccountProfile profile, InstrumentKey key, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var route = profile.Quik!;
        var started = _orderEvents.Version;
        var orders = await client.Orders.GetOrders(key.Board, key.SecurityId).WaitAsync(_timeout, ct);
        var stops = await client.StopOrders.GetStopOrders(key.Board, key.SecurityId).WaitAsync(_timeout, ct);
        var accounts = await client.Class.GetTradeAccounts().WaitAsync(_timeout, ct);
        var firms = accounts.Where(a => a.TrdaccId == route.TradeAccount && a.ClassCodes.Contains(key.Board, StringComparison.Ordinal))
            .Select(a => a.Firmid).Distinct().ToArray();
        if (firms.Length != 1 || firms[0] != route.FirmId)
            throw new InvalidOperationException("QUIK: невозможно однозначно определить фирму условных заявок.");
        var snapshot = orders.Where(o => Matches(route, o.FirmId, o.Account, o.ClientCode) && o.ClassCode == key.Board && o.SecCode == key.SecurityId)
            .Select(o => MapOrder(o, key))
            .Concat(stops.Where(s => s.Account == route.TradeAccount && s.ClientCode == route.ClientCode && s.ClassCode == key.Board && s.SecCode == key.SecurityId)
                .Select(s => MapStop(s, key))).ToArray();
        return _orderEvents.Merge(route, key, snapshot, started);
    }

    /// <summary>Reads executions using account and listing filters before de-duplicating trade identifiers.</summary>
    public async Task<IReadOnlyList<ExecutionSnapshot>> TradesAsync(TradingAccountProfile profile, InstrumentKey key, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var trades = await client.Trading.GetTrades(key.Board, key.SecurityId).WaitAsync(_timeout, ct);
        return trades.Where(t => Matches(profile.Quik!, t.FirmId, t.Account, t.ClientCode) && t.ClassCode == key.Board && t.SecCode == key.SecurityId)
            .GroupBy(t => t.TradeNum).Select(g => g.Last())
            .Select(t => new ExecutionSnapshot(t.TradeNum.ToString(CultureInfo.InvariantCulture), new(key, t.OrderNum.ToString(CultureInfo.InvariantCulture)),
                (t.Flags & QuikSharp.DataStructures.Transaction.OrderTradeFlags.IsSell) != 0 ? OrderSide.Sell : OrderSide.Buy,
                t.Quantity, (decimal)t.Price, Timestamp(t.QuikDateTime))).ToArray();
    }

    /// <summary>Uses the selected limit kind; futures collateral and stock cash remain distinct.</summary>
    public async Task<AccountSnapshot> BalanceAsync(TradingAccountProfile profile, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        if (!await ConnectedAsync(ct)) throw new InvalidOperationException("QUIK не подключён к брокеру.");
        var route = profile.Quik!;
        var context = Context(profile);
        var accounts = await client.Class.GetTradeAccounts().WaitAsync(_timeout, ct);
        var board = profile.Market == TradingMarket.MoexFutures ? "SPBFUT" : "TQBR";
        if (!accounts.Any(a => a.Firmid == route.FirmId && a.TrdaccId == route.TradeAccount && a.ClassCodes.Contains(board, StringComparison.Ordinal)))
            throw new InvalidOperationException("QUIK не вернул выбранный торговый счёт для этого класса инструментов.");
        if (profile.Market == TradingMarket.MoexFutures)
        {
            var limits = await client.Trading.GetFuturesClientLimits().WaitAsync(_timeout, ct);
            var selected = limits.Where(l => l.FirmId == route.FirmId && l.TrdAccId == route.TradeAccount && l.LimitType == route.LimitKind).ToArray();
            return new(context, [], null, selected.Select(l => new MarginSnapshot(l.CurrCode,
                Equity: (decimal)l.CbpLimit, AvailableFunds: (decimal)(l.CbpLimit - l.CbpLUsed),
                InitialMargin: (decimal)l.CbpLUsedForPositions, VariationMargin: (decimal)l.VarMargin)).ToArray(), DateTimeOffset.UtcNow);
        }

        var money = await client.Trading.GetMoneyLimits().WaitAsync(_timeout, ct);
        var balances = money.Where(m => m.FirmId == route.FirmId && m.ClientCode == route.ClientCode && m.LimitKind == route.LimitKind)
            .GroupBy(m => (m.CurrCode, m.Tag)).Select(g => g.Last())
            .Select(m => new AccountBalance(m.CurrCode, (decimal)m.CurrentBal, (decimal)(m.CurrentBal - m.Locked), (decimal)m.Locked)).ToArray();
        return new(context, balances, null, null, DateTimeOffset.UtcNow);
    }

    /// <summary>Reads signed terminal holdings and converts share units to the same lots used in orders.</summary>
    public async Task<IReadOnlyList<PositionSnapshot>> PositionsAsync(TradingAccountProfile profile, InstrumentKey key, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var route = profile.Quik!;
        var instrument = await InstrumentAsync(key, ct);
        decimal quantity;
        decimal? average;
        if (key.Market == "futures")
        {
            var holding = await client.Trading.GetFuturesHolding(route.FirmId, route.TradeAccount, key.SecurityId, route.LimitKind).WaitAsync(_timeout, ct);
            if (holding is null) return [];
            quantity = (decimal)holding.totalNet;
            average = (decimal)holding.avrPosnPrice;
        }
        else
        {
            var holdings = await client.Trading.GetDepoLimits().WaitAsync(_timeout, ct);
            var holding = holdings.SingleOrDefault(h => h.FirmId == route.FirmId && h.TrdAccId == route.TradeAccount &&
                h.ClientCode == route.ClientCode && h.LimitKindInt == route.LimitKind && h.SecCode == key.SecurityId);
            if (holding is null) return [];
            quantity = holding.CurrentBalance / instrument.LotSize;
            average = (decimal)holding.AweragePositionPrice;
        }
        return quantity == 0 ? [] : [new(key, quantity > 0 ? PositionSide.Long : PositionSide.Short,
            Math.Abs(quantity), key.Market == "futures" ? QuantityUnit.Contracts : QuantityUnit.Lots, average, Hedged: false)];
    }

    /// <summary>Keeps every account discriminator explicit instead of accepting partial routing matches.</summary>
    internal static bool Matches(QuikAccountOptions route, string firm, string account, string client) =>
        firm == route.FirmId && account == route.TradeAccount && client == route.ClientCode;

    /// <summary>Uses the configured terminal endpoint as connection identity, not a profile display name.</summary>
    internal static TradingAccountContext Context(TradingAccountProfile profile) => new(
        $"quik:{profile.Quik!.Host}:{profile.Quik.Port}",
        $"{profile.Quik.FirmId}/{profile.Quik.TradeAccount}/{profile.Quik.ClientCode}/{profile.Quik.LimitKind}",
        "MOEX", profile.Environment, profile.Market);

    /// <summary>Maps the native trading class without treating an identically named security as the same listing.</summary>
    internal static InstrumentKey Key(string board, string security) => new("MOEX", board == "SPBFUT" ? "futures" : "shares", board, security);

    /// <summary>Rejects unsupported boards before any read or subscription.</summary>
    private static void ValidateKey(InstrumentKey key)
    {
        if (key.Venue != "MOEX" || key.Board is not ("TQBR" or "SPBFUT") || Key(key.Board, key.SecurityId) != key)
            throw new NotSupportedException("Чтение QUIK поддерживает TQBR и SPBFUT.");
    }

    /// <summary>Copies, sorts and groups levels; QUIK Level II quantities already use lots/contracts.</summary>
    internal static OrderBookSnapshot MapBook(QuikSharp.DataStructures.OrderBook book, Instrument instrument) => new(
        new(instrument.Symbol, instrument.TradingRules!.QuantityUnit == QuantityUnit.Contracts ? "contracts" : "lots",
            instrument.Currency ?? "—", instrument.PriceStep!.Value, instrument),
        (book.bid ?? []).Select(l => new OrderBookLevel((decimal)l.price, (decimal)l.quantity)).GroupBy(l => l.Price)
            .Select(g => new OrderBookLevel(g.Key, g.Sum(l => l.Amount))).OrderByDescending(l => l.Price).ToArray(),
        (book.offer ?? []).Select(l => new OrderBookLevel((decimal)l.price, (decimal)l.quantity)).GroupBy(l => l.Price)
            .Select(g => new OrderBookLevel(g.Key, g.Sum(l => l.Amount))).OrderBy(l => l.Price).ToArray(), DateTimeOffset.UtcNow);

    /// <summary>Preserves exchange identity and partial fill amounts for regular orders.</summary>
    internal static OrderSnapshot MapOrder(NativeOrder order, InstrumentKey key) => new(
        new(key, order.OrderNum.ToString(CultureInfo.InvariantCulture)), null,
        order.Operation == Operation.Sell ? OrderSide.Sell : OrderSide.Buy,
        (order.Flags & QuikSharp.DataStructures.Transaction.OrderTradeFlags.IsLimit) != 0 ? OrderType.Limit : OrderType.Market,
        State(order.State), order.Quantity, order.Quantity - order.Balance, order.Balance, order.Price, null,
        Timestamp(order.Datetime), DateTimeOffset.UtcNow);

    /// <summary>Conditional identity remains separate even when the terminal reuses a numeric order identifier.</summary>
    internal static OrderSnapshot MapStop(StopOrder stop, InstrumentKey key) => new(
        new(key, stop.OrderNum.ToString(CultureInfo.InvariantCulture), OrderCategory.Conditional), null,
        stop.Operation == Operation.Sell ? OrderSide.Sell : OrderSide.Buy,
        stop.Price == 0 ? OrderType.Stop : OrderType.StopLimit, State(stop.State), stop.Quantity,
        stop.FilledQuantity, stop.Quantity - stop.FilledQuantity, stop.Price == 0 ? null : stop.Price, null,
        null, DateTimeOffset.UtcNow, $"Trigger: {stop.ConditionPrice}");

    /// <summary>Records the newer native order state before waking account readers.</summary>
    private void OnOrder(NativeOrder order)
    {
        _orderEvents.Add(order.FirmId, order.Account, order.ClientCode, MapOrder(order, Key(order.ClassCode, order.SecCode)));
        AccountChanged?.Invoke(TradingStream.Orders);
    }

    /// <summary>Uses account/client routing for callbacks whose native structure has no firm field.</summary>
    private void OnStopOrder(StopOrder order)
    {
        _orderEvents.Add(null, order.Account, order.ClientCode, MapStop(order, Key(order.ClassCode, order.SecCode)));
        AccountChanged?.Invoke(TradingStream.Orders);
    }

    /// <summary>Wakes execution readers; IDs are reconciled by the account session.</summary>
    private void OnTrade(NativeTrade trade) => AccountChanged?.Invoke(TradingStream.MyTrades);

    /// <summary>Wakes balance readers when equity holdings change.</summary>
    private void OnDepoLimit(DepoLimitEx limit) => AccountChanged?.Invoke(TradingStream.Balance);

    /// <summary>Wakes balance readers when cash limits change.</summary>
    private void OnMoneyLimit(MoneyLimitEx limit) => AccountChanged?.Invoke(TradingStream.Balance);

    /// <summary>Wakes balance readers when a derivative holding changes.</summary>
    private void OnHolding(FuturesClientHolding holding) => AccountChanged?.Invoke(TradingStream.Balance);

    /// <summary>Wakes collateral readers when futures limits change.</summary>
    private void OnFuturesLimit(FuturesLimits limit) => AccountChanged?.Invoke(TradingStream.Balance);

    /// <summary>Uses Moscow exchange time rather than the workstation's local timezone.</summary>
    private static DateTimeOffset Timestamp(QuikDateTime? value)
    {
        if (value is null || value.year < 1970 || value.month is < 1 or > 12 || value.day < 1) return DateTimeOffset.MinValue;
        try { return new(value.year, value.month, value.day, value.hour, value.min, value.sec, TimeSpan.FromHours(3)); }
        catch (ArgumentOutOfRangeException) { return DateTimeOffset.MinValue; }
    }

    /// <summary>Maps terminal state without inventing pending exchange acknowledgements.</summary>
    private static OrderState State(QuikSharp.DataStructures.State state) => state switch
    {
        QuikSharp.DataStructures.State.Active => OrderState.Open,
        QuikSharp.DataStructures.State.Canceled => OrderState.Canceled,
        QuikSharp.DataStructures.State.Completed => OrderState.Filled,
        _ => OrderState.Unknown
    };

    /// <summary>Removes this client's callbacks before releasing its lease.</summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_client is not null)
        {
            _client.Events.OnQuote -= OnQuote;
            _client.Events.OnOrder -= OnOrder;
            _client.Events.OnStopOrder -= OnStopOrder;
            _client.Events.OnTrade -= OnTrade;
            _client.Events.OnDepoLimit -= OnDepoLimit;
            _client.Events.OnMoneyLimit -= OnMoneyLimit;
            _client.Events.OnFuturesClientHolding -= OnHolding;
            _client.Events.OnFuturesLimitChange -= OnFuturesLimit;
        }
        foreach (var key in _subscriptions.Keys) await ReleaseBookAsync(key).ConfigureAwait(false);
        await _lease.DisposeAsync();
    }
}

/// <summary>Distinguishes a quiet quote stream from a failed terminal RPC.</summary>
internal sealed class QuikQuoteWaitTimeoutException : TimeoutException
{
    /// <summary>Reports waiting for real data without fabricating an empty depth snapshot.</summary>
    internal QuikQuoteWaitTimeoutException() : base("QUIK: ожидание события OnQuote.") { }
}
