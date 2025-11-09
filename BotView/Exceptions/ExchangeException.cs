using System;

namespace BotView.Exceptions
{
    /// <summary>
    /// Exception thrown when exchange operations fail
    /// </summary>
    public class ExchangeException : Exception
    {
        /// <summary>
        /// Name of the exchange where the error occurred
        /// </summary>
        public string ExchangeName { get; }

        /// <summary>
        /// Type of exchange error
        /// </summary>
        public ExchangeErrorType ErrorType { get; }

        /// <summary>
        /// Error code from the exchange API (if available)
        /// </summary>
        public string? ErrorCode { get; }

        /// <summary>
        /// Initializes a new instance of ExchangeException
        /// </summary>
        /// <param name="exchangeName">Name of the exchange</param>
        /// <param name="message">Error message</param>
        /// <param name="errorType">Type of exchange error</param>
        public ExchangeException(string exchangeName, string message, ExchangeErrorType errorType) 
            : base(message)
        {
            ExchangeName = exchangeName;
            ErrorType = errorType;
        }

        /// <summary>
        /// Initializes a new instance of ExchangeException with error code
        /// </summary>
        /// <param name="exchangeName">Name of the exchange</param>
        /// <param name="message">Error message</param>
        /// <param name="errorType">Type of exchange error</param>
        /// <param name="errorCode">Error code from exchange API</param>
        public ExchangeException(string exchangeName, string message, ExchangeErrorType errorType, string errorCode) 
            : base(message)
        {
            ExchangeName = exchangeName;
            ErrorType = errorType;
            ErrorCode = errorCode;
        }

        /// <summary>
        /// Initializes a new instance of ExchangeException with inner exception
        /// </summary>
        /// <param name="exchangeName">Name of the exchange</param>
        /// <param name="message">Error message</param>
        /// <param name="errorType">Type of exchange error</param>
        /// <param name="innerException">Inner exception that caused this error</param>
        public ExchangeException(string exchangeName, string message, ExchangeErrorType errorType, Exception innerException) 
            : base(message, innerException)
        {
            ExchangeName = exchangeName;
            ErrorType = errorType;
        }

        /// <summary>
        /// Initializes a new instance of ExchangeException with error code and inner exception
        /// </summary>
        /// <param name="exchangeName">Name of the exchange</param>
        /// <param name="message">Error message</param>
        /// <param name="errorType">Type of exchange error</param>
        /// <param name="errorCode">Error code from exchange API</param>
        /// <param name="innerException">Inner exception that caused this error</param>
        public ExchangeException(string exchangeName, string message, ExchangeErrorType errorType, string errorCode, Exception innerException) 
            : base(message, innerException)
        {
            ExchangeName = exchangeName;
            ErrorType = errorType;
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// Types of exchange errors
    /// </summary>
    public enum ExchangeErrorType
    {
        /// <summary>
        /// Network connection error
        /// </summary>
        NetworkError,

        /// <summary>
        /// Exchange API error (invalid request, rate limit, etc.)
        /// </summary>
        ApiError,

        /// <summary>
        /// Data processing error
        /// </summary>
        DataError,

        /// <summary>
        /// Configuration error (invalid exchange settings)
        /// </summary>
        ConfigurationError,

        /// <summary>
        /// Rate limit exceeded
        /// </summary>
        RateLimitError,

        /// <summary>
        /// Authentication error (API keys, permissions)
        /// </summary>
        AuthenticationError,

        /// <summary>
        /// Symbol not found or not supported
        /// </summary>
        SymbolNotFound,

        /// <summary>
        /// Timeframe not supported by exchange
        /// </summary>
        TimeframeNotSupported
    }
}