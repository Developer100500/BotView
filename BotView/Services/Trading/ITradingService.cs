using BotView.Models;

namespace BotView.Services.Trading;

/// <summary>Order-book adapter contract. Calls return exchange data or throw; no recovery or local trade policy.</summary>
public interface ITradingService : IAsyncDisposable
{
    TradingAccountContext Account { get; }

    /// <summary>Loads native units and precision metadata for the selected listing.</summary>
    Task<Instrument> FetchInstrumentAsync(InstrumentKey instrument, CancellationToken ct = default);

    /// <summary>Formats native order quantity with the provider's precision rules.</summary>
    decimal AmountToPrecision(InstrumentKey instrument, decimal quantity);

    /// <summary>Formats a limit price with the provider's precision rules.</summary>
    decimal PriceToPrecision(InstrumentKey instrument, decimal price);

    /// <summary>Sends one limit order, returning the provider's acknowledgement.</summary>
    Task<OrderSnapshot> CreateLimitOrderAsync(LimitOrderRequest request, CancellationToken ct = default);

    /// <summary>Sends one market order without a price or maker flag.</summary>
    Task<OrderSnapshot> CreateMarketOrderAsync(MarketOrderRequest request, CancellationToken ct = default);

    /// <summary>Requests cancellation once; a returned acknowledgement is not a promise of no fills.</summary>
    Task<OrderSnapshot> CancelOrderAsync(OrderReference order, CancellationToken ct = default);

    /// <summary>Fetches a specific exchange order.</summary>
    Task<OrderSnapshot> FetchOrderAsync(OrderReference order, CancellationToken ct = default);

    /// <summary>Fetches the selected instrument's current open orders.</summary>
    Task<IReadOnlyList<OrderSnapshot>> FetchOpenOrdersAsync(InstrumentKey instrument, CancellationToken ct = default);

    /// <summary>Fetches recent executions for display.</summary>
    Task<IReadOnlyList<ExecutionSnapshot>> FetchMyTradesAsync(InstrumentKey instrument, CancellationToken ct = default);

    /// <summary>Fetches balances and available collateral reported by the provider.</summary>
    Task<AccountSnapshot> FetchBalanceAsync(CancellationToken ct = default);

    /// <summary>Fetches contract positions; spot returns an empty list.</summary>
    Task<IReadOnlyList<PositionSnapshot>> FetchPositionsAsync(InstrumentKey instrument, CancellationToken ct = default);

    /// <summary>Reports whether this connection supports a private update source.</summary>
    bool SupportsStream(TradingStream stream);

    /// <summary>Reads one detached order batch from a private stream.</summary>
    Task<IReadOnlyList<OrderSnapshot>> WatchOrdersAsync(InstrumentKey instrument, CancellationToken ct = default);

    /// <summary>Reads one detached execution batch from a private stream.</summary>
    Task<IReadOnlyList<ExecutionSnapshot>> WatchMyTradesAsync(InstrumentKey instrument, CancellationToken ct = default);

    /// <summary>Reads one detached account balance update.</summary>
    Task<AccountSnapshot> WatchBalanceAsync(CancellationToken ct = default);
}
