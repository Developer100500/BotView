using System;
using BotView.Interfaces;

namespace BotView.Services
{
    /// <summary>
    /// Console implementation of IExchangeLogger for development and debugging
    /// </summary>
    public class ConsoleExchangeLogger : IExchangeLogger
    {
        private readonly string _prefix = "[ExchangeService]";

        /// <summary>
        /// Logs an API request to an exchange
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="endpoint">API endpoint or operation name</param>
        /// <param name="duration">Time taken for the request</param>
        /// <param name="success">Whether the request was successful</param>
        /// <param name="symbol">Trading symbol (optional)</param>
        /// <param name="timeframe">Timeframe (optional)</param>
        public void LogApiRequest(string exchange, string endpoint, TimeSpan duration, bool success, string symbol = null, string timeframe = null)
        {
            var status = success ? "SUCCESS" : "FAILED";
            var symbolInfo = !string.IsNullOrEmpty(symbol) ? $" Symbol: {symbol}" : "";
            var timeframeInfo = !string.IsNullOrEmpty(timeframe) ? $" Timeframe: {timeframe}" : "";
            
            Console.WriteLine($"{_prefix} API REQUEST [{status}] {exchange.ToUpper()}/{endpoint} " +
                            $"Duration: {duration.TotalMilliseconds:F0}ms{symbolInfo}{timeframeInfo}");
        }

        /// <summary>
        /// Logs an error that occurred during exchange operations
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="exception">Exception that occurred</param>
        /// <param name="context">Additional context information</param>
        public void LogError(string exchange, Exception exception, string context = "")
        {
            var contextInfo = !string.IsNullOrEmpty(context) ? $" Context: {context}" : "";
            Console.WriteLine($"{_prefix} ERROR [{exchange.ToUpper()}]: {exception.Message}{contextInfo}");
            
            if (exception.InnerException != null)
            {
                Console.WriteLine($"{_prefix} Inner exception: {exception.InnerException.Message}");
            }
        }

        /// <summary>
        /// Logs successful data retrieval from exchange
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="symbol">Trading symbol</param>
        /// <param name="candleCount">Number of candles received</param>
        /// <param name="timeframe">Timeframe of the data</param>
        public void LogDataReceived(string exchange, string symbol, int candleCount, string timeframe)
        {
            Console.WriteLine($"{_prefix} DATA RECEIVED [{exchange.ToUpper()}]: {candleCount} candles " +
                            $"for {symbol} ({timeframe})");
        }

        /// <summary>
        /// Logs cache hit event
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="symbol">Trading symbol</param>
        /// <param name="timeframe">Timeframe</param>
        /// <param name="cacheAge">Age of cached data</param>
        public void LogCacheHit(string exchange, string symbol, string timeframe, TimeSpan cacheAge)
        {
            Console.WriteLine($"{_prefix} CACHE HIT [{exchange.ToUpper()}]: {symbol} ({timeframe}) " +
                            $"Age: {cacheAge.TotalMinutes:F1}min");
        }

        /// <summary>
        /// Logs cache miss event
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="symbol">Trading symbol</param>
        /// <param name="timeframe">Timeframe</param>
        public void LogCacheMiss(string exchange, string symbol, string timeframe)
        {
            Console.WriteLine($"{_prefix} CACHE MISS [{exchange.ToUpper()}]: {symbol} ({timeframe})");
        }

        /// <summary>
        /// Logs retry attempt
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="operation">Operation being retried</param>
        /// <param name="attemptNumber">Current attempt number</param>
        /// <param name="maxAttempts">Maximum number of attempts</param>
        /// <param name="delay">Delay before retry</param>
        /// <param name="reason">Reason for retry</param>
        public void LogRetryAttempt(string exchange, string operation, int attemptNumber, int maxAttempts, TimeSpan delay, string reason)
        {
            Console.WriteLine($"{_prefix} RETRY [{exchange.ToUpper()}]: {operation} " +
                            $"Attempt {attemptNumber}/{maxAttempts} in {delay.TotalSeconds:F1}s " +
                            $"Reason: {reason}");
        }

        /// <summary>
        /// Logs fallback to cached data
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="symbol">Trading symbol</param>
        /// <param name="timeframe">Timeframe</param>
        /// <param name="cacheAge">Age of cached data being used</param>
        /// <param name="reason">Reason for fallback</param>
        public void LogCacheFallback(string exchange, string symbol, string timeframe, TimeSpan cacheAge, string reason)
        {
            Console.WriteLine($"{_prefix} CACHE FALLBACK [{exchange.ToUpper()}]: Using cached data for {symbol} ({timeframe}) " +
                            $"Age: {cacheAge.TotalMinutes:F1}min Reason: {reason}");
        }

        /// <summary>
        /// Logs connection test result
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="success">Whether connection test was successful</param>
        /// <param name="duration">Time taken for connection test</param>
        public void LogConnectionTest(string exchange, bool success, TimeSpan duration)
        {
            var status = success ? "SUCCESS" : "FAILED";
            Console.WriteLine($"{_prefix} CONNECTION TEST [{exchange.ToUpper()}]: {status} " +
                            $"Duration: {duration.TotalMilliseconds:F0}ms");
        }

        /// <summary>
        /// Logs performance metrics
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="operation">Operation name</param>
        /// <param name="duration">Duration of operation</param>
        /// <param name="dataSize">Size of data processed (optional)</param>
        public void LogPerformanceMetrics(string exchange, string operation, TimeSpan duration, long? dataSize = null)
        {
            var dataSizeInfo = dataSize.HasValue ? $" DataSize: {dataSize.Value} bytes" : "";
            Console.WriteLine($"{_prefix} PERFORMANCE [{exchange.ToUpper()}]: {operation} " +
                            $"Duration: {duration.TotalMilliseconds:F0}ms{dataSizeInfo}");
        }

        /// <summary>
        /// Logs general information
        /// </summary>
        /// <param name="message">Information message</param>
        /// <param name="exchange">Exchange name (optional)</param>
        public void LogInfo(string message, string exchange = null)
        {
            var exchangeInfo = !string.IsNullOrEmpty(exchange) ? $"[{exchange.ToUpper()}] " : "";
            Console.WriteLine($"{_prefix} INFO: {exchangeInfo}{message}");
        }

        /// <summary>
        /// Logs warning message
        /// </summary>
        /// <param name="message">Warning message</param>
        /// <param name="exchange">Exchange name (optional)</param>
        public void LogWarning(string message, string exchange = null)
        {
            var exchangeInfo = !string.IsNullOrEmpty(exchange) ? $"[{exchange.ToUpper()}] " : "";
            Console.WriteLine($"{_prefix} WARNING: {exchangeInfo}{message}");
        }
    }
}