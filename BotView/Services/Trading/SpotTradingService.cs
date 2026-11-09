using BotView.Models;

namespace BotView.Services.Trading;

/// <summary>Spot-specific CCXT routing and order options. Equity connectivity will be added separately.</summary>
public sealed class SpotTradingService : AbstractTradingService
{
    /// <summary>Uses the shared trading implementation with a spot account and spot instrument identifiers.</summary>
    public SpotTradingService(TradingAccountProfile profile, ccxt.Exchange exchange)
        : base(profile, exchange, TradingMarket.Spot, "spot")
    {
    }

    /// <summary>Uses the spot route without derivative-only request parameters.</summary>
    protected override Dictionary<string, object> CreateParameters() => Venue == "bybit"
        ? new() { ["category"] = "spot" } : new();

    /// <summary>Preserves maker-only intent and otherwise requests an ordinary GTC limit order.</summary>
    protected override Dictionary<string, object> CreateLimitOrderParameters(LimitOrderRequest request, bool? hedged)
    {
        if (hedged is not null)
        {
            throw new NotSupportedException("Spot orders do not use a position mode.");
        }

        var parameters = CreateParameters();
        if (request.PostOnly)
        {
            parameters["postOnly"] = true;
        }
        else
        {
            parameters["timeInForce"] = "GTC";
        }

        return parameters;
    }
}
