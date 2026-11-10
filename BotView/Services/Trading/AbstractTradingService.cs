using System.Globalization;
using BotView.Models;
using BotView.Services.Integrations.Ccxt;
using static BotView.Services.Integrations.Ccxt.CcxtTradingMapper;

namespace BotView.Services.Trading;

/// <summary>Shared CCXT operations, application-model mapping and lifetime for market-specific trading services.</summary>
public abstract class AbstractTradingService : ITradingService
{
    private readonly string _instrumentMarket;
    protected ccxt.Exchange Exchange { get; }
    protected string Venue => Account.Venue; // exchange name
    public TradingAccountContext Account { get; }

    /// <summary>Binds a transport to the expected profile market without making a network request.</summary>
    protected AbstractTradingService(TradingAccountProfile profile, ccxt.Exchange exchange,
        TradingMarket expectedMarket, string instrumentMarket)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(exchange);
        if (profile.Market != expectedMarket)
        {
            throw new ArgumentException($"This service requires a {expectedMarket} profile.", nameof(profile));
        }

        Exchange = exchange;
        _instrumentMarket = instrumentMarket;
        Account = new(profile.Id.ToString(), profile.Name, profile.Exchange, profile.Environment, profile.Market);
    }

    /// <summary>Loads the instrument's units and precision metadata without checking trading funds or limits.</summary>
    public Task<Instrument> FetchInstrumentAsync(InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync(async () =>
    {
        ValidateInstrument(key);
        var markets = await Exchange.LoadMarkets().ConfigureAwait(false);
        var market = markets[key.SecurityId];
        return DescribeInstrument(market, key);
    }, ct);

    /// <summary>Formats the native quantity using the loaded CCXT precision rules.</summary>
    public decimal AmountToPrecision(InstrumentKey key, decimal quantity) =>
        decimal.Parse(
            Exchange.amountToPrecision(key.SecurityId, quantity.ToString(CultureInfo.InvariantCulture))!,
            CultureInfo.InvariantCulture);

    /// <summary>Formats the limit price using the loaded CCXT precision rules.</summary>
    public decimal PriceToPrecision(InstrumentKey key, decimal price) => decimal.Parse(
        Exchange.priceToPrecision(key.SecurityId, price.ToString(CultureInfo.InvariantCulture))!,
        CultureInfo.InvariantCulture);

    /// <summary>Creates one limit order directly through CCXT and returns its detached acknowledgement.</summary>
    public Task<OrderSnapshot> CreateLimitOrderAsync(LimitOrderRequest request, CancellationToken ct = default) =>
        ExecuteAsync(async () =>
    {
        ValidateRequest(request.Instrument, request.Side, request.Quantity);
        var amount = AmountToPrecision(request.Instrument, request.Quantity);
        if (amount <= 0)
        {
            throw new ArgumentException("Quantity rounds to zero.");
        }

        if (request.Price <= 0)
        {
            throw new ArgumentException("A positive limit price is required.");
        }

        var price = PriceToPrecision(request.Instrument, request.Price);
        if (price <= 0)
        {
            throw new ArgumentException("Price rounds to zero.");
        }

        var side = request.Side == OrderSide.Buy ? "buy" : "sell";
        var parameters = CreateLimitOrderParameters(request, request.Hedged);
        ct.ThrowIfCancellationRequested();
        var order = await Exchange.CreateOrder(request.Instrument.SecurityId, "limit", side,
            (double)amount, (double)price, parameters).ConfigureAwait(false);
        return MapOrder(order, request.Instrument);
    }, ct);

    /// <summary>Creates one market order in native units without supplying a price or maker flag.</summary>
    public Task<OrderSnapshot> CreateMarketOrderAsync(MarketOrderRequest request, CancellationToken ct = default) =>
        ExecuteAsync(async () =>
    {
        ValidateRequest(request.Instrument, request.Side, request.Quantity);
        var amount = AmountToPrecision(request.Instrument, request.Quantity);
        if (amount <= 0)
        {
            throw new ArgumentException("Quantity rounds to zero.");
        }

        var side = request.Side == OrderSide.Buy ? "buy" : "sell";
        var parameters = CreateMarketOrderParameters(request, request.Hedged);
        ct.ThrowIfCancellationRequested();
        var order = await Exchange.CreateOrder(request.Instrument.SecurityId, "market", side,
            (double)amount, null, parameters).ConfigureAwait(false);
        return MapOrder(order, request.Instrument);
    }, ct);

    /// <summary>Requests cancellation once; the exchange response is not treated as a guarantee of no fills.</summary>
    public Task<OrderSnapshot> CancelOrderAsync(OrderReference order, CancellationToken ct = default) =>
        ExecuteAsync(async () =>
    {
        ValidateInstrument(order.Instrument);
        var response = await Exchange.CancelOrder(order.ExchangeOrderId, order.Instrument.SecurityId, CreateParameters()).ConfigureAwait(false);
        return MapOrder(response, order.Instrument);
    }, ct);

    /// <summary>Fetches one order by its exchange identity.</summary>
    public Task<OrderSnapshot> FetchOrderAsync(OrderReference order, CancellationToken ct = default) =>
        ExecuteAsync(async () =>
    {
        ValidateInstrument(order.Instrument);
        var response = await Exchange.FetchOrder(order.ExchangeOrderId, order.Instrument.SecurityId, CreateParameters()).ConfigureAwait(false);
        return MapOrder(response, order.Instrument);
    }, ct);

    /// <summary>Fetches the current open-order snapshot with CCXT pagination enabled.</summary>
    public Task<IReadOnlyList<OrderSnapshot>> FetchOpenOrdersAsync(InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<OrderSnapshot>>(async () =>
        {
            ValidateInstrument(key);
            var parameters = CreateParameters();
            parameters["paginate"] = true;
            var orders = await Exchange.FetchOpenOrders(key.SecurityId, null, null, parameters).ConfigureAwait(false);
            return orders.Select(order => MapOrder(order, key)).ToArray();
        }, ct);

    /// <summary>Fetches the latest executions for display, preserving provider execution IDs and fees.</summary>
    public Task<IReadOnlyList<ExecutionSnapshot>> FetchMyTradesAsync(InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<ExecutionSnapshot>>(async () =>
        {
            ValidateInstrument(key);
            var trades = await Exchange.FetchMyTrades(key.SecurityId, null, 100, CreateParameters()).ConfigureAwait(false);
            return trades.Select(trade => Execution(trade, key)).ToArray();
        }, ct);

    /// <summary>Fetches balances and collateral as reported by the exchange.</summary>
    public Task<AccountSnapshot> FetchBalanceAsync(CancellationToken ct = default) =>
        ExecuteAsync(async () => Balance(
            await Exchange.FetchBalance(CreateParameters()).ConfigureAwait(false), Account), ct);

    /// <summary>Spot holdings are balances; no separate futures positions are returned.</summary>
    public virtual Task<IReadOnlyList<PositionSnapshot>> FetchPositionsAsync(
        InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<PositionSnapshot>>(() =>
        {
            ValidateInstrument(key);
            return Task.FromResult<IReadOnlyList<PositionSnapshot>>([]);
        }, ct);

    /// <summary>Spot has no position mode. Futures reads the mode reported by the exchange.</summary>
    public virtual Task<bool?> FetchPositionModeAsync(InstrumentKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateInstrument(key);
        throw new NotSupportedException("Position mode applies to futures accounts.");
    }

    /// <summary>Rejects leverage changes for markets without futures leverage support.</summary>
    public virtual Task SetLeverageAsync(InstrumentKey instrument, int leverage, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new NotSupportedException("Leverage is available only for futures trading.");
    }

    /// <summary>Reports availability of a typed private stream without leaking CCXT method names into the UI.</summary>
    public bool SupportsStream(TradingStream stream) => Exchange.has.GetValueOrDefault(stream switch
    {
        TradingStream.Orders => "watchOrders",
        TradingStream.MyTrades => "watchMyTrades",
        _ => "watchBalance"
    }) is true;

    /// <summary>Copies one private order batch into application models.</summary>
    public Task<IReadOnlyList<OrderSnapshot>> WatchOrdersAsync(InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<OrderSnapshot>>(async () =>
        {
            ValidateInstrument(key);
            var orders = await Exchange.WatchOrders(key.SecurityId, null, null, CreateParameters()).ConfigureAwait(false);
            return orders
                    .Where(order => order.symbol == key.SecurityId)
                    .Select(order => MapOrder(order, key)).ToArray();
        }, ct);

    /// <summary>Copies one private execution batch into application models.</summary>
    public Task<IReadOnlyList<ExecutionSnapshot>> WatchMyTradesAsync(InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<ExecutionSnapshot>>(async () =>
        {
            ValidateInstrument(key);
            var trades = await Exchange.WatchMyTrades(key.SecurityId, null, null, CreateParameters()).ConfigureAwait(false);
            return trades.Where(trade => trade.symbol == key.SecurityId).Select(trade => Execution(trade, key)).ToArray();
        }, ct);

    /// <summary>Copies one private balance update into application models.</summary>
    public Task<AccountSnapshot> WatchBalanceAsync(CancellationToken ct = default) => ExecuteAsync(async () =>
        Balance(await Exchange.WatchBalance(CreateParameters()).ConfigureAwait(false), Account), ct);

    /// <summary>Builds fresh routing parameters for this market; callers may add operation-specific fields.</summary>
    protected abstract Dictionary<string, object> CreateParameters();

    /// <summary>Builds maker and position parameters specific to the selected market's limit orders.</summary>
    protected abstract Dictionary<string, object> CreateLimitOrderParameters(LimitOrderRequest request, bool? hedged);

    /// <summary>Uses ordinary routing for market orders unless the subclass needs position parameters.</summary>
    protected virtual Dictionary<string, object> CreateMarketOrderParameters(MarketOrderRequest request, bool? hedged)
    {
        if (hedged is not null)
        {
            throw new NotSupportedException("Spot orders do not use a position mode.");
        }

        return CreateParameters();
    }

    /// <summary>Maps common listing metadata; subclasses can enrich it with contract-specific units.</summary>
    protected virtual Instrument DescribeInstrument(ccxt.MarketInterface market, InstrumentKey key) =>
        CcxtMarketDataProvider.MapInstrument(Venue, market, Convert.ToInt32(Exchange.precisionMode)) with { Key = key };

    /// <summary>Rejects missing fields and incorrectly routed instruments without imposing exchange trading policy.</summary>
    private void ValidateRequest(InstrumentKey key, OrderSide side, decimal quantity)
    {
        ValidateInstrument(key);
        if (!Enum.IsDefined(side) || quantity <= 0)
        {
            throw new ArgumentException("A direction and positive native quantity are required.");
        }
    }

    /// <summary>Ensures requests remain bound to the profile's venue and market.</summary>
    protected void ValidateInstrument(InstrumentKey key)
    {
        if (key.Venue != Venue || key.Market != _instrumentMarket || string.IsNullOrWhiteSpace(key.SecurityId))
        {
            throw new ArgumentException("The instrument must belong to this account's market and include a symbol.");
        }
    }

    /// <summary>Checks cancellation before dispatch, invokes once and converts errors without retrying.</summary>
    protected static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            return await operation().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TradingServiceException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new TradingServiceException(new("ExchangeError", TradingErrorText.FromException(ex)));
        }
    }

    /// <summary>Closes the transport without canceling exchange orders and clears its credentials.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Exchange.Close().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            Exchange.apiKey = "";
            Exchange.secret = "";
        }
    }
}
