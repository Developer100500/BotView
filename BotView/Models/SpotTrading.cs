namespace BotView.Models;

public enum OrderQuantityCurrency { Base, Quote }
public enum TradingOperationPhase { Prepared, Dispatching, Unknown, Resolved }

/// <summary>Durable intent, scoped to the verified exchange identity and network; contains no credentials.</summary>
public sealed record TradingOperation(string Id, string AccountKey, PlaceOrderRequest Request,
    TradingOperationPhase Phase, DateTimeOffset CreatedAt, OrderReference? CancelTarget = null,
    string? ExchangeOrderId = null);

/// <summary>Detached account state shared by all windows. Uncertainty is separate from an order's exchange status.</summary>
public sealed record SpotTradingState(TradingConnectionState Connection, string Message,
    IReadOnlyList<OrderSnapshot> Orders, IReadOnlyList<ExecutionSnapshot> Executions,
    AccountSnapshot? Balance, IReadOnlyList<TradingOperation> Pending, bool Ready);

public sealed record CancelBatch(TradingAccountContext Account, InstrumentKey Instrument,
    IReadOnlyList<OrderReference> Orders);
