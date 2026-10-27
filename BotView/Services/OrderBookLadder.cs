using BotView.Models;

namespace BotView.Services;

public static class OrderBookLadder
{
    /// <summary>Builds visible price buckets; monetary bars use instrument valuation when supplied.
    /// Unknown monetary values are not represented as a fabricated zero cost.</summary>
    public static IReadOnlyList<OrderBookRow> Build(OrderBookSnapshot book, int multiplier, decimal center, int count,
        decimal? maxVolume = null, OrderBookVolumeCurrency volumeCurrency = OrderBookVolumeCurrency.Base)
    {
        if (maxVolume is <= 0) throw new ArgumentOutOfRangeException(nameof(maxVolume));
        if (multiplier <= 0 || count <= 0 || book.Market.TickSize <= 0) return [];
        var step = book.Market.TickSize * multiplier;
        var bids = Group(book.Bids, step, false, book.Market);
        var asks = Group(book.Asks, step, true, book.Market);
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
            rows.Add(new(price, amount, amount == 0 && known ? 0m : grouped.Cost, side, known,
                side == OrderBookSide.Bid && price == bestBid || side == OrderBookSide.Ask && price == bestAsk, 0));
        }
        // Unknown cost has no monetary bar; the renderer still displays the known base quantity.
        decimal Volume(OrderBookRow row) => volumeCurrency == OrderBookVolumeCurrency.Quote ? row.Cost ?? 0m : row.Amount;
        var max = maxVolume ?? (rows.Count > 0 ? rows.Max(Volume) : 0);
        return rows.Select(r => r with
        {
            VolumeFraction = max <= 0 ? 0 : Volume(r) >= max ? 1 : (double)(Volume(r) / max)
        }).ToArray();
    }

    /// <summary>Aggregates actual level valuations rather than repricing all volume at a rounded bucket price.</summary>
    private static Dictionary<decimal, (decimal Amount, decimal? Cost)> Group(
        IReadOnlyList<OrderBookLevel> levels, decimal step, bool ask, OrderBookMarket market) =>
        levels.GroupBy(l => (ask ? decimal.Ceiling(l.Price / step) : decimal.Floor(l.Price / step)) * step)
            .ToDictionary(g => g.Key, g =>
            {
                var costs = g.Select(l => LevelCost(market, l)).ToArray();
                return (g.Sum(l => l.Amount), costs.All(c => c.HasValue) ? costs.Sum() : null);
            });

    /// <summary>Legacy markets describe crypto spot. Other instruments need complete rules and matching currency.</summary>
    private static decimal? LevelCost(OrderBookMarket market, OrderBookLevel level)
    {
        if (market.Instrument is null) return level.Price * level.Amount;
        var value = TradingValueCalculator.Calculate(market.Instrument, level.Amount, level.Price);
        return value is not null && value.Currency == market.QuoteCurrency ? value.Amount : null;
    }
}
