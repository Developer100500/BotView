using System;
using System.Collections.Generic;
using System.Linq;

namespace BotView.Services
{
    /// <summary>
    /// Tracks performance metrics for exchange operations
    /// </summary>
    public class ExchangePerformanceMetrics
    {
        private readonly Dictionary<string, List<TimeSpan>> _apiResponseTimes;
        private readonly Dictionary<string, int> _cacheHitCounts;
        private readonly Dictionary<string, int> _cacheMissCounts;
        private readonly Dictionary<string, int> _apiRequestCounts;
        private readonly Dictionary<string, int> _apiErrorCounts;
        private readonly object _lock = new object();

        /// <summary>
        /// Initializes a new instance of ExchangePerformanceMetrics
        /// </summary>
        public ExchangePerformanceMetrics()
        {
            _apiResponseTimes = new Dictionary<string, List<TimeSpan>>();
            _cacheHitCounts = new Dictionary<string, int>();
            _cacheMissCounts = new Dictionary<string, int>();
            _apiRequestCounts = new Dictionary<string, int>();
            _apiErrorCounts = new Dictionary<string, int>();
        }

        /// <summary>
        /// Records an API request duration
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="operation">Operation name</param>
        /// <param name="duration">Duration of the request</param>
        /// <param name="success">Whether the request was successful</param>
        public void RecordApiRequest(string exchange, string operation, TimeSpan duration, bool success)
        {
            if (string.IsNullOrEmpty(exchange) || string.IsNullOrEmpty(operation))
                return;

            var key = $"{exchange.ToLowerInvariant()}_{operation}";

            lock (_lock)
            {
                // Record response time
                if (!_apiResponseTimes.ContainsKey(key))
                    _apiResponseTimes[key] = new List<TimeSpan>();
                
                _apiResponseTimes[key].Add(duration);

                // Record request count
                if (!_apiRequestCounts.ContainsKey(key))
                    _apiRequestCounts[key] = 0;
                
                _apiRequestCounts[key]++;

                // Record error count if failed
                if (!success)
                {
                    if (!_apiErrorCounts.ContainsKey(key))
                        _apiErrorCounts[key] = 0;
                    
                    _apiErrorCounts[key]++;
                }
            }
        }

        /// <summary>
        /// Records a cache hit
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="operation">Operation name (e.g., "GetCandlestickData")</param>
        public void RecordCacheHit(string exchange, string operation)
        {
            if (string.IsNullOrEmpty(exchange) || string.IsNullOrEmpty(operation))
                return;

            var key = $"{exchange.ToLowerInvariant()}_{operation}";

            lock (_lock)
            {
                if (!_cacheHitCounts.ContainsKey(key))
                    _cacheHitCounts[key] = 0;
                
                _cacheHitCounts[key]++;
            }
        }

        /// <summary>
        /// Records a cache miss
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="operation">Operation name (e.g., "GetCandlestickData")</param>
        public void RecordCacheMiss(string exchange, string operation)
        {
            if (string.IsNullOrEmpty(exchange) || string.IsNullOrEmpty(operation))
                return;

            var key = $"{exchange.ToLowerInvariant()}_{operation}";

            lock (_lock)
            {
                if (!_cacheMissCounts.ContainsKey(key))
                    _cacheMissCounts[key] = 0;
                
                _cacheMissCounts[key]++;
            }
        }

        /// <summary>
        /// Gets average response time for a specific exchange and operation
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="operation">Operation name</param>
        /// <returns>Average response time, or null if no data available</returns>
        public TimeSpan? GetAverageResponseTime(string exchange, string operation)
        {
            if (string.IsNullOrEmpty(exchange) || string.IsNullOrEmpty(operation))
                return null;

            var key = $"{exchange.ToLowerInvariant()}_{operation}";

            lock (_lock)
            {
                if (!_apiResponseTimes.ContainsKey(key) || _apiResponseTimes[key].Count == 0)
                    return null;

                var times = _apiResponseTimes[key];
                var averageTicks = times.Select(t => t.Ticks).Average();
                return new TimeSpan((long)averageTicks);
            }
        }

