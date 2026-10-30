using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BotView.Models;
using BotView.Services;

namespace BotView.Services.Integrations.Ccxt;

/// <summary>Binance/Bybit spot and USDT contract mapping. Raw CCXT values and signed errors never escape this boundary.</summary>
internal sealed class CcxtSpotTradingClient : ISpotTradingClient
{
    private readonly ccxt.Exchange _client;
    private readonly TradingAccountProfile _profile;
    private readonly string _keyIdentity;
    private readonly Dictionary<string, SpotMarket> _markets = new();
    private readonly Dictionary<string, DateTimeOffset> _marketTimes = new();
    private string _bybitAccountType = "UNIFIED";
    private readonly Dictionary<string, ccxt.MarketInterface> _rawMarkets = new();
    internal ccxt.Exchange Transport => _client;
    private readonly ConcurrentDictionary<string, string> _bybitOrderFilters = new();
    private readonly ConcurrentDictionary<string, (PlaceOrderRequest Request, bool Hedged)> _validatedModes = new();
    public bool IsFutures => _profile.Market == TradingMarket.UsdtFutures;
    private string Category => IsFutures ? "linear" : "spot";
    private string Listing => IsFutures ? "futures" : "spot";

    /// <summary>Creates an isolated Pro client and selects sandbox before any request.</summary>
    internal CcxtSpotTradingClient(TradingAccountProfile profile, AccountCredentials credentials,
        ccxt.Exchange? transport = null)
    {
        _profile = profile;
        var settings = ProOrderBookClient.CreateSettings();
        settings["apiKey"] = credentials.ApiKey;
        settings["secret"] = credentials.Secret;
        settings["verbose"] = false;
        _client = transport ?? (IsFutures ? CreateFuturesExchange(profile, settings) : CreateSpotExchange(profile, settings));
        _keyIdentity = "key:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentials.ApiKey)));
        if (profile.Environment == TradingEnvironment.Testnet) _client.setSandboxMode(true);
    }

    /// <summary>Loads active USDT-margined contract symbols that match the spot pair.</summary>
    public async Task<IReadOnlyList<string>> LoadTradingPairsAsync(string spotSymbol, CancellationToken ct = default)
    {
        var markets = await _client.LoadMarkets().WaitAsync(ct).ConfigureAwait(false);
        var pair = spotSymbol.Split(':')[0];
        return markets.Values
            .Where(m => IsLinearUsdtContract(m) && m.active != false && m.symbol!.Split(':')[0] == pair)
            .OrderBy(m => m.expiry.HasValue)
            .ThenBy(m => m.expiry)
            .Select(m => m.symbol!)
            .ToArray();
    }

    /// <summary>Authenticates and obtains a stable owner ID; a key fingerprint isolates recovery when no owner ID is returned.</summary>
    public Task<(string Identity, bool? CanTrade)> ConnectAsync(CancellationToken ct) =>
        IsFutures ? ConnectFuturesAsync(ct) : ConnectSpotAsync(ct);

    /// <summary>Confirms futures access and rejects Binance multi-asset margin, which this adapter does not settle.</summary>
    private async Task<(string Identity, bool? CanTrade)> ConnectFuturesAsync(CancellationToken ct)
    {
        await _client.FetchBalance(Parameters()).WaitAsync(ct).ConfigureAwait(false);
        if (_client is ccxt.binance futuresBinance)
        {
            var mode = await futuresBinance.fapiPrivateGetMultiAssetsMargin().WaitAsync(ct).ConfigureAwait(false);
            if (mode.TryGetValue("multiAssetsMargin", out var enabled) && Convert.ToBoolean(enabled, CultureInfo.InvariantCulture))
            {
                throw new NotSupportedException("USDT futures currently require single-asset margin mode.");
            }
        }

        if (_client is ccxt.bybit futuresBybit)
        {
            var futuresKeyResponse = await futuresBybit.privateGetV5UserQueryApi().WaitAsync(ct).ConfigureAwait(false);
            var keyInfo = (IDictionary<string, object>)futuresKeyResponse["result"];
            bool? allowed = Text(keyInfo, "readOnly") == "1" ? false : null;
            if (keyInfo.TryGetValue("permissions", out var futuresPermissions) && futuresPermissions is IDictionary<string, object> permissionMap &&
                permissionMap.TryGetValue("ContractTrade", out var contractRights) && contractRights is IEnumerable rights)
            {
                allowed = allowed == false ? false : rights.Cast<object>().Any(p => p?.ToString() == "Order");
            }

            var owner = Text(keyInfo, "userID");
            return (string.IsNullOrWhiteSpace(owner) ? _keyIdentity : "uid:" + owner, allowed);
        }

        return (_keyIdentity, null);
    }

    /// <summary>Reads the spot account owner and whether this key itself may trade.</summary>
    private async Task<(string Identity, bool? CanTrade)> ConnectSpotAsync(CancellationToken ct)
    {
        if (_client is ccxt.binance binance)
        {
            var account = await binance.privateGetAccount().WaitAsync(ct).ConfigureAwait(false);
            var uid = Text(account, "uid");
            bool? canTrade = account.TryGetValue("canTrade", out var flag) && flag is bool allowed ? allowed : null;
            bool? keyCanTrade = null;
            if (_profile.Environment == TradingEnvironment.Production)
            {
                try { keyCanTrade = CcxtAccountVerificationClient.ParseBinancePermissions(await binance.sapiGetAccountApiRestrictions().WaitAsync(ct).ConfigureAwait(false)).CanTrade; }
                catch (ccxt.ExchangeError) { }
            }
            // Account-wide canTrade is not proof that this particular API key has trading permission.
            return (string.IsNullOrWhiteSpace(uid) ? _keyIdentity : "uid:" + uid, canTrade == false || keyCanTrade == false ? false : keyCanTrade);
        }
        var response = await ((ccxt.bybit)_client).privateGetV5UserQueryApi().WaitAsync(ct).ConfigureAwait(false);
        var result = response["result"] as IDictionary<string, object> ?? throw new NotSupportedException("Account identity unavailable.");
        var permissions = CcxtAccountVerificationClient.ParseBybitPermissions(result);
        _bybitAccountType = Text(result, "uta") == "0" ? "SPOT" : "UNIFIED";
        return (string.IsNullOrWhiteSpace(Text(result, "userID")) ? _keyIdentity : "uid:" + Text(result, "userID"), permissions.CanTrade);
    }

