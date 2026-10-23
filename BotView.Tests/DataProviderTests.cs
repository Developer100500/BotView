using BotView.Services.Providers;
using System;
using System.Collections.Generic;
using Xunit;
using BotView.Services;
using BotView.Exceptions;
using BotView.Models;

namespace BotView.Tests
{
    /// <summary> Unit tests for CcxtCandleConverter. </summary>
    public class DataProviderTests
    {
        [Fact]
        public void ConvertFromCCXT_ObjectArray_ValidData_ReturnsCandleArray()
        {
            var ccxtData = new object[][]
            {
                new object[] { 1504541580000L, 4235.4, 4240.6, 4230.0, 4230.7, 37.72941911 },
                new object[] { 1504541640000L, 4230.7, 4238.1, 4225.3, 4235.2, 42.15832156 }
            };

            var result = CcxtCandleConverter.ConvertFromCCXT(ccxtData, "1m");

            Assert.IsType<Candle[]>(result);
            Assert.Equal(2, result.Length);
        }

        [Fact]
        public void ConvertFromCCXT_ObjectArray_NullData_ThrowsDataConversionException()
        {
            object[][] ccxtData = null;

            var exception = Assert.Throws<DataConversionException>(() =>
                CcxtCandleConverter.ConvertFromCCXT(ccxtData, "1m"));

            Assert.Equal(DataConversionErrorType.EmptyData, exception.ErrorType);
        }

        [Fact]
        public void ConvertFromCCXT_List_ValidData_ReturnsCandleArray()
        {
            var ccxtData = new List<ccxt.OHLCV>
            {
                new ccxt.OHLCV { timestamp = 1504541580000L, open = 4235.4, high = 4240.6, low = 4230.0, close = 4230.7, volume = 37.72941911 }
            };

            var result = CcxtCandleConverter.ConvertFromCCXT(ccxtData, "1m");

            Assert.Single(result);
            Assert.Equal(1504541580000L, result[0].Timestamp);
        }

        [Fact]
        public void ConvertCCXTCandle_ValidData_ReturnsOHLCV()
        {
            var candleData = new object[] { 1504541580000L, 4235.4, 4240.6, 4230.0, 4230.7, 37.72941911 };

            var result = CcxtCandleConverter.ConvertCCXTCandle(candleData);

            Assert.Equal(4235.4, result.Open);
            Assert.Equal(4240.6, result.High);
            Assert.Equal(4230.0, result.Low);
            Assert.Equal(4230.7, result.Close);
            Assert.Equal(37.72941911, result.Volume);
        }

        [Fact]
        public void ConvertCCXTCandle_NegativeVolume_ThrowsDataConversionException()
        {
            var candleData = new object[] { 1504541580000L, 4235.4, 4240.6, 4230.0, 4230.7, -1.0 };

            var exception = Assert.Throws<DataConversionException>(() =>
                CcxtCandleConverter.ConvertCCXTCandle(candleData));

            Assert.Equal(DataConversionErrorType.InvalidVolume, exception.ErrorType);
        }

        [Fact]
        public void ValidateCCXTData_ValidData_ReturnsTrue()
        {
            var data = new object[][]
            {
                new object[] { 1504541580000L, 4235.4, 4240.6, 4230.0, 4230.7, 37.72941911 }
            };

            var result = CcxtCandleConverter.ValidateCCXTData(data);

            Assert.True(result);
        }

        [Fact]
        public void ValidateCCXTData_InvalidData_ReturnsFalse()
        {
            var data = new object[][]
            {
                new object[] { 0L, 4235.4, 4240.6, 4230.0, 4230.7, 37.72941911 }
            };

            var result = CcxtCandleConverter.ValidateCCXTData(data);

            Assert.False(result);
        }

        [Fact]
        public void ConvertCCXTCandle_DateTimeConversion_WorksCorrectly()
        {
            var timestamp = 1504541580000L;
            var candle = CcxtCandleConverter.ConvertCCXTCandle(
                new object[] { timestamp, 4235.4, 4240.6, 4230.0, 4230.7, 37.72941911 });

            var expectedDateTime = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).DateTime;
            Assert.Equal(expectedDateTime, candle.GetDateTime());
        }
    }
}
