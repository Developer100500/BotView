namespace BotView.Models;

public sealed record OrderBookLevel(decimal Price, decimal Amount);
public sealed record OrderBookMarket(string Symbol, string BaseCurrency, string QuoteCurrency, decimal TickSize);

/// <summary>A detached, read-only copy of the exchange's mutable order book.</summary>
public sealed record OrderBookSnapshot(
    OrderBookMarket Market, IReadOnlyList<OrderBookLevel> Bids, IReadOnlyList<OrderBookLevel> Asks,
    DateTimeOffset ReceivedAt)
{
    public decimal? BestBid => Bids.Count == 0 ? null : Bids[0].Price;
    public decimal? BestAsk => Asks.Count == 0 ? null : Asks[0].Price;
}

public enum OrderBookConnection { Connecting, Live, Reconnecting, Error, Closed }
public sealed record OrderBookUpdate(OrderBookSnapshot? Snapshot, OrderBookConnection Connection, string Message);
public enum OrderBookSide { Spread, Bid, Ask }
public enum OrderBookVolumeCurrency { Base, Quote }
public sealed record OrderBookRow(decimal Price, decimal Amount, decimal Cost, OrderBookSide Side,
    bool HasDepth, bool IsBest, double VolumeFraction);
