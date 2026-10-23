using BotView.Configuration;
using BotView.Models;

namespace BotView.Interfaces;

/// <summary>One connection to a source of market data. Owns its transport and native identifiers.</summary>
public interface IMarketDataProvider : IAsyncDisposable
{
    ExchangeDefinition Definition { get; }
    Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct = default);

    /// <summary>Returns up to limit nearest bars strictly before beforeExclusive (latest if null),
    /// ordered oldest first. Empty means no earlier accessible history. Handles API paging internally.</summary>
    Task<Candle[]> GetCandlesAsync(InstrumentKey instrument, string timeframe,
        DateTimeOffset? beforeExclusive, int limit, CancellationToken ct = default);

    /// <summary>Publishes full snapshots, including final values of closing bars.
    /// Starts with a current snapshot to bridge the history/subscription race. May poll or use a push
    /// transport. Disposing the enumerator releases this subscription.</summary>
    IAsyncEnumerable<CandleUpdate> WatchCandlesAsync(InstrumentKey instrument, string timeframe,
        CancellationToken ct = default);
}

/// <summary>An optional capability; sources of candles need not implement crypto market movers.</summary>
public interface IMarketMoversProvider
{
    Task<IReadOnlyList<MarketMover>> GetMarketMoversAsync(CancellationToken ct = default);
}
