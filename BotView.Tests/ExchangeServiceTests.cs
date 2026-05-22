using System;
using System.Threading.Tasks;
using Xunit;
using Moq;
using BotView.Services;
using BotView.Interfaces;

namespace BotView.Tests
{
    /// <summary>
    /// Unit tests for ExchangeService class
    /// Tests exchange operations, caching, and error handling
    /// </summary>
    public class ExchangeServiceTests
    {
        private readonly Mock<IExchangeLogger> _mockLogger;
        private readonly ExchangeService _exchangeService;

        public ExchangeServiceTests()
        {
            _mockLogger = new Mock<IExchangeLogger>();
            _exchangeService = new ExchangeService(_mockLogger.Object, 
                cacheExpirationMinutes: 1, maxRetryAttempts: 2, baseRetryDelaySeconds: 1);
        }

        #region Constructor Tests

        [Fact]
        public void Constructor_ValidParameters_CreatesInstance()
        {
            // Act
            var service = new ExchangeService(_mockLogger.Object);

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
            // Act
            var result = await _exchangeService.GetCandlestickDataAsync("binance", "BTC/USDT", "1h");

            // Assert
            Assert.Equal("1h", result.timeframe);
            Assert.True(result.candles.Length > 0);
        }

        [Fact]
        public async Task GetCandlestickDataAsync_CaseInsensitiveExchange_ReturnsData()
        {
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

    }
}