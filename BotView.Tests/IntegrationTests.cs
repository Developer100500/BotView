using BotView.Services.Providers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using BotView.Services;
using BotView.Models;
using BotView.Exceptions;
using BotView.Interfaces;

namespace BotView.Tests
{
    /// <summary>
    /// Integration tests with real cryptocurrency exchanges
    /// These tests connect to actual exchange APIs and verify the complete data flow
    /// Note: These tests require internet connection and may be slower
    /// </summary>
    [Collection("Integration Tests")]
    [Trait("Category", "Integration")]
    public class IntegrationTests
    {
        private readonly IExchangeService _exchangeService;

        public IntegrationTests()
        {
            _exchangeService = new ExchangeService(CcxtMarketDataProvider.CreateDefaults(), null,
                cacheExpirationMinutes: 1, maxRetryAttempts: 3, baseRetryDelaySeconds: 1);
        }

        #region Connection Tests

        [Fact]
        public async Task TestConnectionAsync_Binance_ReturnsTrue()
        {
            // Act
            var result = await _exchangeService.TestConnectionAsync("binance");

            // Assert
            Assert.True(result, "Should be able to connect to Binance");
        }

        [Fact]
        public async Task TestConnectionAsync_Bybit_ReturnsTrue()
        {
            // Act
            var result = await _exchangeService.TestConnectionAsync("bybit");

            // Assert
            Assert.True(result, "Should be able to connect to Bybit");
        }

        [Fact]
        public async Task TestConnectionAsync_OKX_ReturnsTrue()
        {
            // Act
            var result = await _exchangeService.TestConnectionAsync("okx");

            // Assert
            Assert.True(result, "Should be able to connect to OKX");
        }

        [Fact]
        public async Task TestConnectionAsync_Kraken_ReturnsTrue()
        {
            // Act
            var result = await _exchangeService.TestConnectionAsync("kraken");

            // Assert
            Assert.True(result, "Should be able to connect to Kraken");
        }

        #endregion

        #region Data Retrieval Tests

        [Fact]
        public async Task GetCandlestickDataAsync_BinanceBTCUSDT_ReturnsValidData()
        {
            // Act
            var result = await _exchangeService.GetCandlestickDataAsync("binance", "BTC/USDT", "1h", 10);

            // Assert
            Assert.IsType<Candle[]>(result);
            Assert.True(result.Length > 0, "Should return at least one candle");
            Assert.True(result.Length <= 10, "Should not return more than requested limit");
            
            // Verify data integrity
            foreach (var candle in result)
            {
                Assert.True(candle.Timestamp > 0, "Timestamp should be positive");
                Assert.True(candle.High >= candle.Low, "High should be >= Low");
                Assert.True(candle.Open >= 0, "Open should be non-negative");
                Assert.True(candle.Close >= 0, "Close should be non-negative");
                Assert.True(candle.Volume >= 0, "Volume should be non-negative");
            }

            // Verify time ordering (candles should be in chronological order)
            for (int i = 1; i < result.Length; i++)
            {
                Assert.True(result[i].Timestamp >= result[i-1].Timestamp,
                    "Candles should be in chronological order");
            }
        }

        [Fact]
        public async Task GetCandlestickDataAsync_BybitETHUSDT_ReturnsValidData()
        {
            // Act
            var result = await _exchangeService.GetCandlestickDataAsync("bybit", "ETH/USDT", "1d", 5);

            // Assert
            Assert.IsType<Candle[]>(result);
            Assert.True(result.Length > 0, "Should return at least one candle");
            Assert.True(result.Length <= 5, "Should not return more than requested limit");
            
            // Verify basic data structure
            var firstCandle = result[0];
            Assert.True(firstCandle.Timestamp > 0);
            Assert.True(firstCandle.High >= firstCandle.Low);
            Assert.True(firstCandle.Volume >= 0);
        }

