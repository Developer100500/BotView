namespace BotView.Models;

/// <summary>A service instance is permanently bound to this connection, account and environment.
/// Provider routing (QUIK firm/client codes or crypto account type) belongs inside its adapter.</summary>
public sealed record TradingAccountContext(
                    string ConnectionId, string AccountId,
                    string Venue, TradingEnvironment Environment, TradingMarket Market = TradingMarket.Spot);

public enum OrderType { Market, Limit, Stop, StopLimit }
public enum OrderSide { Buy, Sell }
public enum OrderTimeInForce { Gtc, Day, Ioc, Fok, Gtd }
public enum TriggerCondition { AtOrAbove, AtOrBelow }
public enum PositionSide { Long, Short }
public enum PlaceOrderStatus { Submitted, Accepted, Rejected, Unknown }
public enum CancelOrderStatus { Submitted, Canceled, AlreadyClosed, Rejected, Unknown }
public enum OrderState { Pending, Open, Filled, Canceled, Expired, Rejected, Unknown }

/// <summary>Generate and record ClientOrderId before dispatch; never reuse it for a new intent.
/// Quantity uses the instrument's declared unit. Limit and trigger prices are separate.
/// Client-ID lookup/idempotency depends on provider capabilities.</summary>
public sealed record PlaceOrderRequest(
    InstrumentKey Instrument, OrderSide Side, OrderType Type, decimal Quantity, string ClientOrderId,
    decimal? Price = null, decimal? TriggerPrice = null, TriggerCondition? TriggerCondition = null,
    OrderTimeInForce TimeInForce = OrderTimeInForce.Gtc, DateTimeOffset? ExpiresAt = null,
    bool PostOnly = false, bool ReduceOnly = false, PositionSide? PositionSide = null);

/// <summary>Exchange order identity is scoped by the bound account and full listing key.</summary>
public sealed record OrderReference(InstrumentKey Instrument, string ExchangeOrderId);

/// <summary>All quantities use the same unit. Open orders can be partially filled; canceled/expired
/// orders can retain fills. Unknown fields remain null, not zero.</summary>
public sealed record OrderSnapshot(
    OrderReference Reference, string? ClientOrderId, OrderSide Side, OrderType Type, OrderState State,
    decimal Quantity, decimal? FilledQuantity, decimal? RemainingQuantity, decimal? Price,
    decimal? AverageFillPrice, DateTimeOffset? CreatedAt, DateTimeOffset UpdatedAt, string? StatusReason = null,
    PositionSide? PositionSide = null, bool? ReduceOnly = null);

/// <summary>Sanitized application error. Never include raw provider responses or credentials.</summary>
public sealed record TradingError(string Code, string Message);

/// <summary>Submitted confirms dispatch; Accepted confirms acceptance, not execution. ProviderRequestId
/// identifies e.g. a QUIK transaction, independently of ExchangeOrderId. Unknown requires reconciliation.</summary>
public sealed record PlaceOrderResult(
    PlaceOrderStatus Status, string ClientOrderId, InstrumentKey Instrument,
    string? ExchangeOrderId = null, string? ProviderRequestId = null,
    OrderSnapshot? Order = null, TradingError? Error = null);

/// <summary>Submitted is a cancel acknowledgement. Canceled requires confirmed final state.
/// AlreadyClosed includes the observed terminal order when available; partial fills remain authoritative.</summary>
public sealed record CancelOrderResult(
    CancelOrderStatus Status, OrderReference Reference, string? ProviderRequestId = null,
    OrderSnapshot? Order = null, TradingError? Error = null);

public sealed record Money(decimal Amount, string Currency);
public sealed record AccountBalance(string Currency, decimal? Total, decimal? Available, decimal? Reserved);

/// <summary>Quantity is nonnegative; Side carries direction. Hedged accounts may contain both sides.
/// Position value and collateral are distinct. Unknown financial values remain null.</summary>
public sealed record PositionSnapshot(
    InstrumentKey Instrument, PositionSide Side, decimal Quantity, QuantityUnit QuantityUnit,
    decimal? AveragePrice = null, Money? UnrealizedPnl = null, Money? InitialMargin = null,
    decimal? Leverage = null, bool? Hedged = null, string? MarginMode = null);

public sealed record MarginSnapshot(
    string Currency, decimal? Equity = null, decimal? AvailableFunds = null,
    decimal? InitialMargin = null, decimal? MaintenanceMargin = null, decimal? VariationMargin = null);

/// <summary>A null section is unavailable/unknown; an empty section confirms there are no entries.</summary>
public sealed record AccountSnapshot(
    TradingAccountContext Account, IReadOnlyList<AccountBalance>? Balances,
    IReadOnlyList<PositionSnapshot>? Positions, IReadOnlyList<MarginSnapshot>? Margins, DateTimeOffset AsOf);

/// <summary>One actual fill, identified by account + listing + ExecutionId for deduplication. Fee currency may differ and rebates can
/// be negative. Execution quantity uses the same instrument unit as the order.</summary>
public sealed record ExecutionSnapshot(
    string ExecutionId, OrderReference Order, OrderSide Side, decimal Quantity, decimal Price,
    DateTimeOffset ExecutedAt, Money? Fee = null);

/// <summary>UTC bounds and an opaque continuation cursor. Reject unsupported filters instead of
/// ignoring them. A null instrument means all listings only where supported by the provider.</summary>
public sealed record TradingQuery(
    InstrumentKey? Instrument = null, DateTimeOffset? Since = null, DateTimeOffset? Until = null,
    int Limit = 100, string? Cursor = null);
public sealed record TradingPage<T>(IReadOnlyList<T> Items, string? NextCursor = null);

/// <summary>Capabilities are listing/account-specific, not inferred from available enum values.
/// Unsupported methods/options must fail explicitly rather than return an empty success.</summary>
public sealed record TradingCapabilities(
    IReadOnlySet<OrderType> OrderTypes, IReadOnlySet<OrderTimeInForce> TimeInForces,
    bool SupportsPostOnly = false, bool SupportsReduceOnly = false, bool SupportsPositionSide = false,
    bool SupportsLookupByClientOrderId = false, bool SupportsOrderHistory = false,
    bool SupportsExecutions = false, bool SupportsPositions = false);

public sealed record OrderValidationResult(IReadOnlyList<TradingError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public enum TradingConnectionState { Connecting, Connected, Reconnecting, Disconnected }
public abstract record TradingUpdate(DateTimeOffset AsOf);
public sealed record OrderChanged(OrderSnapshot Order) : TradingUpdate(Order.UpdatedAt);
public sealed record ExecutionReceived(ExecutionSnapshot Execution) : TradingUpdate(Execution.ExecutedAt);
public sealed record AccountChanged(AccountSnapshot Account) : TradingUpdate(Account.AsOf);
/// <summary>RequiresResync signals that consumers must refresh snapshots and reconcile history.
/// A reconnected stream is not proof that no order or execution updates were missed.</summary>
public sealed record TradingConnectionChanged(
    TradingConnectionState State, DateTimeOffset Timestamp, bool RequiresResync = false, TradingError? Error = null)
    : TradingUpdate(Timestamp);
