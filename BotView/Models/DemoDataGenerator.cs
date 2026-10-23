using BotView.Configuration;

namespace BotView.Models
{
    public static class DemoDataGenerator
    {
        public static Candle[] Generate(string timeframe, int candleCount = 30)
        {
            var startTime = new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero);
            var demoCandles = new Candle[candleCount];
            Random random = new Random();
            double basePrice = 100.0;

            TimeSpan timeframeInterval = MarketCatalog.TryGetTimeframe(timeframe, out var definition)
                ? definition!.Duration
                : MarketCatalog.GetTimeframe(MarketCatalog.DefaultTimeframeId).Duration;

            for (int i = 0; i < demoCandles.Length; i++)
            {
                double open = basePrice + random.NextDouble() * 10 - 5;
                double close = open + random.NextDouble() * 8 - 4;
                double high = Math.Max(open, close) + random.NextDouble() * 3;
                double low = Math.Min(open, close) - random.NextDouble() * 3;
                double volume = 1000 + random.NextDouble() * 2000;

                var candleTime = startTime.Add(TimeSpan.FromTicks(timeframeInterval.Ticks * i));
                long timestamp = candleTime.ToUnixTimeMilliseconds();

                demoCandles[i] = new Candle { Timestamp = timestamp, Open = open, High = high, Low = low, Close = close, Volume = volume };
                basePrice = close;
            }

            return demoCandles;
        }
    }
}
