using BotView.Models;
using BotView.Services.Integrations.Ccxt;

namespace BotView.Services.Trading;

/// <summary>Creates isolated transports and selects the network before any request.</summary>
public static class TradingServiceFactory
{
    /// <summary>Creates one direct trading service for the profile's market.</summary>
    public static AbstractTradingService Create(TradingAccountProfile profile, AccountCredentials? credentials = null) => profile.Market switch
    {
        TradingMarket.Spot => new SpotTradingService(profile, CreateExchange(profile, credentials)),
        TradingMarket.UsdtFutures => CreateFutures(profile, credentials),
        _ => throw new NotSupportedException("Unsupported trading market.")
    };

    /// <summary>Creates the futures service, including public contract catalog access without API keys.</summary>
    public static FuturesTradingService CreateFutures(TradingAccountProfile profile, AccountCredentials? credentials = null)
    {
        if (profile.Market != TradingMarket.UsdtFutures)
        {
            throw new ArgumentException("A futures profile is required.", nameof(profile));
        }

        return new(profile, CreateExchange(profile, credentials));
    }

    /// <summary>Creates a configured spot or linear USDT CCXT.Pro client without connecting.</summary>
    internal static ccxt.Exchange CreateExchange(TradingAccountProfile profile, AccountCredentials? credentials = null)
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
        return exchange;
    }
}
