using System.Windows.Input;

namespace BotView.Services.Commands;

public interface ICommandInvocationHandler
{
    CommandInvocationResult Invoke(CommandInvocation invocation);
}

/// <summary>Has no transport or trading-service dependency and cannot perform a trading operation.</summary>
public sealed class StubCommandInvocationHandler : ICommandInvocationHandler
{
    /// <summary>Returns the supplied intent as a detached diagnostic result without creating exchange state.</summary>
    public CommandInvocationResult Invoke(CommandInvocation invocation) =>
        new(InvocationStatus.Stub, invocation with { Settings = invocation.Settings.Copy() });
}

/// <summary>Owns one book's gesture state; the WPF router decides whether input may reach it.</summary>
public sealed class BookCommandController
{
    private readonly BookCommandSettingsStore _store;
    private readonly ICommandInvocationHandler _handler;
    private readonly Func<(Guid? Profile, string? Account)> _account;
    private BookCommandDefinition? _mode;
    private decimal? _rulerStart;

    public string Connection { get; }
    public InstrumentKey Instrument { get; }
    public InstrumentCommandSettings Settings { get; private set; }
    public CommandInvocationResult? LastResult { get; private set; }
    public string? ActiveMode => _mode?.Label;
    public event Action? Changed;
    public event Action? SettingsRequested;

    /// <summary>Accepts only a settings store and invocation handler, never the real trade path.</summary>
    public BookCommandController(string connection, InstrumentKey instrument, BookCommandSettingsStore store,
        ICommandInvocationHandler handler, Func<(Guid? Profile, string? Account)>? account = null)
    {
        Connection = connection;
        Instrument = instrument;
        _store = store;
        _handler = handler;
        _account = account ?? (() => (null, null));
        Settings = store.Get(connection, instrument);
    }

    /// <summary>Reloads persisted preferences after a settings dialog closes.</summary>
    public void Reload()
    {
        Reset();
        Settings = _store.Get(Connection, Instrument);
        Changed?.Invoke();
    }

    /// <summary>Consumes a recognized initial press, suppressing repeats and applying only local preferences.</summary>
    public bool KeyDown(Key key, bool repeat = false)
    {
        if (key == Key.Escape)
        {
            var active = _mode is not null;
            Reset();
            return active;
        }

        var binding = _store.Load().Bindings.SingleOrDefault(b => b.Enabled && b.Key == key && key != Key.None);
        if (binding is null) return false;

        var command = BookCommandCatalog.All.Single(d => d.Id == binding.Command);
        if (command.FuturesOnly && Instrument.Market != "futures") return true;
        if (repeat) return true;

        if (command.Id == BookCommand.InstrumentSettings)
        {
            Reset();
            SettingsRequested?.Invoke();
        }
        else if (command.Id is >= BookCommand.Volume1 and <= BookCommand.Volume5)
        {
            Save(Settings with { SelectedVolume = (int)command.Id - (int)BookCommand.Volume1 });
        }
        else if (command.Id == BookCommand.QuantityUnits)
        {
            Save(Settings with { Monetary = !Settings.Monetary });
        }
        else if (command.Gesture == CommandGesture.Press)
        {
            Reset();
            Invoke(command.Id);
        }
        else
        {
            var toggleOff = _mode?.Id == command.Id;
            Reset();
            if (!toggleOff) _mode = command;
            Changed?.Invoke();
        }

        return true;
    }

    /// <summary>Ends held modes on physical release even when the cursor has left the ladder.</summary>
    public void KeyUp(Key key)
    {
        var binding = _mode is null ? null : _store.Load().Bindings.Single(b => b.Command == _mode.Id);
        if (binding?.Key == key && _mode!.Gesture is CommandGesture.HoldClick or CommandGesture.HoldWheel) Reset();
    }

    /// <summary>Consumes all clicks during a mode; a missing row or double click never reaches mouse trading.</summary>
    public bool Click(decimal? price, MouseButton button, int clickCount = 1)
    {
        if (_mode is null) return false;
        if (price is null || clickCount != 1 || _mode.Gesture == CommandGesture.HoldWheel) return true;

        var command = _mode;
        if (command.Gesture == CommandGesture.TwoPrices && _rulerStart is null)
        {
            _rulerStart = price;
            Changed?.Invoke();
            return true;
        }

        Invoke(command.Id, _rulerStart ?? price, _rulerStart is null ? null : price, button);
        if (command.Gesture is CommandGesture.ClickOnce or CommandGesture.TwoPrices) Reset();
        return true;
    }

    /// <summary>Intercepts wheel input for any active mode; only wheel modes issue a stub invocation.</summary>
    public bool Wheel(int delta)
    {
        if (_mode is null) return false;
        if (_mode.Gesture == CommandGesture.HoldWheel && delta != 0) Invoke(_mode.Id, wheel: delta);
        return true;
    }

    /// <summary>Clears incomplete gestures without touching exchange requests or positions.</summary>
    public void Reset()
    {
        _mode = null;
        _rulerStart = null;
        Changed?.Invoke();
    }

    /// <summary>Saves local quantity selection without editing the legacy order form.</summary>
    private void Save(InstrumentCommandSettings settings)
    {
        _store.Put(Connection, Instrument, settings);
        Settings = settings;
        Changed?.Invoke();
    }

    /// <summary>Freezes context and optional values before invoking the transport-free handler.</summary>
    private void Invoke(BookCommand command, decimal? price = null, decimal? endPrice = null,
        MouseButton? button = null, int? wheel = null)
    {
        var account = _account();
        var volume = (Settings.Monetary ? Settings.Budgets : Settings.Quantities)[Settings.SelectedVolume];
        LastResult = _handler.Invoke(new(command, Connection, account.Profile, account.Account, Instrument,
            Settings.Copy(), volume, price, endPrice, button, wheel));
        Changed?.Invoke();
    }
}
