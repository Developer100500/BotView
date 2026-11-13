using System.Windows.Input;

namespace BotView.Models;

public enum BookCommand
{
    InstrumentSettings, Volume1, Volume2, Volume3, Volume4, Volume5, QuantityUnits,
    ClosePosition, CancelLimits, CancelStops, BestBuy, BestSell, MarketBuy, MarketSell,
    ReversePosition, Interrupt, Protection, Trailing, StopOrder, CloseHeld, CloseOnce,
    FirstVolume, Level, SignalLevel, DeleteLevels, FastScroll, PriceStepWheel,
    IncreaseStep, DecreaseStep, DisplayUnits, Ruler, MarketData, AllMarketData,
    ExpandBook, NextFuture, PreviousFuture
}

public enum CommandGesture { Press, HoldClick, ClickOnce, HoldWheel, TwoPrices }
public enum DistanceUnit { Ticks, Percent }
public enum ExecutionLocation { Application, Server }
public enum VolumeSource { Position, WorkingVolume }
public enum ProtectionPlacement { Manual, Automatic }
public enum StopExecution { Market, Limit }
public enum RulerUnit { Price, Ticks, Percent, Volume }
public enum MarketDataScope { Connection, AllConnections }

public sealed record BookCommandDefinition(BookCommand Id, string Label, Key Key,
    CommandGesture Gesture = CommandGesture.Press, bool FuturesOnly = false, bool Fixed = false);

public sealed record CommandBinding(BookCommand Command, Key Key, bool Enabled);

public sealed record ProtectionSettings
{
    public bool Enabled { get; init; }
    public ProtectionPlacement Placement { get; init; }
    public decimal? Distance { get; init; }
    public DistanceUnit Unit { get; init; }
    public ExecutionLocation Location { get; init; }
    public VolumeSource Volume { get; init; }
}

public sealed record TrailingSettings
{
    public bool Enabled { get; init; }
    public decimal? Distance { get; init; }
    public decimal? Step { get; init; }
    public decimal Activation { get; init; }
    public DistanceUnit Unit { get; init; }
}

public sealed record StopEntrySettings
{
    public OrderSide Side { get; init; }
    public StopExecution Execution { get; init; }
    public decimal? TriggerPrice { get; init; }
    public decimal? LimitPrice { get; init; }
    public decimal? Offset { get; init; }
    public VolumeSource Volume { get; init; } = VolumeSource.WorkingVolume;
    public ExecutionLocation Location { get; init; } = ExecutionLocation.Server;
}

public sealed record InstrumentCommandSettings
{
    public decimal?[] Quantities { get; init; } = new decimal?[5];
    public decimal?[] Budgets { get; init; } = new decimal?[5];
    public int SelectedVolume { get; init; }
    public bool Monetary { get; init; }
    public ProtectionSettings StopLoss { get; init; } = new();
    public ProtectionSettings TakeProfit { get; init; } = new();
    public TrailingSettings Trailing { get; init; } = new();
    public StopEntrySettings Stop { get; init; } = new();
    public PositionSide ClosingSide { get; init; }
    public VolumeSource ClosingVolume { get; init; }
    public bool FirstVolumeWorkingLimit { get; init; } = true;
    public bool FirstVolumePositionLimit { get; init; } = true;
    public string LevelLabel { get; init; } = "";
    public string LevelColor { get; init; } = "#FBBF24";
    public bool SignalNotification { get; init; } = true;
    public int FastScrollSteps { get; init; } = 30;
    public int StepIncrement { get; init; } = 5;
    public RulerUnit RulerUnits { get; init; }
    public string MarketDataSource { get; init; } = "";
    public MarketDataScope MarketDataScope { get; init; }

    /// <summary>Copies mutable preset arrays before a settings document or invocation leaves its owner.</summary>
    public InstrumentCommandSettings Copy() => this with
    {
        Quantities = (decimal?[])Quantities.Clone(),
        Budgets = (decimal?[])Budgets.Clone()
    };
}

public sealed record InstrumentSettingsEntry(string Connection, InstrumentKey Instrument, InstrumentCommandSettings Settings);
public sealed record BookCommandDocument(int Version, CommandBinding[] Bindings, InstrumentSettingsEntry[] Instruments);

public sealed record CommandInvocation(BookCommand Command, string Connection, Guid? ProfileId,
    string? AccountId, InstrumentKey Instrument, InstrumentCommandSettings Settings, decimal? Volume,
    decimal? Price = null, decimal? EndPrice = null, MouseButton? Button = null, int? WheelDelta = null);

public enum InvocationStatus { Stub }
public sealed record CommandInvocationResult(InvocationStatus Status, CommandInvocation Invocation,
    string Message = "Заглушка: действие не выполнено");
