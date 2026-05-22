using BotView.Chart;

namespace BotView.Interfaces
{
    /// <summary> Interface for loading candlestick data used by the UI layer. </summary>
    public interface IDataProvider
    {
        /// <summary> Loads candlestick data from the configured exchange service. </summary>
        Task<CandlestickData?> LoadDataAsync(string exchange, string symbol, string timeframe, int limit = 500);

        /// <summary> Loads generated demo candlestick data for the specified timeframe. </summary>
        CandlestickData LoadDemoData(string timeframe);
    }
}