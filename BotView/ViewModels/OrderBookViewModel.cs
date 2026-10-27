using System.ComponentModel;
using System.Globalization;
using BotView.Interfaces;
using BotView.Models;
using BotView.Services;

namespace BotView.ViewModels;

public sealed record OrderBookVolumeOption(OrderBookVolumeCurrency Currency, string Label);

public sealed partial class OrderBookViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IOrderBookService _service;
    private readonly CancellationTokenSource _stop = new();
    private IOrderBookSubscription? _subscription;
    private Task? _startTask;
    private Task? _disposeTask;
    private readonly object _disposeSync = new();
    private bool _disposed;
    private OrderBookUpdate? _update;
    private string _priceText = "";
    private string _amountText = "";
    private string _tradeStatus = "";
    private readonly string[] _maxVolumeTexts = ["", ""];
    private readonly decimal?[] _maxVolumes = new decimal?[2];
    private OrderBookVolumeCurrency _volumeCurrency;
    private int _multiplier = 1;
    private decimal? _center;
    private bool _followMarket = true;

    /// <summary>Creates a book fixed to one network; trading sessions are owned by the application manager.</summary>
    public OrderBookViewModel(string exchange, string symbol, IOrderBookService service,
        TradingSessionManager? trading = null, TradingEnvironment environment = TradingEnvironment.Production)
    {
        Exchange = exchange;
        Symbol = symbol;
        VolumeCurrencyOptions =
        [
            new(OrderBookVolumeCurrency.Base, $"Base ({symbol.Split('/')[0]})"),
            new(OrderBookVolumeCurrency.Quote, $"Quote ({symbol.Split('/').Last()})")
        ];
        _service = service;
        BuyCommand = new RelayCommand(_ => TradeAsync(OrderSide.Buy), _ => CanTrade);
        SellCommand = new RelayCommand(_ => TradeAsync(OrderSide.Sell), _ => CanTrade);
        CenterCommand = new RelayCommand(() => { _followMarket = true; _center = null; Changed(); });
        InitializeTrading(trading, environment);
    }

    public string Exchange { get; }
    public string Symbol { get; }
    public string Title => $"Order book · {Symbol} · {Exchange} · {EnvironmentText}";
    public string BaseHeader => $"Volume ({_update?.Snapshot?.Market.BaseCurrency ?? Symbol.Split('/')[0]})";
    public string QuoteHeader => $"Total ({_update?.Snapshot?.Market.QuoteCurrency ?? Symbol.Split('/').Last()})";
    public OrderBookVolumeOption[] VolumeCurrencyOptions { get; }
    public OrderBookVolumeCurrency VolumeCurrency
    {
        get => _volumeCurrency;
        set
        {
            if (value is not (OrderBookVolumeCurrency.Base or OrderBookVolumeCurrency.Quote) || value == _volumeCurrency) return;
            _volumeCurrency = value;
            Changed();
        }
    }
    public string MaxVolumeHeader => $"Volume for 100% ({(VolumeCurrency == OrderBookVolumeCurrency.Base
        ? _update?.Snapshot?.Market.BaseCurrency ?? Symbol.Split('/')[0]
        : _update?.Snapshot?.Market.QuoteCurrency ?? Symbol.Split('/').Last())})";
    public string MaxVolumeText
    {
        get => _maxVolumeTexts[(int)VolumeCurrency];
        set
        {
            _maxVolumeTexts[(int)VolumeCurrency] = value;
            if (string.IsNullOrWhiteSpace(value)) _maxVolumes[(int)VolumeCurrency] = null;
            else if (TryPositive(value, out var volume)) _maxVolumes[(int)VolumeCurrency] = volume;
            Changed();
        }
    }
    public bool HasVolumeScaleError => !string.IsNullOrWhiteSpace(MaxVolumeText) && !TryPositive(MaxVolumeText, out _);
    public string VolumeScaleHint => HasVolumeScaleError
        ? "Enter a number greater than zero; the previous scale is retained."
        : string.IsNullOrWhiteSpace(MaxVolumeText) ? "Blank uses the maximum visible volume for automatic scaling."
        : "Volumes above the threshold fill the bar to 100%.";
    public int[] Multipliers { get; } = [1, 5, 10];
    public int Multiplier
    {
        get => _multiplier;
        set { if (Multipliers.Contains(value)) { _multiplier = value; Changed(); } }
    }
    public bool IsFollowing => _followMarket;
    public string FollowText => _followMarket ? "Following market" : "Manual scrolling";
    public string StatusText
    {
        get
        {
            if (_update is null) return "Connecting…";
            var snapshot = _update.Snapshot;
            var age = snapshot is null ? "" : $" · {snapshot.ReceivedAt.ToLocalTime():HH:mm:ss.fff}";
            bool stale = snapshot is not null && DateTimeOffset.UtcNow - snapshot.ReceivedAt > TimeSpan.FromSeconds(10);
            return _update.Message + (stale && _update.Connection == OrderBookConnection.Live ? " · No recent data" : "") + age;
        }
    }

    public string StepText => _update?.Snapshot is { } b ? $"Step: {Format(b.Market.TickSize * Multiplier)}" : "Step: —";
    public string PriceText { get => _priceText; set { _priceText = value; Changed(); } }
    public string AmountText { get => _amountText; set { _amountText = value; Changed(); } }
    public string TradeStatus => _tradeStatus;
    public bool CanTrade => !_disposed && !_tradeBusy && !_bindingAccount && _trading?.IsReady(TradingInstrument) == true
        && TradingInputValid && !_trading.Current.Pending.Any(o => o.Request.Instrument == TradingInstrument);
    public RelayCommand BuyCommand { get; }
    public RelayCommand SellCommand { get; }
    public RelayCommand CenterCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public Task StartAsync() => _disposed ? Task.CompletedTask : _startTask ??= StartCoreAsync();

    private async Task StartCoreAsync()
    {
        try
        {
            var subscription = Environment == TradingEnvironment.Production
                ? await _service.SubscribeAsync(Exchange, Symbol, _stop.Token).ConfigureAwait(false)
                : await _service.SubscribeAsync(Exchange, Symbol, Environment, _stop.Token).ConfigureAwait(false);
            _subscription = subscription;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { _update = new(null, OrderBookConnection.Error, ex.Message); }
    }

    /// <summary>Called only by the UI's 50 ms timer; no dispatcher queue is accumulated by the feed.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        if (_subscription is not null) _update = _subscription.Current;
        RefreshTrading();
        Changed();
    }

    public IReadOnlyList<OrderBookRow> GetRows(int count)
    {
        var book = _update?.Snapshot;
        if (book is null) return [];
        var marketCenter = book.BestBid is { } bid && book.BestAsk is { } ask ? (bid + ask) / 2
            : book.BestBid ?? book.BestAsk;
        if (marketCenter is null) return [];
        if (_followMarket || _center is null) _center = marketCenter;
        var rows = OrderBookLadder.Build(book, Multiplier, _center.Value, count, _maxVolumes[(int)VolumeCurrency], VolumeCurrency);
        var step = book.Market.TickSize * Multiplier;
        return rows.Select(row => row with { OwnOrderCount = Orders.Count(o => !CcxtTradingService.Terminal(o.State) && o.Price is { } price &&
            (o.Side == OrderSide.Buy ? decimal.Floor(price / step) : decimal.Ceiling(price / step)) * step == row.Price) }).ToArray();
    }

    public void Scroll(int levels)
    {
        if (_update?.Snapshot is not { } book) return;
        if (_center is null) GetRows(1);
        if (_center is null) return;
        _followMarket = false;
        _center = Math.Max(book.Market.TickSize, _center.Value + levels * book.Market.TickSize * Multiplier);
        Changed();
    }

    /// <summary>Copies the exact clicked price only while the order draft is editable.</summary>
    public void SelectPrice(decimal price) { if (IsTradeEditable) PriceText = Format(price); }

    internal static bool TryPositive(string text, out decimal value) =>
        decimal.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out value) && value > 0;

    internal static string Format(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    private void Changed()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        BuyCommand.RaiseCanExecuteChanged();
        SellCommand.RaiseCanExecuteChanged();
        CancelSelectedCommand?.RaiseCanExecuteChanged();
        CancelAllCommand?.RaiseCanExecuteChanged();
        ReconnectCommand?.RaiseCanExecuteChanged();
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        ++_accountGeneration;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_startTask is not null) await _startTask.ConfigureAwait(false);
        if (_subscription is not null) await _subscription.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
