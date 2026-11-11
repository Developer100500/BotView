using System.Windows;

namespace BotView.Windows;

/// <summary>Owns account/pair windows independently of the order books that opened them.</summary>
public sealed class AccountOrdersWindowRegistry
{
    private readonly Window _owner;
    private readonly TradingSessionManager _manager;
    private readonly Dictionary<(Guid Profile, TradingEnvironment Environment, TradingMarket Market, InstrumentKey Instrument), AccountOrdersWindow> _windows = new();

    /// <summary>Uses MainWindow as owner so closing a book never closes its account-order window.</summary>
    public AccountOrdersWindowRegistry(Window owner, TradingSessionManager manager)
    {
        _owner = owner;
        _manager = manager;
    }

    /// <summary>Activates an existing account/pair window or opens a new independent one.</summary>
    public AccountOrdersWindow Show(TradingAccountProfile profile, InstrumentKey instrument)
    {
        var key = (profile.Id, profile.Environment, profile.Market, instrument);
        if (_windows.TryGetValue(key, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }

        var window = new AccountOrdersWindow(_manager, profile, instrument) { Owner = _owner };
        _windows.Add(key, window);
        window.Closed += (_, _) => _windows.Remove(key);
        window.Show();
        return window;
    }

    /// <summary>Closes all display windows during application shutdown without canceling exchange orders.</summary>
    public void CloseAll()
    {
        foreach (var window in _windows.Values.ToArray()) window.Close();
    }
}
