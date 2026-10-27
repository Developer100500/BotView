using System.Windows;
using System.Windows.Threading;
using BotView.Interfaces;
using BotView.ViewModels;
using BotView.Models;
using BotView.Services;

namespace BotView.Views;

public partial class OrderBookWindow : Window
{
    private readonly OrderBookViewModel _viewModel;
    private readonly DispatcherTimer _timer;
    public Task CleanupTask { get; private set; } = Task.CompletedTask;

    /// <summary>Opens a network-specific public book with an optional shared account session manager.</summary>
    public OrderBookWindow(string exchange, string symbol, IOrderBookService service,
        TradingSessionManager? trading = null, TradingEnvironment environment = TradingEnvironment.Production)
    {
        InitializeComponent();
        _viewModel = new(exchange, symbol, service, trading, environment);
        _viewModel.ConfirmCancelAllAsync = batch => Task.FromResult(MessageBox.Show(this,
            $"Cancel {batch.Orders.Count} orders for {symbol}?\n{_viewModel.SelectedAccount?.DisplayName}\nAccount: {batch.Account.AccountId}\nIncludes orders created outside BotView.",
            "Cancel all orders for pair", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);
        DataContext = _viewModel;
        // update order book every 50ms
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => _viewModel.Refresh();
        Loaded += async (_, _) =>
        {
            _timer.Start();
            await _viewModel.StartAsync();
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        CleanupTask = _viewModel.DisposeAsync().AsTask();
        base.OnClosed(e);
    }
}
