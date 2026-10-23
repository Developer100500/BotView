using BotView.Models;

namespace BotView.Services;

public interface ICandleSeriesReader
{
    /// <summary>Captures the current block index and a value copy of the live candle.</summary>
    CandleSeriesSnapshot GetSnapshot();
}

public enum LiveCandleChange
{
    Stale, // The update is older than the current candle and was ignored.
	Updated, // The current live candle was inserted or replaced.
	Closed // The previous live candle closed and a new one became live.
}

/// <summary>A stable view of closed blocks plus a value copy of the current live candle.</summary>
public readonly struct CandleSeriesSnapshot
{
    // Fixed index of block references captured when this snapshot was created; the last block is Forward.
    private readonly Candle[][] _blocks;
    // Global starting index of each block; the last entry equals the number of candles in sealed blocks.
    private readonly int[] _prefixCounts;
    // Number of closed candles in Forward when this snapshot was created.
    private readonly int _forwardClosedCount;
    // Value copy of the current live candle, so later ticks cannot change this snapshot.
    private readonly Candle? _live;

    /// <summary>Combines a published block index with the Forward state captured under the series lock.</summary>
    internal CandleSeriesSnapshot(Candle[][] blocks, int[] prefixCounts, int forwardClosedCount, Candle? live)
    {
        _blocks = blocks;
        _prefixCounts = prefixCounts;
        _forwardClosedCount = forwardClosedCount;
        _live = live;
    }

    public int Count => (_prefixCounts?.Length > 0 ? _prefixCounts[^1] : 0)
        + _forwardClosedCount + (_live.HasValue ? 1 : 0);
    public int ClosedCount => Count - (_live.HasValue ? 1 : 0);
    public Candle? Live => _live;
    public long? OldestTimestamp => Count > 0 ? this[0].Timestamp : null;
    public long? NewestClosedTimestamp => ClosedCount > 0 ? this[ClosedCount - 1].Timestamp : null;

    /// <summary>Reads a candle by its index across sealed blocks, Forward, and the live value.</summary>
    public Candle this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            // Forward's closed candles use its array; the live candle comes from the value copy.
            int sealedCount = _prefixCounts[^1];
            if (index >= sealedCount + _forwardClosedCount)
                return _live!.Value;
            if (index >= sealedCount)
                return _blocks[^1][index - sealedCount];

            // Locate the sealed block, then translate the global index to a position in that block.
            int block = Array.BinarySearch(_prefixCounts, index);
            // A missing value belongs to the block immediately before its insertion point.
            block = block >= 0 ? block : ~block - 1;
            return _blocks[block][index - _prefixCounts[block]];
        }
    }

    /// <summary>Enumerates candles in an inclusive time range with their global indexes.</summary>
    public IEnumerable<(int Index, Candle Candle)> Enumerate(long fromTimestamp, long toTimestamp)
    {
        if (fromTimestamp > toTimestamp || _blocks == null)
            yield break;

        // Closed blocks are ordered by time, so skip blocks wholly left of the viewport.
        int first = 0;
        int end = _blocks.Length - 1;
        while (first < end)
        {
            int middle = first + (end - first) / 2;
            if (_blocks[middle][^1].Timestamp < fromTimestamp)
                first = middle + 1;
            else
                end = middle;
        }

        // Walk only blocks that can intersect the requested time range.
        for (int block = first; block < _blocks.Length; block++)
        {
            // Forward has spare capacity, so only its closed slots can be read from the array.
            int length = block == _blocks.Length - 1 ? _forwardClosedCount : _blocks[block].Length;
            if (length == 0 || _blocks[block][length - 1].Timestamp < fromTimestamp)
                continue;
            if (_blocks[block][0].Timestamp > toTimestamp)
                yield break;

            int baseIndex = _prefixCounts[block];
            for (int i = 0; i < length; i++)
            {
                var candle = _blocks[block][i];
                if (candle.Timestamp >= fromTimestamp && candle.Timestamp <= toTimestamp)
                    yield return (baseIndex + i, candle);
            }
        }

        // Forward's live slot is read from the snapshot copy, not from the mutable array.
        if (_live.HasValue && _live.Value.Timestamp >= fromTimestamp && _live.Value.Timestamp <= toTimestamp)
            yield return (Count - 1, _live.Value);
    }

    internal Candle[][] BlockReferences => _blocks;
}

