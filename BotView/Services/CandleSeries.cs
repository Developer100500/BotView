using BotView.Models;

namespace BotView.Services;

public interface ICandleSeriesReader
{
    /// <summary>Captures the current block index and a value copy of the live candle.</summary>
    CandleSeriesSnapshot GetSnapshot();
}

public enum LiveCandleChange
{
    // The update is older than the current candle and was ignored.
    Stale,
    // The current live candle was inserted or replaced.
    Updated,
    // The previous live candle closed and a new one became live.
    Closed
}

/// <summary>A stable view of closed blocks plus a value copy of the current live candle.</summary>
public readonly struct CandleSeriesSnapshot
{
    // Fixed index of block references captured when this snapshot was created; the last block is Forward.
    private readonly OHLCV[][] _blocks;
    // Global starting index of each block; the last entry equals the number of candles in sealed blocks.
    private readonly int[] _prefixCounts;
    // Number of closed candles in Forward when this snapshot was created.
    private readonly int _forwardClosedCount;
    // Value copy of the current live candle, so later ticks cannot change this snapshot.
    private readonly OHLCV? _live;

    /// <summary>Combines a published block index with the Forward state captured under the series lock.</summary>
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

    /// <summary>Reads a candle by its index across sealed blocks, Forward, and the live value.</summary>
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

    /// <summary>Enumerates candles in an inclusive time range with their global indexes.</summary>
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
    /// <summary>Maximum number of closed candles stored in one new block.</summary>
    public const int CandleChunkSize = 250;

    // Protects block changes, Forward writes, and snapshot capture.
    private readonly object _sync = new();
    // Blocks in time order; the last array is the active Forward block.
    private readonly List<OHLCV[]> _blocks = new() { new OHLCV[CandleChunkSize] };
    // Published array of block references, rebuilt only when the block list changes.
    private OHLCV[][] _publishedBlocks = Array.Empty<OHLCV[]>();
    // Starting global index of each published block, including Forward.
    private int[] _prefixCounts = Array.Empty<int>();
    // Number of closed candles in Forward; its live candle occupies the next slot.
    private int _forwardClosedCount;
    // Whether Forward currently contains a live candle after its closed candles.
    private bool _hasLive;

    /// <summary>Creates an empty series with one Forward block and publishes its initial index.</summary>
    public CandleSeries() => PublishBlockIndex();

    /// <summary>Captures block references, counts, and the live candle while holding the series lock.</summary>
    public CandleSeriesSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var live = _hasLive ? _blocks[^1][_forwardClosedCount] : (OHLCV?)null;
            return new CandleSeriesSnapshot(_publishedBlocks, _prefixCounts, _forwardClosedCount, live);
        }
    }

    /// <summary>Replaces the series with sorted, deduplicated candles; the newest candle becomes live.</summary>
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

    /// <summary>Prepends only candles older than the series and returns the number accepted.</summary>
    public int PrependHistory(IEnumerable<OHLCV>? older)
        => PrependHistory(older, out _);

    /// <summary>Prepends older candles and returns the exact accepted batch for notifications.</summary>
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

    /// <summary>Appends missing closed candles while preserving any later live candle.</summary>
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

    /// <summary>Replaces the live slot or closes it and opens the next slot when time advances.</summary>
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

    /// <summary>Appends one closed candle and rolls a full Forward block without copying it; caller holds the lock.</summary>
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

    /// <summary>Finds the first stored timestamp; caller holds the lock.</summary>
    private long? GetOldestTimestampUnsafe()
    {
        foreach (var block in _blocks)
            if (block != _blocks[^1] && block.Length > 0)
                return block[0].timestamp;
        if (_forwardClosedCount > 0)
            return _blocks[^1][0].timestamp;
        return _hasLive ? _blocks[^1][0].timestamp : null;
    }

    /// <summary>Finds the last closed candle in Forward or a sealed block; caller holds the lock.</summary>
    private long? GetNewestClosedTimestampUnsafe()
    {
        if (_forwardClosedCount > 0)
            return _blocks[^1][_forwardClosedCount - 1].timestamp;
        return _blocks.Count > 1 ? _blocks[^2][^1].timestamp : null;
    }

    /// <summary>Splits candles into sealed arrays and inserts their references at the chosen end.</summary>
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

    /// <summary>Rebuilds the immutable reference and prefix indexes after the block list changes.</summary>
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
