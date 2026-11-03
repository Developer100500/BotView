using BotView.Models;
using static BotView.Services.Integrations.Ccxt.CcxtTradingMapper;

namespace BotView.Services.Trading;

/// <summary>USDT contract metadata, position queries and futures-specific CCXT order options.</summary>
public sealed class FuturesTradingService : AbstractTradingService
{
    /// <summary>Uses the shared trading implementation with a futures account and contract instrument identifiers.</summary>
    public FuturesTradingService(TradingAccountProfile profile, ccxt.Exchange exchange)
        : base(profile, exchange, TradingMarket.UsdtFutures, "futures")
    {
    }

    /// <summary>Enriches shared listing metadata with native contract units and USDT settlement.</summary>
    protected override Instrument DescribeInstrument(ccxt.MarketInterface market, InstrumentKey key)
    {
        var instrument = base.DescribeInstrument(market, key);
        return instrument with
        {
            TradingRules = instrument.TradingRules! with
            {
                QuantityUnit = QuantityUnit.Contracts,
                ContractSize = Number(market.contractSize),
                SettlementCurrency = "USDT"
            }
        };
    }

    /// <summary>Fetches contract positions, keeping hedge legs and margin information.</summary>
    public override Task<IReadOnlyList<PositionSnapshot>> FetchPositionsAsync(InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<PositionSnapshot>>(async () =>
        {
            ValidateInstrument(key);
            var positions = await Exchange.FetchPositions(new List<string> { key.SecurityId }, CreateParameters()).ConfigureAwait(false);
            return positions.Select(position => Position(position, key)).ToArray();
        }, ct);

    /// <summary>Selects the linear USDT futures account route.</summary>
    protected override Dictionary<string, object> CreateParameters() => Venue == "binance"
        ? new() { ["type"] = "future" } : new() { ["category"] = "linear", ["type"] = "swap" };

    /// <summary>Maps the selected position side directly; the exchange validates account mode and Reduce-only.</summary>
    private Dictionary<string, object> OrderParameters(bool reduceOnly, PositionSide? side)
    {
        var parameters = CreateParameters();
        if (Venue == "binance")
        {
            parameters["positionSide"] = side == PositionSide.Long ? "LONG" : side == PositionSide.Short ? "SHORT" : "BOTH";
        }
        else
        {
            parameters["positionIdx"] = side == PositionSide.Long ? 1 : side == PositionSide.Short ? 2 : 0;
        }

        if (reduceOnly)
        {
            parameters["reduceOnly"] = true;
        }

        return parameters;
    }

    /// <summary>Applies the venue's maker-only duration to a futures limit order.</summary>
    protected override Dictionary<string, object> CreateLimitOrderParameters(LimitOrderRequest request)
    {
        var parameters = OrderParameters(request.ReduceOnly, request.PositionSide);
        parameters["timeInForce"] = request.PostOnly ? Venue == "binance" ? "GTX" : "PostOnly" : "GTC";
        return parameters;
    }

    /// <summary>Adds the requested hedge leg and Reduce-only flag to a futures market order.</summary>
    protected override Dictionary<string, object> CreateMarketOrderParameters(MarketOrderRequest request) =>
        OrderParameters(request.ReduceOnly, request.PositionSide);

    /// <summary>Loads the public list of linear USDT contracts matching a spot pair without requiring keys.</summary>
    public Task<IReadOnlyList<string>> FetchTradingPairsAsync(string spotSymbol, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<string>>(async () =>
        {
            var markets = await Exchange.LoadMarkets().ConfigureAwait(false);
            var pair = spotSymbol.Split(':')[0];
            return markets.Values.Where(m => m.contract == true && m.linear == true && m.settle == "USDT"
                    && m.active != false && m.symbol!.Split(':')[0] == pair)
                .OrderBy(m => m.expiry.HasValue).ThenBy(m => m.expiry).Select(m => m.symbol!).ToArray();
        }, ct);
}
