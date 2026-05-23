using System.Collections.Generic;
using BotView.Models;
using Xunit;

namespace BotView.Tests
{
    public class CandleStoreTests
    {
        /// <summary> Добавляет новые свечи справа и обновляет метаданные серии. </summary>
        [Fact]
        public void AppendClosed_WithStrictlyNewCandles_AddsAllCandles()
        {
            var store = new CandleStore();
            var key = CreateKey("BTC/USDT");
            var candles = new List<OHLCV>
            {
                CreateCandle(1000),
                CreateCandle(2000),
                CreateCandle(3000)
            };

            var added = store.AppendClosed(key, candles);

            Assert.Equal(3, added);
            Assert.Equal(3, store.GetCount(key));
            Assert.Equal(1000, store.GetOldestTimestamp(key));
            Assert.Equal(3000, store.GetNewestTimestamp(key));
        }

        /// <summary> Игнорирует нестрого новые и дублирующиеся свечи при добавлении справа. </summary>
        [Fact]
        public void AppendClosed_WithDuplicateAndOlderCandles_AddsOnlyStrictlyNewCandles()
        {
            var store = new CandleStore();
            var key = CreateKey("BTC/USDT");
            store.AppendClosed(key, new[] { CreateCandle(1000), CreateCandle(2000) });

            var added = store.AppendClosed(
                key,
                new[]
                {
                    CreateCandle(1500),
                    CreateCandle(2000),
                    CreateCandle(3000),
                    CreateCandle(4000)
                });

            Assert.Equal(2, added);
            Assert.Equal(4, store.GetCount(key));
            Assert.Equal(4000, store.GetNewestTimestamp(key));
        }

        /// <summary> Игнорирует нестрого старые и дублирующиеся свечи при добавлении слева. </summary>
        [Fact]
        public void PrependHistory_WithDuplicateAndNewerCandles_AddsOnlyStrictlyOlderCandles()
        {
            var store = new CandleStore();
            var key = CreateKey("BTC/USDT");
            store.AppendClosed(key, new[] { CreateCandle(3000), CreateCandle(4000) });

            var added = store.PrependHistory(
                key,
                new[]
                {
                    CreateCandle(2000),
                    CreateCandle(3000),
                    CreateCandle(1000),
                    CreateCandle(3500)
                });

            Assert.Equal(2, added);
            Assert.Equal(4, store.GetCount(key));
            Assert.Equal(1000, store.GetOldestTimestamp(key));
            Assert.Equal(4000, store.GetNewestTimestamp(key));
        }

        /// <summary> Возвращает свечи в диапазоне включительно и в порядке времени. </summary>
        [Fact]
        public void GetRange_WithInclusiveBounds_ReturnsExpectedCandlesInOrder()
        {
            var store = new CandleStore();
            var key = CreateKey("ETH/USDT");
            store.AppendClosed(
                key,
                new[]
                {
                    CreateCandle(1000),
                    CreateCandle(2000),
                    CreateCandle(3000),
                    CreateCandle(4000)
                });

            var result = store.GetRange(key, 2000, 3000);

            Assert.Equal(2, result.Count);
            Assert.Equal(2000, result[0].timestamp);
            Assert.Equal(3000, result[1].timestamp);
        }

        /// <summary> Возвращает пустой список, когда границы диапазона некорректны. </summary>
        [Fact]
        public void GetRange_WhenFromIsGreaterThanTo_ReturnsEmpty()
        {
            var store = new CandleStore();
            var key = CreateKey("ETH/USDT");
            store.AppendClosed(key, new[] { CreateCandle(1000), CreateCandle(2000) });

            var result = store.GetRange(key, 3000, 1000);

            Assert.Empty(result);
        }

        /// <summary> Очищает только выбранный ключ и корректно обновляет количество ключей. </summary>
        [Fact]
        public void Clear_RemovesOnlySpecifiedKeyAndUpdatesKeyCount()
        {
            var store = new CandleStore();
            var btcKey = CreateKey("BTC/USDT");
            var ethKey = CreateKey("ETH/USDT");

            store.AppendClosed(btcKey, new[] { CreateCandle(1000) });
            store.AppendClosed(ethKey, new[] { CreateCandle(2000) });

            var removed = store.Clear(btcKey);

            Assert.True(removed);
            Assert.Equal(1, store.GetKeyCount());
            Assert.Equal(0, store.GetCount(btcKey));
            Assert.Equal(1, store.GetCount(ethKey));
            Assert.Null(store.GetOldestTimestamp(btcKey));
            Assert.Equal(2000, store.GetOldestTimestamp(ethKey));
        }

        /// <summary> Создает единый ключ серии для тестов. </summary>
        private static CandleCacheKey CreateKey(string symbol)
        {
            return new CandleCacheKey("Binance", symbol, "1m", 500);
        }

        /// <summary> Создает свечу с заданным timestamp и предсказуемыми значениями. </summary>
        private static OHLCV CreateCandle(long timestamp)
        {
            return new OHLCV(timestamp, 1, 2, 0.5, 1.5, 10);
        }
    }
}