/// <summary>Stores one market series in immutable closed blocks and one mutable forward block.</summary>
public sealed class CandleSeries : ICandleSeriesReader
{
    /// <summary>Maximum number of closed candles stored in one new block.</summary>
    public const int CandleChunkSize = 250;

    // Protects block changes, Forward writes, and snapshot capture.
    private readonly object _sync = new();
    // Blocks in time order; the last array is the active Forward block.
    private readonly List<Candle[]> _blocks = new() { new Candle[CandleChunkSize] };
    // Published array of block references, rebuilt only when the block list changes.
    private Candle[][] _publishedBlocks = Array.Empty<Candle[]>();
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
            // The published indexes are reused; only the mutable live value is copied here.
            var live = _hasLive ? _blocks[^1][_forwardClosedCount] : (Candle?)null;
            return new CandleSeriesSnapshot(_publishedBlocks, _prefixCounts, _forwardClosedCount, live);
        }
    }

    /// <summary>Replaces the series with sorted, deduplicated candles; the newest candle becomes live.</summary>
    public void LoadInitial(IEnumerable<Candle>? candles)
    {
        // Keep the last value for each timestamp before splitting the batch into blocks.
        var ordered = (candles ?? Array.Empty<Candle>()).OrderBy(c => c.Timestamp)
            .GroupBy(c => c.Timestamp).Select(g => g.Last()).ToArray();
        lock (_sync)
        {
            _blocks.Clear();
            _forwardClosedCount = 0;
            _hasLive = false;
            // All but the newest candle become immutable closed blocks.
            AddSealedBlocks(ordered.AsSpan(0, Math.Max(0, ordered.Length - 1)), atFront: false);
            _blocks.Add(new Candle[CandleChunkSize]);
            if (ordered.Length > 0)
            {
                _blocks[^1][0] = ordered[^1];
                _hasLive = true;
            }
            // Block composition changed, so publish new reference and prefix indexes.
            PublishBlockIndex();
        }
    }

    /// <summary>Prepends only candles older than the series and returns the number accepted.</summary>
    public int PrependHistory(IEnumerable<Candle>? older)
        => PrependHistory(older, out _);

    /// <summary>Prepends older candles and returns the exact accepted batch for notifications.</summary>
    public int PrependHistory(IEnumerable<Candle>? older, out Candle[] addedCandles)
    {
        var ordered = (older ?? Array.Empty<Candle>()).OrderBy(c => c.Timestamp)
            .GroupBy(c => c.Timestamp).Select(g => g.Last()).ToArray();
        lock (_sync)
        {
            // Reject overlap with the current oldest candle, including duplicates.
            var oldest = GetOldestTimestampUnsafe();
            var accepted = oldest.HasValue
                ? ordered.Where(c => c.Timestamp < oldest.Value).ToArray()
                : ordered;
            addedCandles = accepted;
            if (accepted.Length == 0)
                return 0;
            // Insert new block references on the left without moving existing candle values.
            AddSealedBlocks(accepted, atFront: true);
            PublishBlockIndex();
            return accepted.Length;
        }
    }

    /// <summary>Appends missing closed candles while preserving any later live candle.</summary>
    public int AppendClosed(IEnumerable<Candle>? newer)
    {
        var ordered = (newer ?? Array.Empty<Candle>()).OrderBy(c => c.Timestamp)
            .GroupBy(c => c.Timestamp).Select(g => g.Last()).ToArray();
        lock (_sync)
        {
            int added = 0;
            foreach (var candle in ordered)
            {
                // A catch-up candle must be newer than closed data but older than the live candle.
                var newest = GetNewestClosedTimestampUnsafe();
                if (newest.HasValue && candle.Timestamp <= newest.Value)
                    continue;
                if (_hasLive && candle.Timestamp >= _blocks[^1][_forwardClosedCount].Timestamp)
                    continue;
                Candle live = default;
                bool hadLive = _hasLive;
                // Make room before live; appending may also seal a full Forward block.
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
    public LiveCandleChange UpdateLive(Candle updated, out Candle closed)
    {
        lock (_sync)
        {
            closed = default;
            var forward = _blocks[^1];
            if (!_hasLive)
            {
                // Start a live candle only after the newest closed timestamp.
                var newest = GetNewestClosedTimestampUnsafe();
                if (newest.HasValue && updated.Timestamp <= newest.Value)
                    return LiveCandleChange.Stale;
                if (_forwardClosedCount == CandleChunkSize)
                {
                    _blocks.Add(new Candle[CandleChunkSize]);
                    _forwardClosedCount = 0;
                    PublishBlockIndex();
                    forward = _blocks[^1];
                }
                forward[_forwardClosedCount] = updated;
                _hasLive = true;
                return LiveCandleChange.Updated;
            }

            var current = forward[_forwardClosedCount];
            if (updated.Timestamp < current.Timestamp)
                return LiveCandleChange.Stale;
            if (updated.Timestamp == current.Timestamp)
            {
                // A tick changes only the current Forward slot; block indexes stay unchanged.
                forward[_forwardClosedCount] = updated;
                return LiveCandleChange.Updated;
            }

            // Time advanced: keep the previous value as closed and move live to the next slot.
            closed = current;
            _forwardClosedCount++;
            if (_forwardClosedCount == CandleChunkSize)
            {
                // The filled array stays in the list by reference; allocate only a new Forward.
                _blocks.Add(new Candle[CandleChunkSize]);
                _forwardClosedCount = 0;
                PublishBlockIndex();
            }
            _blocks[^1][_forwardClosedCount] = updated;
            return LiveCandleChange.Closed;
        }
    }

    /// <summary>Seals the live bar on a provider's explicit close, even during a trading break.</summary>
    public bool FinalizeLive(long timestamp)
    {
        lock (_sync)
        {
            if (!_hasLive || _blocks[^1][_forwardClosedCount].Timestamp != timestamp) return false;
            var candle = _blocks[^1][_forwardClosedCount];
            _hasLive = false;
            AppendClosedUnsafe(candle);
            return true;
        }
    }

    /// <summary>Appends one closed candle and rolls a full Forward block without copying it; caller holds the lock.</summary>
    private void AppendClosedUnsafe(Candle candle)
    {
        _blocks[^1][_forwardClosedCount++] = candle;
        if (_forwardClosedCount == CandleChunkSize)
        {
            // Keep the filled array as a sealed block and start an empty Forward.
            _blocks.Add(new Candle[CandleChunkSize]);
            _forwardClosedCount = 0;
            PublishBlockIndex();
        }
    }

    /// <summary>Finds the first stored timestamp; caller holds the lock.</summary>
    private long? GetOldestTimestampUnsafe()
    {
        foreach (var block in _blocks)
            if (block != _blocks[^1] && block.Length > 0)
                return block[0].Timestamp;
        if (_forwardClosedCount > 0)
            return _blocks[^1][0].Timestamp;
        return _hasLive ? _blocks[^1][0].Timestamp : null;
    }

    /// <summary>Finds the last closed candle in Forward or a sealed block; caller holds the lock.</summary>
    private long? GetNewestClosedTimestampUnsafe()
    {
        if (_forwardClosedCount > 0)
            return _blocks[^1][_forwardClosedCount - 1].Timestamp;
        return _blocks.Count > 1 ? _blocks[^2][^1].Timestamp : null;
    }

    /// <summary>Splits candles into sealed arrays and inserts their references at the chosen end.</summary>
    private void AddSealedBlocks(ReadOnlySpan<Candle> candles, bool atFront)
    {
        var blocks = new List<Candle[]>();
        // The final sealed block may be shorter than CandleChunkSize.
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
        // Replace the reference index; snapshots already in use keep their previous array.
        _publishedBlocks = _blocks.ToArray();
        var prefix = new int[_blocks.Count];
        int count = 0;
        for (int i = 0; i < _blocks.Count; i++)
        {
            // Prefix[i] is the global index of this block's first candle.
            prefix[i] = count;
            // Forward's occupied slots are tracked separately from its 250-slot capacity.
            if (i < _blocks.Count - 1)
                count += _blocks[i].Length;
        }
        _prefixCounts = prefix;
    }
}
