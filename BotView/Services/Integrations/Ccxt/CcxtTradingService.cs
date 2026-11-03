using BotView.Models;
using static BotView.Services.Integrations.Ccxt.CcxtTradingMapper;

namespace BotView.Services.Integrations.Ccxt;

/// <summary>Converts order-book calls and native CCXT results. Owns no background work or order state.</summary>
public sealed class CcxtTradingService : ITradingService
{
    private readonly CcxtTradingClient _client;
    private bool IsFutures => Account.Market == TradingMarket.UsdtFutures;
    public TradingAccountContext Account { get; }

    /// <summary>Binds a configured CCXT boundary to a single profile and network.</summary>
    public CcxtTradingService(TradingAccountProfile profile, CcxtTradingClient client)
    {
        _client = client;
        Account = new(profile.Id.ToString(), profile.Name, profile.Exchange, profile.Environment, profile.Market);
    }

    /// <summary>Loads and maps units without querying fees or pre-validating account funds.</summary>
    public Task<Instrument> FetchInstrumentAsync(InstrumentKey key, CancellationToken ct = default) => ReadAsync(async () =>
    {
        if (key.Venue != Account.Venue || key.Market != (IsFutures ? "futures" : "spot"))
            throw new NotSupportedException("The instrument belongs to another account market.");
        var market = (await _client.LoadMarketsAsync(ct))[key.SecurityId];
        var instrument = CcxtMarketDataProvider.MapInstrument(Account.Venue, market, _client.PrecisionMode) with { Key = key };
        return IsFutures ? instrument with
        {
            TradingRules = instrument.TradingRules! with
            {
                QuantityUnit = QuantityUnit.Contracts, ContractSize = Number(market.contractSize), SettlementCurrency = "USDT"
            }
        } : instrument;
    });

    /// <summary>Uses the exchange's native amount formatter.</summary>
    public decimal AmountToPrecision(InstrumentKey key, decimal quantity) => _client.AmountToPrecision(key.SecurityId, quantity);

    /// <summary>Uses the exchange's native price formatter.</summary>
    public decimal PriceToPrecision(InstrumentKey key, decimal price) => _client.PriceToPrecision(key.SecurityId, price);

    /// <summary>Routes one limit order to a named market operation.</summary>
    public Task<OrderSnapshot> CreateLimitOrderAsync(LimitOrderRequest request, CancellationToken ct = default) => ReadAsync(async () =>
    {
        ValidateRequest(request.Instrument, request.Side, request.Quantity);
        var amount = AmountToPrecision(request.Instrument, request.Quantity);
        if (amount <= 0) throw new ArgumentException("Quantity rounds to zero.");
        if (request.Price <= 0) throw new ArgumentException("A positive limit price is required.");
        var price = PriceToPrecision(request.Instrument, request.Price);
        if (price <= 0) throw new ArgumentException("Price rounds to zero.");
        var side = request.Side == OrderSide.Buy ? "buy" : "sell";
        var order = IsFutures
            ? await _client.CreateUsdtFuturesLimitOrderAsync(request.Instrument.SecurityId, side, amount, price,
                request.PostOnly, request.ReduceOnly, request.PositionSide, ct)
            : await _client.CreateSpotLimitOrderAsync(request.Instrument.SecurityId, side, amount, price, request.PostOnly, ct);
        return MapOrder(order, request.Instrument);
    });

    /// <summary>Routes one market order without manufacturing a limit price.</summary>
    public Task<OrderSnapshot> CreateMarketOrderAsync(MarketOrderRequest request, CancellationToken ct = default) => ReadAsync(async () =>
    {
        ValidateRequest(request.Instrument, request.Side, request.Quantity);
        var amount = AmountToPrecision(request.Instrument, request.Quantity);
        if (amount <= 0) throw new ArgumentException("Quantity rounds to zero.");
        var side = request.Side == OrderSide.Buy ? "buy" : "sell";
        var order = IsFutures
            ? await _client.CreateUsdtFuturesMarketOrderAsync(request.Instrument.SecurityId, side, amount,
                request.ReduceOnly, request.PositionSide, ct)
            : await _client.CreateSpotMarketOrderAsync(request.Instrument.SecurityId, side, amount, ct);
        return MapOrder(order, request.Instrument);
    });

    /// <summary>Requests cancellation through the profile's market route.</summary>
    public Task<OrderSnapshot> CancelOrderAsync(OrderReference order, CancellationToken ct = default) => ReadAsync(async () =>
        MapOrder(IsFutures
            ? await _client.CancelUsdtFuturesOrderAsync(order.ExchangeOrderId, order.Instrument.SecurityId, ct)
            : await _client.CancelSpotOrderAsync(order.ExchangeOrderId, order.Instrument.SecurityId, ct), order.Instrument));

