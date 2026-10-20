using BotView.Models;

namespace BotView.Services;

public static class OrderBookLadder
{
    public static IReadOnlyList<OrderBookRow> Build(OrderBookSnapshot book, int multiplier, decimal center, int count)
    {
        if (multiplier <= 0 || count <= 0 || book.Market.TickSize <= 0) return [];
        var step = book.Market.TickSize * multiplier;
        var bids = Group(book.Bids, step, false);
        var asks = Group(book.Asks, step, true);
        var bestBid = bids.Count > 0 ? bids.Keys.Max() : (decimal?)null;
        var bestAsk = asks.Count > 0 ? asks.Keys.Min() : (decimal?)null;
        var bottomBid = book.Bids.Count > 0 ? book.Bids.Min(l => l.Price) : (decimal?)null;
        var topAsk = book.Asks.Count > 0 ? book.Asks.Max(l => l.Price) : (decimal?)null;
        var top = decimal.Floor(center / step) * step + (count / 2) * step;
        var rows = new List<OrderBookRow>(count);
        for (var i = 0; i < count; i++)
        {
            var price = top - i * step;
            if (price <= 0) continue;
            var side = bestAsk.HasValue && price >= bestAsk ? OrderBookSide.Ask
                : bestBid.HasValue && price <= bestBid ? OrderBookSide.Bid : OrderBookSide.Spread;
            var groups = side == OrderBookSide.Bid ? bids : asks;
            var grouped = side == OrderBookSide.Spread ? default : groups.GetValueOrDefault(price);
            var amount = grouped.Amount;
            // A partially covered bucket is unknown unless an actual level is present in it.
            var known = side == OrderBookSide.Spread || amount > 0 || (side == OrderBookSide.Bid
                ? bottomBid.HasValue && price >= bottomBid
                : topAsk.HasValue && price <= topAsk);
            rows.Add(new(price, amount, grouped.Cost, side, known,
                side == OrderBookSide.Bid && price == bestBid || side == OrderBookSide.Ask && price == bestAsk, 0));
        }
        var max = rows.Count > 0 ? rows.Max(r => r.Amount) : 0;
        return rows.Select(r => r with { VolumeFraction = max > 0 ? (double)(r.Amount / max) : 0 }).ToArray();
    }

    private static Dictionary<decimal, (decimal Amount, decimal Cost)> Group(IReadOnlyList<OrderBookLevel> levels, decimal step, bool ask) =>
        levels.GroupBy(l => (ask ? decimal.Ceiling(l.Price / step) : decimal.Floor(l.Price / step)) * step)
            .ToDictionary(g => g.Key, g => (g.Sum(l => l.Amount), g.Sum(l => l.Price * l.Amount)));
}
