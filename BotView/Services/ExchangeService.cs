using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;
using System.Net;
using System.Net.Http;
using System.Diagnostics;
using ccxt;
using BotView.Chart;
using BotView.Interfaces;
using BotView.Exceptions;

namespace BotView.Services
{
    /// <summary>
    /// Service for working with cryptocurrency exchanges using CCXT library
    /// </summary>
    public class ExchangeService : IExchangeService
    {
        private readonly Dictionary<string, Exchange> _exchanges;
        private readonly IDataProvider _dataProvider;
        private readonly IExchangeLogger _logger;
        private readonly ExchangePerformanceMetrics _performanceMetrics;
        private readonly Dictionary<string, CandlestickData> _cache;
        private readonly Dictionary<string, DateTime> _cacheTimestamps;
        private readonly TimeSpan _cacheExpiration;
        private readonly int _maxRetryAttempts;
        private readonly TimeSpan _baseRetryDelay;

        /// <summary>
        /// Supported timeframes across all exchanges
        /// </summary>
        private static readonly List<string> SupportedTimeframes = new List<string>
        {
            "1m", "5m", "15m", "30m", "1h", "4h", "1d", "1w"
        };

        /// <summary>
        /// Initializes a new instance of ExchangeService
        /// </summary>
        /// <param name="dataProvider">Data provider for converting CCXT data</param>
        /// <param name="logger">Logger for exchange operations (optional)</param>
        /// <param name="cacheExpirationMinutes">Cache expiration time in minutes (default: 5)</param>
        /// <param name="maxRetryAttempts">Maximum number of retry attempts for failed requests (default: 3)</param>
        /// <param name="baseRetryDelaySeconds">Base delay between retry attempts in seconds (default: 1)</param>
        public ExchangeService(IDataProvider dataProvider, IExchangeLogger logger = null, int cacheExpirationMinutes = 5, int maxRetryAttempts = 3, int baseRetryDelaySeconds = 1)
        {
            _dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
            _logger = logger;
            _performanceMetrics = new ExchangePerformanceMetrics();
            _exchanges = new Dictionary<string, Exchange>();
            _cache = new Dictionary<string, CandlestickData>();
            _cacheTimestamps = new Dictionary<string, DateTime>();
            _cacheExpiration = TimeSpan.FromMinutes(cacheExpirationMinutes);
            _maxRetryAttempts = Math.Max(1, maxRetryAttempts);
            _baseRetryDelay = TimeSpan.FromSeconds(Math.Max(1, baseRetryDelaySeconds));
        }

