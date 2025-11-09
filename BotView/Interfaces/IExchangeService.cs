using BotView.Chart;
using BotView.Services;

namespace BotView.Interfaces
{
    /// <summary>
    /// Interface for working with cryptocurrency exchanges
    /// </summary>
    public interface IExchangeService
    {
        /// <summary>
        /// Gets candlestick data from specified exchange
        /// </summary>
        /// <param name="exchange">Exchange name (e.g., "binance", "bybit")</param>
        /// <param name="symbol">Trading pair symbol (e.g., "BTC/USDT")</param>
        /// <param name="timeframe">Timeframe (e.g., "1m", "5m", "1h", "1d")</param>
        /// <param name="limit">Maximum number of candles to retrieve (default: 500)</param>
        /// <returns>CandlestickData containing the retrieved candles</returns>
        Task<CandlestickData> GetCandlestickDataAsync(string exchange, string symbol, string timeframe, int limit = 500);

        /// <summary>
        /// Gets list of available trading symbols from specified exchange
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <returns>List of available trading pair symbols</returns>
        Task<List<string>> GetAvailableSymbolsAsync(string exchange);

        /// <summary>
        /// Tests connection to specified exchange
        /// </summary>
        /// <param name="exchange">Exchange name to test</param>
        /// <returns>True if connection is successful, false otherwise</returns>
        Task<bool> TestConnectionAsync(string exchange);

        /// <summary>
        /// Gets list of supported exchanges
        /// </summary>
        /// <returns>List of supported exchange names</returns>
        List<string> GetSupportedExchanges();

        /// <summary>
        /// Gets list of supported timeframes
        /// </summary>
        /// <returns>List of supported timeframe strings</returns>
        List<string> GetSupportedTimeframes();

        /// <summary>
        /// Gets performance metrics for exchange operations
        /// </summary>
        /// <returns>Performance metrics instance</returns>
        ExchangePerformanceMetrics GetPerformanceMetrics();

        /// <summary>
        /// Logs detailed performance metrics for monitoring
        /// </summary>
        void LogDetailedPerformanceMetrics();

        /// <summary>
        /// Clears expired cache entries
        /// </summary>
        void ClearExpiredCache();
    }
}