using ccxt;
using System;
using BotView.Configuration;

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

            return MarketCatalog.GetExchange(exchangeName).CreateClient();
        }

        /// <summary>
        /// Gets the list of supported exchange names
        /// </summary>
        /// <returns>Array of supported exchange names in lowercase</returns>
        public static string[] GetSupportedExchanges()
        {
            return MarketCatalog.Exchanges.Select(exchange => exchange.Id).ToArray();
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

            return MarketCatalog.TryGetExchange(exchangeName, out _);
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

            return MarketCatalog.GetExchange(exchangeName).DisplayName;
        }
    }
}
