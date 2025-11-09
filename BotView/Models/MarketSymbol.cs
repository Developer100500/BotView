namespace BotView.Models
{
    /// <summary>
    /// Model representing a trading pair symbol
    /// </summary>
    public class MarketSymbol
    {
        /// <summary>
        /// Full symbol name (e.g., "BTC/USDT")
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Base asset (e.g., "BTC" in "BTC/USDT")
        /// </summary>
        public string BaseAsset { get; set; } = string.Empty;

        /// <summary>
        /// Quote asset (e.g., "USDT" in "BTC/USDT")
        /// </summary>
        public string QuoteAsset { get; set; } = string.Empty;

        /// <summary>
        /// Whether this symbol is currently active for trading
        /// </summary>
        public bool IsActive { get; set; } = true;
    }
}