    /// <summary>Loads spot filters and account fees, retaining only detached application metadata.</summary>
    public async Task<SpotMarket> MarketAsync(InstrumentKey key, CancellationToken ct)
    {
        if (key.Venue != _profile.Exchange || key.Market != Listing || key.Board != "") throw new NotSupportedException("Only this account's spot listings are supported.");
        if (_markets.TryGetValue(key.SecurityId, out var cached) && DateTimeOffset.UtcNow - _marketTimes[key.SecurityId] < TimeSpan.FromMinutes(5)) return cached;
        if (_markets.Count > 0) await _client.loadMarkets(true).WaitAsync(ct).ConfigureAwait(false);
        var markets = await _client.LoadMarkets().WaitAsync(ct).ConfigureAwait(false);
        if (!markets.TryGetValue(key.SecurityId, out var market) || !IsTradable(market) || market.active == false)
            throw new NotSupportedException("The spot instrument is unavailable.");
        decimal? fee = null;
        if (_client.has.GetValueOrDefault("fetchTradingFee") is true && !(_profile.Exchange == "binance" && _profile.Environment == TradingEnvironment.Testnet))
        {
            try
            {
                var rates = await _client.FetchTradingFee(key.SecurityId).WaitAsync(ct).ConfigureAwait(false);
                if (rates.percentage != false && rates.maker.HasValue && rates.taker.HasValue)
                    fee = Math.Max(0, Math.Max(SpotOrderMath.Number(rates.maker)!.Value, SpotOrderMath.Number(rates.taker)!.Value));
            }
            catch (ccxt.ExchangeError) { }
        }
        if (fee is null && market.percentage == true && market.maker.HasValue && market.taker.HasValue)
            fee = Math.Max(0, Math.Max(SpotOrderMath.Number(market.maker)!.Value, SpotOrderMath.Number(market.taker)!.Value));
        var instrument = DescribeInstrument(CcxtMarketDataProvider.MapInstrument(_profile.Exchange, market, Convert.ToInt32(_client.precisionMode)), market, key);

        cached = new(instrument, market.baseCurrency!, market.quote!, fee, market.feeSide);
        _rawMarkets[key.SecurityId] = market;
        _markets[key.SecurityId] = cached;
        _marketTimes[key.SecurityId] = DateTimeOffset.UtcNow;
        return cached;
    }

