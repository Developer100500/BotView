using BotView.Models;

namespace BotView.Interfaces;

public interface IOrderBookService
{
    Task<IOrderBookSubscription> SubscribeAsync(string exchange, string symbol, CancellationToken ct = default);
}

public interface IOrderBookSubscription : IAsyncDisposable
{
    /// <summary>Latest atomic update; intermediate updates may be coalesced by the view.</summary>
    OrderBookUpdate Current { get; }
}
