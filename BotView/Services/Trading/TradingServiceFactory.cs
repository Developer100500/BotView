using BotView.Models;
using BotView.Services.Integrations.Ccxt;
using BotView.Services.Integrations.Quik;

namespace BotView.Services.Trading;

/// <summary>Creates isolated transports and selects the network before any request.</summary>
public static class TradingServiceFactory
{
    /// <summary>Creates a crypto spot or USDT futures service, including public catalog access without keys.</summary>
    public static CryptoTradingService CreateCryptoService(TradingAccountProfile profile, AccountCredentials? credentials = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.Market is not (TradingMarket.CryptoSpot or TradingMarket.UsdtFutures))
        {
            throw new ArgumentException("A crypto spot or USDT futures profile is required.", nameof(profile));
        }

        return new CryptoTradingService(profile, CreateExchange(profile, credentials));
    }

    /// <summary>Creates the read-only QUIK service for a MOEX shares or futures profile.</summary>
    public static QuikTradingService CreateMoexService(TradingAccountProfile profile)
    {
        if (profile.Market is not (TradingMarket.MoexShares or TradingMarket.MoexFutures))
            throw new ArgumentException("A MOEX shares or futures profile is required.", nameof(profile));

        return new QuikTradingService(profile);
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