        [Fact]
        public async Task GetCandlestickDataAsync_OKXBTCUSDTSmallLimit_ReturnsCorrectAmount()
        {
            // Act
            var result = await _exchangeService.GetCandlestickDataAsync("okx", "BTC/USDT", "4h", 3);

            // Assert
            Assert.IsType<Candle[]>(result);
            Assert.True(result.Length > 0, "Should return at least one candle");
            Assert.True(result.Length <= 3, "Should respect the limit parameter");
        }

        [Fact]
        public async Task GetCandlestickDataAsync_KrakenBTCUSD_ReturnsValidData()
        {
            // Note: Kraken uses different symbol format (BTC/USD instead of BTC/USDT)
            // Act
            var result = await _exchangeService.GetCandlestickDataAsync("kraken", "BTC/USD", "1d", 7);

            // Assert
            Assert.IsType<Candle[]>(result);
            Assert.True(result.Length > 0, "Should return at least one candle");
            Assert.True(result.Length <= 7, "Should not return more than requested limit");
        }

        #endregion

        #region Symbol Availability Tests

        [Fact]
        public async Task GetAvailableSymbolsAsync_Binance_ReturnsPopularSymbols()
        {
            // Act
            var symbols = await _exchangeService.GetAvailableSymbolsAsync("binance");

            // Assert
            Assert.NotNull(symbols);
            Assert.True(symbols.Count > 0, "Should return at least some symbols");
            Assert.Contains("BTC/USDT", symbols);
            Assert.Contains("ETH/USDT", symbols);
        }

        [Fact]
        public async Task GetAvailableSymbolsAsync_Bybit_ReturnsPopularSymbols()
        {
            // Act
            var symbols = await _exchangeService.GetAvailableSymbolsAsync("bybit");

            // Assert
            Assert.NotNull(symbols);
            Assert.True(symbols.Count > 0, "Should return at least some symbols");
            Assert.Contains("BTC/USDT", symbols);
        }

        #endregion

        #region Error Handling Tests

        [Fact]
        public async Task GetCandlestickDataAsync_InvalidSymbol_ThrowsExchangeException()
        {
            // Act & Assert
            await Assert.ThrowsAsync<ExchangeException>(() => 
                _exchangeService.GetCandlestickDataAsync("binance", "INVALID/SYMBOL", "1h", 10));
        }

        [Fact]
        public async Task GetAvailableSymbolsAsync_InvalidExchange_ThrowsNotSupportedException()
        {
            // Act & Assert
            await Assert.ThrowsAsync<NotSupportedException>(() => 
                _exchangeService.GetAvailableSymbolsAsync("invalidexchange"));
        }

        #endregion

        #region Caching Integration Tests

        [Fact]
        public async Task GetCandlestickDataAsync_SameRequestTwice_SecondCallIsFaster()
        {
            // Arrange
            var exchange = "binance";
            var symbol = "BTC/USDT";
            var timeframe = "1h";
            var limit = 5;

            // Act
            var stopwatch1 = System.Diagnostics.Stopwatch.StartNew();
            var result1 = await _exchangeService.GetCandlestickDataAsync(exchange, symbol, timeframe, limit);
            stopwatch1.Stop();

            var stopwatch2 = System.Diagnostics.Stopwatch.StartNew();
            var result2 = await _exchangeService.GetCandlestickDataAsync(exchange, symbol, timeframe, limit);
            stopwatch2.Stop();

            // Assert
            Assert.Same(result1, result2);
            Assert.Equal(result1.Length, result2.Length);
            
            // Second call should be significantly faster due to caching
            // Allow some tolerance for timing variations
            Assert.True(stopwatch2.ElapsedMilliseconds < stopwatch1.ElapsedMilliseconds / 2 || 
                       stopwatch2.ElapsedMilliseconds < 50, 
                       $"Second call should be faster. First: {stopwatch1.ElapsedMilliseconds}ms, Second: {stopwatch2.ElapsedMilliseconds}ms");
        }

