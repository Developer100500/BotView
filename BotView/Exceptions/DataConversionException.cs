using System;

namespace BotView.Exceptions
{
    /// <summary>
    /// Exception thrown when data conversion from CCXT format fails
    /// </summary>
    public class DataConversionException : Exception
    {
        /// <summary>
        /// Type of data conversion error
        /// </summary>
        public DataConversionErrorType ErrorType { get; }

        /// <summary>
        /// Index of the problematic candle data (if applicable)
        /// </summary>
        public int? CandleIndex { get; }

        /// <summary>
        /// Initializes a new instance of DataConversionException
        /// </summary>
        /// <param name="message">Error message</param>
        /// <param name="errorType">Type of conversion error</param>
        public DataConversionException(string message, DataConversionErrorType errorType) 
            : base(message)
        {
            ErrorType = errorType;
        }

        /// <summary>
        /// Initializes a new instance of DataConversionException with candle index
        /// </summary>
        /// <param name="message">Error message</param>
        /// <param name="errorType">Type of conversion error</param>
        /// <param name="candleIndex">Index of the problematic candle</param>
        public DataConversionException(string message, DataConversionErrorType errorType, int candleIndex) 
            : base(message)
        {
            ErrorType = errorType;
            CandleIndex = candleIndex;
        }

        /// <summary>
        /// Initializes a new instance of DataConversionException with inner exception
        /// </summary>
        /// <param name="message">Error message</param>
        /// <param name="errorType">Type of conversion error</param>
        /// <param name="innerException">Inner exception that caused this error</param>
        public DataConversionException(string message, DataConversionErrorType errorType, Exception innerException) 
            : base(message, innerException)
        {
            ErrorType = errorType;
        }

        /// <summary>
        /// Initializes a new instance of DataConversionException with candle index and inner exception
        /// </summary>
        /// <param name="message">Error message</param>
        /// <param name="errorType">Type of conversion error</param>
        /// <param name="candleIndex">Index of the problematic candle</param>
        /// <param name="innerException">Inner exception that caused this error</param>
        public DataConversionException(string message, DataConversionErrorType errorType, int candleIndex, Exception innerException) 
            : base(message, innerException)
        {
            ErrorType = errorType;
            CandleIndex = candleIndex;
        }
    }

    /// <summary>
    /// Types of data conversion errors
    /// </summary>
    public enum DataConversionErrorType
    {
        /// <summary>
        /// Invalid data format or structure
        /// </summary>
        InvalidFormat,

        /// <summary>
        /// Data type conversion failed
        /// </summary>
        TypeConversion,

        /// <summary>
        /// Numeric overflow during conversion
        /// </summary>
        NumericOverflow,

        /// <summary>
        /// Invalid price data (negative prices, high < low, etc.)
        /// </summary>
        InvalidPriceData,

        /// <summary>
        /// Invalid timestamp data
        /// </summary>
        InvalidTimestamp,

        /// <summary>
        /// Invalid volume data
        /// </summary>
        InvalidVolume,

        /// <summary>
        /// Empty or null data
        /// </summary>
        EmptyData
    }
}