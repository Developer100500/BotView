using System;
using System.Linq;
using BotView.Chart;
using BotView.Interfaces;
using BotView.Exceptions;

namespace BotView.Services
{
    /// <summary>
    /// Implementation of IDataProvider for converting CCXT data to application format
    /// </summary>
    public class DataProvider : IDataProvider
    {
        private readonly IDataProviderLogger _logger;

        /// <summary>
        /// Initializes a new instance of DataProvider
        /// </summary>
        /// <param name="logger">Logger for data conversion operations (optional)</param>
        public DataProvider(IDataProviderLogger? logger = null)
        {
            _logger = logger ?? new ConsoleDataProviderLogger();
        }
        /// <summary>
        /// Converts CCXT OHLCV data list to CandlestickData format
        /// </summary>
        /// <param name="ccxtData">List of CCXT OHLCV objects</param>
        /// <param name="timeframe">Timeframe string (e.g., "1m", "5m", "1h", "1d")</param>
        /// <returns>CandlestickData object containing converted candles</returns>
        /// <exception cref="DataConversionException">Thrown when CCXT data conversion fails</exception>
        /// <exception cref="ArgumentNullException">Thrown when ccxtData or timeframe is null</exception>
        public CandlestickData ConvertFromCCXT(System.Collections.Generic.List<ccxt.OHLCV> ccxtData, string timeframe)
        {
            try
            {
                _logger.LogInfo($"Starting conversion of CCXT OHLCV list for timeframe {timeframe}");

                if (ccxtData == null)
                {
                    _logger.LogConversionError(new ArgumentNullException(nameof(ccxtData)), "CCXT data is null");
                    throw new DataConversionException("CCXT data cannot be null", DataConversionErrorType.EmptyData);
                }
                
                if (string.IsNullOrWhiteSpace(timeframe))
                {
                    _logger.LogConversionError(new ArgumentNullException(nameof(timeframe)), "Timeframe is null or empty");
                    throw new DataConversionException("Timeframe cannot be null or empty", DataConversionErrorType.InvalidFormat);
                }

                // Convert CCXT OHLCV list to object[][] format for existing validation
                var objectArray = ccxtData.Select(ohlcv => new object[] 
                { 
                    ohlcv.timestamp, 
                    ohlcv.open, 
                    ohlcv.high, 
                    ohlcv.low, 
                    ohlcv.close, 
                    ohlcv.volume 
                }).ToArray();

                // Use existing conversion method
                return ConvertFromCCXT(objectArray, timeframe);
            }
            catch (DataConversionException)
            {
                // Re-throw DataConversionException as-is
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogConversionError(ex, "Unexpected error during CCXT OHLCV list conversion");
                throw new DataConversionException($"Unexpected error during data conversion: {ex.Message}", 
                    DataConversionErrorType.InvalidFormat, ex);
            }
        }

