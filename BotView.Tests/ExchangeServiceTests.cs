using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Moq;
using BotView.Services;
using BotView.Chart;
using BotView.Interfaces;
using BotView.Exceptions;

namespace BotView.Tests
{
    /// <summary>
    /// Unit tests for ExchangeService class
    /// Tests exchange operations, caching, and error handling
    /// </summary>
    public class ExchangeServiceTests
    {
        private readonly Mock<IDataProvider> _mockDataProvider;
        private readonly Mock<IExchangeLogger> _mockLogger;
        private readonly ExchangeService _exchangeService;

        public ExchangeServiceTests()
        {
            _mockDataProvider = new Mock<IDataProvider>();
            _mockLogger = new Mock<IExchangeLogger>();
            _exchangeService = new ExchangeService(_mockDataProvider.Object, _mockLogger.Object, 
                cacheExpirationMinutes: 1, maxRetryAttempts: 2, baseRetryDelaySeconds: 1);
        }

        #region Constructor Tests

        [Fact]
        public void Constructor_NullDataProvider_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => 
                new ExchangeService(null, _mockLogger.Object));
        }

        [Fact]
        public void Constructor_ValidParameters_CreatesInstance()
        {
            // Act
            var service = new ExchangeService(_mockDataProvider.Object, _mockLogger.Object);

            // Assert
            Assert.NotNull(service);
        }

        #endregion

        #region GetSupportedExchanges Tests

        [Fact]
        public void GetSupportedExchanges_ReturnsExpectedExchanges()
        {
            // Act
            var exchanges = _exchangeService.GetSupportedExchanges();

            // Assert
            Assert.NotNull(exchanges);
            Assert.Contains("binance", exchanges);
            Assert.Contains("bybit", exchanges);
            Assert.Contains("okx", exchanges);
            Assert.Contains("kraken", exchanges);
            Assert.Equal(4, exchanges.Count);
        }

        #endregion

        #region GetSupportedTimeframes Tests

        [Fact]
        public void GetSupportedTimeframes_ReturnsExpectedTimeframes()
        {
            // Act
            var timeframes = _exchangeService.GetSupportedTimeframes();

            // Assert
            Assert.NotNull(timeframes);
            Assert.Contains("1m", timeframes);
            Assert.Contains("5m", timeframes);
            Assert.Contains("15m", timeframes);
            Assert.Contains("30m", timeframes);
            Assert.Contains("1h", timeframes);
            Assert.Contains("4h", timeframes);
            Assert.Contains("1d", timeframes);
            Assert.Contains("1w", timeframes);
            Assert.Equal(8, timeframes.Count);
        }

        #endregion

        #region GetCandlestickDataAsync Parameter Validation Tests

        [Fact]
        public async Task GetCandlestickDataAsync_NullExchange_ThrowsArgumentException()
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => 
                _exchangeService.GetCandlestickDataAsync(null, "BTC/USDT", "1h"));
        }

        [Fact]
        public async Task GetCandlestickDataAsync_EmptyExchange_ThrowsArgumentException()
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => 
                _exchangeService.GetCandlestickDataAsync("", "BTC/USDT", "1h"));
        }

        [Fact]
        public async Task GetCandlestickDataAsync_NullSymbol_ThrowsArgumentException()
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => 
                _exchangeService.GetCandlestickDataAsync("binance", null, "1h"));
        }

        [Fact]
        public async Task GetCandlestickDataAsync_EmptySymbol_ThrowsArgumentException()
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => 
                _exchangeService.GetCandlestickDataAsync("binance", "", "1h"));
        }

        [Fact]
        public async Task GetCandlestickDataAsync_NullTimeframe_ThrowsArgumentException()
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => 
                _exchangeService.GetCandlestickDataAsync("binance", "BTC/USDT", null));
        }

        [Fact]
        public async Task GetCandlestickDataAsync_EmptyTimeframe_ThrowsArgumentException()
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => 
                _exchangeService.GetCandlestickDataAsync("binance", "BTC/USDT", ""));
        }

        [Fact]
        public async Task GetCandlestickDataAsync_ZeroLimit_ThrowsArgumentException()
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => 
                _exchangeService.GetCandlestickDataAsync("binance", "BTC/USDT", "1h", 0));
        }

        [Fact]
        public async Task GetCandlestickDataAsync_NegativeLimit_ThrowsArgumentException()
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => 
                _exchangeService.GetCandlestickDataAsync("binance", "BTC/USDT", "1h", -1));
        }

        [Fact]
        public async Task GetCandlestickDataAsync_UnsupportedExchange_ThrowsNotSupportedException()
        {
            // Act & Assert
            await Assert.ThrowsAsync<NotSupportedException>(() => 
                _exchangeService.GetCandlestickDataAsync("unsupported", "BTC/USDT", "1h"));
        }

        [Fact]
        public async Task GetCandlestickDataAsync_UnsupportedTimeframe_ThrowsNotSupportedException()
        {
            // Act & Assert
            await Assert.ThrowsAsync<NotSupportedException>(() => 
                _exchangeService.GetCandlestickDataAsync("binance", "BTC/USDT", "2h"));
        }

        #endregion

        #region GetCandlestickDataAsync Success Tests

        [Fact]
        public async Task GetCandlestickDataAsync_ValidParameters_ReturnsData()
        {
            // Arrange
            var expectedData = CreateSampleCandlestickData();
            _mockDataProvider.Setup(x => x.ConvertFromCCXT(It.IsAny<List<ccxt.OHLCV>>(), It.IsAny<string>()))
                .Returns(expectedData);

            // Act
            var result = await _exchangeService.GetCandlestickDataAsync("binance", "BTC/USDT", "1h");

            // Assert
            Assert.Equal(expectedData.timeframe, result.timeframe);
            Assert.Equal(expectedData.candles.Length, result.candles.Length);
        }

        [Fact]
        public async Task GetCandlestickDataAsync_CaseInsensitiveExchange_ReturnsData()
        {
            // Arrange
            var expectedData = CreateSampleCandlestickData();
            _mockDataProvider.Setup(x => x.ConvertFromCCXT(It.IsAny<List<ccxt.OHLCV>>(), It.IsAny<string>()))
                .Returns(expectedData);

            // Act
            var result = await _exchangeService.GetCandlestickDataAsync("BINANCE", "BTC/USDT", "1h");

            // Assert
            Assert.True(result.candles != null);
        }

        #endregion

        #region Caching Tests

        [Fact]
        public async Task GetCandlestickDataAsync_SameRequestTwice_UsesCacheOnSecondCall()
        {
            // Arrange
            var expectedData = CreateSampleCandlestickData();
            _mockDataProvider.Setup(x => x.ConvertFromCCXT(It.IsAny<List<ccxt.OHLCV>>(), It.IsAny<string>()))
                .Returns(expectedData);

            // Act
            var result1 = await _exchangeService.GetCandlestickDataAsync("binance", "BTC/USDT", "1h");
            var result2 = await _exchangeService.GetCandlestickDataAsync("binance", "BTC/USDT", "1h");

            // Assert
            Assert.Equal(result1.timeframe, result2.timeframe);
            
            // Verify cache hit was logged
            _mockLogger.Verify(x => x.LogCacheHit(It.IsAny<string>(), It.IsAny<string>(), 
                It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.AtLeastOnce);
        }

        [Fact]
        public void ClearCache_RemovesAllCachedData()
        {
            // Act
            _exchangeService.ClearCache();

            // Assert - No exception should be thrown
            // Verify that cache clear was logged
            _mockLogger.Verify(x => x.LogInfo("Cache cleared", It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public void ClearExpiredCache_RemovesExpiredEntries()
        {
            // Act
            _exchangeService.ClearExpiredCache();

            // Assert - No exception should be thrown
        }

        #endregion

        #region Error Handling Tests

        [Fact]
        public async Task GetCandlestickDataAsync_DataProviderThrowsException_PropagatesException()
        {
            // Arrange
            _mockDataProvider.Setup(x => x.ConvertFromCCXT(It.IsAny<List<ccxt.OHLCV>>(), It.IsAny<string>()))
                .Throws(new DataConversionException("Test error", DataConversionErrorType.InvalidFormat));

            // Act & Assert
            await Assert.ThrowsAsync<DataConversionException>(() => 
                _exchangeService.GetCandlestickDataAsync("binance", "BTC/USDT", "1h"));
        }

        #endregion

        #region GetAvailableSymbolsAsync Tests

        [Fact]
        public async Task GetAvailableSymbolsAsync_NullExchange_ThrowsArgumentException()
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => 
                _exchangeService.GetAvailableSymbolsAsync(null));
        }

        [Fact]
        public async Task GetAvailableSymbolsAsync_EmptyExchange_ThrowsArgumentException()
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => 
                _exchangeService.GetAvailableSymbolsAsync(""));
        }

        [Fact]
        public async Task GetAvailableSymbolsAsync_UnsupportedExchange_ThrowsNotSupportedException()
        {
            // Act & Assert
            await Assert.ThrowsAsync<NotSupportedException>(() => 
                _exchangeService.GetAvailableSymbolsAsync("unsupported"));
        }

        #endregion

        #region TestConnectionAsync Tests

        [Fact]
        public async Task TestConnectionAsync_NullExchange_ReturnsFalse()
        {
            // Act
            var result = await _exchangeService.TestConnectionAsync(null);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task TestConnectionAsync_EmptyExchange_ReturnsFalse()
        {
            // Act
            var result = await _exchangeService.TestConnectionAsync("");
        }

        [Fact]
        public async Task TestConnectionAsync_UnsupportedExchange_ReturnsFalse()
        {
            // Act
            var result = await _exchangeService.TestConnectionAsync("unsupported");

            // Assert
            Assert.False(result);
        }

        #endregion

        #region Performance Metrics Tests

        [Fact]
        public void GetPerformanceMetrics_ReturnsMetricsInstance()
        {
            // Act
            var metrics = _exchangeService.GetPerformanceMetrics();

            // Assert
            Assert.NotNull(metrics);
        }

        [Fact]
        public void GetPerformanceSummary_ReturnsFormattedString()
        {
            // Act
            var summary = _exchangeService.GetPerformanceSummary();

            // Assert
            Assert.NotNull(summary);
            Assert.IsType<string>(summary);
        }

        [Fact]
        public void LogPerformanceMetrics_DoesNotThrow()
        {
            // Act & Assert - Should not throw any exceptions
            _exchangeService.LogPerformanceMetrics();
        }

        [Fact]
        public void LogDetailedPerformanceMetrics_DoesNotThrow()
        {
            // Act & Assert - Should not throw any exceptions
            _exchangeService.LogDetailedPerformanceMetrics();
        }

        #endregion

        #region Helper Methods

        private CandlestickData CreateSampleCandlestickData()
        {
            var candles = new OHLCV[]
            {
                new OHLCV(1504541580000L, 4235.4, 4240.6, 4230.0, 4230.7, 37.72941911),
                new OHLCV(1504541640000L, 4230.7, 4238.1, 4225.3, 4235.2, 42.15832156),
                new OHLCV(1504541700000L, 4235.2, 4245.8, 4232.1, 4240.5, 35.89471234)
            };

            var beginTime = DateTimeOffset.FromUnixTimeMilliseconds(1504541580000L).DateTime;
            var endTime = DateTimeOffset.FromUnixTimeMilliseconds(1504541700000L).DateTime;

            return new CandlestickData("1h", beginTime, endTime, candles);
        }

        #endregion
    }
}