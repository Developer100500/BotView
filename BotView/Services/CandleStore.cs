using BotView.Models;
using BotView.Services;

public readonly record struct CandleCacheKey(string Exchange, string Symbol, string Timeframe, int Limit);

/// <summary>Owns the shared candle series for each market data key.</summary>
public sealed class CandleStore
{
    private readonly Dictionary<CandleCacheKey, CandleSeries> _seriesByKey = new();
    private readonly object _sync = new();

    internal CandleSeries GetOrCreateSeries(CandleCacheKey key)
    {
        lock (_sync)
        {
            if (!_seriesByKey.TryGetValue(key, out var series))
                _seriesByKey[key] = series = new CandleSeries();
            return series;
        }
    }

    public ICandleSeriesReader GetSeries(CandleCacheKey key) => GetOrCreateSeries(key);

    private CandleSeries? FindSeries(CandleCacheKey key)
    {
        lock (_sync)
            return _seriesByKey.TryGetValue(key, out var series) ? series : null;
    }

    public void LoadInitial(CandleCacheKey key, IEnumerable<OHLCV> candles) =>
        GetOrCreateSeries(key).LoadInitial(candles);

    public LiveCandleChange UpdateLive(CandleCacheKey key, OHLCV candle, out OHLCV closed) =>
        GetOrCreateSeries(key).UpdateLive(candle, out closed);

    public IReadOnlyList<OHLCV> GetRange(CandleCacheKey key, long fromTs, long toTs)
    {
        if (fromTs > toTs)
            return Array.Empty<OHLCV>();
        var series = FindSeries(key);
        if (series == null)
            return Array.Empty<OHLCV>();
        var snapshot = series.GetSnapshot();
        return snapshot.Enumerate(fromTs, toTs)
            .Where(item => item.Index < snapshot.ClosedCount)
            .Select(item => item.Candle).ToArray();
    }

    public int PrependHistory(CandleCacheKey key, IReadOnlyList<OHLCV> older) =>
        GetOrCreateSeries(key).PrependHistory(older);

    public int PrependHistory(CandleCacheKey key, IReadOnlyList<OHLCV> older, out OHLCV[] added) =>
        GetOrCreateSeries(key).PrependHistory(older, out added);

    public int AppendClosed(CandleCacheKey key, IReadOnlyList<OHLCV> newer) =>
        GetOrCreateSeries(key).AppendClosed(newer);

    public long? GetOldestTimestamp(CandleCacheKey key) =>
        FindSeries(key)?.GetSnapshot().OldestTimestamp;

    public long? GetNewestTimestamp(CandleCacheKey key) =>
        FindSeries(key)?.GetSnapshot().NewestClosedTimestamp;

    public int GetCount(CandleCacheKey key) =>
        FindSeries(key)?.GetSnapshot().ClosedCount ?? 0;

    public bool Clear(CandleCacheKey key)
    {
        lock (_sync)
            return _seriesByKey.Remove(key);
    }

    public int GetKeyCount()
    {
        lock (_sync)
            return _seriesByKey.Count;
    }
}
