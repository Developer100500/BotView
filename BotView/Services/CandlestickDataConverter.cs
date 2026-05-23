using System;
using System.Collections.Generic;
using System.Linq;
using BotView.Models;
using BotView.Exceptions;

namespace BotView.Services
{
    /// <summary> Pure conversion from CCXT OHLCV shapes to application candlestick types. </summary>
    public static class CandlestickDataConverter
    {
        /// <summary> Converts CCXT OHLCV list to CandlestickData format. </summary>
        public static CandlestickData ConvertFromCCXT(List<ccxt.OHLCV> ccxtData, string timeframe)
        {
            try
            {
                if (ccxtData == null || ccxtData.Count == 0)
                {
                    throw new DataConversionException("CCXT data cannot be null", DataConversionErrorType.EmptyData);
                }

                if (string.IsNullOrWhiteSpace(timeframe))
                {
                    throw new DataConversionException("Timeframe cannot be null or empty", DataConversionErrorType.InvalidFormat);
                }

                var objectArray = ccxtData.Select(ohlcv => new object[]
                {
                    ohlcv.timestamp,
                    ohlcv.open,
                    ohlcv.high,
                    ohlcv.low,
                    ohlcv.close,
                    ohlcv.volume
                }).ToArray();

                return ConvertFromCCXT(objectArray, timeframe);
            }
            catch (DataConversionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new DataConversionException($"Unexpected error during data conversion: {ex.Message}",
                    DataConversionErrorType.InvalidFormat, ex);
            }
        }

        /// <summary> Converts CCXT OHLCV array to CandlestickData format. </summary>
        public static CandlestickData ConvertFromCCXT(object[][] ccxtData, string timeframe)
        {
            try
            {
                if (ccxtData == null)
                {
                    throw new DataConversionException("CCXT data cannot be null", DataConversionErrorType.EmptyData);
                }

                if (string.IsNullOrWhiteSpace(timeframe))
                {
                    throw new DataConversionException("Timeframe cannot be null or empty", DataConversionErrorType.InvalidFormat);
                }

                if (!ValidateCCXTData(ccxtData))
                {
                    throw new DataConversionException("Invalid CCXT data format", DataConversionErrorType.InvalidFormat);
                }

                var candles = new OHLCV[ccxtData.Length];
                for (int i = 0; i < ccxtData.Length; i++)
                {
                    try
                    {
                        candles[i] = ConvertCCXTCandle(ccxtData[i]);
                    }
                    catch (Exception ex)
                    {
                        throw new DataConversionException($"Failed to convert candle at index {i}: {ex.Message}",
                            DataConversionErrorType.TypeConversion, i, ex);
                    }
                }

                if (candles.Length == 0)
                {
                    DateTime currentTime = DateTime.UtcNow;
                    return new CandlestickData(timeframe, currentTime, currentTime, new OHLCV[0]);
                }

                var beginTime = candles[0].GetDateTime();
                var endTime = candles[candles.Length - 1].GetDateTime();

                return new CandlestickData(timeframe, beginTime, endTime, candles);
            }
            catch (DataConversionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new DataConversionException($"Unexpected error during data conversion: {ex.Message}",
                    DataConversionErrorType.InvalidFormat, ex);
            }
        }

        /// <summary> Converts a single CCXT candle row to OHLCV. </summary>
        public static OHLCV ConvertCCXTCandle(object[] candleData)
        {
            if (candleData == null)
            {
                throw new DataConversionException("Candle data cannot be null", DataConversionErrorType.EmptyData);
            }

            if (candleData.Length < 6)
            {
                throw new DataConversionException($"Invalid candle data format. Expected 6 elements, got {candleData.Length}",
                    DataConversionErrorType.InvalidFormat);
            }

            try
            {
                var timestamp = Convert.ToInt64(candleData[0]);
                var open = Convert.ToDouble(candleData[1]);
                var high = Convert.ToDouble(candleData[2]);
                var low = Convert.ToDouble(candleData[3]);
                var close = Convert.ToDouble(candleData[4]);
                var volume = Convert.ToDouble(candleData[5]);

                if (timestamp <= 0)
                {
                    throw new DataConversionException("Invalid timestamp: must be positive", DataConversionErrorType.InvalidTimestamp);
                }

                if (high < low)
                {
                    throw new DataConversionException("Invalid price data: high price cannot be less than low price",
                        DataConversionErrorType.InvalidPriceData);
                }

                if (open < 0 || high < 0 || low < 0 || close < 0)
                {
                    throw new DataConversionException("Invalid price data: prices cannot be negative",
                        DataConversionErrorType.InvalidPriceData);
                }

                if (volume < 0)
                {
                    throw new DataConversionException("Invalid volume data: volume cannot be negative",
                        DataConversionErrorType.InvalidVolume);
                }

                return new OHLCV(timestamp, open, high, low, close, volume);
            }
            catch (InvalidCastException ex)
            {
                throw new DataConversionException("Failed to convert candle data: invalid data types",
                    DataConversionErrorType.TypeConversion, ex);
            }
            catch (OverflowException ex)
            {
                throw new DataConversionException("Failed to convert candle data: numeric overflow",
                    DataConversionErrorType.NumericOverflow, ex);
            }
            catch (DataConversionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new DataConversionException($"Unexpected error during candle conversion: {ex.Message}",
                    DataConversionErrorType.InvalidFormat, ex);
            }
        }

        /// <summary> Returns whether CCXT rows are structurally valid without throwing. </summary>
        public static bool ValidateCCXTData(object[][] data)
        {
            if (data == null)
            {
                return false;
            }

            for (int i = 0; i < data.Length; i++)
            {
                var candle = data[i];
                if (candle == null)
                {
                    return false;
                }

                if (candle.Length < 6)
                {
                    return false;
                }

                if (!IsValidCandleData(candle))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidCandleData(object[] candleData)
        {
            try
            {
                var timestamp = Convert.ToInt64(candleData[0]);
                var open = Convert.ToDouble(candleData[1]);
                var high = Convert.ToDouble(candleData[2]);
                var low = Convert.ToDouble(candleData[3]);
                var close = Convert.ToDouble(candleData[4]);
                var volume = Convert.ToDouble(candleData[5]);

                return timestamp > 0 &&
                       high >= low &&
                       open >= 0 && high >= 0 && low >= 0 && close >= 0 &&
                       volume >= 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
