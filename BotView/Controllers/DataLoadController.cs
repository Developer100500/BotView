using System.Diagnostics;
using BotView.Chart;
using BotView.Interfaces;
using BotView.Models;

namespace BotView.Controllers
{
    public class DataLoadController
    {
        private readonly IExchangeService _exchangeService;

        public DataLoadController(IExchangeService exchangeService)
        {
            _exchangeService = exchangeService;
        }

        public async Task<CandlestickData?> LoadDataAsync(string exchange, string symbol, string timeframe, int limit = 500)
        {
            try
            {
                var candlestickData = await _exchangeService.GetCandlestickDataAsync(exchange, symbol, timeframe, limit);
                Debug.WriteLine($"Successfully loaded {candlestickData.candles.Length} candles from {exchange} for {symbol} ({timeframe})");
                return candlestickData;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load real data: {ex.Message}");
                return null;
            }
        }

        public CandlestickData LoadDemoData(string timeframe)
        {
            int candleCount = timeframe switch
            {
                "1m" => 1440,
                "5m" => 288,
                "15m" => 96,
                "1h" => 24,
                "1d" => 30,
                "1w" => 12,
                _ => 30
            };

            return DemoDataGenerator.Generate(timeframe, candleCount);
        }
    }
}