        /// <summary>
        /// Gets cache hit ratio for a specific exchange and operation
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="operation">Operation name</param>
        /// <returns>Cache hit ratio (0.0 to 1.0), or null if no data available</returns>
        public double? GetCacheHitRatio(string exchange, string operation)
        {
            if (string.IsNullOrEmpty(exchange) || string.IsNullOrEmpty(operation))
                return null;

            var key = $"{exchange.ToLowerInvariant()}_{operation}";

            lock (_lock)
            {
                var hits = _cacheHitCounts.ContainsKey(key) ? _cacheHitCounts[key] : 0;
                var misses = _cacheMissCounts.ContainsKey(key) ? _cacheMissCounts[key] : 0;
                var total = hits + misses;

                if (total == 0)
                    return null;

                return (double)hits / total;
            }
        }

        /// <summary>
        /// Gets error rate for a specific exchange and operation
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="operation">Operation name</param>
        /// <returns>Error rate (0.0 to 1.0), or null if no data available</returns>
        public double? GetErrorRate(string exchange, string operation)
        {
            if (string.IsNullOrEmpty(exchange) || string.IsNullOrEmpty(operation))
                return null;

            var key = $"{exchange.ToLowerInvariant()}_{operation}";

            lock (_lock)
            {
                var errors = _apiErrorCounts.ContainsKey(key) ? _apiErrorCounts[key] : 0;
                var total = _apiRequestCounts.ContainsKey(key) ? _apiRequestCounts[key] : 0;

                if (total == 0)
                    return null;

                return (double)errors / total;
            }
        }

        /// <summary>
        /// Gets total number of API requests for a specific exchange and operation
        /// </summary>
        /// <param name="exchange">Exchange name</param>
        /// <param name="operation">Operation name</param>
        /// <returns>Total number of requests</returns>
        public int GetTotalRequests(string exchange, string operation)
        {
            if (string.IsNullOrEmpty(exchange) || string.IsNullOrEmpty(operation))
                return 0;

            var key = $"{exchange.ToLowerInvariant()}_{operation}";

            lock (_lock)
            {
                return _apiRequestCounts.ContainsKey(key) ? _apiRequestCounts[key] : 0;
            }
        }

        /// <summary>
        /// Gets performance summary for all exchanges and operations
        /// </summary>
        /// <returns>Dictionary containing performance metrics</returns>
        public Dictionary<string, object> GetPerformanceSummary()
        {
            lock (_lock)
            {
                var summary = new Dictionary<string, object>();

                // API Response Times Summary
                var responseTimeSummary = new Dictionary<string, object>();
                foreach (var kvp in _apiResponseTimes)
                {
                    if (kvp.Value.Count > 0)
                    {
                        var times = kvp.Value;
                        responseTimeSummary[kvp.Key] = new
                        {
                            Average = new TimeSpan((long)times.Select(t => t.Ticks).Average()),
                            Min = new TimeSpan(times.Min(t => t.Ticks)),
                            Max = new TimeSpan(times.Max(t => t.Ticks)),
                            Count = times.Count
                        };
                    }
                }
                summary["ResponseTimes"] = responseTimeSummary;

                // Cache Performance Summary
                var cacheSummary = new Dictionary<string, object>();
                var allCacheKeys = _cacheHitCounts.Keys.Union(_cacheMissCounts.Keys).Distinct();
                foreach (var key in allCacheKeys)
                {
                    var hits = _cacheHitCounts.ContainsKey(key) ? _cacheHitCounts[key] : 0;
                    var misses = _cacheMissCounts.ContainsKey(key) ? _cacheMissCounts[key] : 0;
                    var total = hits + misses;
                    var hitRatio = total > 0 ? (double)hits / total : 0.0;

                    cacheSummary[key] = new
                    {
                        Hits = hits,
                        Misses = misses,
                        Total = total,
                        HitRatio = hitRatio
                    };
                }
                summary["Cache"] = cacheSummary;

                // Error Rate Summary
                var errorSummary = new Dictionary<string, object>();
                foreach (var kvp in _apiRequestCounts)
                {
                    var errors = _apiErrorCounts.ContainsKey(kvp.Key) ? _apiErrorCounts[kvp.Key] : 0;
                    var errorRate = kvp.Value > 0 ? (double)errors / kvp.Value : 0.0;

                    errorSummary[kvp.Key] = new
                    {
                        TotalRequests = kvp.Value,
                        Errors = errors,
                        ErrorRate = errorRate
                    };
                }
                summary["Errors"] = errorSummary;

                return summary;
            }
        }

