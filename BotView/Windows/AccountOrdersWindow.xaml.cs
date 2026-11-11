using System.Windows;
using System.Windows.Threading;
using BotView.ViewModels;

namespace BotView.Windows;

public partial class AccountOrdersWindow : Window
{
    private readonly AccountOrdersViewModel _viewModel;
    private readonly DispatcherTimer _timer;

    /// <summary>Opens a standalone view over an application-owned account session.</summary>
    public AccountOrdersWindow(TradingSessionManager manager, TradingAccountProfile profile, InstrumentKey instrument)
    {
        InitializeComponent();
        _viewModel = new(manager, profile, instrument);
        _viewModel.ConfirmCancelAllAsync = batch => Task.FromResult(MessageBox.Show(this,
            $"Cancel {batch.Orders.Count} orders for {instrument.SecurityId}?\n{_viewModel.ContextText}\nAccount: {batch.Account.AccountId}\nIncludes orders created outside BotView.",
            "Cancel all orders for pair", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);
        DataContext = _viewModel;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => _viewModel.Refresh();
        Loaded += async (_, _) =>
        {
            _timer.Start();
            await _viewModel.BindAsync();
        };
    }

    /// <summary>Detaches this window without stopping the shared account session.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
