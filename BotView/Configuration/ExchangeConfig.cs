using System;
using System.Collections.Generic;
using System.Linq;

namespace BotView.Configuration
{
    public static class ExchangeConfig
    {
        // Настройки по умолчанию
        public static readonly ApplicationDefaults Defaults = new ApplicationDefaults
        {
            Exchange = MarketCatalog.GetExchange(MarketCatalog.DefaultExchangeId).DisplayName,
            Symbol = "BTC/USDT",
            Timeframe = MarketCatalog.DefaultTimeframeId,
            CacheExpirationMinutes = 5,
            MaxRetryAttempts = 3,
            RequestTimeoutSeconds = 30,
            MaxCandlesPerRequest = 1000
        };

        public static List<string> AvailableExchanges =>
            MarketCatalog.Exchanges.Select(exchange => exchange.DisplayName).ToList();

        public static List<string> SupportedTimeframes => MarketCatalog.TimeframeIds.ToList();

        public static readonly List<string> DefaultSymbols = new List<string>
        {
            "BTC/USDT", "ETH/USDT", "BNB/USDT", "ADA/USDT", "XRP/USDT"
        };

        public static List<string> GetActiveExchanges()
        {
            return MarketCatalog.Exchanges
                .Where(exchange => exchange.IsActive)
                .Select(exchange => exchange.DisplayName)
                .ToList();
        }

        public static List<string> GetSupportedTimeframes(string? exchange = null)
        {
            if (string.IsNullOrEmpty(exchange))
                return SupportedTimeframes;

            var definition = MarketCatalog.Exchanges.FirstOrDefault(item =>
                item.Id.Equals(exchange, StringComparison.OrdinalIgnoreCase) ||
                item.DisplayName.Equals(exchange, StringComparison.OrdinalIgnoreCase));
            return definition?.SupportedTimeframes.ToList() ?? SupportedTimeframes;
        }

        public static CacheSettings GetCacheSettings()
        {
            return new CacheSettings
            {
                ExpirationMinutes = Defaults.CacheExpirationMinutes,
                MaxCacheSize = 100, // Максимальное количество кэшированных запросов
                EnableCaching = true
            };
        }

        public static ConnectionSettings GetConnectionSettings()
        {
            return new ConnectionSettings
            {
                TimeoutSeconds = Defaults.RequestTimeoutSeconds,
                MaxRetryAttempts = Defaults.MaxRetryAttempts,
                RetryDelaySeconds = 1
            };
        }
    }

    public class ApplicationDefaults
    {
        public string Exchange { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string Timeframe { get; set; } = string.Empty;
        public int CacheExpirationMinutes { get; set; }
        public int MaxRetryAttempts { get; set; }
        public int RequestTimeoutSeconds { get; set; }
        public int MaxCandlesPerRequest { get; set; }
    }

    public class CacheSettings
    {
        public int ExpirationMinutes { get; set; }
        public int MaxCacheSize { get; set; }
        public bool EnableCaching { get; set; }
    }

    public class ConnectionSettings
    {
        public int TimeoutSeconds { get; set; }
        public int MaxRetryAttempts { get; set; }
        public int RetryDelaySeconds { get; set; }
    }
}
