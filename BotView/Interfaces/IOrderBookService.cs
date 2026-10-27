using BotView.Models;

namespace BotView.Interfaces;

public interface IOrderBookService
{
    Task<IOrderBookSubscription> SubscribeAsync(string exchange, string symbol, CancellationToken ct = default);

    /// <summary>Testnet requests must never fall back to production market depth.</summary>
    Task<IOrderBookSubscription> SubscribeAsync(string exchange, string symbol, TradingEnvironment environment, CancellationToken ct = default) =>
        environment == TradingEnvironment.Production ? SubscribeAsync(exchange, symbol, ct)
            : throw new NotSupportedException("This source does not support Testnet depth.");
}

public interface IOrderBookSubscription : IAsyncDisposable
{
    /// <summary>Latest atomic update; intermediate updates may be coalesced by the view.</summary>
    OrderBookUpdate Current { get; }
}
