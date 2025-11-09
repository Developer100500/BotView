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
            Exchange = "Binance",
            Symbol = "BTC/USDT",
            Timeframe = "1d",
            CacheExpirationMinutes = 5,
            MaxRetryAttempts = 3,
            RequestTimeoutSeconds = 30,
            MaxCandlesPerRequest = 1000
        };

        public static readonly List<string> AvailableExchanges = new List<string>
        {
            "Binance",
            "Bybit",
            "OKX",
            "Kraken"
        };

        public static readonly List<string> SupportedTimeframes = new List<string>
        {
            "1m", "5m", "15m", "30m", "1h", "4h", "1d", "1w"
        };

        public static readonly List<string> DefaultSymbols = new List<string>
        {
            "BTC/USDT", "ETH/USDT", "BNB/USDT", "ADA/USDT", "XRP/USDT"
        };

        public static readonly Dictionary<string, ExchangeInfo> ExchangeDetails = new Dictionary<string, ExchangeInfo>
        {
            { 
                "Binance", 
                new ExchangeInfo("Binance", "https://api.binance.com", true, SupportedTimeframes) 
            },
            { 
                "Bybit", 
                new ExchangeInfo("Bybit", "https://api.bybit.com", true, SupportedTimeframes) 
            },
            { 
                "OKX", 
                new ExchangeInfo("OKX", "https://www.okx.com/api", false, SupportedTimeframes) 
            },
            { 
                "Kraken", 
                new ExchangeInfo("Kraken", "https://api.kraken.com", false, SupportedTimeframes) 
            }
        };

        public static List<string> GetActiveExchanges()
        {
            return ExchangeDetails
                .Where(x => x.Value.IsActive)
                .Select(x => x.Key)
                .ToList();
        }

        public static List<string> GetSupportedTimeframes(string exchange = null)
        {
            if (string.IsNullOrEmpty(exchange))
                return SupportedTimeframes;

            return ExchangeDetails.ContainsKey(exchange) 
                ? ExchangeDetails[exchange].SupportedTimeframes 
                : SupportedTimeframes;
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

    public class ExchangeInfo
    {
        public string Name { get; }
        public string ApiUrl { get; }
        public bool IsActive { get; set; }
        public List<string> SupportedTimeframes { get; }

        public ExchangeInfo(string name, string apiUrl, bool isActive = true, List<string>? supportedTimeframes = null)
        {
            Name = name;
            ApiUrl = apiUrl;
            IsActive = isActive;
            SupportedTimeframes = supportedTimeframes ?? new List<string>();
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