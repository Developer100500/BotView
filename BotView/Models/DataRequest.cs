namespace BotView.Models
{
    /// <summary>
    /// Model representing a request for market data
    /// </summary>
    public class DataRequest
    {
        /// <summary>
        /// Exchange name to request data from
        /// </summary>
        public string Exchange { get; set; } = string.Empty;

        /// <summary>
        /// Trading pair symbol (e.g., "BTC/USDT")
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Timeframe for the data (e.g., "1m", "5m", "1h", "1d")
        /// </summary>
        public string Timeframe { get; set; } = string.Empty;

        /// <summary>
        /// Maximum number of candles to retrieve
        /// </summary>
        public int Limit { get; set; } = 500;

        /// <summary>
        /// Optional start time for historical data
        /// </summary>
        public DateTime? Since { get; set; }
    }
}