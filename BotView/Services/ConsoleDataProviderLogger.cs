using System;
using BotView.Interfaces;

namespace BotView.Services
{
    /// <summary>
    /// Console implementation of IDataProviderLogger for development and debugging
    /// </summary>
    public class ConsoleDataProviderLogger : IDataProviderLogger
    {
        private readonly string _prefix = "[DataProvider]";

        /// <summary>
        /// Logs successful data conversion
        /// </summary>
        /// <param name="candleCount">Number of candles converted</param>
        /// <param name="timeframe">Timeframe of the data</param>
        public void LogDataConversion(int candleCount, string timeframe)
        {
            Console.WriteLine($"{_prefix} Successfully converted {candleCount} candles for timeframe {timeframe}");
        }

        /// <summary>
        /// Logs data conversion error
        /// </summary>
        /// <param name="exception">Exception that occurred during conversion</param>
        /// <param name="context">Additional context information</param>
        public void LogConversionError(Exception exception, string context = "")
        {
            string contextInfo = string.IsNullOrEmpty(context) ? "" : $" Context: {context}";
            Console.WriteLine($"{_prefix} ERROR: Data conversion failed. {exception.Message}{contextInfo}");
            
            if (exception.InnerException != null)
            {
                Console.WriteLine($"{_prefix} Inner exception: {exception.InnerException.Message}");
            }
        }

        /// <summary>
        /// Logs data validation failure
        /// </summary>
        /// <param name="reason">Reason for validation failure</param>
        /// <param name="candleIndex">Index of problematic candle (if applicable)</param>
        public void LogValidationFailure(string reason, int? candleIndex = null)
        {
            string indexInfo = candleIndex.HasValue ? $" at index {candleIndex.Value}" : "";
            Console.WriteLine($"{_prefix} VALIDATION FAILED: {reason}{indexInfo}");
        }

        /// <summary>
        /// Logs general information
        /// </summary>
        /// <param name="message">Information message</param>
        public void LogInfo(string message)
        {
            Console.WriteLine($"{_prefix} INFO: {message}");
        }

        /// <summary>
        /// Logs warning message
        /// </summary>
        /// <param name="message">Warning message</param>
        public void LogWarning(string message)
        {
            Console.WriteLine($"{_prefix} WARNING: {message}");
        }
    }
}