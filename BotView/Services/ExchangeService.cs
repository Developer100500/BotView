using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using BotView.Configuration;
using BotView.Exceptions;
using BotView.Interfaces;
using BotView.Models;

namespace BotView.Services;

/// <summary>Provider registry, retry policy and initial-history cache shared by all transports.</summary>
public sealed class ExchangeService : IExchangeService
{
    private readonly Dictionary<string, IMarketDataProvider> _providers;
    private readonly IExchangeLogger? _logger;
    private readonly ExchangePerformanceMetrics _performanceMetrics = new();
    private readonly Dictionary<InitialCacheKey, (Candle[] Candles, DateTime At)> _cache = new();
    private readonly object _cacheSync = new();
    private readonly TimeSpan _cacheExpiration;
    private readonly int _maxRetryAttempts;
    private readonly TimeSpan _baseRetryDelay;
    private int _disposed;
    private readonly record struct InitialCacheKey(string Exchange, string Symbol, string Timeframe, int Limit);

    public ExchangeService(IEnumerable<IMarketDataProvider> providers, IExchangeLogger? logger = null,
        int cacheExpirationMinutes = 5, int maxRetryAttempts = 3, int baseRetryDelaySeconds = 1)
    {
        _logger = logger;
        _cacheExpiration = TimeSpan.FromMinutes(cacheExpirationMinutes);
        _maxRetryAttempts = Math.Max(1, maxRetryAttempts);
        _baseRetryDelay = TimeSpan.FromSeconds(Math.Max(1, baseRetryDelaySeconds));
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(p => p.Definition.Id, StringComparer.OrdinalIgnoreCase);
        if (_providers.Count == 0) throw new ArgumentException("Register at least one provider.", nameof(providers));
    }

