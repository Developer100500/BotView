namespace BotView.Models
{
    public static class DemoDataGenerator
    {
        public static CandlestickData Generate(string timeframe, int candleCount = 30)
        {
            DateTime startTime = DateTime.Parse("2025/10/01 00:00:00");
            var demoCandles = new OHLCV[candleCount];
            Random random = new Random();
            double basePrice = 100.0;

            TimeSpan timeframeInterval = timeframe switch
            {
                "1m" => TimeSpan.FromMinutes(1),
                "5m" => TimeSpan.FromMinutes(5),
                "15m" => TimeSpan.FromMinutes(15),
                "1h" => TimeSpan.FromHours(1),
                "1d" => TimeSpan.FromDays(1),
                "1w" => TimeSpan.FromDays(7),
                _ => TimeSpan.FromDays(1)
            };

            for (int i = 0; i < demoCandles.Length; i++)
            {
                double open = basePrice + random.NextDouble() * 10 - 5;
                double close = open + random.NextDouble() * 8 - 4;
                double high = Math.Max(open, close) + random.NextDouble() * 3;
                double low = Math.Min(open, close) - random.NextDouble() * 3;
                double volume = 1000 + random.NextDouble() * 2000;

                DateTime candleTime = startTime.Add(TimeSpan.FromTicks(timeframeInterval.Ticks * i));
                long timestamp = ((DateTimeOffset)candleTime).ToUnixTimeMilliseconds();

                demoCandles[i] = new OHLCV(timestamp, open, high, low, close, volume);
                basePrice = close;
            }

            return new CandlestickData(
                timeframe: timeframe,
                beginDateTime: startTime,
                endDateTime: startTime.Add(TimeSpan.FromTicks(timeframeInterval.Ticks * candleCount)),
                candles: demoCandles
            );
        }
    }
}
