using System;

namespace BotView.Interfaces
{
    /// <summary>
    /// Interface for logging exchange operations, API requests, and performance metrics
    /// </summary>
    public interface IExchangeLogger
    {
        /// <summary>
        /// Logs an API request to an exchange
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="endpoint">API endpoint or operation name</param>
        /// <param name="duration">Time taken for the request</param>
        /// <param name="success">Whether the request was successful</param>
        /// <param name="symbol">Trading symbol (optional)</param>
        /// <param name="timeframe">Timeframe (optional)</param>
        void LogApiRequest(string exchange, string endpoint, TimeSpan duration, bool success, string symbol = null, string timeframe = null);

        /// <summary>
        /// Logs an error that occurred during exchange operations
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="exception">Exception that occurred</param>
        /// <param name="context">Additional context information</param>
        void LogError(string exchange, Exception exception, string context = "");

        /// <summary>
        /// Logs successful data retrieval from exchange
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="symbol">Trading symbol</param>
        /// <param name="candleCount">Number of candles received</param>
        /// <param name="timeframe">Timeframe of the data</param>
        void LogDataReceived(string exchange, string symbol, int candleCount, string timeframe);

        /// <summary>
        /// Logs cache hit event
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="symbol">Trading symbol</param>
        /// <param name="timeframe">Timeframe</param>
        /// <param name="cacheAge">Age of cached data</param>
        void LogCacheHit(string exchange, string symbol, string timeframe, TimeSpan cacheAge);

        /// <summary>
        /// Logs cache miss event
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="symbol">Trading symbol</param>
        /// <param name="timeframe">Timeframe</param>
        void LogCacheMiss(string exchange, string symbol, string timeframe);

        /// <summary>
        /// Logs retry attempt
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="operation">Operation being retried</param>
        /// <param name="attemptNumber">Current attempt number</param>
        /// <param name="maxAttempts">Maximum number of attempts</param>
        /// <param name="delay">Delay before retry</param>
        /// <param name="reason">Reason for retry</param>
        void LogRetryAttempt(string exchange, string operation, int attemptNumber, int maxAttempts, TimeSpan delay, string reason);

        /// <summary>
        /// Logs fallback to cached data
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="symbol">Trading symbol</param>
        /// <param name="timeframe">Timeframe</param>
        /// <param name="cacheAge">Age of cached data being used</param>
        /// <param name="reason">Reason for fallback</param>
        void LogCacheFallback(string exchange, string symbol, string timeframe, TimeSpan cacheAge, string reason);

        /// <summary>
        /// Logs connection test result
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="success">Whether connection test was successful</param>
        /// <param name="duration">Time taken for connection test</param>
        void LogConnectionTest(string exchange, bool success, TimeSpan duration);

        /// <summary>
        /// Logs performance metrics
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="operation">Operation name</param>
        /// <param name="duration">Duration of operation</param>
        /// <param name="dataSize">Size of data processed (optional)</param>
        void LogPerformanceMetrics(string exchange, string operation, TimeSpan duration, long? dataSize = null);

        /// <summary>
        /// Logs general information
        /// </summary>
        /// <param name="message">Information message</param>
        /// <param name="exchange">Exchange name (optional)</param>
        void LogInfo(string message, string exchange = null);

        /// <summary>
        /// Logs warning message
        /// </summary>
        /// <param name="message">Warning message</param>
        /// <param name="exchange">Exchange name (optional)</param>
        void LogWarning(string message, string exchange = null);
    }
}