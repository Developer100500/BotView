using BotView.Models;

namespace BotView.Services;

public interface ICandleSeriesReader
{
    CandleSeriesSnapshot GetSnapshot();
}

public enum LiveCandleChange
{
    Stale,
    Updated,
    Closed
}

/// <summary>A stable view of closed blocks plus a value copy of the current live candle.</summary>
public readonly struct CandleSeriesSnapshot
{
    private readonly OHLCV[][] _blocks;
    private readonly int[] _prefixCounts;
    private readonly int _forwardClosedCount;
    private readonly OHLCV? _live;

    internal CandleSeriesSnapshot(OHLCV[][] blocks, int[] prefixCounts, int forwardClosedCount, OHLCV? live)
    {
        _blocks = blocks;
        _prefixCounts = prefixCounts;
        _forwardClosedCount = forwardClosedCount;
        _live = live;
    }

    public int Count => (_prefixCounts?.Length > 0 ? _prefixCounts[^1] : 0)
        + _forwardClosedCount + (_live.HasValue ? 1 : 0);
    public int ClosedCount => Count - (_live.HasValue ? 1 : 0);
    public OHLCV? Live => _live;
    public long? OldestTimestamp => Count > 0 ? this[0].timestamp : null;
    public long? NewestClosedTimestamp => ClosedCount > 0 ? this[ClosedCount - 1].timestamp : null;

    public OHLCV this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            int sealedCount = _prefixCounts[^1];
            if (index >= sealedCount + _forwardClosedCount)
                return _live!.Value;
            if (index >= sealedCount)
                return _blocks[^1][index - sealedCount];

            // Prefix entries hold the number of candles before each sealed block.
            int block = Array.BinarySearch(_prefixCounts, index);
            block = block >= 0 ? block : ~block - 1;
            return _blocks[block][index - _prefixCounts[block]];
        }
    }

    public IEnumerable<(int Index, OHLCV Candle)> Enumerate(long fromTimestamp, long toTimestamp)
    {
        if (fromTimestamp > toTimestamp || _blocks == null)
            yield break;

        // Closed blocks are ordered by time, so skip blocks wholly left of the viewport.
        int first = 0;
        int end = _blocks.Length - 1;
        while (first < end)
        {
            int middle = first + (end - first) / 2;
            if (_blocks[middle][^1].timestamp < fromTimestamp)
                first = middle + 1;
            else
                end = middle;
        }

        for (int block = first; block < _blocks.Length; block++)
        {
            int length = block == _blocks.Length - 1 ? _forwardClosedCount : _blocks[block].Length;
            if (length == 0 || _blocks[block][length - 1].timestamp < fromTimestamp)
                continue;
            if (_blocks[block][0].timestamp > toTimestamp)
                yield break;

            int baseIndex = _prefixCounts[block];
            for (int i = 0; i < length; i++)
            {
                var candle = _blocks[block][i];
                if (candle.timestamp >= fromTimestamp && candle.timestamp <= toTimestamp)
                    yield return (baseIndex + i, candle);
            }
        }

        if (_live.HasValue && _live.Value.timestamp >= fromTimestamp && _live.Value.timestamp <= toTimestamp)
            yield return (Count - 1, _live.Value);
    }

    internal OHLCV[][] BlockReferences => _blocks;
}

/// <summary>Stores one market series in immutable closed blocks and one mutable forward block.</summary>
public sealed class CandleSeries : ICandleSeriesReader
{
    public const int CandleChunkSize = 250;

    private readonly object _sync = new();
    private readonly List<OHLCV[]> _blocks = new() { new OHLCV[CandleChunkSize] };
    private OHLCV[][] _publishedBlocks = Array.Empty<OHLCV[]>();
    private int[] _prefixCounts = Array.Empty<int>();
    private int _forwardClosedCount;
    private bool _hasLive;

    public CandleSeries() => PublishBlockIndex();