        /// <summary>
        /// Clears all performance metrics
        /// </summary>
        public void ClearMetrics()
        {
            lock (_lock)
            {
                _apiResponseTimes.Clear();
                _cacheHitCounts.Clear();
                _cacheMissCounts.Clear();
                _apiRequestCounts.Clear();
                _apiErrorCounts.Clear();
            }
        }

        /// <summary>
        /// Gets a formatted string representation of performance metrics
        /// </summary>
        /// <returns>Formatted performance metrics string</returns>
        public string GetFormattedSummary()
        {
            var summary = GetPerformanceSummary();
            var result = new List<string>();

            result.Add("=== Exchange Performance Metrics ===");
            result.Add($"Generated at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            // Response Times
            if (summary.ContainsKey("ResponseTimes"))
            {
                result.Add("\n--- API Response Times ---");
                var responseTimes = (Dictionary<string, object>)summary["ResponseTimes"];
                if (responseTimes.Count == 0)
                {
                    result.Add("No API requests recorded yet.");
                }
                else
                {
                    foreach (var kvp in responseTimes.OrderBy(x => x.Key))
                    {
                        dynamic metrics = kvp.Value;
                        result.Add($"{kvp.Key}:");
                        result.Add($"  Average: {((TimeSpan)metrics.Average).TotalMilliseconds:F0}ms");
                        result.Add($"  Min: {((TimeSpan)metrics.Min).TotalMilliseconds:F0}ms");
                        result.Add($"  Max: {((TimeSpan)metrics.Max).TotalMilliseconds:F0}ms");
                        result.Add($"  Total Requests: {metrics.Count}");
                        result.Add("");
                    }
                }
            }

            // Cache Performance
            if (summary.ContainsKey("Cache"))
            {
                result.Add("--- Cache Performance ---");
                var cache = (Dictionary<string, object>)summary["Cache"];
                if (cache.Count == 0)
                {
                    result.Add("No cache operations recorded yet.");
                }
                else
                {
                    foreach (var kvp in cache.OrderBy(x => x.Key))
                    {
                        dynamic metrics = kvp.Value;
                        result.Add($"{kvp.Key}:");
                        result.Add($"  Cache Hits: {metrics.Hits}");
                        result.Add($"  Cache Misses: {metrics.Misses}");
                        result.Add($"  Total Operations: {metrics.Total}");
                        result.Add($"  Hit Ratio: {metrics.HitRatio:P1}");
                        result.Add("");
                    }
                }
            }

            // Error Rates
            if (summary.ContainsKey("Errors"))
            {
                result.Add("--- Error Rates ---");
                var errors = (Dictionary<string, object>)summary["Errors"];
                if (errors.Count == 0)
                {
                    result.Add("No API requests recorded yet.");
                }
                else
                {
                    foreach (var kvp in errors.OrderBy(x => x.Key))
                    {
                        dynamic metrics = kvp.Value;
                        result.Add($"{kvp.Key}:");
                        result.Add($"  Total Requests: {metrics.TotalRequests}");
                        result.Add($"  Failed Requests: {metrics.Errors}");
                        result.Add($"  Success Rate: {(1 - metrics.ErrorRate):P1}");
                        result.Add($"  Error Rate: {metrics.ErrorRate:P1}");
                        result.Add("");
                    }
                }
            }

            // Overall Statistics
            result.Add("--- Overall Statistics ---");
            var totalRequests = _apiRequestCounts.Values.Sum();
            var totalErrors = _apiErrorCounts.Values.Sum();
            var totalCacheHits = _cacheHitCounts.Values.Sum();
            var totalCacheMisses = _cacheMissCounts.Values.Sum();
            var totalCacheOperations = totalCacheHits + totalCacheMisses;
            
            result.Add($"Total API Requests: {totalRequests}");
            result.Add($"Total API Errors: {totalErrors}");
            result.Add($"Overall Success Rate: {(totalRequests > 0 ? (1.0 - (double)totalErrors / totalRequests) : 0):P1}");
            result.Add($"Total Cache Operations: {totalCacheOperations}");
            result.Add($"Overall Cache Hit Ratio: {(totalCacheOperations > 0 ? (double)totalCacheHits / totalCacheOperations : 0):P1}");

            if (_apiResponseTimes.Values.Any(list => list.Count > 0))
            {
                var allResponseTimes = _apiResponseTimes.Values.SelectMany(list => list).ToList();
                var overallAverage = new TimeSpan((long)allResponseTimes.Select(t => t.Ticks).Average());
                result.Add($"Overall Average Response Time: {overallAverage.TotalMilliseconds:F0}ms");
            }

            return string.Join("\n", result);
        }

        /// <summary>
        /// Gets performance metrics for a specific time period
        /// </summary>
        /// <param name="since">Start time for metrics calculation</param>
        /// <returns>Performance metrics since the specified time</returns>
        public Dictionary<string, object> GetMetricsSince(DateTime since)
        {
            // Note: This is a simplified implementation
            // In a production system, you would store timestamps with each metric
            // For now, we return all metrics as we don't track timestamps per metric
            return GetPerformanceSummary();
        }

        /// <summary>
        /// Exports performance metrics to a formatted string suitable for logging or file output
        /// </summary>
        /// <returns>CSV-formatted performance metrics</returns>
        public string ExportMetricsToCSV()
        {
            var csv = new List<string>();
            csv.Add("Timestamp,Exchange,Operation,MetricType,Value,Unit");
            
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            
            // Export response times
            foreach (var kvp in _apiResponseTimes)
            {
                if (kvp.Value.Count > 0)
                {
                    var parts = kvp.Key.Split('_');
                    var exchange = parts[0];
                    var operation = parts.Length > 1 ? parts[1] : "unknown";
                    var avgMs = kvp.Value.Select(t => t.TotalMilliseconds).Average();
                    var minMs = kvp.Value.Min(t => t.TotalMilliseconds);
                    var maxMs = kvp.Value.Max(t => t.TotalMilliseconds);
                    
                    csv.Add($"{timestamp},{exchange},{operation},AvgResponseTime,{avgMs:F2},ms");
                    csv.Add($"{timestamp},{exchange},{operation},MinResponseTime,{minMs:F2},ms");
                    csv.Add($"{timestamp},{exchange},{operation},MaxResponseTime,{maxMs:F2},ms");
                    csv.Add($"{timestamp},{exchange},{operation},RequestCount,{kvp.Value.Count},count");
                }
            }
            
            // Export cache metrics
            var allCacheKeys = _cacheHitCounts.Keys.Union(_cacheMissCounts.Keys).Distinct();
            foreach (var key in allCacheKeys)
            {
                var parts = key.Split('_');
                var exchange = parts[0];
                var operation = parts.Length > 1 ? parts[1] : "unknown";
                var hits = _cacheHitCounts.ContainsKey(key) ? _cacheHitCounts[key] : 0;
                var misses = _cacheMissCounts.ContainsKey(key) ? _cacheMissCounts[key] : 0;
                var total = hits + misses;
                var hitRatio = total > 0 ? (double)hits / total : 0.0;
                
                csv.Add($"{timestamp},{exchange},{operation},CacheHits,{hits},count");
                csv.Add($"{timestamp},{exchange},{operation},CacheMisses,{misses},count");
                csv.Add($"{timestamp},{exchange},{operation},CacheHitRatio,{hitRatio:F3},ratio");
            }
            
            // Export error rates
            foreach (var kvp in _apiRequestCounts)
            {
                var parts = kvp.Key.Split('_');
                var exchange = parts[0];
                var operation = parts.Length > 1 ? parts[1] : "unknown";
                var errors = _apiErrorCounts.ContainsKey(kvp.Key) ? _apiErrorCounts[kvp.Key] : 0;
                var errorRate = kvp.Value > 0 ? (double)errors / kvp.Value : 0.0;
                
                csv.Add($"{timestamp},{exchange},{operation},TotalRequests,{kvp.Value},count");
                csv.Add($"{timestamp},{exchange},{operation},ErrorCount,{errors},count");
                csv.Add($"{timestamp},{exchange},{operation},ErrorRate,{errorRate:F3},ratio");
            }
            
            return string.Join("\n", csv);
        }
    }
}