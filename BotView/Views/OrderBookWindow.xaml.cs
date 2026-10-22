using System.Windows;
using System.Windows.Threading;
using BotView.Interfaces;
using BotView.ViewModels;

namespace BotView.Views;

public partial class OrderBookWindow : Window
{
    private readonly OrderBookViewModel _viewModel;
    private readonly DispatcherTimer _timer;
    public Task CleanupTask { get; private set; } = Task.CompletedTask;

    public OrderBookWindow(string exchange, string symbol, IOrderBookService service)
    {
        InitializeComponent();
        _viewModel = new(exchange, symbol, service);
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
