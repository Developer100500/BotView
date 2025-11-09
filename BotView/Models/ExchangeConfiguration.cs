namespace BotView.Models
{
    /// <summary>
    /// Configuration model for cryptocurrency exchanges
    /// </summary>
    public class ExchangeConfiguration
    {
        /// <summary>
        /// Internal exchange name (e.g., "binance")
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Display name for UI (e.g., "Binance")
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Whether this exchange is enabled
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// List of supported timeframes for this exchange
        /// </summary>
        public List<string> SupportedTimeframes { get; set; } = new List<string>();

        /// <summary>
        /// Additional exchange-specific settings
        /// </summary>
        public Dictionary<string, object> Settings { get; set; } = new Dictionary<string, object>();
    }
}