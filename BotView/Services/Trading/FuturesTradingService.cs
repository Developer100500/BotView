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

    /// <summary>Sets one leverage value for the instrument. Bybit applies that value to both hedge legs.</summary>
    public override Task SetLeverageAsync(InstrumentKey key, int leverage, CancellationToken ct = default) =>
        ExecuteAsync(async () =>
        {
            ValidateInstrument(key);

            if (leverage < 1)
            {
                throw new ArgumentException("Leverage must be a whole number greater than or equal to 1.", nameof(leverage));
            }

            var parameters = new Dictionary<string, object>();

            if (Venue == "bybit")
            {
                parameters["category"] = "linear";
            }

            await Exchange.SetLeverage(leverage, key.SecurityId, parameters).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>Selects the linear USDT futures account route for a supported venue.</summary>
    protected override Dictionary<string, object> CreateParameters() => Venue switch
    {
        "binance" => new() { ["type"] = "future" },
        "bybit" => new() { ["category"] = "linear", ["type"] = "swap" },
        "okx" => new() { ["type"] = "swap" },
        _ => throw new TradingServiceException(new("VenueUnsupported", $"Futures trading is not supported for {Venue}."))
    };

    /// <summary>Reads hedge versus one-way from the exchange. A missing report stays null and blocks new orders.</summary>
    public override Task<bool?> FetchPositionModeAsync(InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync(async () =>
        {
            ValidateInstrument(key);
            return await ReadHedgedAsync(key).ConfigureAwait(false);
        }, ct);

    /// <summary>Prefers the unified position-mode call. Venues that only expose the flag on positions, such as Bybit, use that report instead.</summary>
    private async Task<bool?> ReadHedgedAsync(InstrumentKey key)
    {
        if (Exchange.has.GetValueOrDefault("fetchPositionMode") is true)
        {
            var mode = await Exchange.FetchPositionMode(key.SecurityId, CreateParameters()).ConfigureAwait(false);
            return mode.hedged;
        }

        if (Exchange.has.GetValueOrDefault("fetchPositions") is not true)
        {
            return null;
        }

        var positions = await Exchange.FetchPositions(new List<string> { key.SecurityId }, CreateParameters()).ConfigureAwait(false);
        bool? hedged = null;
        foreach (var position in positions)
        {
            if (position.symbol is string symbol && symbol != key.SecurityId)
            {
                continue;
            }

            if (position.hedged is not bool value)
            {
                continue;
            }

            if (hedged is bool known && known != value)
            {
                return null;
            }

            hedged = value;
        }

        return hedged;
    }

    /// <summary>Maps one-way and hedge orders to the venue's explicit parameters. The account mode is never guessed.</summary>
    private Dictionary<string, object> OrderParameters(bool reduceOnly, PositionSide? side, bool? hedged)
    {
        // The exchange did not report one-way or hedge mode, so the order is refused rather than guessed.
        if (hedged is not bool twoWay)
        {
            throw new TradingServiceException(new("PositionModeUnavailable",
                "The exchange did not report whether this account is in one-way or hedge mode."));
        }

        var parameters = CreateParameters();
        // Reduce-only tells the venue this order may only shrink an existing position.
        if (reduceOnly)
        {
            parameters["reduceOnly"] = true;
        }

        // One-way mode has a single net position, addressed by Buy or Sell alone.
        if (!twoWay)
        {
            // A Long or Short side belongs to hedge mode and is not accepted here.
            if (side is not null)
            {
                throw new TradingServiceException(new("PositionSideNotUsed", "One-way mode uses Buy or Sell only."));
            }

            switch (Venue)
            {
                case "binance": parameters["positionSide"] = "BOTH"; break;
                case "bybit": parameters["positionIdx"] = 0; break;
                case "okx": parameters["posSide"] = "net"; break;
                default: throw UnsupportedVenue();
            }

            return parameters;
        }

        // Hedge mode keeps long and short open together, so the order must name one of those legs.
        if (side is not (PositionSide.Long or PositionSide.Short))
        {
            throw new TradingServiceException(new("PositionSideRequired", "Select Long or Short for a hedge-mode futures order."));
        }

        switch (Venue)
        {
            case "binance":
                parameters["positionSide"] = side == PositionSide.Long ? "LONG" : "SHORT";
                break;
            case "bybit":
                parameters["positionIdx"] = side == PositionSide.Long ? 1 : 2;
                break;
            case "okx":
                parameters["posSide"] = side == PositionSide.Long ? "long" : "short";
                break;
            default:
                throw UnsupportedVenue();
        }

        return parameters;
    }

    /// <summary>Keeps an unknown venue from receiving another exchange's position fields.</summary>
    private TradingServiceException UnsupportedVenue() =>
        new(new("VenueUnsupported", $"Futures position mode is not supported for {Venue}."));

    /// <summary>Applies the venue's maker-only duration to a futures limit order.</summary>
    protected override Dictionary<string, object> CreateLimitOrderParameters(LimitOrderRequest request, bool? hedged)
    {
        var parameters = OrderParameters(request.ReduceOnly, request.PositionSide, hedged);
        parameters["timeInForce"] = request.PostOnly ? Venue == "binance" ? "GTX" : "PostOnly" : "GTC";
        return parameters;
    }

    /// <summary>Adds the reported position mode and Reduce-only flag to a futures market order.</summary>
    protected override Dictionary<string, object> CreateMarketOrderParameters(MarketOrderRequest request, bool? hedged) =>
        OrderParameters(request.ReduceOnly, request.PositionSide, hedged);

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
