using BotView.Chart;

namespace BotView.Interfaces
{
    /// <summary>
    /// Interface for converting data from CCXT format to application format
    /// </summary>
    public interface IDataProvider
    {
        /// <summary>
        /// Converts CCXT OHLCV data list to CandlestickData format
        /// </summary>
        /// <param name="ccxtData">List of CCXT OHLCV objects</param>
        /// <param name="timeframe">Timeframe string (e.g., "1m", "5m", "1h", "1d")</param>
        /// <returns>CandlestickData object containing converted candles</returns>
        CandlestickData ConvertFromCCXT(System.Collections.Generic.List<ccxt.OHLCV> ccxtData, string timeframe);

        /// <summary>
        /// Converts CCXT OHLCV data array to CandlestickData format
        /// </summary>
        /// <param name="ccxtData">Array of CCXT candle data in format [timestamp, open, high, low, close, volume]</param>
        /// <param name="timeframe">Timeframe string (e.g., "1m", "5m", "1h", "1d")</param>
        /// <returns>CandlestickData object containing converted candles</returns>
        CandlestickData ConvertFromCCXT(object[][] ccxtData, string timeframe);

        /// <summary>
        /// Converts a single CCXT candle data array to OHLCV structure
        /// </summary>
        /// <param name="candleData">Single candle data in CCXT format [timestamp, open, high, low, close, volume]</param>
        /// <returns>OHLCV structure with converted data</returns>
        OHLCV ConvertCCXTCandle(object[] candleData);

        /// <summary>
        /// Validates CCXT data format and structure
        /// </summary>
        /// <param name="data">CCXT data array to validate</param>
        /// <returns>True if data is valid, false otherwise</returns>
        bool ValidateCCXTData(object[][] data);
    }
}