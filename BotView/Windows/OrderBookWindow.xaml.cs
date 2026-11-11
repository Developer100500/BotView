using System.Windows;
using System.Windows.Threading;
using BotView.ViewModels;
using BotView.Models;
using BotView.Services;

namespace BotView.Windows;

public partial class OrderBookWindow : Window
{
    private readonly OrderBookViewModel _viewModel;
    private readonly DispatcherTimer _timer;
    public Task CleanupTask { get; private set; } = Task.CompletedTask;

    /// <summary>Opens a network-specific book and delegates standalone account windows to its MainWindow owner.</summary>
    public OrderBookWindow(string exchange, string symbol, IOrderBookService service,
        TradingSessionManager? trading = null, TradingEnvironment environment = TradingEnvironment.Production, TradingMarket market = TradingMarket.Spot,
        Action<TradingAccountProfile, InstrumentKey>? showOrders = null)
    {
        InitializeComponent();
        _viewModel = new(exchange, symbol, service, trading, environment, market);
        OrdersButton.Click += (_, _) =>
        {
            if (_viewModel.CanOpenOrders && _viewModel.SelectedAccount is { } profile)
                showOrders?.Invoke(profile, _viewModel.TradingInstrument);
        };
        DataContext = _viewModel;
        Root.Configure(BookHeader, DepthSettings, ConnectionStatus, TradeActions, TradeScroll);
        // update order book every 50ms
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => _viewModel.Refresh();
        Loaded += async (_, _) =>
        {
            _timer.Start();
            await _viewModel.StartAsync();
        };
    }

    /// <summary>Stops the public book and UI timer while independent account windows remain open.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        CleanupTask = _viewModel.DisposeAsync().AsTask();
        base.OnClosed(e);
    }
}
