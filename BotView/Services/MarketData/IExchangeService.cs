using BotView.Configuration;
using BotView.Models;

namespace BotView.Services.MarketData;

/// <summary>Routes requests to registered connections without exposing provider library types.</summary>
public interface IExchangeService : IAsyncDisposable
{
    IReadOnlyList<ExchangeDefinition> GetExchanges();
    Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(string exchange, CancellationToken ct = default);
    Task<List<string>> GetAvailableSymbolsAsync(string exchange);
    Task<List<string>> GetAvailableSymbolsAsync(string exchange, CancellationToken ct);
    Task<Candle[]> GetCandlestickDataAsync(string exchange, string symbol, string timeframe, int limit = 500);
    Task<Candle[]> GetCandlestickDataAsync(string exchange, string symbol, string timeframe, int limit, CancellationToken ct);

    /// <summary>Nearest accessible bars strictly before the bound, or latest bars if it is null.
    /// Results are ordered oldest first; providers handle native paging and trading breaks.</summary>
    Task<Candle[]> GetCandlesAsync(string exchange, string symbol, string timeframe,
        DateTimeOffset? beforeExclusive, int limit, CancellationToken ct = default);
    IAsyncEnumerable<CandleUpdate> WatchCandlesAsync(string exchange, string symbol, string timeframe,
        CancellationToken ct = default);

    Task<IReadOnlyList<MarketMover>> GetMarketMoversAsync(string exchange, CancellationToken ct = default);
    Task<bool> TestConnectionAsync(string exchange);
    List<string> GetSupportedExchanges();
    List<string> GetSupportedTimeframes();
    ExchangePerformanceMetrics GetPerformanceMetrics();
    void LogDetailedPerformanceMetrics();
    void ClearExpiredCache();
    void EvictCandlestickData(string exchange, string symbol, string timeframe);
}
