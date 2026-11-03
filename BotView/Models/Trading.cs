namespace BotView.Models;

/// <summary>A service instance is permanently bound to this connection, account and environment.
/// Provider routing (QUIK firm/client codes or crypto account type) belongs inside its adapter.</summary>
public sealed record TradingAccountContext(
                    string ConnectionId, string AccountId,
                    string Venue, TradingEnvironment Environment, TradingMarket Market = TradingMarket.Spot);

public enum OrderType { Market, Limit, Stop, StopLimit }
public enum OrderSide { Buy, Sell }
public enum PositionSide { Long, Short }
public enum OrderState { Pending, Open, Filled, Canceled, Expired, Rejected, Unknown }

/// <summary>Limit order in native asset or contract units, with an explicit limit price.</summary>
public sealed record LimitOrderRequest(InstrumentKey Instrument, OrderSide Side, decimal Quantity, decimal Price,
    bool PostOnly = false, bool ReduceOnly = false, PositionSide? PositionSide = null);

/// <summary>Market order in native units; a limit price and Post-only cannot be supplied.</summary>
public sealed record MarketOrderRequest(InstrumentKey Instrument, OrderSide Side, decimal Quantity,
    bool ReduceOnly = false, PositionSide? PositionSide = null);

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
