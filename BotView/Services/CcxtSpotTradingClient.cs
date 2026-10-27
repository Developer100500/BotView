using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BotView.Models;
using BotView.Services.Providers;

namespace BotView.Services;

/// <summary>Binance/Bybit spot mapping. Raw CCXT values and signed errors never escape this boundary.</summary>
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

    /// <summary>Creates an isolated Pro client and selects sandbox before any request.</summary>
    internal CcxtSpotTradingClient(TradingAccountProfile profile, AccountCredentials credentials)
    {
        _profile = profile;
        var settings = ProOrderBookClient.CreateSettings();
        settings["apiKey"] = credentials.ApiKey;
        settings["secret"] = credentials.Secret;
        settings["verbose"] = false;
        _client = profile.Exchange switch
        {
            "binance" => new ccxt.pro.binance(settings),
            "bybit" => new ccxt.pro.bybit(settings),
            _ => throw new NotSupportedException("Spot trading supports Binance and Bybit only.")
        };
        _keyIdentity = "key:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentials.ApiKey)));
        if (profile.Environment == TradingEnvironment.Testnet) _client.setSandboxMode(true);
    }

    /// <summary>Authenticates and obtains a stable owner ID; a key fingerprint isolates recovery when no owner ID is returned.</summary>
    public async Task<(string Identity, bool? CanTrade)> ConnectAsync(CancellationToken ct)
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
        if (key.Venue != _profile.Exchange || key.Market != "spot" || key.Board != "") throw new NotSupportedException("Only this account's spot listings are supported.");
        if (_markets.TryGetValue(key.SecurityId, out var cached) && DateTimeOffset.UtcNow - _marketTimes[key.SecurityId] < TimeSpan.FromMinutes(5)) return cached;
        if (_markets.Count > 0) await _client.loadMarkets(true).WaitAsync(ct).ConfigureAwait(false);
        var markets = await _client.LoadMarkets().WaitAsync(ct).ConfigureAwait(false);
        if (!markets.TryGetValue(key.SecurityId, out var market) || market.spot != true || market.active == false)
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
        var instrument = CcxtMarketDataProvider.MapInstrument(_profile.Exchange, market, Convert.ToInt32(_client.precisionMode));
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
        var order = await _client.CreateOrder(symbol, "limit", request.Side == OrderSide.Buy ? "buy" : "sell", amount, price,
            OrderParameters(_profile.Exchange, request)).WaitAsync(ct).ConfigureAwait(false);
        return MapOrder(order, request.Instrument) with { ClientOrderId = request.ClientOrderId,
            Quantity = request.Quantity, Price = request.Price, Side = request.Side };
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
        var parameters = Parameters();
        if (_profile.Exchange == "binance")
        {
            parameters["origClientOrderId"] = clientId;
            parameters["symbol"] = _client.Market(key.SecurityId).id!;
            try
            {
                var raw = await ((ccxt.binance)_client).privateGetOrder(parameters).WaitAsync(ct).ConfigureAwait(false);
                return MapOrder(new ccxt.Order(_client.parseOrder(raw, _client.market(key.SecurityId))), key);
            }
            catch (ccxt.OrderNotFound) { return null; }
        }
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
    public async Task<AccountSnapshot> BalanceAsync(TradingAccountContext account, CancellationToken ct)
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

    /// <summary>Restricts all Bybit requests to spot; unified balances are never used to enable borrowing.</summary>
    private Dictionary<string, object> Parameters() => _profile.Exchange == "bybit" ? new() { ["category"] = "spot", ["type"] = "spot" } : new();

    /// <summary>Preserves provider status rather than treating an asynchronous acknowledgement as an open order.</summary>
    internal static OrderSnapshot MapOrder(ccxt.Order order, InstrumentKey key) => new(
        new(key, order.id ?? ""), order.clientOrderId, order.side == "sell" ? OrderSide.Sell : OrderSide.Buy,
        order.type switch { "market" => OrderType.Market, "stop" or "stop_market" => OrderType.Stop,
            "stop_limit" => OrderType.StopLimit, _ => OrderType.Limit }, order.status switch { "open" => OrderState.Open, "closed" => OrderState.Filled,
            "canceled" or "cancelled" => OrderState.Canceled, "expired" => OrderState.Expired, "rejected" => OrderState.Rejected, _ => OrderState.Pending },
        SpotOrderMath.Number(order.amount) ?? 0, SpotOrderMath.Number(order.filled), SpotOrderMath.Number(order.remaining),
        SpotOrderMath.Number(order.price), SpotOrderMath.Number(order.average), At(order.timestamp),
        At(order.lastUpdateTimestamp ?? order.lastTradeTimestamp ?? order.timestamp) ?? DateTimeOffset.MinValue, StatusReason(order));

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
    internal Dictionary<string, object> BybitCancelParameters(OrderReference order) => new() { ["category"] = "spot",
        ["symbol"] = _client.Market(order.Instrument.SecurityId).id!, ["orderId"] = order.ExchangeOrderId,
        ["orderFilter"] = _bybitOrderFilters.GetValueOrDefault(order.ExchangeOrderId, "Order") };

    /// <summary>Keeps provider routing for externally created conditional spot orders inside the adapter.</summary>
    private OrderSnapshot MapAccountOrder(ccxt.Order order, InstrumentKey key)
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

    /// <summary>Builds an explicitly spot-only query and preserves caller bounds and cursor.</summary>
    private Dictionary<string, object> BybitQuery(TradingQuery query)
    {
        if (query.Since.HasValue && query.Until.HasValue && query.Until - query.Since > TimeSpan.FromDays(7))
            throw new NotSupportedException("Bybit history requests must use windows of at most seven days.");
        var request = new Dictionary<string, object> { ["category"] = "spot", ["symbol"] = _client.Market(query.Instrument!.SecurityId).id!, ["limit"] = Math.Clamp(query.Limit, 1, 50) };
        if (query.Cursor is not null) request["cursor"] = query.Cursor;
        if (query.Since.HasValue) request["startTime"] = query.Since.Value.ToUnixTimeMilliseconds();
        if (query.Until.HasValue) request["endTime"] = query.Until.Value.ToUnixTimeMilliseconds();
        return request;
    }

    /// <summary>Checks Binance's moving price bands with a fresh reference instead of stale depth.</summary>
    public async Task<IReadOnlyList<TradingError>> ValidateMarketAsync(PlaceOrderRequest request, CancellationToken ct)
    {
        WireNumber(request.Instrument.SecurityId, request.Quantity, true);
        WireNumber(request.Instrument.SecurityId, request.Price!.Value, false);
        if (_client is ccxt.bybit bybit)
        {
            var response = await bybit.publicGetV5MarketPriceLimit(new Dictionary<string, object>
                { ["category"] = "spot", ["symbol"] = _client.Market(request.Instrument.SecurityId).id! }).WaitAsync(ct).ConfigureAwait(false);
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
}
