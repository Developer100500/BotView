using BotView.Models;

namespace BotView.Services.Integrations.Ccxt;

/// <summary>Creates isolated transports and selects the network before any request.</summary>
public static class CcxtTradingClientFactory
{
    /// <summary>Creates a configured spot or linear USDT CCXT.Pro client without connecting.</summary>
    public static CcxtTradingClient Create(TradingAccountProfile profile, AccountCredentials? credentials = null)
    {
        if (!Enum.IsDefined(profile.Market) || !Enum.IsDefined(profile.Environment))
            throw new NotSupportedException("Unsupported trading market or network.");

        var settings = ProOrderBookClient.CreateSettings();
        settings["apiKey"] = credentials?.ApiKey ?? "";
        settings["secret"] = credentials?.Secret ?? "";
        settings["verbose"] = false;
        var options = (Dictionary<string, object>)settings["options"];
        if (profile.Market == TradingMarket.UsdtFutures)
        {
            options["defaultType"] = profile.Exchange == "binance" ? "future" : "swap";
            options["defaultSubType"] = "linear";
            options["fetchMarkets"] = new Dictionary<string, object> { ["types"] = new List<object> { "linear" } };
        }
        else options["defaultType"] = "spot";

        ccxt.Exchange exchange = profile.Exchange switch
        {
            "binance" when profile.Market == TradingMarket.UsdtFutures => new ccxt.pro.binanceusdm(settings),
            "binance" => new ccxt.pro.binance(settings),
            "bybit" => new ccxt.pro.bybit(settings),
            _ => throw new NotSupportedException("Trading supports Binance and Bybit only.")
        };
        if (profile.Environment == TradingEnvironment.Testnet) exchange.setSandboxMode(true);
        return new(exchange, profile.Exchange);
    }
}
