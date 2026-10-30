using System;
using System.Diagnostics;
using BotView.Models;
using BotView.Configuration;

namespace BotView.Services.MarketData
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
        public async Task<Candle[]?> LoadDataAsync(string exchange, string symbol, string timeframe, int limit = 500)
        {
            try
            {
                if (_exchangeService == null)
                {
                    throw new InvalidOperationException("Exchange service is not configured.");
                }

                var candles = await _exchangeService.GetCandlestickDataAsync(exchange, symbol, timeframe, limit);
                Debug.WriteLine($"Successfully loaded {candles.Length} candles from {exchange} for {symbol} ({timeframe})");
                return candles;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load real data: {ex.Message}");
                return null;
            }
        }

        /// <summary> Loads generated demo candlestick data for the specified timeframe. </summary>
        public Candle[] LoadDemoData(string timeframe)
        {
            int candleCount = MarketCatalog.TryGetTimeframe(timeframe, out var definition)
                ? definition!.DemoCandleCount
                : MarketCatalog.GetTimeframe(MarketCatalog.DefaultTimeframeId).DemoCandleCount;

            return DemoDataGenerator.Generate(timeframe, candleCount);
        }

    }
}