        /// <summary>
        /// Gets candlestick data from specified exchange using CCXT FetchOHLCV with retry logic and fallback to cache
        /// </summary>
        /// <param name="exchange">Exchange name (e.g., "binance", "bybit")</param>
        /// <param name="symbol">Trading pair symbol (e.g., "BTC/USDT")</param>
        /// <param name="timeframe">Timeframe (e.g., "1m", "5m", "1h", "1d")</param>
        /// <param name="limit">Maximum number of candles to retrieve (default: 500)</param>
        /// <returns>CandlestickData containing the retrieved candles</returns>
        /// <exception cref="ArgumentException">Thrown when parameters are invalid</exception>
        /// <exception cref="NotSupportedException">Thrown when exchange or timeframe is not supported</exception>
        /// <exception cref="ExchangeException">Thrown when exchange API call fails and no cached data is available</exception>
        public async Task<CandlestickData> GetCandlestickDataAsync(string exchange, string symbol, string timeframe, int limit = 500)
        {
            // Validate input parameters
            if (string.IsNullOrWhiteSpace(exchange))
                throw new ArgumentException("Exchange name cannot be null or empty", nameof(exchange));
            
            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentException("Symbol cannot be null or empty", nameof(symbol));
            
            if (string.IsNullOrWhiteSpace(timeframe))
                throw new ArgumentException("Timeframe cannot be null or empty", nameof(timeframe));
            
            if (limit <= 0)
                throw new ArgumentException("Limit must be greater than zero", nameof(limit));

            // Normalize exchange name
            var normalizedExchange = exchange.ToLowerInvariant();
            
            // Check if exchange is supported
            if (!ExchangeFactory.IsExchangeSupported(normalizedExchange))
                throw new NotSupportedException($"Exchange '{exchange}' is not supported");

            // Check if timeframe is supported
            if (!SupportedTimeframes.Contains(timeframe))
                throw new NotSupportedException($"Timeframe '{timeframe}' is not supported");

            // Check cache first
            var cacheKey = $"{normalizedExchange}_{symbol}_{timeframe}_{limit}";
            if (IsCacheValid(cacheKey))
            {
                var cacheAge = DateTime.UtcNow - _cacheTimestamps[cacheKey];
                _logger?.LogCacheHit(normalizedExchange, symbol, timeframe, cacheAge);
                _performanceMetrics.RecordCacheHit(normalizedExchange, "GetCandlestickData");
                return _cache[cacheKey];
            }

            _logger?.LogCacheMiss(normalizedExchange, symbol, timeframe);
            _performanceMetrics.RecordCacheMiss(normalizedExchange, "GetCandlestickData");

            // Try to fetch data with retry logic
            Exception lastException = null;
            
            for (int attempt = 1; attempt <= _maxRetryAttempts; attempt++)
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    // Get or create exchange instance
                    var exchangeInstance = GetExchangeInstance(normalizedExchange);
                    
                    // Fetch OHLCV data from exchange
                    var ccxtData = await exchangeInstance.FetchOHLCV(symbol, timeframe, null, limit);
                    
                    stopwatch.Stop();
                    _logger?.LogApiRequest(normalizedExchange, "FetchOHLCV", stopwatch.Elapsed, true, symbol, timeframe);
                    _performanceMetrics.RecordApiRequest(normalizedExchange, "FetchOHLCV", stopwatch.Elapsed, true);
                    
                    // Convert CCXT data to application format
                    var result = _dataProvider.ConvertFromCCXT(ccxtData, timeframe);
                    
                    // Log successful data retrieval
                    _logger?.LogDataReceived(normalizedExchange, symbol, result.candles.Length, timeframe);
                    
                    // Cache the result
                    _cache[cacheKey] = result;
                    _cacheTimestamps[cacheKey] = DateTime.UtcNow;
                    
                    return result;
                }
                catch (DataConversionException)
                {
                    // Re-throw data conversion exceptions immediately - no retry needed
                    stopwatch.Stop();
                    _logger?.LogApiRequest(normalizedExchange, "FetchOHLCV", stopwatch.Elapsed, false, symbol, timeframe);
                    _performanceMetrics.RecordApiRequest(normalizedExchange, "FetchOHLCV", stopwatch.Elapsed, false);
                    throw;
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    lastException = ex;
                    var errorType = DetermineErrorType(ex);
                    
                    _logger?.LogApiRequest(normalizedExchange, "FetchOHLCV", stopwatch.Elapsed, false, symbol, timeframe);
                    _performanceMetrics.RecordApiRequest(normalizedExchange, "FetchOHLCV", stopwatch.Elapsed, false);
                    _logger?.LogError(normalizedExchange, ex, $"Attempt {attempt}/{_maxRetryAttempts} for {symbol} {timeframe}");
                    
                    // Don't retry for certain error types
                    if (!ShouldRetry(errorType, attempt))
                    {
                        break;
                    }
                    
                    // Wait before retry with exponential backoff
                    if (attempt < _maxRetryAttempts)
                    {
                        var delay = CalculateRetryDelay(attempt);
                        _logger?.LogRetryAttempt(normalizedExchange, "FetchOHLCV", attempt + 1, _maxRetryAttempts, delay, ex.Message);
                        await Task.Delay(delay);
                    }
                }
            }
            
            // All retry attempts failed, try to fallback to cached data (even if expired)
            if (HasCachedData(cacheKey))
            {
                var cacheAge = DateTime.UtcNow - _cacheTimestamps[cacheKey];
                _logger?.LogCacheFallback(normalizedExchange, symbol, timeframe, cacheAge, "API requests failed");
                // Return expired cached data as fallback
                return _cache[cacheKey];
            }
            