    public IReadOnlyList<ExchangeDefinition> GetExchanges() =>
        _providers.Values.Select(p => p.Definition).ToArray();
    public List<string> GetSupportedExchanges() => _providers.Keys.ToList();
    public List<string> GetSupportedTimeframes() => _providers.Values.SelectMany(p => p.Definition.SupportedTimeframes)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private IMarketDataProvider GetProvider(string exchange)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (string.IsNullOrWhiteSpace(exchange)) throw new ArgumentException("Exchange cannot be empty.", nameof(exchange));
        return _providers.TryGetValue(exchange, out var provider) ? provider :
            throw new NotSupportedException($"Exchange '{exchange}' is not supported.");
    }

    private IMarketDataProvider ValidateRequest(string exchange, string symbol, string timeframe, int limit)
    {
        var provider = GetProvider(exchange);
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("Symbol cannot be empty.", nameof(symbol));
        if (string.IsNullOrWhiteSpace(timeframe)) throw new ArgumentException("Timeframe cannot be empty.", nameof(timeframe));
        if (limit <= 0) throw new ArgumentException("Limit must be greater than zero.", nameof(limit));
        if (!provider.Definition.SupportsTimeframe(timeframe))
            throw new NotSupportedException($"Timeframe '{timeframe}' is not supported by '{exchange}'.");
        return provider;
    }

    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(string exchange, CancellationToken ct = default)
    {
        var provider = GetProvider(exchange);
        return ExecuteAsync(provider.Definition.Id, "GetInstruments", () => provider.GetInstrumentsAsync(ct), ct);
    }

    public Task<List<string>> GetAvailableSymbolsAsync(string exchange) => GetAvailableSymbolsAsync(exchange, CancellationToken.None);
    public async Task<List<string>> GetAvailableSymbolsAsync(string exchange, CancellationToken ct) =>
        (await GetInstrumentsAsync(exchange, ct).ConfigureAwait(false)).Select(i => i.Symbol)
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

    private static async Task<InstrumentKey> ResolveAsync(IMarketDataProvider provider, string symbol, CancellationToken ct)
    {
        var instruments = await provider.GetInstrumentsAsync(ct).ConfigureAwait(false);
        var matches = instruments.Where(i => string.Equals(i.Symbol, symbol, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0].Key,
            0 => throw new ExchangeException(provider.Definition.Id,
                $"Instrument '{symbol}' is unavailable.", ExchangeErrorType.SymbolNotFound),
            _ => throw new InvalidOperationException($"Provider '{provider.Definition.Id}' returned ambiguous symbol '{symbol}'.")
        };
    }

    public Task<Candle[]> GetCandlestickDataAsync(string exchange, string symbol, string timeframe, int limit = 500) =>
        GetCandlestickDataAsync(exchange, symbol, timeframe, limit, CancellationToken.None);

    public async Task<Candle[]> GetCandlestickDataAsync(string exchange, string symbol, string timeframe, int limit, CancellationToken ct)
    {
        var provider = ValidateRequest(exchange, symbol, timeframe, limit);
        ct.ThrowIfCancellationRequested();
        var key = new InitialCacheKey(provider.Definition.Id, symbol, timeframe, limit);
        (Candle[] Candles, DateTime At) cached = default;
        lock (_cacheSync) _cache.TryGetValue(key, out cached);
        if (cached.Candles is not null && DateTime.UtcNow - cached.At < _cacheExpiration)
        {
            _logger?.LogCacheHit(key.Exchange, symbol, timeframe, DateTime.UtcNow - cached.At);
            _performanceMetrics.RecordCacheHit(key.Exchange, "GetCandlestickData");
            return cached.Candles;
        }
        _logger?.LogCacheMiss(key.Exchange, symbol, timeframe);
        _performanceMetrics.RecordCacheMiss(key.Exchange, "GetCandlestickData");
        try
        {
            var candles = await GetCandlesAsync(exchange, symbol, timeframe, null, limit, ct).ConfigureAwait(false);
            lock (_cacheSync) _cache[key] = (candles, DateTime.UtcNow);
            return candles;
        }
        catch (ExchangeException ex) when (cached.Candles is not null && !ct.IsCancellationRequested)
        {
            _logger?.LogCacheFallback(key.Exchange, symbol, timeframe, DateTime.UtcNow - cached.At, ex.Message);
            return cached.Candles;
        }
    }

    public Task<Candle[]> GetCandlesAsync(string exchange, string symbol, string timeframe,
        DateTimeOffset? beforeExclusive, int limit, CancellationToken ct = default)
    {
        var provider = ValidateRequest(exchange, symbol, timeframe, limit);
        return ExecuteAsync(provider.Definition.Id, "GetCandles", async () =>
        {
            var instrument = await ResolveAsync(provider, symbol, ct).ConfigureAwait(false);
            var candles = await provider.GetCandlesAsync(instrument, timeframe, beforeExclusive, limit, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            foreach (var candle in candles) ValidateCandle(candle);
            // Enforce the public boundary even if a native endpoint returns an inclusive end.
            return candles.Where(c => !beforeExclusive.HasValue || c.Timestamp < beforeExclusive.Value.ToUnixTimeMilliseconds())
                .DistinctBy(c => c.Timestamp).OrderBy(c => c.Timestamp).TakeLast(limit).ToArray();
        }, ct);
    }

    public async IAsyncEnumerable<CandleUpdate> WatchCandlesAsync(string exchange, string symbol, string timeframe,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var provider = ValidateRequest(exchange, symbol, timeframe, 1);
        var instrument = await ResolveAsync(provider, symbol, ct).ConfigureAwait(false);
        await foreach (var update in provider.WatchCandlesAsync(instrument, timeframe, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            ValidateCandle(update.Candle);
            yield return update;
        }
    }

    private static void ValidateCandle(Candle candle)
    {
        _ = candle.OpenTimeUtc;
        if (!double.IsFinite(candle.Open) || !double.IsFinite(candle.High) || !double.IsFinite(candle.Low) ||
            !double.IsFinite(candle.Close) || !double.IsFinite(candle.Volume) || candle.High < candle.Low || candle.Volume < 0)
            throw new DataConversionException("Provider returned an invalid candle.", DataConversionErrorType.InvalidFormat);
    }

    public Task<IReadOnlyList<MarketMover>> GetMarketMoversAsync(string exchange, CancellationToken ct = default)
    {
        var provider = GetProvider(exchange);
        if (provider is not IMarketMoversProvider movers)
            return Task.FromResult<IReadOnlyList<MarketMover>>(Array.Empty<MarketMover>());
        return ExecuteAsync(provider.Definition.Id, "GetMarketMovers", () => movers.GetMarketMoversAsync(ct), ct);
    }

    public async Task<bool> TestConnectionAsync(string exchange)
    {
        if (string.IsNullOrWhiteSpace(exchange) || !_providers.ContainsKey(exchange)) return false;
        var timer = Stopwatch.StartNew();
        try
        {
            await GetInstrumentsAsync(exchange).ConfigureAwait(false);
            _logger?.LogConnectionTest(exchange, true, timer.Elapsed);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogConnectionTest(exchange, false, timer.Elapsed);
            _logger?.LogError(exchange, ex, "Connection test failed");
            return false;
        }
    }

    private async Task<T> ExecuteAsync<T>(string exchange, string operation, Func<Task<T>> request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            try
            {
                var result = await request().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                _logger?.LogApiRequest(exchange, operation, timer.Elapsed, true);
                _performanceMetrics.RecordApiRequest(exchange, operation, timer.Elapsed, true);
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not ArgumentException and not NotSupportedException and not DataConversionException)
            {
                ct.ThrowIfCancellationRequested();
                _logger?.LogApiRequest(exchange, operation, timer.Elapsed, false);
                _performanceMetrics.RecordApiRequest(exchange, operation, timer.Elapsed, false);
                _logger?.LogError(exchange, ex, $"Attempt {attempt}/{_maxRetryAttempts}: {operation}");
                var errorType = Classify(ex);
                if (attempt >= _maxRetryAttempts || errorType is ExchangeErrorType.AuthenticationError or ExchangeErrorType.SymbolNotFound)
                    throw new ExchangeException(exchange, $"{operation} failed: {ex.Message}", errorType, ex);
                var delay = TimeSpan.FromMilliseconds(Math.Min(30000, _baseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)));
                _logger?.LogRetryAttempt(exchange, operation, attempt + 1, _maxRetryAttempts, delay, ex.Message);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private static ExchangeErrorType Classify(Exception ex)
    {
        if (ex is ExchangeException known) return known.ErrorType;
        var message = ex.Message.ToLowerInvariant();
        if (message.Contains("429") || message.Contains("rate limit")) return ExchangeErrorType.RateLimitError;
        if (message.Contains("401") || message.Contains("403") || message.Contains("unauthorized")) return ExchangeErrorType.AuthenticationError;
        if (ex is HttpRequestException || message.Contains("network") || message.Contains("connection") || message.Contains("timeout"))
            return ExchangeErrorType.NetworkError;
        return ExchangeErrorType.ApiError;
    }

    public void EvictCandlestickData(string exchange, string symbol, string timeframe)
    {
        lock (_cacheSync)
            foreach (var key in _cache.Keys.Where(k => string.Equals(k.Exchange, exchange, StringComparison.OrdinalIgnoreCase) &&
                         k.Symbol == symbol && k.Timeframe == timeframe).ToArray())
                _cache.Remove(key);
    }
    public void ClearExpiredCache()
    {
        lock (_cacheSync)
            foreach (var key in _cache.Where(p => DateTime.UtcNow - p.Value.At >= _cacheExpiration).Select(p => p.Key).ToArray())
                _cache.Remove(key);
    }
    public void ClearCache()
    {
        lock (_cacheSync) _cache.Clear();
        _logger?.LogInfo("Cache cleared");
    }
    public ExchangePerformanceMetrics GetPerformanceMetrics() => _performanceMetrics;
    public string GetPerformanceSummary() => _performanceMetrics.GetFormattedSummary();
    public void LogPerformanceMetrics() => _logger?.LogInfo(_performanceMetrics.GetFormattedSummary());
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        ClearCache();
        await Task.WhenAll(_providers.Values.Select(p => p.DisposeAsync().AsTask())).ConfigureAwait(false);
    }
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