    /// <summary>Fetches the provider's current view of one order.</summary>
    public Task<OrderSnapshot> FetchOrderAsync(OrderReference order, CancellationToken ct = default) => ReadAsync(async () =>
        MapOrder(IsFutures
            ? await _client.FetchUsdtFuturesOrderAsync(order.ExchangeOrderId, order.Instrument.SecurityId, ct)
            : await _client.FetchSpotOrderAsync(order.ExchangeOrderId, order.Instrument.SecurityId, ct), order.Instrument));

    /// <summary>Maps a fresh open-order snapshot.</summary>
    public Task<IReadOnlyList<OrderSnapshot>> FetchOpenOrdersAsync(InstrumentKey key, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<OrderSnapshot>>(async () => (IsFutures
            ? await _client.FetchUsdtFuturesOpenOrdersAsync(key.SecurityId, ct)
            : await _client.FetchSpotOpenOrdersAsync(key.SecurityId, ct)).Select(o => MapOrder(o, key)).ToArray());

    /// <summary>Maps recent exchange executions for the selected listing.</summary>
    public Task<IReadOnlyList<ExecutionSnapshot>> FetchMyTradesAsync(InstrumentKey key, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<ExecutionSnapshot>>(async () => (IsFutures
            ? await _client.FetchUsdtFuturesMyTradesAsync(key.SecurityId, ct)
            : await _client.FetchSpotMyTradesAsync(key.SecurityId, ct)).Select(t => Execution(t, key)).ToArray());

    /// <summary>Maps current balances and reported collateral.</summary>
    public Task<AccountSnapshot> FetchBalanceAsync(CancellationToken ct = default) => ReadAsync(async () => Balance(IsFutures
        ? await _client.FetchUsdtFuturesBalanceAsync(ct) : await _client.FetchSpotBalanceAsync(ct), Account));

    /// <summary>Maps native contract positions; spot has no separate contract position table.</summary>
    public Task<IReadOnlyList<PositionSnapshot>> FetchPositionsAsync(InstrumentKey key, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<PositionSnapshot>>(async () => IsFutures
            ? (await _client.FetchUsdtFuturesPositionsAsync(key.SecurityId, ct)).Select(p => Position(p, key)).ToArray() : []);

    /// <summary>Checks typed stream support without exposing CCXT strings to the order book.</summary>
    public bool SupportsStream(TradingStream stream) => _client.Supports(stream switch
    {
        TradingStream.Orders => "watchOrders", TradingStream.MyTrades => "watchMyTrades", _ => "watchBalance"
    });

    /// <summary>Copies a native private order batch.</summary>
    public Task<IReadOnlyList<OrderSnapshot>> WatchOrdersAsync(InstrumentKey key, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<OrderSnapshot>>(async () => (IsFutures
            ? await _client.WatchUsdtFuturesOrdersAsync(key.SecurityId, ct)
            : await _client.WatchSpotOrdersAsync(key.SecurityId, ct)).Where(o => o.symbol == key.SecurityId)
            .Select(o => MapOrder(o, key)).ToArray());

    /// <summary>Copies a native private execution batch.</summary>
    public Task<IReadOnlyList<ExecutionSnapshot>> WatchMyTradesAsync(InstrumentKey key, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<ExecutionSnapshot>>(async () => (IsFutures
            ? await _client.WatchUsdtFuturesMyTradesAsync(key.SecurityId, ct)
            : await _client.WatchSpotMyTradesAsync(key.SecurityId, ct)).Where(t => t.symbol == key.SecurityId)
            .Select(t => Execution(t, key)).ToArray());

    /// <summary>Copies a native balance stream update.</summary>
    public Task<AccountSnapshot> WatchBalanceAsync(CancellationToken ct = default) => ReadAsync(async () => Balance(IsFutures
        ? await _client.WatchUsdtFuturesBalanceAsync(ct) : await _client.WatchSpotBalanceAsync(ct), Account));

    /// <summary>Checks required fields and account routing without imposing exchange trading policy.</summary>
    private void ValidateRequest(InstrumentKey key, OrderSide side, decimal quantity)
    {
        if (!Enum.IsDefined(side) || quantity <= 0 || string.IsNullOrWhiteSpace(key.SecurityId))
            throw new ArgumentException("A symbol, direction and positive native quantity are required.");
        if (key.Venue != Account.Venue || key.Market != (IsFutures ? "futures" : "spot"))
            throw new ArgumentException("The instrument belongs to another account market.");
    }

    /// <summary>Converts errors once; it never repeats the underlying operation.</summary>
    private static async Task<T> ReadAsync<T>(Func<Task<T>> operation)
    {
        try { return await operation().ConfigureAwait(false); }
        catch (TradingServiceException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new TradingServiceException(new("ExchangeError", TradingErrorText.FromException(ex))); }
    }

    /// <summary>Releases the configured CCXT connection without canceling orders.</summary>
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