    public CandleSeriesSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var live = _hasLive ? _blocks[^1][_forwardClosedCount] : (OHLCV?)null;
            return new CandleSeriesSnapshot(_publishedBlocks, _prefixCounts, _forwardClosedCount, live);
        }
    }

    public void LoadInitial(IEnumerable<OHLCV>? candles)
    {
        var ordered = (candles ?? Array.Empty<OHLCV>()).OrderBy(c => c.timestamp)
            .GroupBy(c => c.timestamp).Select(g => g.Last()).ToArray();
        lock (_sync)
        {
            _blocks.Clear();
            _forwardClosedCount = 0;
            _hasLive = false;
            AddSealedBlocks(ordered.AsSpan(0, Math.Max(0, ordered.Length - 1)), atFront: false);
            _blocks.Add(new OHLCV[CandleChunkSize]);
            if (ordered.Length > 0)
            {
                _blocks[^1][0] = ordered[^1];
                _hasLive = true;
            }
            PublishBlockIndex();
        }
    }

    public int PrependHistory(IEnumerable<OHLCV>? older)
        => PrependHistory(older, out _);

    public int PrependHistory(IEnumerable<OHLCV>? older, out OHLCV[] addedCandles)
    {
        var ordered = (older ?? Array.Empty<OHLCV>()).OrderBy(c => c.timestamp)
            .GroupBy(c => c.timestamp).Select(g => g.Last()).ToArray();
        lock (_sync)
        {
            var oldest = GetOldestTimestampUnsafe();
            var accepted = oldest.HasValue
                ? ordered.Where(c => c.timestamp < oldest.Value).ToArray()
                : ordered;
            addedCandles = accepted;
            if (accepted.Length == 0)
                return 0;
            AddSealedBlocks(accepted, atFront: true);
            PublishBlockIndex();
            return accepted.Length;
        }
    }

    public int AppendClosed(IEnumerable<OHLCV>? newer)
    {
        var ordered = (newer ?? Array.Empty<OHLCV>()).OrderBy(c => c.timestamp)
            .GroupBy(c => c.timestamp).Select(g => g.Last()).ToArray();
        lock (_sync)
        {
            int added = 0;
            foreach (var candle in ordered)
            {
                var newest = GetNewestClosedTimestampUnsafe();
                if (newest.HasValue && candle.timestamp <= newest.Value)
                    continue;
                if (_hasLive && candle.timestamp >= _blocks[^1][_forwardClosedCount].timestamp)
                    continue;
                OHLCV live = default;
                bool hadLive = _hasLive;
                if (hadLive)
                {
                    live = _blocks[^1][_forwardClosedCount];
                    _hasLive = false;
                }
                AppendClosedUnsafe(candle);
                if (hadLive)
                {
                    _blocks[^1][_forwardClosedCount] = live;
                    _hasLive = true;
                }
                added++;
            }
            return added;
        }
    }

    public LiveCandleChange UpdateLive(OHLCV updated, out OHLCV closed)
    {
        lock (_sync)
        {
            closed = default;
            var forward = _blocks[^1];
            if (!_hasLive)
            {
                var newest = GetNewestClosedTimestampUnsafe();
                if (newest.HasValue && updated.timestamp <= newest.Value)
                    return LiveCandleChange.Stale;
                if (_forwardClosedCount == CandleChunkSize)
                {
                    _blocks.Add(new OHLCV[CandleChunkSize]);
                    _forwardClosedCount = 0;
                    PublishBlockIndex();
                    forward = _blocks[^1];
                }
                forward[_forwardClosedCount] = updated;
                _hasLive = true;
                return LiveCandleChange.Updated;
            }

            var current = forward[_forwardClosedCount];
            if (updated.timestamp < current.timestamp)
                return LiveCandleChange.Stale;
            if (updated.timestamp == current.timestamp)
            {
                forward[_forwardClosedCount] = updated;
                return LiveCandleChange.Updated;
            }

            closed = current;
            _forwardClosedCount++;
            if (_forwardClosedCount == CandleChunkSize)
            {
                _blocks.Add(new OHLCV[CandleChunkSize]);
                _forwardClosedCount = 0;
                PublishBlockIndex();
            }
            _blocks[^1][_forwardClosedCount] = updated;
            return LiveCandleChange.Closed;
        }
    }

    private void AppendClosedUnsafe(OHLCV candle)
    {
        _blocks[^1][_forwardClosedCount++] = candle;
        if (_forwardClosedCount == CandleChunkSize)
        {
            _blocks.Add(new OHLCV[CandleChunkSize]);
            _forwardClosedCount = 0;
            PublishBlockIndex();
        }
    }

    private long? GetOldestTimestampUnsafe()
    {
        foreach (var block in _blocks)
            if (block != _blocks[^1] && block.Length > 0)
                return block[0].timestamp;
        if (_forwardClosedCount > 0)
            return _blocks[^1][0].timestamp;
        return _hasLive ? _blocks[^1][0].timestamp : null;
    }

    private long? GetNewestClosedTimestampUnsafe()
    {
        if (_forwardClosedCount > 0)
            return _blocks[^1][_forwardClosedCount - 1].timestamp;
        return _blocks.Count > 1 ? _blocks[^2][^1].timestamp : null;
    }

    private void AddSealedBlocks(ReadOnlySpan<OHLCV> candles, bool atFront)
    {
        var blocks = new List<OHLCV[]>();
        for (int offset = 0; offset < candles.Length; offset += CandleChunkSize)
            blocks.Add(candles.Slice(offset, Math.Min(CandleChunkSize, candles.Length - offset)).ToArray());
        if (atFront)
            _blocks.InsertRange(0, blocks);
        else
            _blocks.AddRange(blocks);
    }

    private void PublishBlockIndex()
    {
        _publishedBlocks = _blocks.ToArray();
        var prefix = new int[_blocks.Count];
        int count = 0;
        for (int i = 0; i < _blocks.Count; i++)
        {
            prefix[i] = count;
            if (i < _blocks.Count - 1)
                count += _blocks[i].Length;
        }
        _prefixCounts = prefix;
    }
}
