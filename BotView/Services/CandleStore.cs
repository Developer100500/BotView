using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BotView.Models;

public readonly record struct CandleCacheKey(string Exchange, string Symbol, string Timeframe, int Limit);

public sealed class CandleStore
{
    private readonly Dictionary<CandleCacheKey, SortedList<long, OHLCV>> _candlesByKey = new();
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly int _perKeyCapacity;

    public CandleStore(int perKeyCapacity = 1000)
    {
        _perKeyCapacity = Math.Max(1, perKeyCapacity);
        _lock = new ReaderWriterLockSlim();
    }

    /// <summary> Возвращает свечи в диапазоне [fromTs, toTs] с LINQ-фильтрацией. </summary>
    public IReadOnlyList<OHLCV> GetRange(CandleCacheKey key, long fromTs, long toTs)
    {
        if (fromTs > toTs)
        {
            return Array.Empty<OHLCV>();
        }

        _lock.EnterReadLock();
        try
        {
            if (!_candlesByKey.TryGetValue(key, out var candles))
            {
                return Array.Empty<OHLCV>();
            }

            return candles
                .Where(kvp => kvp.Key >= fromTs && kvp.Key <= toTs)
                .Select(kvp => kvp.Value)
                .ToArray();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary> Добавляет блок исторических свечей слева; вернёт сколько действительно новых. </summary>
    public int PrependHistory(CandleCacheKey key, IReadOnlyList<OHLCV> older)
    {
        if (older == null || older.Count == 0)
        {
            return 0;
        }

        _lock.EnterWriteLock();
        try
        {
            var candles = GetOrCreateSeriesUnsafe(key);
            int added = 0;
            long? currentOldest = candles.Count == 0 ? null : candles.Keys[0];

            foreach (var candle in older)
            {
                // Храним только закрытую историю: принимаем только строго более старые свечи.
                if (currentOldest.HasValue && candle.timestamp >= currentOldest.Value)
                {
                    continue;
                }

                if (candles.ContainsKey(candle.timestamp))
                {
                    continue;
                }

                candles.Add(candle.timestamp, candle);
                added++;
            }

            return added;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary> Добавляет блок свежих закрытых свечей справа (например после reconnect). </summary>
    public int AppendClosed(CandleCacheKey key, IReadOnlyList<OHLCV> newer)
    {
        if (newer == null || newer.Count == 0)
        {
            return 0;
        }

        _lock.EnterWriteLock();
        try
        {
            var candles = GetOrCreateSeriesUnsafe(key);
            int added = 0;
            long? currentNewest = candles.Count == 0 ? null : candles.Keys[candles.Count - 1];

            foreach (var candle in newer)
            {
                // Храним только закрытую историю: справа принимаем только строго новые свечи.
                if (currentNewest.HasValue && candle.timestamp <= currentNewest.Value)
                {
                    continue;
                }

                if (candles.ContainsKey(candle.timestamp))
                {
                    continue;
                }

                candles.Add(candle.timestamp, candle);
                added++;
            }

            return added;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary> Возвращает timestamp самой старой свечи для заданного ключа. </summary>
    public long? GetOldestTimestamp(CandleCacheKey key)
    {
        _lock.EnterReadLock();
        try
        {
            return _candlesByKey.TryGetValue(key, out var candles) && candles.Count > 0
                ? candles.Keys[0]
                : null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary> Возвращает timestamp самой новой закрытой свечи для заданного ключа. </summary>
    public long? GetNewestTimestamp(CandleCacheKey key)
    {
        _lock.EnterReadLock();
        try
        {
            return _candlesByKey.TryGetValue(key, out var candles) && candles.Count > 0
                ? candles.Keys[candles.Count - 1]
                : null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary> Возвращает количество закрытых свечей для заданного ключа. </summary>
    public int GetCount(CandleCacheKey key)
    {
        _lock.EnterReadLock();
        try
        {
            return _candlesByKey.TryGetValue(key, out var candles) ? candles.Count : 0;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary> Удаляет все сохранённые свечи для заданного ключа. </summary>
    public bool Clear(CandleCacheKey key)
    {
        _lock.EnterWriteLock();
        try
        {
            return _candlesByKey.Remove(key);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary> Возвращает количество ключей с историей свечей. </summary>
    public int GetKeyCount()
    {
        _lock.EnterReadLock();
        try
        {
            return _candlesByKey.Count;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary> Возвращает или создаёт коллекцию свечей для ключа. </summary>
    private SortedList<long, OHLCV> GetOrCreateSeriesUnsafe(CandleCacheKey key)
    {
        if (!_candlesByKey.TryGetValue(key, out var candles))
        {
            candles = new SortedList<long, OHLCV>(_perKeyCapacity);
            _candlesByKey[key] = candles;
        }

        return candles;
    }
}