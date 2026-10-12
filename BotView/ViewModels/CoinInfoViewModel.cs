using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using BotView.Interfaces;
using BotView.Models;

namespace BotView.ViewModels;

public sealed class CoinInfoViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ICoinInfoProvider _provider;
    private CancellationTokenSource? _request;
    private bool _disposed;
    private bool _isLoading;
    private string _statusText = "";
    private CoinInfo? _info;
    private CoinSearchResult? _selectedCandidate;

    public CoinInfoViewModel(string pair, ICoinInfoProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pair);
        Pair = pair;
        BaseSymbol = pair.Split('/')[0].Trim().ToUpperInvariant();
        _provider = provider;
        RetryCommand = new RelayCommand(_ => LoadAsync(), _ => !IsLoading && !_disposed);
    }

    public string Pair { get; }
    public string BaseSymbol { get; }
    public string Title => $"Info · {Pair}";
    public ObservableCollection<CoinSearchResult> Candidates { get; } = new();
    public bool HasChoices => Candidates.Count > 1;
    public bool HasInfo => _info is not null;
    public bool HasWebsite => !string.IsNullOrEmpty(_info?.WebsiteUrl);
    public string Name => _info?.Name ?? BaseSymbol;
    public string Symbol => _info?.Symbol ?? BaseSymbol;
    public string Description => _info is null ? "" : string.IsNullOrWhiteSpace(_info.Description)
        ? "В CoinGecko пока нет описания этой монеты." : _info.Description;
    public string LanguageText => _info?.Language switch { "ru" => "Описание на русском", "en" => "Описание на английском", _ => "" };
    public string CategoriesText => _info is null ? "" : string.Join(" · ", _info.Categories);
    public string? WebsiteUrl => _info?.WebsiteUrl;
    public string? SourceUrl => _info?.SourceUrl;
    public RelayCommand RetryCommand { get; }
    public bool IsLoading { get => _isLoading; private set { Set(ref _isLoading, value); RetryCommand.RaiseCanExecuteChanged(); } }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public CoinSearchResult? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (_selectedCandidate == value) return;
            _selectedCandidate = value;
            OnPropertyChanged();
            if (value is not null) _ = SelectCandidateAsync(value);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Task LoadAsync() => RunAsync(async ct =>
    {
        ClearInfo();
        _selectedCandidate = null;
        OnPropertyChanged(nameof(SelectedCandidate));
        Candidates.Clear();
        OnPropertyChanged(nameof(HasChoices));
        StatusText = $"Поиск {BaseSymbol} в CoinGecko…";
        var matches = await _provider.SearchAsync(BaseSymbol, ct);
        ct.ThrowIfCancellationRequested();
        foreach (var match in matches) Candidates.Add(match);
        OnPropertyChanged(nameof(HasChoices));
        if (matches.Count == 0)
        {
            StatusText = $"Монета {BaseSymbol} не найдена в CoinGecko.";
        }
        else if (matches.Count == 1)
        {
            _selectedCandidate = matches[0];
            OnPropertyChanged(nameof(SelectedCandidate));
            await FetchInfoAsync(matches[0], ct);
        }
        else
        {
            StatusText = "Этот тикер используют несколько проектов. Выберите монету.";
        }
    });

    internal Task SelectCandidateAsync(CoinSearchResult candidate) => RunAsync(async ct =>
    {
        ClearInfo();
        await FetchInfoAsync(candidate, ct);
    });

    private async Task FetchInfoAsync(CoinSearchResult candidate, CancellationToken ct)
    {
        StatusText = $"Загрузка описания {candidate.Name}…";
        var info = await _provider.GetInfoAsync(candidate.Id, ct);
        ct.ThrowIfCancellationRequested();
        if (info.Id != candidate.Id || !info.Symbol.Equals(BaseSymbol, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("CoinGecko вернул данные другой монеты.");
        _info = info;
        NotifyInfoChanged();
        StatusText = "Источник: CoinGecko";
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_disposed) return;
        _request?.Cancel();
        var source = new CancellationTokenSource();
        var ct = source.Token;
        _request = source;
        IsLoading = true;
        try { await action(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && ReferenceEquals(_request, source))
                StatusText = ex is OperationCanceledException
                    ? "CoinGecko не ответил вовремя. Повторите попытку." : $"Не удалось загрузить описание. {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_request, source))
            {
                _request = null;
                IsLoading = false;
            }
            source.Dispose();
        }
    }

    private void ClearInfo() { _info = null; NotifyInfoChanged(); }
    private void NotifyInfoChanged()
    {
        foreach (var property in new[] { nameof(HasInfo), nameof(HasWebsite), nameof(Name), nameof(Symbol),
                     nameof(Description), nameof(LanguageText), nameof(CategoriesText), nameof(WebsiteUrl), nameof(SourceUrl) })
            OnPropertyChanged(property);
    }
    public void Dispose() { _disposed = true; _request?.Cancel(); }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