        /// <summary>
        /// Converts CCXT OHLCV data array to CandlestickData format
        /// </summary>
        /// <param name="ccxtData">Array of CCXT candle data in format [timestamp, open, high, low, close, volume]</param>
        /// <param name="timeframe">Timeframe string (e.g., "1m", "5m", "1h", "1d")</param>
        /// <returns>CandlestickData object containing converted candles</returns>
        /// <exception cref="DataConversionException">Thrown when CCXT data conversion fails</exception>
        /// <exception cref="ArgumentNullException">Thrown when ccxtData or timeframe is null</exception>
        public CandlestickData ConvertFromCCXT(object[][] ccxtData, string timeframe)
        {
            try
            {
                _logger.LogInfo($"Starting conversion of CCXT data for timeframe {timeframe}");

                if (ccxtData == null)
                {
                    _logger.LogConversionError(new ArgumentNullException(nameof(ccxtData)), "CCXT data is null");
                    throw new DataConversionException("CCXT data cannot be null", DataConversionErrorType.EmptyData);
                }
                
                if (string.IsNullOrWhiteSpace(timeframe))
                {
                    _logger.LogConversionError(new ArgumentNullException(nameof(timeframe)), "Timeframe is null or empty");
                    throw new DataConversionException("Timeframe cannot be null or empty", DataConversionErrorType.InvalidFormat);
                }

                if (!ValidateCCXTData(ccxtData))
                {
                    _logger.LogValidationFailure("CCXT data format validation failed");
                    throw new DataConversionException("Invalid CCXT data format", DataConversionErrorType.InvalidFormat);
                }

                // Convert each CCXT candle to OHLCV structure with error handling
                var candles = new OHLCV[ccxtData.Length];
                for (int i = 0; i < ccxtData.Length; i++)
                {
                    try
                    {
                        candles[i] = ConvertCCXTCandle(ccxtData[i]);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogConversionError(ex, $"Failed to convert candle at index {i}");
                        throw new DataConversionException($"Failed to convert candle at index {i}: {ex.Message}", 
                            DataConversionErrorType.TypeConversion, i, ex);
                    }
                }

                if (candles.Length == 0)
                {
                    _logger.LogWarning("No candles to convert, returning empty data");
                    DateTime currentTime = DateTime.UtcNow;
                    return new CandlestickData(timeframe, currentTime, currentTime, new OHLCV[0]);
                }

                // Calculate begin and end times from the converted candles
                var beginTime = candles[0].GetDateTime();
                var endTime = candles[candles.Length - 1].GetDateTime();

                _logger.LogDataConversion(candles.Length, timeframe);
                return new CandlestickData(timeframe, beginTime, endTime, candles);
            }
            catch (DataConversionException)
            {
                // Re-throw DataConversionException as-is
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogConversionError(ex, "Unexpected error during CCXT data conversion");
                throw new DataConversionException($"Unexpected error during data conversion: {ex.Message}", 
                    DataConversionErrorType.InvalidFormat, ex);
            }
        }

        /// <summary>
        /// Converts a single CCXT candle data array to OHLCV structure
        /// </summary>
        /// <param name="candleData">Single candle data in CCXT format [timestamp, open, high, low, close, volume]</param>
        /// <returns>OHLCV structure with converted data</returns>
        /// <exception cref="DataConversionException">Thrown when candle data conversion fails</exception>
        public OHLCV ConvertCCXTCandle(object[] candleData)
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
                // CCXT format: [timestamp, open, high, low, close, volume]
                var timestamp = Convert.ToInt64(candleData[0]);
                var open = Convert.ToDouble(candleData[1]);
                var high = Convert.ToDouble(candleData[2]);
                var low = Convert.ToDouble(candleData[3]);
                var close = Convert.ToDouble(candleData[4]);
                var volume = Convert.ToDouble(candleData[5]);

                // Validate the converted values
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
                // Re-throw DataConversionException as-is
                throw;
            }
            catch (Exception ex)
            {
                throw new DataConversionException($"Unexpected error during candle conversion: {ex.Message}", 
                    DataConversionErrorType.InvalidFormat, ex);
            }
        }

        /// <summary>
        /// Validates CCXT data format and structure
        /// </summary>
        /// <param name="data">CCXT data array to validate</param>
        /// <returns>True if data is valid, false otherwise</returns>
        public bool ValidateCCXTData(object[][] data)
        {
            if (data == null)
            {
                _logger.LogValidationFailure("Data array is null");
                return false;
            }

            // Check if all candles have the correct format
            for (int i = 0; i < data.Length; i++)
            {
                var candle = data[i];
                if (candle == null)
                {
                    _logger.LogValidationFailure("Candle data is null", i);
                    return false;
                }

                if (candle.Length < 6)
                {
                    _logger.LogValidationFailure($"Candle has insufficient data elements: {candle.Length}", i);
                    return false;
                }

                if (!IsValidCandleData(candle))
                {
                    _logger.LogValidationFailure("Candle data validation failed", i);
                    return false;
                }
            }

            _logger.LogInfo($"Successfully validated {data.Length} candles");
            return true;
        }

        /// <summary>
        /// Validates individual candle data without throwing exceptions
        /// </summary>
        /// <param name="candleData">Single candle data to validate</param>
        /// <returns>True if candle data is valid, false otherwise</returns>
        private bool IsValidCandleData(object[] candleData)
        {
            try
            {
                // Try to convert each field to ensure they are valid numbers
                var timestamp = Convert.ToInt64(candleData[0]);
                var open = Convert.ToDouble(candleData[1]);
                var high = Convert.ToDouble(candleData[2]);
                var low = Convert.ToDouble(candleData[3]);
                var close = Convert.ToDouble(candleData[4]);
                var volume = Convert.ToDouble(candleData[5]);

                // Basic validation checks
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