        #endregion

        #region Full Data Flow Tests

        [Fact]
        public async Task FullDataFlow_BinanceToChart_CompletesSuccessfully()
        {
            // This test verifies the complete data flow from API to chart-ready format
            
            // Act - Get data from exchange
            var candlestickData = await _exchangeService.GetCandlestickDataAsync("binance", "BTC/USDT", "1h", 20);

            // Assert - Verify data is ready for chart consumption
            Assert.IsType<Candle[]>(candlestickData);
            Assert.True(candlestickData.Length > 0);
            Assert.True(candlestickData[0].Timestamp <= candlestickData[^1].Timestamp);

            // Verify each candle can be used for chart rendering
            foreach (var candle in candlestickData)
            {
                // Test GetDateTime() method works correctly
                var dateTime = candle.GetDateTime();
                Assert.True(dateTime > DateTime.MinValue);
                Assert.True(dateTime < DateTime.UtcNow.AddDays(1)); // Should not be in the future

                // Verify Candle data integrity for chart rendering
                Assert.True(candle.High >= Math.Max(candle.Open, candle.Close),
                    "High should be >= max(open, close)");
                Assert.True(candle.Low <= Math.Min(candle.Open, candle.Close),
                    "Low should be <= min(open, close)");
            }

            // Verify time range consistency
            var firstCandleTime = candlestickData[0].GetDateTime();
            var lastCandleTime = candlestickData[candlestickData.Length - 1].GetDateTime();
            
            Assert.True(firstCandleTime <= lastCandleTime);
        }

        [Fact]
        public async Task FullDataFlow_MultipleExchanges_ConsistentFormat()
        {
            // This test verifies that data from different exchanges has consistent format
            
            // Act - Get data from multiple exchanges
            var binanceData = await _exchangeService.GetCandlestickDataAsync("binance", "BTC/USDT", "1d", 3);
            var bybitData = await _exchangeService.GetCandlestickDataAsync("bybit", "BTC/USDT", "1d", 3);

            // Assert - Both should have the same structure and format
            Assert.IsType<Candle[]>(binanceData);
            Assert.IsType<Candle[]>(bybitData);
            Assert.True(binanceData.Length > 0);
            Assert.True(bybitData.Length > 0);

            // Verify both use the same timestamp format and Candle structure
            var binanceCandle = binanceData[0];
            var bybitCandle = bybitData[0];

            Assert.True(binanceCandle.Timestamp > 0);
            Assert.True(bybitCandle.Timestamp > 0);
            
            // Both should have valid DateTime conversion
            var binanceDateTime = binanceCandle.GetDateTime();
            var bybitDateTime = bybitCandle.GetDateTime();
            
            Assert.True(binanceDateTime > DateTime.MinValue);
            Assert.True(bybitDateTime > DateTime.MinValue);
        }

        #endregion

        #region Performance Tests

        [Fact]
        public async Task GetCandlestickDataAsync_LargeDataset_CompletesInReasonableTime()
        {
            // Act
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await _exchangeService.GetCandlestickDataAsync("binance", "BTC/USDT", "1h", 500);
            stopwatch.Stop();

            // Assert
            Assert.True(result.Length > 0);
            Assert.True(result.Length <= 500);
            
            // Should complete within 30 seconds (generous timeout for network operations)
            Assert.True(stopwatch.ElapsedMilliseconds < 30000, 
                $"Large dataset retrieval took too long: {stopwatch.ElapsedMilliseconds}ms");
        }

        [Fact]
        public void GetPerformanceMetrics_AfterOperations_ReturnsValidMetrics()
        {
            // Act
            var metrics = _exchangeService.GetPerformanceMetrics();

            // Assert
            Assert.NotNull(metrics);
            
            // Test that detailed performance logging doesn't throw
            _exchangeService.LogDetailedPerformanceMetrics();
        }

        #endregion
    }
}
