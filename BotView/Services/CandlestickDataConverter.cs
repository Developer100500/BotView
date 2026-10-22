using System;
using System.Collections.Generic;
using System.Linq;
using BotView.Models;
using BotView.Exceptions;

namespace BotView.Services
{
    /// <summary>Validates CCXT candle batches and adapts legacy object rows to the same CCXT type.</summary>
    public static class CandlestickDataConverter
    {
        /// <summary>Copies a validated CCXT batch to an array, preserving the existing fallback for missing numeric values.</summary>
        public static OHLCV[] ConvertFromCCXT(List<ccxt.OHLCV> ccxtData, string timeframe)
        {
			if (ccxtData == null || ccxtData.Count == 0)
			{
				throw new DataConversionException("CCXT data cannot be null", DataConversionErrorType.EmptyData);
			}

			if (string.IsNullOrWhiteSpace(timeframe))
			{
				throw new DataConversionException("Timeframe cannot be null or empty", DataConversionErrorType.InvalidFormat);
			}

			try
            {
				var candles = new OHLCV[ccxtData.Count];

				for (int i = 0; i < ccxtData.Count; i++)
				{
					var source = ccxtData[i];

					long timestamp = source.timestamp.GetValueOrDefault();
					double open = source.open.GetValueOrDefault();
					double high = source.high.GetValueOrDefault();
					double low = source.low.GetValueOrDefault();
					double close = source.close.GetValueOrDefault();
					double volume = source.volume.GetValueOrDefault();

					bool valid =
						timestamp > 0 &&
						high >= low &&
						open >= 0 && high >= 0 &&
						low >= 0 && close >= 0 &&
						volume >= 0;

					if (!valid)
						throw new DataConversionException(
							$"Invalid candle data at index {i}", DataConversionErrorType.InvalidFormat, i
					    );

					candles[i] = new OHLCV { timestamp = timestamp, open = open, high = high, low = low, close = close, volume = volume };
				}

				//ValidateEndpointTimestamps(candles);
				return candles;
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

        /// <summary>Converts legacy CCXT object rows to typed CCXT candles.</summary>
        public static OHLCV[] ConvertFromCCXT(object[][] ccxtData, string timeframe)
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

                ValidateEndpointTimestamps(candles);
                return candles;
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

        // Keep the timestamp validation previously performed when constructing batch time bounds.
        private static void ValidateEndpointTimestamps(OHLCV[] candles)
        {
            if (candles.Length == 0)
                return;
            _ = candles[0].GetDateTime();
            _ = candles[^1].GetDateTime();
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

                return new OHLCV { timestamp = timestamp, open = open, high = high, low = low, close = close, volume = volume };
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
