using System.Globalization;
using BotView.Models;

namespace BotView.Services.Integrations.Ccxt;

/// <summary>Thin, stateless boundary around a configured CCXT exchange. Responses remain native CCXT types.</summary>
public sealed class CcxtTradingClient : IAsyncDisposable
{
    private readonly ccxt.Exchange _exchange;
    private readonly string _venue;
    internal ccxt.Exchange Transport => _exchange;

    /// <summary>Uses an already configured transport; construction never makes a network request.</summary>
    public CcxtTradingClient(ccxt.Exchange exchange, string venue)
    {
        _exchange = exchange;
        _venue = venue;
    }

    /// <summary>Loads the public instrument catalog using CCXT caching.</summary>
    public Task<Dictionary<string, ccxt.MarketInterface>> LoadMarketsAsync(CancellationToken ct = default) =>
        CallAsync(() => _exchange.LoadMarkets(), ct);

    /// <summary>Returns whether the configured transport supports a native operation.</summary>
    public bool Supports(string operation) => _exchange.has.GetValueOrDefault(operation) is true;

    /// <summary>Formats amount precision using the loaded market metadata.</summary>
    public decimal AmountToPrecision(string symbol, decimal amount) => decimal.Parse(
        _exchange.amountToPrecision(symbol, amount.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);

    /// <summary>Formats price precision using the loaded market metadata.</summary>
    public decimal PriceToPrecision(string symbol, decimal price) => decimal.Parse(
        _exchange.priceToPrecision(symbol, price.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);

    /// <summary>Returns the precision convention required to map market metadata.</summary>
    public int PrecisionMode => Convert.ToInt32(_exchange.precisionMode);

    /// <summary>Creates one spot limit order, optionally maker-only.</summary>
    public Task<ccxt.Order> CreateSpotLimitOrderAsync(string symbol, string side, decimal amount, decimal price,
        bool postOnly = false, CancellationToken ct = default) =>
        CallAsync(() => _exchange.CreateOrder(symbol, "limit", side, (double)amount, (double)price, SpotLimitParameters(postOnly)), ct);

    /// <summary>Creates one spot market order with a base-asset quantity and no limit price.</summary>
    public Task<ccxt.Order> CreateSpotMarketOrderAsync(string symbol, string side, decimal amount, CancellationToken ct = default) =>
        CallAsync(() => _exchange.CreateOrder(symbol, "market", side, (double)amount, null, SpotParameters()), ct);

    /// <summary>Creates one USDT contract limit order with explicit position parameters.</summary>
    public Task<ccxt.Order> CreateUsdtFuturesLimitOrderAsync(string symbol, string side, decimal contracts, decimal price,
        bool postOnly = false, bool reduceOnly = false, PositionSide? positionSide = null, CancellationToken ct = default)
    {
        var parameters = FuturesOrderParameters(reduceOnly, positionSide);
        parameters["timeInForce"] = postOnly ? _venue == "binance" ? "GTX" : "PostOnly" : "GTC";
        return CallAsync(() => _exchange.CreateOrder(symbol, "limit", side, (double)contracts, (double)price, parameters), ct);
    }

    /// <summary>Creates one USDT contract market order without a price or maker-only flag.</summary>
    public Task<ccxt.Order> CreateUsdtFuturesMarketOrderAsync(string symbol, string side, decimal contracts,
        bool reduceOnly = false, PositionSide? positionSide = null, CancellationToken ct = default) =>
        CallAsync(() => _exchange.CreateOrder(symbol, "market", side, (double)contracts, null,
            FuturesOrderParameters(reduceOnly, positionSide)), ct);

    /// <summary>Builds spot routing without derivative-only options.</summary>
    private Dictionary<string, object> SpotParameters() => _venue == "bybit"
        ? new() { ["category"] = "spot" } : new();

    /// <summary>Builds maker parameters without changing a rejected maker request into a taker order.</summary>
    private Dictionary<string, object> SpotLimitParameters(bool postOnly)
    {
        var parameters = SpotParameters();
        if (postOnly) parameters["postOnly"] = true;
        else parameters["timeInForce"] = "GTC";
        return parameters;
    }

    /// <summary>Selects linear USDT routing; exchange-specific options stay at this boundary.</summary>
    private Dictionary<string, object> UsdtFuturesParameters() => _venue == "binance"
        ? new() { ["type"] = "future" } : new() { ["category"] = "linear", ["type"] = "swap" };

    /// <summary>Maps the user's position selection directly; the exchange checks the account's position mode.</summary>
    private Dictionary<string, object> FuturesOrderParameters(bool reduceOnly, PositionSide? side)
    {
        var parameters = UsdtFuturesParameters();
        if (_venue == "binance")
        {
            parameters["positionSide"] = side == PositionSide.Long ? "LONG" : side == PositionSide.Short ? "SHORT" : "BOTH";
            if (reduceOnly) parameters["reduceOnly"] = true;
        }
        else
        {
            parameters["positionIdx"] = side == PositionSide.Long ? 1 : side == PositionSide.Short ? 2 : 0;
            if (reduceOnly) parameters["reduceOnly"] = true;
        }
        return parameters;
    }

    /// <summary>Cancels a spot order by exchange ID without assuming it was fully unfilled.</summary>
    public Task<ccxt.Order> CancelSpotOrderAsync(string id, string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.CancelOrder(id, symbol, SpotParameters()), ct);

    /// <summary>Cancels a contract order using its linear account route.</summary>
    public Task<ccxt.Order> CancelUsdtFuturesOrderAsync(string id, string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.CancelOrder(id, symbol, UsdtFuturesParameters()), ct);

    /// <summary>Fetches one spot order by its exchange ID.</summary>
    public Task<ccxt.Order> FetchSpotOrderAsync(string id, string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.FetchOrder(id, symbol, SpotParameters()), ct);

    /// <summary>Fetches one contract order by its exchange ID.</summary>
    public Task<ccxt.Order> FetchUsdtFuturesOrderAsync(string id, string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.FetchOrder(id, symbol, UsdtFuturesParameters()), ct);

    /// <summary>Fetches the current spot open-order snapshot using CCXT pagination.</summary>
    public Task<List<ccxt.Order>> FetchSpotOpenOrdersAsync(string symbol, CancellationToken ct = default)
    {
        var parameters = SpotParameters();
        parameters["paginate"] = true;
        return CallAsync(() => _exchange.FetchOpenOrders(symbol, null, null, parameters), ct);
    }

    /// <summary>Fetches the current contract open-order snapshot using CCXT pagination.</summary>
    public Task<List<ccxt.Order>> FetchUsdtFuturesOpenOrdersAsync(string symbol, CancellationToken ct = default)
    {
        var parameters = UsdtFuturesParameters();
        parameters["paginate"] = true;
        return CallAsync(() => _exchange.FetchOpenOrders(symbol, null, null, parameters), ct);
    }

    /// <summary>Fetches recent spot executions; this is a display window, not a durable history.</summary>
    public Task<List<ccxt.Trade>> FetchSpotMyTradesAsync(string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.FetchMyTrades(symbol, null, 100, SpotParameters()), ct);

    /// <summary>Fetches recent executions for a USDT contract.</summary>
    public Task<List<ccxt.Trade>> FetchUsdtFuturesMyTradesAsync(string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.FetchMyTrades(symbol, null, 100, UsdtFuturesParameters()), ct);

    /// <summary>Fetches native spot balances.</summary>
    public Task<ccxt.Balances> FetchSpotBalanceAsync(CancellationToken ct = default) =>
        CallAsync(() => _exchange.FetchBalance(SpotParameters()), ct);

    /// <summary>Fetches native futures collateral balances.</summary>
    public Task<ccxt.Balances> FetchUsdtFuturesBalanceAsync(CancellationToken ct = default) =>
        CallAsync(() => _exchange.FetchBalance(UsdtFuturesParameters()), ct);

    /// <summary>Fetches positions for one USDT contract.</summary>
    public Task<List<ccxt.Position>> FetchUsdtFuturesPositionsAsync(string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.FetchPositions(new List<string> { symbol }, UsdtFuturesParameters()), ct);

    /// <summary>Reads one private spot order batch.</summary>
    public Task<List<ccxt.Order>> WatchSpotOrdersAsync(string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.WatchOrders(symbol, null, null, SpotParameters()), ct);

    /// <summary>Reads one private contract order batch.</summary>
    public Task<List<ccxt.Order>> WatchUsdtFuturesOrdersAsync(string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.WatchOrders(symbol, null, null, UsdtFuturesParameters()), ct);

    /// <summary>Reads one private spot execution batch.</summary>
    public Task<List<ccxt.Trade>> WatchSpotMyTradesAsync(string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.WatchMyTrades(symbol, null, null, SpotParameters()), ct);

    /// <summary>Reads one private contract execution batch.</summary>
    public Task<List<ccxt.Trade>> WatchUsdtFuturesMyTradesAsync(string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.WatchMyTrades(symbol, null, null, UsdtFuturesParameters()), ct);

    /// <summary>Reads one private spot balance update.</summary>
    public Task<ccxt.Balances> WatchSpotBalanceAsync(CancellationToken ct = default) =>
        CallAsync(() => _exchange.WatchBalance(SpotParameters()), ct);

    /// <summary>Reads one private futures balance update.</summary>
    public Task<ccxt.Balances> WatchUsdtFuturesBalanceAsync(CancellationToken ct = default) =>
        CallAsync(() => _exchange.WatchBalance(UsdtFuturesParameters()), ct);

    /// <summary>Checks cancellation before dispatch and waits without repeating the native call.</summary>
    private static Task<T> CallAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return operation().WaitAsync(ct);
    }

    /// <summary>Closes local transports without canceling exchange orders.</summary>
    public async ValueTask DisposeAsync()
    {
        try { await _exchange.Close().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        finally { _exchange.apiKey = ""; _exchange.secret = ""; }
    }
}
