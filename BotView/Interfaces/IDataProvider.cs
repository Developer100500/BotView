using BotView.Models;

namespace BotView.Interfaces
{
    /// <summary> Interface for loading candlestick data used by the UI layer. </summary>
    public interface IDataProvider
    {
        /// <summary> Loads candlestick data from the configured exchange service. </summary>
        Task<Candle[]?> LoadDataAsync(string exchange, string symbol, string timeframe, int limit = 500);

        /// <summary> Loads generated demo candlestick data for the specified timeframe. </summary>
        Candle[] LoadDemoData(string timeframe);
    }
}