    /// <summary>Truncates quantities with CCXT's actual precision mode rather than assuming decimal places.</summary>
    public decimal NormalizeAmount(string symbol, decimal amount) => decimal.Parse(_client.amountToPrecision(symbol, amount.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);

    /// <summary>Normalizes prices for equality checks; callers reject any changed entered price.</summary>
    public decimal NormalizePrice(string symbol, decimal price) => decimal.Parse(_client.priceToPrecision(symbol, price.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);

    /// <summary>Verifies both precision and the typed CCXT double boundary before dispatch.</summary>
    internal double WireNumber(string symbol, decimal value, bool amount)
    {
        var wire = (double)value;
        var text = wire.ToString("R", CultureInfo.InvariantCulture);
        var roundTrip = decimal.Parse((amount ? _client.amountToPrecision(symbol, text) : _client.priceToPrecision(symbol, text))!, CultureInfo.InvariantCulture);
        if (roundTrip != value) throw new ArgumentException("The value cannot be represented at the exchange precision.");
        return wire;
    }

    /// <summary>Maps Post-only explicitly, never downgrading a rejected maker request to an ordinary limit.</summary>
    internal static Dictionary<string, object> OrderParameters(string exchange, PlaceOrderRequest request)
    {
        var parameters = new Dictionary<string, object> { ["clientOrderId"] = request.ClientOrderId };
        if (exchange == "binance")
        {
            if (request.PostOnly) parameters["postOnly"] = true;
            else parameters["timeInForce"] = "GTC";
            parameters["newOrderRespType"] = "FULL";
        }
        else
        {
            parameters["category"] = "spot";
            parameters["isLeverage"] = 0;
            parameters["timeInForce"] = request.PostOnly ? "PostOnly" : "GTC";
        }
        return parameters;
    }

    /// <summary>Sends exactly once; transport uncertainty is classified by the account service.</summary>
    public async Task<OrderSnapshot> PlaceAsync(PlaceOrderRequest request, CancellationToken ct)
    {
        var symbol = request.Instrument.SecurityId;
        var amount = WireNumber(symbol, request.Quantity, true);
        var price = WireNumber(symbol, request.Price!.Value, false);
        var parameters = await CreateOrderParametersAsync(request, ct).ConfigureAwait(false);

        var order = await _client.CreateOrder(symbol, "limit", request.Side == OrderSide.Buy ? "buy" : "sell", amount, price,
            parameters).WaitAsync(ct).ConfigureAwait(false);
        return MapOrder(order, request.Instrument) with { ClientOrderId = request.ClientOrderId,
            Quantity = request.Quantity, Price = request.Price, Side = request.Side,
            PositionSide = request.PositionSide, ReduceOnly = request.ReduceOnly };
    }

    /// <summary>An acknowledgement of cancellation is not assumed to be a final canceled state.</summary>
    public async Task<OrderSnapshot?> CancelAsync(OrderReference order, CancellationToken ct)
    {
        if (_client is ccxt.bybit bybit)
        {
            await bybit.privatePostV5OrderCancel(BybitCancelParameters(order)).WaitAsync(ct).ConfigureAwait(false);
            return null;
        }
        return MapOrder(await _client.CancelOrder(order.ExchangeOrderId, order.Instrument.SecurityId, Parameters()).WaitAsync(ct).ConfigureAwait(false), order.Instrument);
    }

    /// <summary>Returns null only for the provider's explicit order-not-found response.</summary>
    public async Task<OrderSnapshot?> OrderAsync(OrderReference order, CancellationToken ct)
    {
        if (_profile.Exchange == "bybit")
        {
            foreach (var open in new[] { true, false })
            {
                var page = await BybitOrdersAsync(new(order.Instrument), open, ct, exchangeId: order.ExchangeOrderId).ConfigureAwait(false);
                var found = page.Items.FirstOrDefault(o => o.Reference == order);
                if (found is not null) return found;
            }
            return null;
        }
        try { return MapOrder(await _client.FetchOrder(order.ExchangeOrderId, order.Instrument.SecurityId, Parameters()).WaitAsync(ct).ConfigureAwait(false), order.Instrument); }
        catch (ccxt.OrderNotFound) { return null; }
    }

    /// <summary>Looks up the original client ID, including Bybit history after an order leaves the realtime cache.</summary>
    public async Task<OrderSnapshot?> FindAsync(InstrumentKey key, string clientId, CancellationToken ct)
    {
        if (_profile.Exchange == "binance")
            return await (IsFutures ? FindBinanceFuturesOrderAsync(key, clientId, ct) : FindBinanceSpotOrderAsync(key, clientId, ct)).ConfigureAwait(false);
        foreach (var open in new[] { true, false })
        {
            var page = await BybitOrdersAsync(new(key), open, ct, clientId).ConfigureAwait(false);
            foreach (var order in page.Items) if (order.ClientOrderId == clientId) return order;
        }
        return null;
    }

    /// <summary>Uses provider continuation: Bybit cursor, Binance increasing order ID for history.</summary>
    public async Task<TradingPage<OrderSnapshot>> OrdersAsync(TradingQuery query, bool open, CancellationToken ct)
    {
        var key = query.Instrument ?? throw new NotSupportedException("A spot instrument is required.");
        if (_profile.Exchange == "bybit") return await BybitOrdersAsync(query, open, ct).ConfigureAwait(false);
        var parameters = Parameters();
        if (query.Until.HasValue) parameters[_profile.Exchange == "binance" ? "endTime" : "endTime"] = query.Until.Value.ToUnixTimeMilliseconds();
        if (query.Cursor is not null) parameters[_profile.Exchange == "binance" ? "orderId" : "cursor"] = query.Cursor;
        var limit = _profile.Exchange == "binance" && open ? (int?)null : Math.Clamp(query.Limit, 1, _profile.Exchange == "bybit" ? 50 : 1000);
        var orders = open
            ? await _client.FetchOpenOrders(key.SecurityId, null, limit, parameters).WaitAsync(ct).ConfigureAwait(false)
            : await _client.FetchOrders(key.SecurityId, query.Since?.ToUnixTimeMilliseconds(), limit, parameters).WaitAsync(ct).ConfigureAwait(false);
        string? next = null;
        if (_profile.Exchange == "binance" && !open && orders.Count == limit && long.TryParse(orders[^1].id, out var id)) next = (id + 1).ToString(CultureInfo.InvariantCulture);
        return new(orders.Select(o => MapOrder(o, key)).ToArray(), string.IsNullOrEmpty(next) ? null : next);
    }

    /// <summary>Fetches paged actual executions; the caller persists identities and overlaps its watermark.</summary>
    public async Task<TradingPage<ExecutionSnapshot>> ExecutionsAsync(TradingQuery query, CancellationToken ct)
    {
        var key = query.Instrument ?? throw new NotSupportedException("A spot instrument is required.");
        if (_profile.Exchange == "bybit")
        {
            var request = BybitQuery(query);
            var response = await ((ccxt.bybit)_client).privateGetV5ExecutionList(request).WaitAsync(ct).ConfigureAwait(false);
            return ParseBybitExecutions(response, key);
        }
        var parameters = Parameters();
        if (query.Cursor is not null) parameters[_profile.Exchange == "binance" ? "fromId" : "cursor"] = query.Cursor;
        if (query.Until.HasValue) parameters["endTime"] = query.Until.Value.ToUnixTimeMilliseconds();
        var limit = Math.Clamp(query.Limit, 1, _profile.Exchange == "bybit" ? 100 : 1000);
        var trades = await _client.FetchMyTrades(key.SecurityId, query.Cursor is null ? query.Since?.ToUnixTimeMilliseconds() : null, limit, parameters).WaitAsync(ct).ConfigureAwait(false);
        string? next = trades.Count > 0 ? ResponseCursor(trades[^1].info) : null;
        if (_profile.Exchange == "binance" && trades.Count == limit && long.TryParse(trades[^1].id, out var id)) next = (id + 1).ToString(CultureInfo.InvariantCulture);
        return new(trades.Select(t => MapExecution(t, key)).ToArray(), string.IsNullOrEmpty(next) ? null : next);
    }

    /// <summary>Copies balances, preserving unknown free/used values instead of manufacturing zeroes.</summary>
    public Task<AccountSnapshot> BalanceAsync(TradingAccountContext account, CancellationToken ct) =>
        IsFutures ? FuturesBalanceAsync(account, ct) : SpotBalanceAsync(account, ct);

    /// <summary>Copies spot wallet balances, preserving unknown free/used values instead of manufacturing zeroes.</summary>
    private async Task<AccountSnapshot> SpotBalanceAsync(TradingAccountContext account, CancellationToken ct)
    {
        if (_client is ccxt.bybit bybit)
        {
            var raw = await bybit.privateGetV5AccountWalletBalance(new Dictionary<string, object> { ["accountType"] = _bybitAccountType }).WaitAsync(ct).ConfigureAwait(false);
            var walletBalances = new List<AccountBalance>();
            var result = (IDictionary<string, object>)raw["result"];
            foreach (var wallet in ((IEnumerable)result["list"]).Cast<IDictionary<string, object>>())
                foreach (var coin in ((IEnumerable)wallet["coin"]).Cast<IDictionary<string, object>>())
                {
                    var total = DecimalField(coin, "walletBalance");
                    var locked = DecimalField(coin, "locked");
                    var borrow = DecimalField(coin, "spotBorrow") ?? DecimalField(coin, "borrowAmount");
                    // Wallet holdings, less locked/borrowed assets; available margin is not spot cash.
                    var free = DecimalField(coin, "free") ?? (total.HasValue && locked.HasValue ? Math.Max(0, total.Value - locked.Value - (borrow ?? 0)) : (decimal?)null);
                    walletBalances.Add(new(Text(coin, "coin")!, total, free, locked));
                }
            return new(account, walletBalances, null, null, DateTimeOffset.UtcNow);
        }
        var balances = await _client.FetchBalance(Parameters()).WaitAsync(ct).ConfigureAwait(false);
        return MapBalance(balances, account);
    }

    /// <summary>Checks runtime capabilities because private-stream support differs by exchange and version.</summary>
    public bool SupportsStream(string kind) => _client.has.GetValueOrDefault(kind) is true;

    /// <summary>Copies a private stream batch before calling watch again.</summary>
    public async Task<IReadOnlyList<TradingUpdate>> WatchAsync(string kind, InstrumentKey key, TradingAccountContext account, CancellationToken ct)
    {
        if (kind == "watchOrders") return (await _client.WatchOrders(key.SecurityId, null, null, Parameters()).WaitAsync(ct).ConfigureAwait(false))
            .Where(o => o.symbol == key.SecurityId).Select(o => (TradingUpdate)new OrderChanged(MapAccountOrder(o, key))).ToArray();
        if (kind == "watchMyTrades") return (await _client.WatchMyTrades(key.SecurityId, null, null, Parameters()).WaitAsync(ct).ConfigureAwait(false))
            .Where(t => t.symbol == key.SecurityId).Select(t => (TradingUpdate)new ExecutionReceived(MapExecution(t, key))).ToArray();
        return [new AccountChanged(MapBalance(await _client.WatchBalance(Parameters()).WaitAsync(ct).ConfigureAwait(false), account))];
    }

    /// <summary>Routes unified calls without leaking derivative-only options into spot HTTP requests.
    /// The linear subtype is configured on the futures client, not forwarded to individual endpoints.</summary>
    private Dictionary<string, object> Parameters() => IsFutures ? FuturesParameters() : SpotParameters();

    /// <summary>Spot requests stay free of derivative routing options.</summary>
    private static Dictionary<string, object> SpotParameters() => new();

    /// <summary>Selects the USDT-M route. Linearity stays on the futures client, not on each request.</summary>
    private Dictionary<string, object> FuturesParameters() => new()
    {
        ["type"] = _profile.Exchange == "binance" ? "future" : "swap"
    };

    /// <summary>Preserves provider status rather than treating an asynchronous acknowledgement as an open order.</summary>
    internal static OrderSnapshot MapOrder(ccxt.Order order, InstrumentKey key) => new(
        new(key, order.id ?? ""), order.clientOrderId, order.side == "sell" ? OrderSide.Sell : OrderSide.Buy,
        order.type switch { "market" => OrderType.Market, "stop" or "stop_market" => OrderType.Stop,
            "stop_limit" => OrderType.StopLimit, _ => OrderType.Limit }, order.status switch { "open" => OrderState.Open, "closed" => OrderState.Filled,
            "canceled" or "cancelled" => OrderState.Canceled, "expired" => OrderState.Expired, "rejected" => OrderState.Rejected, _ => OrderState.Pending },
        SpotOrderMath.Number(order.amount) ?? 0, SpotOrderMath.Number(order.filled), SpotOrderMath.Number(order.remaining),
        SpotOrderMath.Number(order.price), SpotOrderMath.Number(order.average), At(order.timestamp),
        At(order.lastUpdateTimestamp ?? order.lastTradeTimestamp ?? order.timestamp) ?? DateTimeOffset.MinValue, StatusReason(order),
        GetPositionSide(order), order.reduceOnly);

    /// <summary>Retains the hedge leg for exchange orders created by this or another application.</summary>
    private static PositionSide? GetPositionSide(ccxt.Order order)
    {
        if (order.info is null)
        {
            return null;
        }

        return Text(order.info, "positionSide") switch
        {
            "LONG" => PositionSide.Long,
            "SHORT" => PositionSide.Short,
            _ => Text(order.info, "positionIdx") switch
            {
                "1" => PositionSide.Long,
                "2" => PositionSide.Short,
                _ => null
            }
        };
    }

    /// <summary>Maps an allowlist of provider reason codes; raw rejection text is never shown or persisted.</summary>
    private static string? StatusReason(ccxt.Order order)
    {
        var reason = order.info is null ? null : Text(order.info, "rejectReason") ?? Text(order.info, "cancelType") ?? Text(order.info, "expiryReason");
        if (reason is "EC_PostOnlyWillTakeLiquidity" or "CancelByPostOnly") return "Post-only would execute immediately.";
        if (reason is "EC_NoEnoughQtyToFill" or "INSUFFICIENT_LIQUIDITY") return "Insufficient exchange liquidity.";
        return order.status switch { "canceled" or "cancelled" => "Canceled by the exchange.", "rejected" => "Rejected by the exchange.",
            "expired" => "Expired at the exchange.", _ => null };
    }

    /// <summary>Maps a fill independently of its order's current state, retaining the actual commission currency.</summary>
    private static ExecutionSnapshot MapExecution(ccxt.Trade trade, InstrumentKey key) => new(trade.id ?? "",
        new(key, trade.order ?? trade.orderId ?? ""), trade.side == "sell" ? OrderSide.Sell : OrderSide.Buy,
        SpotOrderMath.Number(trade.amount) ?? 0, SpotOrderMath.Number(trade.price) ?? 0,
        At(trade.timestamp) ?? DateTimeOffset.MinValue,
        trade.fee is { } fee && fee.currency is not null && SpotOrderMath.Number(fee.cost) is { } cost ? new(cost, fee.currency) : null);

    /// <summary>Copies all currency balances out of the mutable CCXT cache.</summary>
    private static AccountSnapshot MapBalance(ccxt.Balances balance, TradingAccountContext account) => new(account,
        balance.balances.Select(p => new AccountBalance(p.Key, SpotOrderMath.Number(p.Value.total),
            SpotOrderMath.Number(p.Value.free), SpotOrderMath.Number(p.Value.used))).ToArray(), null, null, DateTimeOffset.UtcNow);

    /// <summary>Reads optional detached provider metadata.</summary>
    private static string? Text(IDictionary<string, object> data, string key) => data.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    /// <summary>Extracts a cursor if a provider preserves it in unified item metadata.</summary>
    private static string? ResponseCursor(IDictionary<string, object>? data) => data is null ? null : Text(data, "nextPageCursor");

    /// <summary>Retains Bybit's envelope cursor, which unified CCXT order lists do not expose.</summary>
    private async Task<TradingPage<OrderSnapshot>> BybitOrdersAsync(TradingQuery query, bool open, CancellationToken ct, string? clientId = null, string? exchangeId = null)
    {
        var request = BybitQuery(query);
        if (clientId is not null) request["orderLinkId"] = clientId;
        if (exchangeId is not null) request["orderId"] = exchangeId;
        var bybit = (ccxt.bybit)_client;
        var response = open ? await bybit.privateGetV5OrderRealtime(request).WaitAsync(ct).ConfigureAwait(false)
            : await bybit.privateGetV5OrderHistory(request).WaitAsync(ct).ConfigureAwait(false);
        return ParseBybitOrders(response, query.Instrument!);
    }

    /// <summary>Retains the cursor from the response envelope while parsing detached spot orders.</summary>
    internal TradingPage<OrderSnapshot> ParseBybitOrders(IDictionary<string, object> response, InstrumentKey key)
    {
        var result = (IDictionary<string, object>)response["result"];
        var items = ((IEnumerable)result["list"]).Cast<object>()
            .Select(raw => MapAccountOrder(new ccxt.Order(_client.parseOrder(raw, _client.market(key.SecurityId))), key)).ToArray();
        return new(items, EmptyCursor(Text(result, "nextPageCursor")));
    }

    /// <summary>Parses actual fill IDs, commissions and the execution endpoint's independent cursor.</summary>
    internal TradingPage<ExecutionSnapshot> ParseBybitExecutions(IDictionary<string, object> response, InstrumentKey key)
    {
        var result = (IDictionary<string, object>)response["result"];
        var items = ((IEnumerable)result["list"]).Cast<object>().Select(raw => MapExecution(new ccxt.Trade(_client.parseTrade(raw, _client.market(key.SecurityId))), key)).ToArray();
        return new(items, EmptyCursor(Text(result, "nextPageCursor")));
    }

    /// <summary>Routes an external conditional order's cancellation using its original spot order filter.</summary>
    internal Dictionary<string, object> BybitCancelParameters(OrderReference order) =>
        IsFutures ? FuturesBybitCancelParameters(order) : SpotBybitCancelParameters(order);

    /// <summary>Keeps provider routing for externally created conditional spot orders inside the adapter.</summary>
    private OrderSnapshot MapAccountOrder(ccxt.Order order, InstrumentKey key) =>
        IsFutures ? MapOrder(order, key) : MapSpotAccountOrder(order, key);

    /// <summary>Builds an explicitly spot-only query and preserves caller bounds and cursor.</summary>
    private Dictionary<string, object> BybitQuery(TradingQuery query)
    {
        if (query.Since.HasValue && query.Until.HasValue && query.Until - query.Since > TimeSpan.FromDays(7))
            throw new NotSupportedException("Bybit history requests must use windows of at most seven days.");
        var request = new Dictionary<string, object> { ["category"] = Category, ["symbol"] = _client.Market(query.Instrument!.SecurityId).id!, ["limit"] = Math.Clamp(query.Limit, 1, 50) };
        if (query.Cursor is not null) request["cursor"] = query.Cursor;
        if (query.Since.HasValue) request["startTime"] = query.Since.Value.ToUnixTimeMilliseconds();
        if (query.Until.HasValue) request["endTime"] = query.Until.Value.ToUnixTimeMilliseconds();
        return request;
    }

    /// <summary>Checks Binance's moving price bands with a fresh reference instead of stale depth.</summary>
    public Task<IReadOnlyList<TradingError>> ValidateMarketAsync(PlaceOrderRequest request, CancellationToken ct) =>
        IsFutures ? Task.FromResult<IReadOnlyList<TradingError>>([]) : ValidateSpotMarketAsync(request, ct);

    /// <summary>Rejects a spot limit that sits outside the exchange's current price band.</summary>
    private async Task<IReadOnlyList<TradingError>> ValidateSpotMarketAsync(PlaceOrderRequest request, CancellationToken ct)
    {
        WireNumber(request.Instrument.SecurityId, request.Quantity, true);
        WireNumber(request.Instrument.SecurityId, request.Price!.Value, false);
        if (_client is ccxt.bybit bybit)
        {
            var response = await bybit.publicGetV5MarketPriceLimit(new Dictionary<string, object>
                { ["category"] = Category, ["symbol"] = _client.Market(request.Instrument.SecurityId).id! }).WaitAsync(ct).ConfigureAwait(false);
            var limits = (IDictionary<string, object>)response["result"];
            var maximumBuy = DecimalField(limits, "buyLmt");
            var minimumSell = DecimalField(limits, "sellLmt");
            if (maximumBuy is null || minimumSell is null) return [new("PriceBandUnavailable", "The current spot price band is unavailable.")];
            if (request.Side == OrderSide.Buy && request.Price > maximumBuy || request.Side == OrderSide.Sell && request.Price < minimumSell)
                return [new("PriceBand", "Price is outside the exchange's current allowed band.")];
            return [];
        }
        if (_client is not ccxt.binance binance || !_rawMarkets.TryGetValue(request.Instrument.SecurityId, out var market) ||
            market.info is null || !market.info.TryGetValue("filters", out var raw) || raw is not IEnumerable filters) return [];
        decimal? referencePrice = null;
        try
        {
            var reference = await binance.publicGetReferencePrice(new Dictionary<string, object> { ["symbol"] = market.id! }).WaitAsync(ct).ConfigureAwait(false);
            referencePrice = DecimalField(reference, "referencePrice");
        }
        catch (ccxt.NotSupported) { }
        catch (ccxt.BadRequest) { }
        foreach (var filter in filters.Cast<IDictionary<string, object>>().Where(f => Text(f, "filterType") is "PERCENT_PRICE" or "PERCENT_PRICE_BY_SIDE"))
        {
            var parameters = new Dictionary<string, object> { ["symbol"] = market.id! };
            var zeroMinutes = Text(filter, "avgPriceMins") == "0";
            var average = referencePrice;
            if (average is null)
            {
                var reference = zeroMinutes ? await binance.publicGetTickerPrice(parameters).WaitAsync(ct).ConfigureAwait(false)
                    : await binance.publicGetAvgPrice(parameters).WaitAsync(ct).ConfigureAwait(false);
                average = DecimalField((IDictionary<string, object>)reference, "price");
            }
            var prefix = Text(filter, "filterType") == "PERCENT_PRICE_BY_SIDE" ? request.Side == OrderSide.Buy ? "bidMultiplier" : "askMultiplier" : "multiplier";
            var lower = DecimalField(filter, prefix + "Down");
            var upper = DecimalField(filter, prefix + "Up");
            if (average is null || lower is null || upper is null) return [new("PriceBandUnavailable", "The exchange price band could not be checked.")];
            if (request.Price < average * lower || request.Price > average * upper) return [new("PriceBand", "Price is outside the exchange's current allowed band.")];
        }
        return [];
    }

    /// <summary>Parses optional provider decimal strings without passing through binary floating point.</summary>
    private static decimal? DecimalField(IDictionary<string, object> value, string key) => decimal.TryParse(Text(value, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    /// <summary>Normalizes an empty provider cursor to the end of pagination.</summary>
    private static string? EmptyCursor(string? cursor) => string.IsNullOrWhiteSpace(cursor) ? null : cursor;

    /// <summary>Converts only actual exchange timestamps; unknown timestamps do not get invented.</summary>
    private static DateTimeOffset? At(long? timestamp) => timestamp.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value) : null;

    /// <summary>Closes transport and removes client-owned credential references without canceling any order.</summary>
    public async ValueTask DisposeAsync()
    {
        try { await _client.Close().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        finally { _client.apiKey = ""; _client.secret = ""; }
    }

    /// <summary>Opens a public spot client. Futures options are never applied here.</summary>
    private static ccxt.Exchange CreateSpotExchange(TradingAccountProfile profile, Dictionary<string, object> settings) => profile.Exchange switch
    {
        "binance" => new ccxt.pro.binance(settings),
        "bybit" => new ccxt.pro.bybit(settings),
        _ => throw new NotSupportedException("Spot trading supports Binance and Bybit only.")
    };

    /// <summary>Opens a USDT-M client and limits market loading to linear contracts.</summary>
    private static ccxt.Exchange CreateFuturesExchange(TradingAccountProfile profile, Dictionary<string, object> settings)
    {
        var options = (Dictionary<string, object>)settings["options"];
        options["defaultType"] = profile.Exchange == "binance" ? "future" : "swap";
        options["defaultSubType"] = "linear";
        options["fetchMarkets"] = new Dictionary<string, object> { ["types"] = new List<object> { "linear" } };
        return profile.Exchange switch
        {
            "binance" => new ccxt.pro.binanceusdm(settings),
            "bybit" => new ccxt.pro.bybit(settings),
            _ => throw new NotSupportedException("Spot trading supports Binance and Bybit only.")
        };
    }

    private bool IsTradable(ccxt.MarketInterface market) => IsFutures ? IsLinearUsdtContract(market) : market.spot == true;

    private static bool IsLinearUsdtContract(ccxt.MarketInterface market) =>
        market.contract == true && market.linear == true && market.settle == "USDT";

    private Instrument DescribeInstrument(Instrument instrument, ccxt.MarketInterface market, InstrumentKey key) =>
        IsFutures ? DescribeFuturesInstrument(instrument, market, key) : instrument;

    /// <summary>Records contract size and USDT settlement on top of the shared market metadata.</summary>
    private static Instrument DescribeFuturesInstrument(Instrument instrument, ccxt.MarketInterface market, InstrumentKey key) => instrument with
    {
        Key = key,
        Expiry = market.expiry.HasValue ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(market.expiry.Value).UtcDateTime) : null,
        TradingRules = instrument.TradingRules! with
        {
            QuantityUnit = QuantityUnit.Contracts,
            ContractSize = SpotOrderMath.Number(market.contractSize),
            SettlementCurrency = "USDT"
        }
    };

    private Task<Dictionary<string, object>> CreateOrderParametersAsync(PlaceOrderRequest request, CancellationToken ct) =>
        IsFutures ? FuturesParametersAsync(request, ct) : Task.FromResult(OrderParameters(_profile.Exchange, request));

    /// <summary>Resolves a spot order by the original client id. Implicit Binance endpoints ignore CCXT routing options.</summary>
    private async Task<OrderSnapshot?> FindBinanceSpotOrderAsync(InstrumentKey key, string clientId, CancellationToken ct)
    {
        try
        {
            var raw = await ((ccxt.binance)_client).privateGetOrder(BinanceClientOrderQuery(key, clientId)).WaitAsync(ct).ConfigureAwait(false);
            return MapOrder(new ccxt.Order(_client.parseOrder(raw, _client.market(key.SecurityId))), key);
        }
        catch (ccxt.OrderNotFound) { return null; }
    }

    /// <summary>Resolves a USDT-M order by the original client id through the futures endpoint.</summary>
    private async Task<OrderSnapshot?> FindBinanceFuturesOrderAsync(InstrumentKey key, string clientId, CancellationToken ct)
    {
        try
        {
            var raw = await ((ccxt.binance)_client).fapiPrivateGetOrder(BinanceClientOrderQuery(key, clientId)).WaitAsync(ct).ConfigureAwait(false);
            return MapOrder(new ccxt.Order(_client.parseOrder(raw, _client.market(key.SecurityId))), key);
        }
        catch (ccxt.OrderNotFound) { return null; }
    }

    private Dictionary<string, object> BinanceClientOrderQuery(InstrumentKey key, string clientId) => new()
    {
        ["origClientOrderId"] = clientId,
        ["symbol"] = _client.Market(key.SecurityId).id!
    };

    private Dictionary<string, object> SpotBybitCancelParameters(OrderReference order)
    {
        var parameters = BybitCancelRequest(order);
        parameters["orderFilter"] = _bybitOrderFilters.GetValueOrDefault(order.ExchangeOrderId, "Order");
        return parameters;
    }

    private Dictionary<string, object> FuturesBybitCancelParameters(OrderReference order) => BybitCancelRequest(order);

    private Dictionary<string, object> BybitCancelRequest(OrderReference order) => new()
    {
        ["category"] = Category,
        ["symbol"] = _client.Market(order.Instrument.SecurityId).id!,
        ["orderId"] = order.ExchangeOrderId
    };

    /// <summary>Remembers Bybit's order filter so a later spot cancel reaches the same conditional order.</summary>
    private OrderSnapshot MapSpotAccountOrder(ccxt.Order order, InstrumentKey key)
    {
        if (_profile.Exchange == "bybit" && order.id is not null && order.info is { } info)
        {
            var filter = Text(info, "orderFilter");
            if (filter is not ("Order" or "StopOrder" or "tpslOrder" or "OcoOrder" or "BidirectionalTpslOrder"))
                filter = Text(info, "stopOrderType") switch { "Stop" => "StopOrder", "TakeProfit" or "StopLoss" => "tpslOrder",
                    "OcoOrder" => "OcoOrder", "BidirectionalTpslOrder" => "BidirectionalTpslOrder", _ => "Order" };
            _bybitOrderFilters[order.id] = filter;
        }
        return MapOrder(order, key);
    }

    /// <summary>Spot accounts use balances instead of contract positions.</summary>
    public Task<IReadOnlyList<PositionSnapshot>> PositionsAsync(InstrumentKey? key, CancellationToken ct) =>
        IsFutures ? LoadFuturesPositionsAsync(key, ct) : throw new NotSupportedException("Spot accounts use balances instead of contract positions.");

    /// <summary>Reads position size, actual leverage and position mode without changing exchange settings.</summary>
    private async Task<IReadOnlyList<PositionSnapshot>> LoadFuturesPositionsAsync(InstrumentKey? key, CancellationToken ct)
    {
        var parameters = Parameters();
        parameters["settleCoin"] = "USDT";
        var positions = await _client.FetchPositions(key is null ? null! : new List<object> { key.SecurityId }, parameters)
            .WaitAsync(ct).ConfigureAwait(false);
        var result = new List<PositionSnapshot>();

        foreach (var position in positions)
        {
            var market = _client.Market(position.symbol!);
            if (market.linear != true || market.settle != "USDT")
            {
                continue;
            }

            var index = position.info is null ? null : Text(position.info, "positionIdx");
            var side = index == "2" || position.side == "short" ? PositionSide.Short : PositionSide.Long;
            var hedged = index is "1" or "2" ? true : index == "0" ? false : position.hedged;
            result.Add(new PositionSnapshot(
                new(_profile.Exchange, "futures", "", position.symbol!), side,
                Math.Abs(SpotOrderMath.Number(position.contracts) ?? 0), QuantityUnit.Contracts,
                SpotOrderMath.Number(position.entryPrice),
                SpotOrderMath.Number(position.unrealizedPnl) is { } pnl ? new(pnl, "USDT") : null,
                SpotOrderMath.Number(position.initialMargin) is { } margin ? new(margin, "USDT") : null,
                SpotOrderMath.Number(position.leverage), hedged, position.marginMode));
        }

        if (_client is ccxt.binance binance && key is not null)
        {
            var mode = await binance.fapiPrivateGetPositionSideDual().WaitAsync(ct).ConfigureAwait(false);
            var hedged = mode.TryGetValue("dualSidePosition", out var flag) && Convert.ToBoolean(flag, CultureInfo.InvariantCulture);
            var configuration = await binance.fapiPrivateGetSymbolConfig(new Dictionary<string, object>
            {
                ["symbol"] = _client.Market(key.SecurityId).id!
            }).WaitAsync(ct).ConfigureAwait(false);
            var config = configuration.OfType<IDictionary<string, object>>().FirstOrDefault();
            var leverage = config is null ? null : DecimalField(config, "leverage");

            for (var index = 0; index < result.Count; index++)
            {
                result[index] = result[index] with { Leverage = leverage, Hedged = hedged };
            }

            foreach (var side in hedged ? new[] { PositionSide.Long, PositionSide.Short } : new[] { PositionSide.Long })
            {
                if (!result.Any(p => p.Side == side))
                {
                    result.Add(new(key, side, 0, QuantityUnit.Contracts, Leverage: leverage, Hedged: hedged,
                        MarginMode: config is null ? null : Text(config, "marginType")));
                }
            }
        }

        return result;
    }

    /// <summary>Maps hedge and one-way semantics, including Binance's prohibition on the reduceOnly flag in hedge mode.</summary>
    internal static Dictionary<string, object> FuturesOrderParameters(string exchange, PlaceOrderRequest request, bool hedged)
    {
        var parameters = new Dictionary<string, object>
        {
            ["clientOrderId"] = request.ClientOrderId,
            ["timeInForce"] = request.PostOnly ? exchange == "binance" ? "GTX" : "PostOnly" : "GTC"
        };

        if (exchange == "binance")
        {
            parameters["positionSide"] = hedged
                ? request.PositionSide == PositionSide.Long ? "LONG" : "SHORT"
                : "BOTH";

            if (!hedged)
            {
                parameters["reduceOnly"] = request.ReduceOnly;
            }
        }
        else
        {
            parameters["category"] = "linear";
            parameters["positionIdx"] = hedged ? request.PositionSide == PositionSide.Long ? 1 : 2 : 0;
            parameters["reduceOnly"] = request.ReduceOnly;
        }

        return parameters;
    }

    /// <summary>Obtains the current mode immediately before building the exchange request.</summary>
    private Task<Dictionary<string, object>> FuturesParametersAsync(PlaceOrderRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_validatedModes.TryRemove(request.ClientOrderId, out var validated) || validated.Request != request)
        {
            throw new TradingServiceException(new("ValidationRequired", "The exact futures request must be validated before dispatch."));
        }

        return Task.FromResult(FuturesOrderParameters(_profile.Exchange, request, validated.Hedged));
    }

    /// <summary>Rejects ambiguous mode/side combinations and closing requests that could increase exposure.</summary>
    internal static IReadOnlyList<TradingError> ValidatePositionIntent(PlaceOrderRequest request, IReadOnlyList<PositionSnapshot> positions)
    {
        if (positions.Count == 0 || positions.Any(p => p.Hedged is null))
        {
            return [new("PositionModeUnknown", "The exchange position mode could not be established.")];
        }

        var hedged = positions.Any(p => p.Hedged == true);
        if (hedged != request.PositionSide.HasValue)
        {
            return [new("PositionSide", hedged ? "Select Long or Short for hedge mode." : "Use Auto for one-way mode.")];
        }

        if (request.ReduceOnly)
        {
            var target = hedged ? request.PositionSide!.Value
                : request.Side == OrderSide.Sell ? PositionSide.Long : PositionSide.Short;
            var available = positions.Where(p => p.Side == target).Sum(p => p.Quantity);
            var closingSide = target == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            if (request.Side != closingSide || request.Quantity > available)
            {
                return [new("ReduceOnly", "Reduce-only must close an existing position without exceeding its size.")];
            }
        }

        return [];
    }

    /// <summary>Checks real available collateral and current leverage; spot cash checks are not reused for contracts.</summary>
    public async Task<IReadOnlyList<TradingError>> ValidateFuturesAsync(
        PlaceOrderRequest request, SpotMarket market, AccountSnapshot balance, CancellationToken ct)
    {
        var positions = await PositionsAsync(request.Instrument, ct).ConfigureAwait(false);
        var errors = ValidatePositionIntent(request, positions);
        if (errors.Count > 0)
        {
            return errors;
        }

        if (request.ReduceOnly)
        {
            _validatedModes[request.ClientOrderId] = (request, positions.Any(p => p.Hedged == true));
            return [];
        }

        var leverage = positions.Where(p => !request.PositionSide.HasValue || p.Side == request.PositionSide)
            .Select(p => p.Leverage).FirstOrDefault();
        var collateral = balance.Margins?.FirstOrDefault(m => m.Currency == "USDT")?.AvailableFunds;
        var contractSize = market.Instrument.TradingRules?.ContractSize;

        if (leverage is not > 0 || collateral is null || contractSize is not > 0 || market.FeeRate is null)
        {
            return [new("MarginUnavailable", "Available USDT margin, leverage or contract metadata is unknown.")];
        }

        var ticker = await _client.FetchTicker(request.Instrument.SecurityId, Parameters()).WaitAsync(ct).ConfigureAwait(false);
        var reference = SpotOrderMath.Number(ticker.last);
        if (reference is not > 0)
        {
            return [new("PriceUnavailable", "A fresh contract price is required for the margin check.")];
        }

        var required = RequiredMargin(request.Quantity, contractSize.Value, request.Price!.Value,
            reference.Value, leverage.Value, market.FeeRate.Value, request.Side);
        if (collateral < required)
        {
            return [new("InsufficientMargin", "Insufficient available USDT margin, including commission coverage.")];
        }

        _validatedModes[request.ClientOrderId] = (request, positions.Any(p => p.Hedged == true));
        return [];
    }

    /// <summary>Reserves initial margin plus opening/closing commission at the higher current or limit price.</summary>
    internal static decimal RequiredMargin(decimal quantity, decimal contractSize, decimal price,
        decimal reference, decimal leverage, decimal fee, OrderSide side = OrderSide.Buy)
    {
        if (quantity <= 0 || contractSize <= 0 || price <= 0 || reference <= 0 || leverage <= 0 || fee < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        var notional = quantity * contractSize * Math.Max(price, reference);
        var openingLoss = quantity * contractSize * Math.Max(0, side == OrderSide.Buy ? price - reference : reference - price);
        return notional / leverage + notional * fee * 2 + openingLoss;
    }

    /// <summary>Reads derivative collateral; Bybit multi-coin and portfolio margin remain explicitly unavailable.</summary>
    private async Task<AccountSnapshot> FuturesBalanceAsync(TradingAccountContext account, CancellationToken ct)
    {
        var balances = await _client.FetchBalance(Parameters()).WaitAsync(ct).ConfigureAwait(false);
        decimal? available = null;

        if (_profile.Exchange == "binance")
        {
            available = SpotOrderMath.Number(balances.free.GetValueOrDefault("USDT"));
        }
        else if (balances.info?.GetValueOrDefault("result") is IDictionary<string, object> result &&
                 result.TryGetValue("list", out var walletList) && walletList is IEnumerable wallets)
        {
            foreach (var wallet in wallets.Cast<IDictionary<string, object>>())
            {
                var coins = ((IEnumerable)wallet["coin"]).Cast<IDictionary<string, object>>().ToArray();
                var usdt = coins.FirstOrDefault(c => Text(c, "coin") == "USDT");
                var otherCollateral = coins.Any(c => Text(c, "coin") != "USDT" && DecimalField(c, "equity") is not (null or 0) &&
                    (!bool.TryParse(Text(c, "collateralSwitch"), out var enabled) || enabled));

                if (usdt is not null && !otherCollateral && DecimalField(usdt, "equity") is > 0 &&
                    DecimalField(usdt, "usdValue") is > 0 && DecimalField(wallet, "totalAvailableBalance") is { } dollars)
                {
                    available = dollars * DecimalField(usdt, "equity") / DecimalField(usdt, "usdValue");
                }
            }
        }

        var snapshot = MapBalance(balances, account);
        return snapshot with
        {
            Margins = [new MarginSnapshot("USDT", AvailableFunds: available)],
            Positions = null
        };
    }
}
