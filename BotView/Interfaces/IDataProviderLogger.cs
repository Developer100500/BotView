using System;

namespace BotView.Interfaces
{
    /// <summary>
    /// Interface for logging DataProvider operations and errors
    /// </summary>
    public interface IDataProviderLogger
    {
        /// <summary>
        /// Logs successful data conversion
        /// </summary>
        /// <param name="candleCount">Number of candles converted</param>
        /// <param name="timeframe">Timeframe of the data</param>
        void LogDataConversion(int candleCount, string timeframe);

        /// <summary>
        /// Logs data conversion error
        /// </summary>
        /// <param name="exception">Exception that occurred during conversion</param>
        /// <param name="context">Additional context information</param>
        void LogConversionError(Exception exception, string context = "");

        /// <summary>
        /// Logs data validation failure
        /// </summary>
        /// <param name="reason">Reason for validation failure</param>
        /// <param name="candleIndex">Index of problematic candle (if applicable)</param>
        void LogValidationFailure(string reason, int? candleIndex = null);

        /// <summary>
        /// Logs general information
        /// </summary>
        /// <param name="message">Information message</param>
        void LogInfo(string message);

        /// <summary>
        /// Logs warning message
        /// </summary>
        /// <param name="message">Warning message</param>
        void LogWarning(string message);
    }
}