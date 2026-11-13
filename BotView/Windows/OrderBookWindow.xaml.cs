using System.Windows;
using System.Windows.Threading;
using BotView.ViewModels;
using BotView.Models;
using BotView.Services;
using BotView.Services.Commands;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotView.Windows;

public partial class OrderBookWindow : Window
{
    private readonly OrderBookViewModel _viewModel;
    private readonly DispatcherTimer _timer;
    private BookCommandController? _commands;
    public Task CleanupTask { get; private set; } = Task.CompletedTask;

    /// <summary>Opens a network-specific book and delegates standalone account windows to its MainWindow owner.</summary>
    public OrderBookWindow(string exchange, string symbol, IOrderBookService service,
        TradingSessionManager? trading = null, TradingEnvironment environment = TradingEnvironment.Production, TradingMarket market = TradingMarket.Spot,
        Action<TradingAccountProfile, InstrumentKey>? showOrders = null, Instrument? instrument = null, string? connectionId = null)
    {
        InitializeComponent();
        _viewModel = new(exchange, symbol, service, trading, environment, market, instrument, connectionId);
        OrdersButton.Click += (_, _) =>
        {
            if (_viewModel.CanOpenOrders && _viewModel.SelectedAccount is { } profile)
                showOrders?.Invoke(profile, _viewModel.TradingInstrument);
        };
        DataContext = _viewModel;
        try
        {
            _commands = new(_viewModel.ConnectionId, _viewModel.TradingInstrument, App.BookCommands,
                new StubCommandInvocationHandler(), () => (_viewModel.SelectedAccount?.Id,
                    _viewModel.ConnectedAccountId ?? (_viewModel.SelectedAccount?.Quik is { } q
                        ? $"{q.FirmId}/{q.TradeAccount}/{q.ClientCode}/{q.LimitKind}" : null)));
            _commands.Changed += RefreshCommands;
            _commands.SettingsRequested += OpenInstrumentSettings;
            App.CommandInput?.Register(Ladder, _commands);
            App.BookCommands.Changed += ReloadCommandSettings;
            RefreshCommands();
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            CommandStatus.Text = $"Настройки команд не загружены: {ex.Message}";
        }
        CommandSettingsButton.Click += (_, _) => OpenInstrumentSettings();
        HotkeysButton.Click += (_, _) =>
        {
            try { new HotkeysSettingsWindow(App.BookCommands) { Owner = this }.ShowDialog(); _commands?.Reload(); }
            catch (Exception ex) { CommandStatus.Text = ex.Message; }
        };
        Deactivated += (_, _) => _commands?.Reset();
        Ladder.LostKeyboardFocus += (_, _) => _commands?.Reset();
        Ladder.MouseLeave += (_, _) => { if (_commands?.ActiveMode is not null) _commands.Reset(); };
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

    /// <summary>Applies imports and edits to all open books on their owning UI thread.</summary>
    private void ReloadCommandSettings()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ReloadCommandSettings); return; }
        try { _commands?.Reload(); }
        catch (Exception ex) { CommandStatus.Text = ex.Message; }
    }

    /// <summary>Opens a detached settings draft; its controls never reference real trading commands.</summary>
    private void OpenInstrumentSettings()
    {
        try
        {
            new InstrumentCommandSettingsWindow(App.BookCommands, _viewModel.ConnectionId, _viewModel.ListedInstrument)
                { Owner = this }.ShowDialog();
            _commands?.Reload();
        }
        catch (Exception ex) { CommandStatus.Text = ex.Message; }
    }

    /// <summary>Shows selection and the complete accepted stub context without fabricating exchange state.</summary>
    private void RefreshCommands()
    {
        if (_commands is not { } commands) return;
        var settings = commands.Settings;
        var values = settings.Monetary ? settings.Budgets : settings.Quantities;
        WorkingVolumes.Children.Clear();
        for (var index = 0; index < 5; index++)
        {
            var slot = index;
            var button = new System.Windows.Controls.Button
            {
                Content = $"{index + 1}: {values[index]?.ToString() ?? "—"}", Margin = new(0, 0, 4, 4),
                FontWeight = index == settings.SelectedVolume ? FontWeights.Bold : FontWeights.Normal
            };
            button.Click += (_, _) => commands.KeyDown(System.Windows.Input.Key.D1 + slot);
            WorkingVolumes.Children.Add(button);
        }
        WorkingVolumeUnits.Text = settings.Monetary
            ? $"Сумма ({_viewModel.ListedInstrument.TradingRules?.SettlementCurrency ?? _viewModel.ListedInstrument.Currency ?? "неизвестна"})"
            : $"Количество ({_viewModel.ListedInstrument.TradingRules?.QuantityUnit})";
        CommandStatus.Text = commands.ActiveMode is { } mode ? $"Режим: {mode} · Esc — отмена" :
            commands.LastResult is { } result ? $"{result.Message}\n{BookCommandCatalog.All.Single(d => d.Id == result.Invocation.Command).Label} · " +
                $"объём: {result.Invocation.Volume?.ToString() ?? "не задан"} · цена: {result.Invocation.Price?.ToString() ?? "не задана"}" +
                (result.Invocation.EndPrice is { } end ? $" → {end}" : "") +
                (result.Invocation.WheelDelta is { } delta ? $" · колесо {delta}" : "") +
                $"\n{result.Invocation.Instrument.Board} {result.Invocation.Instrument.SecurityId} · {result.Invocation.AccountId ?? "счёт не выбран"}"
                : "Новые команды: заглушки. SL/TP и трейлинг не исполняются.";
        CommandStatus.ToolTip = commands.LastResult is { } last
            ? JsonSerializer.Serialize(last.Invocation, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })
            : CommandStatus.Text;
    }

    /// <summary>Stops the public book and UI timer while independent account windows remain open.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        App.CommandInput?.Unregister(Ladder);
        App.BookCommands.Changed -= ReloadCommandSettings;
        CleanupTask = _viewModel.DisposeAsync().AsTask();
        base.OnClosed(e);
    }
}