            // No cached data available, throw the last exception
            var finalErrorType = DetermineErrorType(lastException);
            throw new ExchangeException(normalizedExchange, 
                $"Failed to get candlestick data from {exchange} after {_maxRetryAttempts} attempts: {lastException?.Message}", 
                finalErrorType, lastException);
        }

        /// <summary>
        /// Gets list of available trading symbols from specified exchange with retry logic
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <returns>List of available trading pair symbols</returns>
        /// <exception cref="ArgumentException">Thrown when exchange name is invalid</exception>
        /// <exception cref="NotSupportedException">Thrown when exchange is not supported</exception>
        /// <exception cref="ExchangeException">Thrown when exchange API call fails</exception>
        public async Task<List<string>> GetAvailableSymbolsAsync(string exchange)
        {
            if (string.IsNullOrWhiteSpace(exchange))
                throw new ArgumentException("Exchange name cannot be null or empty", nameof(exchange));

            var normalizedExchange = exchange.ToLowerInvariant();
            
            if (!ExchangeFactory.IsExchangeSupported(normalizedExchange))
                throw new NotSupportedException($"Exchange '{exchange}' is not supported");

            Exception lastException = null;
            
            for (int attempt = 1; attempt <= _maxRetryAttempts; attempt++)
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    var exchangeInstance = GetExchangeInstance(normalizedExchange);
                    var markets = await exchangeInstance.LoadMarkets();
                    
                    stopwatch.Stop();
                    _logger?.LogApiRequest(normalizedExchange, "LoadMarkets", stopwatch.Elapsed, true);
                    _performanceMetrics.RecordApiRequest(normalizedExchange, "LoadMarkets", stopwatch.Elapsed, true);
                    _logger?.LogInfo($"Loaded {markets.Count} symbols", normalizedExchange);
                    
                    return markets.Keys.ToList();
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    lastException = ex;
                    var errorType = DetermineErrorType(ex);
                    
                    _logger?.LogApiRequest(normalizedExchange, "LoadMarkets", stopwatch.Elapsed, false);
                    _performanceMetrics.RecordApiRequest(normalizedExchange, "LoadMarkets", stopwatch.Elapsed, false);
                    _logger?.LogError(normalizedExchange, ex, $"Attempt {attempt}/{_maxRetryAttempts} for LoadMarkets");
                    
                    // Don't retry for certain error types
                    if (!ShouldRetry(errorType, attempt))
                    {
                        break;
                    }
                    
                    // Wait before retry with exponential backoff
                    if (attempt < _maxRetryAttempts)
                    {
                        var delay = CalculateRetryDelay(attempt);
                        _logger?.LogRetryAttempt(normalizedExchange, "LoadMarkets", attempt + 1, _maxRetryAttempts, delay, ex.Message);
                        await Task.Delay(delay);
                    }
                }
            }
            
            var finalErrorType = DetermineErrorType(lastException);
            throw new ExchangeException(normalizedExchange, 
                $"Failed to get available symbols from {exchange} after {_maxRetryAttempts} attempts: {lastException?.Message}", 
                finalErrorType, lastException);
        }

        /// <summary>
        /// Tests connection to specified exchange
        /// </summary>
        /// <param name="exchange">Exchange name to test</param>
        /// <returns>True if connection is successful, false otherwise</returns>
        public async Task<bool> TestConnectionAsync(string exchange)
        {
            if (string.IsNullOrWhiteSpace(exchange))
                return false;

            var normalizedExchange = exchange.ToLowerInvariant();
            
            if (!ExchangeFactory.IsExchangeSupported(normalizedExchange))
                return false;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var exchangeInstance = GetExchangeInstance(normalizedExchange);
                
                // Try to load markets as a connection test
                await exchangeInstance.LoadMarkets();
                
                stopwatch.Stop();
                _logger?.LogConnectionTest(normalizedExchange, true, stopwatch.Elapsed);
                _performanceMetrics.RecordApiRequest(normalizedExchange, "ConnectionTest", stopwatch.Elapsed, true);
                
                return true;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger?.LogConnectionTest(normalizedExchange, false, stopwatch.Elapsed);
                _logger?.LogError(normalizedExchange, ex, "Connection test failed");
                _performanceMetrics.RecordApiRequest(normalizedExchange, "ConnectionTest", stopwatch.Elapsed, false);
                
                return false;
            }
        }

        /// <summary>
        /// Gets list of supported exchanges
        /// </summary>
        /// <returns>List of supported exchange names</returns>
        public List<string> GetSupportedExchanges()
        {
            return ExchangeFactory.GetSupportedExchanges().ToList();
        }

        /// <summary>
        /// Gets list of supported timeframes
        /// </summary>
        /// <returns>List of supported timeframe strings</returns>
        public List<string> GetSupportedTimeframes()
        {
            return new List<string>(SupportedTimeframes);
        }

        /// <summary>
        /// Gets or creates an exchange instance for the specified exchange name
        /// </summary>
        /// <param name="exchangeName">Normalized exchange name (lowercase)</param>
        /// <returns>CCXT Exchange instance</returns>
        private Exchange GetExchangeInstance(string exchangeName)
        {
            if (!_exchanges.ContainsKey(exchangeName))
            {
                _exchanges[exchangeName] = ExchangeFactory.CreateExchange(exchangeName);
            }
            return _exchanges[exchangeName];
        }

        /// <summary>
        /// Determines the error type based on the exception
        /// </summary>
        /// <param name="exception">Exception to analyze</param>
        /// <returns>Appropriate ExchangeErrorType</returns>
        private ExchangeErrorType DetermineErrorType(Exception exception)
        {
            if (exception == null)
                return ExchangeErrorType.ApiError;

            var message = exception.Message?.ToLowerInvariant() ?? "";
            
            // Network-related errors
            if (exception is WebException || 
                exception is HttpRequestException ||
                message.Contains("timeout") ||
                message.Contains("connection") ||
                message.Contains("network") ||
                message.Contains("unreachable"))
            {
                return ExchangeErrorType.NetworkError;
            }
            
            // Rate limiting errors
            if (message.Contains("rate limit") ||
                message.Contains("too many requests") ||
                message.Contains("429"))
            {
                return ExchangeErrorType.RateLimitError;
            }
            
            // Authentication errors
            if (message.Contains("unauthorized") ||
                message.Contains("authentication") ||
                message.Contains("api key") ||
                message.Contains("401") ||
                message.Contains("403"))
            {
                return ExchangeErrorType.AuthenticationError;
            }
            
            // Symbol not found errors
            if (message.Contains("symbol") && 
                (message.Contains("not found") || message.Contains("invalid")))
            {
                return ExchangeErrorType.SymbolNotFound;
            }
            
            // Timeframe not supported errors
            if (message.Contains("timeframe") && 
                (message.Contains("not supported") || message.Contains("invalid")))
            {
                return ExchangeErrorType.TimeframeNotSupported;
            }
            
            // Default to API error
            return ExchangeErrorType.ApiError;
        }

        /// <summary>
        /// Determines if a retry should be attempted based on error type and attempt number
        /// </summary>
        /// <param name="errorType">Type of error that occurred</param>
        /// <param name="attemptNumber">Current attempt number</param>
        /// <returns>True if retry should be attempted, false otherwise</returns>
        private bool ShouldRetry(ExchangeErrorType errorType, int attemptNumber)
        {
            // Don't retry if we've reached max attempts
            if (attemptNumber >= _maxRetryAttempts)
                return false;
            
            // Retry for network errors and rate limit errors
            return errorType == ExchangeErrorType.NetworkError || 
                   errorType == ExchangeErrorType.RateLimitError ||
                   errorType == ExchangeErrorType.ApiError; // Generic API errors might be transient
        }

        /// <summary>
        /// Calculates retry delay with exponential backoff
        /// </summary>
        /// <param name="attemptNumber">Current attempt number (1-based)</param>
        /// <returns>TimeSpan representing the delay before next retry</returns>
        private TimeSpan CalculateRetryDelay(int attemptNumber)
        {
            // Exponential backoff: baseDelay * 2^(attempt-1)
            var multiplier = Math.Pow(2, attemptNumber - 1);
            var delayMs = (int)(_baseRetryDelay.TotalMilliseconds * multiplier);
            
            // Cap the maximum delay at 30 seconds
            delayMs = Math.Min(delayMs, 30000);
            
            return TimeSpan.FromMilliseconds(delayMs);
        }

        /// <summary>
        /// Checks if cached data exists for the given key (regardless of expiration)
        /// </summary>
        /// <param name="cacheKey">Cache key to check</param>
        /// <returns>True if cached data exists, false otherwise</returns>
        private bool HasCachedData(string cacheKey)
        {
            return _cache.ContainsKey(cacheKey);
        }

        /// <summary>
        /// Checks if cached data is still valid
        /// </summary>
        /// <param name="cacheKey">Cache key to check</param>
        /// <returns>True if cache is valid, false otherwise</returns>
        private bool IsCacheValid(string cacheKey)
        {
            if (!_cache.ContainsKey(cacheKey) || !_cacheTimestamps.ContainsKey(cacheKey))
                return false;

            var cacheTime = _cacheTimestamps[cacheKey];
            return DateTime.UtcNow - cacheTime < _cacheExpiration;
        }

        /// <summary>
        /// Clears expired cache entries
        /// </summary>
        public void ClearExpiredCache()
        {
            var expiredKeys = _cacheTimestamps
                .Where(kvp => DateTime.UtcNow - kvp.Value >= _cacheExpiration)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _cache.Remove(key);
                _cacheTimestamps.Remove(key);
            }
        }

        /// <summary>
        /// Clears all cached data
        /// </summary>
        public void ClearCache()
        {
            _cache.Clear();
            _cacheTimestamps.Clear();
            _logger?.LogInfo("Cache cleared");
        }

        /// <summary>
        /// Gets performance metrics for exchange operations
        /// </summary>
        /// <returns>Performance metrics instance</returns>
        public ExchangePerformanceMetrics GetPerformanceMetrics()
        {
            return _performanceMetrics;
        }

        /// <summary>
        /// Gets formatted performance summary
        /// </summary>
        /// <returns>Formatted string with performance metrics</returns>
        public string GetPerformanceSummary()
        {
            return _performanceMetrics.GetFormattedSummary();
        }

        /// <summary>
        /// Logs current performance metrics
        /// </summary>
        public void LogPerformanceMetrics()
        {
            var summary = _performanceMetrics.GetFormattedSummary();
            _logger?.LogInfo("Performance Metrics:\n" + summary);
        }

        /// <summary>
        /// Logs detailed performance metrics for monitoring
        /// </summary>
        public void LogDetailedPerformanceMetrics()
        {
            var performanceSummary = _performanceMetrics.GetPerformanceSummary();
            
            _logger?.LogInfo("=== DETAILED PERFORMANCE METRICS ===");
            
            // Log API Response Times
            if (performanceSummary.ContainsKey("ResponseTimes"))
            {
                var responseTimes = (Dictionary<string, object>)performanceSummary["ResponseTimes"];
                _logger?.LogInfo("API Response Times:");
                foreach (var kvp in responseTimes)
                {
                    dynamic metrics = kvp.Value;
                    _logger?.LogPerformanceMetrics(
                        kvp.Key.Split('_')[0], // Extract exchange name
                        kvp.Key.Split('_')[1], // Extract operation name
                        (TimeSpan)metrics.Average
                    );
                }
            }
            
            // Log Cache Performance
            if (performanceSummary.ContainsKey("Cache"))
            {
                var cache = (Dictionary<string, object>)performanceSummary["Cache"];
                _logger?.LogInfo("Cache Performance:");
                foreach (var kvp in cache)
                {
                    dynamic metrics = kvp.Value;
                    _logger?.LogInfo($"  {kvp.Key}: Hits={metrics.Hits}, Misses={metrics.Misses}, HitRatio={metrics.HitRatio:P1}");
                }
            }
            
            // Log Error Rates
            if (performanceSummary.ContainsKey("Errors"))
            {
                var errors = (Dictionary<string, object>)performanceSummary["Errors"];
                _logger?.LogInfo("Error Rates:");
                foreach (var kvp in errors)
                {
                    dynamic metrics = kvp.Value;
                    _logger?.LogInfo($"  {kvp.Key}: Requests={metrics.TotalRequests}, Errors={metrics.Errors}, ErrorRate={metrics.ErrorRate:P1}");
                }
            }
            
            _logger?.LogInfo("=== END PERFORMANCE METRICS ===");
        }
    }
}