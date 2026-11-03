namespace BotView.Models;

public enum OrderQuantityCurrency { Base, Quote }
public enum TradingStream { Orders, MyTrades, Balance }

/// <summary>Current display state; no local intentions, reservations or durable operation state.</summary>
public sealed record TradingAccountState(string Message, IReadOnlyList<OrderSnapshot> Orders,
    IReadOnlyList<ExecutionSnapshot> Executions, AccountSnapshot? Balance, bool Connected);

/// <summary>Frozen list of open exchange IDs for a user-confirmed cancellation.</summary>
public sealed record CancelBatch(TradingAccountContext Account, InstrumentKey Instrument,
    IReadOnlyList<OrderReference> Orders);
