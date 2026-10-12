using BotView.Models;
using ccxt;

namespace BotView.Services;

public static class MarketMoversCalculator
{
    public static IReadOnlyList<MarketMover> Calculate(
        IEnumerable<MarketInterface> markets, IReadOnlyDictionary<string, Ticker> tickers)
    {
        var result = new List<MarketMover>();
        foreach (var market in markets.Where(m => m.spot == true && m.active != false &&
                     string.Equals(m.quote, "USDT", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(market.symbol) || !tickers.TryGetValue(market.symbol, out var ticker))
                continue;

            var last = ticker.last ?? ticker.close;
            if (last is not > 0 || !double.IsFinite(last.Value))
                continue;

            var change = ticker.percentage;
            if (!change.HasValue && ticker.open is > 0 && double.IsFinite(ticker.open.Value))
                change = (last.Value / ticker.open.Value - 1) * 100;

            if (change.HasValue && double.IsFinite(change.Value))
                result.Add(new MarketMover(market.symbol, change.Value));
        }
        return result.DistinctBy(m => m.Symbol, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
