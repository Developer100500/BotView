using System.ComponentModel;
using System.Globalization;
using BotView.Interfaces;
using BotView.Models;
using BotView.Services;

namespace BotView.ViewModels;

public sealed class OrderBookViewModel : INotifyPropertyChanged, IAsyncDisposable
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
    private int _multiplier = 1;
    private decimal? _center;
    private bool _followMarket = true;

    public OrderBookViewModel(string exchange, string symbol, IOrderBookService service)
    {
        Exchange = exchange;
        Symbol = symbol;
        _service = service;
        BuyCommand = new RelayCommand(_ => Trade("Buy"), _ => CanTrade);
        SellCommand = new RelayCommand(_ => Trade("Sell"), _ => CanTrade);
        CenterCommand = new RelayCommand(() => { _followMarket = true; _center = null; Changed(); });
    }

    public string Exchange { get; }
    public string Symbol { get; }
    public string Title => $"Стакан · {Symbol} · {Exchange}";
    public string BaseHeader => $"Объем ({_update?.Snapshot?.Market.BaseCurrency ?? Symbol.Split('/')[0]})";
    public string QuoteHeader => $"Сумма ({_update?.Snapshot?.Market.QuoteCurrency ?? Symbol.Split('/').Last()})";
    public int[] Multipliers { get; } = [1, 5, 10];
    public int Multiplier
    {
        get => _multiplier;
        set { if (Multipliers.Contains(value)) { _multiplier = value; Changed(); } }
    }
    public bool IsFollowing => _followMarket;
    public string FollowText => _followMarket ? "Слежение за рынком" : "Ручная прокрутка";
    public string StatusText
    {
        get
        {
            if (_update is null) return "Подключение…";
            var snapshot = _update.Snapshot;
            var age = snapshot is null ? "" : $" · {snapshot.ReceivedAt.ToLocalTime():HH:mm:ss.fff}";
            var stale = snapshot is not null && DateTimeOffset.UtcNow - snapshot.ReceivedAt > TimeSpan.FromSeconds(10);
            return _update.Message + (stale && _update.Connection == OrderBookConnection.Live ? " · Нет свежих данных" : "") + age;
        }
    }
    public string SpreadText
    {
        get
        {
            var book = _update?.Snapshot;
            if (book?.BestBid is not { } bid || book.BestAsk is not { } ask) return "Спред: —";
            return $"Спред: {Format(ask - bid)} ({(ask - bid) / bid * 100:0.####}%)";
        }
    }
    public string StepText => _update?.Snapshot is { } b ? $"Шаг: {Format(b.Market.TickSize * Multiplier)}" : "Шаг: —";
    public string PriceText { get => _priceText; set { _priceText = value; Changed(); } }
    public string AmountText { get => _amountText; set { _amountText = value; Changed(); } }
    public string TradeStatus => _tradeStatus;
    public bool CanTrade => !_disposed && TryPositive(PriceText, out _) && TryPositive(AmountText, out _);
    public RelayCommand BuyCommand { get; }
    public RelayCommand SellCommand { get; }
    public RelayCommand CenterCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public Task StartAsync() => _disposed ? Task.CompletedTask : _startTask ??= StartCoreAsync();

    private async Task StartCoreAsync()
    {
        try
        {
            var subscription = await _service.SubscribeAsync(Exchange, Symbol, _stop.Token).ConfigureAwait(false);
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
        return OrderBookLadder.Build(book, Multiplier, _center.Value, count);
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

    public void SelectPrice(decimal price) => PriceText = Format(price);

    private void Trade(string side)
    {
        if (!CanTrade) return;
        _tradeStatus = $"Торговля пока не подключена. {side}: {AmountText} {Symbol.Split('/')[0]} по {PriceText}.";
        Changed();
    }

    internal static bool TryPositive(string text, out decimal value) =>
        decimal.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out value) && value > 0;

    internal static string Format(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    private void Changed()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        BuyCommand.RaiseCanExecuteChanged();
        SellCommand.RaiseCanExecuteChanged();
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_startTask is not null) await _startTask.ConfigureAwait(false);
        if (_subscription is not null) await _subscription.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
