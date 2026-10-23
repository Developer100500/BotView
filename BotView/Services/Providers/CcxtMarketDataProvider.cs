using System.Runtime.CompilerServices;
using BotView.Configuration;
using BotView.Interfaces;
using BotView.Models;

namespace BotView.Services.Providers;

/// <summary>All CCXT candle transport, native paging and symbol mapping stays in this adapter.</summary>
public sealed class CcxtMarketDataProvider : IMarketDataProvider, IMarketMoversProvider
{
    private readonly Lazy<ccxt.Exchange> _client;
    private readonly TimeSpan _pollInterval;
    private readonly SemaphoreSlim _instrumentGate = new(1, 1);
    private Instrument[]? _instruments;

    public ExchangeDefinition Definition { get; }

    public static IEnumerable<IMarketDataProvider> CreateDefaults(TimeSpan? pollInterval = null) =>
        MarketCatalog.Exchanges.Where(d => d.ProviderId == "ccxt")
            .Select(d => (IMarketDataProvider)new CcxtMarketDataProvider(d, pollInterval));

    public CcxtMarketDataProvider(ExchangeDefinition definition, TimeSpan? pollInterval = null)
    {
        Definition = definition;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        if (_pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _client = new Lazy<ccxt.Exchange>(() => CreateClient(definition.Id));
    }

    internal static ccxt.Exchange CreateClient(string id) => id.ToLowerInvariant() switch
    {
        "binance" => new ccxt.binance(),
        "bybit" => new ccxt.bybit(),
        "okx" => new ccxt.okx(),
        "kraken" => new ccxt.kraken(),
        _ => throw new NotSupportedException($"No CCXT adapter is registered for '{id}'.")
    };

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct = default)
    {
        await _instrumentGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_instruments is not null) return _instruments;
            var markets = await _client.Value.LoadMarkets().WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _instruments = markets.Values.Where(m => m.active != false && m.spot == true &&
                    !string.IsNullOrWhiteSpace(m.symbol))
                .Select(m => new Instrument(new InstrumentKey(Definition.Venue, "spot", "", m.symbol!),
                    m.symbol!, m.symbol!, m.quote))
                .DistinctBy(m => m.Symbol, StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m.Symbol, StringComparer.OrdinalIgnoreCase).ToArray();
            return _instruments;
        }
        finally { _instrumentGate.Release(); }
    }

    public async Task<Candle[]> GetCandlesAsync(InstrumentKey instrument, string timeframe,
        DateTimeOffset? beforeExclusive, int limit, CancellationToken ct = default)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (!Definition.SupportsTimeframe(timeframe)) throw new NotSupportedException($"Unsupported timeframe '{timeframe}'.");
        if (instrument.Venue != Definition.Venue || instrument.Market != "spot" || instrument.Board != "")
            throw new NotSupportedException("This CCXT connection provides spot candles only.");
        var result = new SortedDictionary<long, Candle>();
        var before = beforeExclusive?.ToUnixTimeMilliseconds();
        var pageLimit = Definition.Id switch { "okx" => 100, "kraken" => 720, _ => 1000 };
        while (result.Count < limit)
        {
            ct.ThrowIfCancellationRequested();
            var requested = Definition.Id == "kraken" ? pageLimit : Math.Min(pageLimit, limit - result.Count);
            var parameters = new Dictionary<string, object>();
            // CCXT 4.5.84 maps until to Binance endTime, Bybit end and OKX after.
            if (before.HasValue && Definition.Id != "kraken")
            {
                parameters["until"] = Definition.Id == "okx" ? before.Value : before.Value - 1;
                if (Definition.Id == "okx") parameters["type"] = "HistoryCandles";
            }
            var rows = await _client.Value.FetchOHLCV(instrument.SecurityId, timeframe, null, requested, parameters)
                .WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (rows.Count == 0) break;
            var candles = CcxtCandleConverter.ConvertFromCCXT(rows, timeframe);
            var earlier = candles.Where(c => !before.HasValue || c.Timestamp < before.Value).ToArray();
            if (earlier.Length == 0) break;
            foreach (var candle in earlier) result[candle.Timestamp] = candle;
            var nextBefore = earlier.Min(c => c.Timestamp);
            if (before.HasValue && nextBefore >= before.Value) break;
            before = nextBefore;
            // Kraken exposes only its recent OHLC window; another request cannot extend it.
            if (Definition.Id == "kraken") break;
        }
        return result.Values.TakeLast(limit).ToArray();
    }

    public async IAsyncEnumerable<CandleUpdate> WatchCandlesAsync(InstrumentKey instrument, string timeframe,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var candles = await GetCandlesAsync(instrument, timeframe, null, 3, ct).ConfigureAwait(false);
            for (var i = 0; i < candles.Length; i++)
                yield return new CandleUpdate(candles[i], IsClosed: i < candles.Length - 1 ||
                    candles[i].OpenTimeUtc + MarketCatalog.GetTimeframe(timeframe).Duration <= DateTimeOffset.UtcNow);
            await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<MarketMover>> GetMarketMoversAsync(CancellationToken ct = default)
    {
        var client = _client.Value;
        var markets = await client.LoadMarkets().WaitAsync(ct).ConfigureAwait(false);
        var tickers = await client.FetchTickers(null!, new Dictionary<string, object> { ["type"] = "spot" })
            .WaitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return MarketMoversCalculator.Calculate(markets.Values, tickers.tickers);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsValueCreated) await _client.Value.Close().ConfigureAwait(false);
    }
}
