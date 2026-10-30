using BotView.Models;

namespace BotView.Services.Trading;

/// <summary>Account-bound contract for CCXT and broker/QUIK adapters, using application models only.
/// Dispose releases the connection without canceling live orders. Read failures throw TradingServiceException; only confirmed
/// absence returns null. Unsupported capabilities throw NotSupportedException. Mutation timeouts after
/// dispatch return Unknown and require reconciliation, not automatic resubmission.</summary>
public interface ITradingService : IAsyncDisposable
{
    TradingAccountContext Account { get; }

    /// <summary>Loads trading metadata including units, steps, limits and monetary valuation parameters.</summary>
    Task<Instrument> GetTradingInstrumentAsync(InstrumentKey instrument, CancellationToken ct = default);

    /// <summary>Returns options actually supported for this listing and the bound account.</summary>
    Task<TradingCapabilities> GetCapabilitiesAsync(InstrumentKey instrument, CancellationToken ct = default);

    /// <summary>Checks instrument/provider/account constraints without sending an order.
    /// PlaceOrderAsync must check again because balances and limits can change.</summary>
    Task<OrderValidationResult> ValidateOrderAsync(PlaceOrderRequest request, CancellationToken ct = default);

    /// <summary>Sends with an ID recorded before dispatch. Accepted does not mean filled.
    /// Cancellation before dispatch may throw; after dispatch preserve/reconcile the unknown outcome.</summary>
    Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default);

    /// <summary>Cancels by listing and exchange ID, preserving partial fills and uncertain outcomes.</summary>
    Task<CancelOrderResult> CancelOrderAsync(OrderReference order, CancellationToken ct = default);

    /// <summary>Returns latest state, filled quantity and remaining quantity; null only confirms absence.</summary>
    Task<OrderSnapshot?> GetOrderAsync(OrderReference order, CancellationToken ct = default);

    /// <summary>Reconciles a lost placement response where client-ID lookup is supported.
    /// Unsupported lookup or a failed request must not be represented as an absent order.</summary>
    Task<OrderSnapshot?> FindOrderByClientIdAsync(InstrumentKey instrument, string clientOrderId, CancellationToken ct = default);

    /// <summary>Returns a bounded page of open orders with continuation instead of silent truncation.</summary>
    Task<TradingPage<OrderSnapshot>> GetOpenOrdersAsync(TradingQuery query, CancellationToken ct = default);

    /// <summary>Returns history with filters and provider-owned continuation where supported.</summary>
    Task<TradingPage<OrderSnapshot>> GetOrderHistoryAsync(TradingQuery query, CancellationToken ct = default);

    /// <summary>Returns actual fills and fees; deduplicate by account, listing and execution identity.</summary>
    Task<TradingPage<ExecutionSnapshot>> GetExecutionsAsync(TradingQuery query, CancellationToken ct = default);

    /// <summary>Returns funds, reservations, positions and margin. Unknown values remain null.</summary>
    Task<AccountSnapshot> GetAccountStateAsync(CancellationToken ct = default);

    /// <summary>Returns cash holdings or futures positions, including direction and quantity unit.</summary>
    Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(InstrumentKey? instrument = null, CancellationToken ct = default);

    /// <summary>Streams order, execution, account and connection updates to each subscriber.
    /// Refresh snapshots/reconcile history after reconnect; the stream is not durable history.</summary>
    IAsyncEnumerable<TradingUpdate> WatchUpdatesAsync(CancellationToken ct = default);
}
