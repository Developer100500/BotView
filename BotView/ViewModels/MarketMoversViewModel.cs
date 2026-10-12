using System.Collections.ObjectModel;
using System.ComponentModel;
using BotView.Interfaces;
using BotView.Models;

namespace BotView.ViewModels;

public sealed class MarketMoversViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IExchangeService _service;
    private CancellationTokenSource? _request;
    private string _exchange = string.Empty;
    private bool _disposed;
    private string _statusText = "";

    public MarketMoversViewModel(IExchangeService service)
    {
        _service = service;
        RefreshCommand = new RelayCommand(RefreshAsync);
    }

    public RelayCommand RefreshCommand { get; }

    public ObservableCollection<MarketMover> Losers { get; } = new();
    public ObservableCollection<MarketMover> Gainers { get; } = new();
    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Task SetExchangeAsync(string exchange)
    {
        if (_disposed) return Task.CompletedTask;
        _exchange = exchange;
        var previous = _request;
        _request = null;
        previous?.Cancel();
        Losers.Clear();
        Gainers.Clear();
        return RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_disposed || _request is not null || string.IsNullOrEmpty(_exchange)) return;
        var source = new CancellationTokenSource();
        var ct = source.Token;
        _request = source;
        StatusText = "Загрузка топ-10…";
        try
        {
            var movers = await _service.GetMarketMoversAsync(_exchange, ct);
            if (_disposed || ct.IsCancellationRequested || !ReferenceEquals(_request, source)) return;
            Losers.Clear();
            foreach (var mover in movers.Where(m => m.ChangePercent < 0)
                         .OrderBy(m => m.ChangePercent).ThenBy(m => m.Symbol, StringComparer.OrdinalIgnoreCase).Take(10))
                Losers.Add(mover);
            Gainers.Clear();
            foreach (var mover in movers.Where(m => m.ChangePercent > 0)
                         .OrderByDescending(m => m.ChangePercent).ThenBy(m => m.Symbol, StringComparer.OrdinalIgnoreCase).Take(10))
                Gainers.Add(mover);
            StatusText = $"Обновлено {DateTime.Now:HH:mm} · USDT";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && ReferenceEquals(_request, source))
                StatusText = (Losers.Count + Gainers.Count > 0 ? "Данные устарели. " : "") +
                    $"Ошибка загрузки: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_request, source)) _request = null;
            source.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _request?.Cancel();
    }
}
