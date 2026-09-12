using System;
using System.Diagnostics;
using BotView.Models;
using BotView.Interfaces;

namespace BotView.Services
{
    /// <summary> Implementation of IDataProvider for UI data loading operations. </summary>
    public class DataProvider : IDataProvider
    {
        private IExchangeService? _exchangeService;

        /// <summary> Initializes a new instance of DataProvider. </summary>
        /// <param name="exchangeService">Exchange service for market data loading (optional)</param>
        public DataProvider(IExchangeService? exchangeService = null)
        {
            _exchangeService = exchangeService;
        }

        /// <summary> Sets the exchange service used for loading market data. </summary>
        public void SetExchangeService(IExchangeService exchangeService)
        {
            _exchangeService = exchangeService ?? throw new ArgumentNullException(nameof(exchangeService));
        }

        /// <summary> Loads candlestick data from the configured exchange service. </summary>
        public async Task<CandlestickData?> LoadDataAsync(string exchange, string symbol, string timeframe, int limit = 500)
        {
            try
            {
                if (_exchangeService == null)
                {
                    throw new InvalidOperationException("Exchange service is not configured.");
                }

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

        /// <summary> Loads generated demo candlestick data for the specified timeframe. </summary>
        public CandlestickData LoadDemoData(string timeframe)
        {
            int candleCount = timeframe switch
            {
                "1m" => 1440,
                "5m" => 288,
                "15m" => 96,
                "30m" => 48,
                "1h" => 24,
                "4h" => 42,
                "1d" => 30,
                "1w" => 12,
                _ => 30
            };

            return DemoDataGenerator.Generate(timeframe, candleCount);
        }

    }
}
