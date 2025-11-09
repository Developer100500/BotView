using ccxt;
using System;

namespace BotView.Services
{
    /// <summary>
    /// Factory for creating CCXT exchange instances
    /// </summary>
    public static class ExchangeFactory
    {
        /// <summary>
        /// Creates a CCXT exchange instance for the specified exchange name
        /// </summary>
        /// <param name="exchangeName">Name of the exchange (case-insensitive)</param>
        /// <returns>CCXT Exchange instance</returns>
        /// <exception cref="ArgumentException">Thrown when exchangeName is null or empty</exception>
        /// <exception cref="NotSupportedException">Thrown when the exchange is not supported</exception>
        public static Exchange CreateExchange(string exchangeName)
        {
            if (string.IsNullOrWhiteSpace(exchangeName))
            {
                throw new ArgumentException("Exchange name cannot be null or empty", nameof(exchangeName));
            }

            return exchangeName.ToLowerInvariant() switch
            {
                "binance" => new binance(),
                "bybit" => new bybit(),
                "okx" => new okx(),
                "kraken" => new kraken(),
                _ => throw new NotSupportedException($"Exchange '{exchangeName}' is not supported. Supported exchanges: Binance, Bybit, OKX, Kraken")
            };
        }

        /// <summary>
        /// Gets the list of supported exchange names
        /// </summary>
        /// <returns>Array of supported exchange names in lowercase</returns>
        public static string[] GetSupportedExchanges()
        {
            return new string[] { "binance", "bybit", "okx", "kraken" };
        }

        /// <summary>
        /// Checks if the specified exchange is supported
        /// </summary>
        /// <param name="exchangeName">Name of the exchange to check</param>
        /// <returns>True if the exchange is supported, false otherwise</returns>
        public static bool IsExchangeSupported(string exchangeName)
        {
            if (string.IsNullOrWhiteSpace(exchangeName))
            {
                return false;
            }

            return exchangeName.ToLowerInvariant() switch
            {
                "binance" or "bybit" or "okx" or "kraken" => true,
                _ => false
            };
        }

        /// <summary>
        /// Gets the display name for the specified exchange
        /// </summary>
        /// <param name="exchangeName">Name of the exchange</param>
        /// <returns>Display name of the exchange</returns>
        /// <exception cref="NotSupportedException">Thrown when the exchange is not supported</exception>
        public static string GetExchangeDisplayName(string exchangeName)
        {
            if (string.IsNullOrWhiteSpace(exchangeName))
            {
                throw new ArgumentException("Exchange name cannot be null or empty", nameof(exchangeName));
            }

            return exchangeName.ToLowerInvariant() switch
            {
                "binance" => "Binance",
                "bybit" => "Bybit",
                "okx" => "OKX",
                "kraken" => "Kraken",
                _ => throw new NotSupportedException($"Exchange '{exchangeName}' is not supported")
            };
        }
    }
}