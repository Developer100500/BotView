using BotView.Models;

namespace BotView.Services.OrderBooks;

public interface IOrderBookService
{
    Task<IOrderBookSubscription> SubscribeAsync(string exchange, string symbol, CancellationToken ct = default);

    /// <summary>Testnet requests must never fall back to production market depth.</summary>
    Task<IOrderBookSubscription> SubscribeAsync(string exchange, string symbol, TradingEnvironment environment, CancellationToken ct = default) =>
        environment == TradingEnvironment.Production ? SubscribeAsync(exchange, symbol, ct)
            : throw new NotSupportedException("This source does not support Testnet depth.");
    /// <summary>Requests depth for a specific derivatives or spot market.</summary>
    Task<IOrderBookSubscription> SubscribeAsync(string exchange, string symbol, TradingEnvironment environment,
        TradingMarket market, CancellationToken ct = default) => market == TradingMarket.Spot
            ? SubscribeAsync(exchange, symbol, environment, ct)
            : throw new NotSupportedException("Futures depth is not supported by this source.");
}

public interface IOrderBookSubscription : IAsyncDisposable
{
    /// <summary>Latest atomic update; intermediate updates may be coalesced by the view.</summary>
    OrderBookUpdate Current { get; }
}
