using BotView.Models;

namespace BotView.ViewModels;

public sealed partial class MainWindowViewModel
{
    private IReadOnlyList<Instrument> _catalogInstruments = [];
    private IReadOnlyList<InstrumentCatalogRow> _catalogRows = [];
    private string _selectedCatalogCategory = "All";
    private string _catalogErrorText = "";
    private bool _isCatalogLoading;

    public bool IsInstrumentCatalogMode =>
        _exchangeDefinitions.FirstOrDefault(d => d.Id == SelectedExchange)?.SupportedTimeframes.Count == 0;
    public bool IsChartMode => !IsInstrumentCatalogMode;
    public string SearchPlaceholder => IsInstrumentCatalogMode ? "Search by ticker or name" : "Search for a pair, e.g. BTC/USDT";
    public bool IsBlockingBusy => IsBusy && IsChartMode;
    public IReadOnlyList<string> CatalogCategories { get; } = ["All", "Shares", "Bonds", "Futures"];
    public RelayCommand RefreshCatalogCommand { get; }
    public IReadOnlyList<InstrumentCatalogRow> CatalogRows => _catalogRows;
    public string CatalogCountText => $"Found: {_catalogRows.Count} of {_catalogInstruments.Count}";
    public string CatalogErrorText
    {
        get => _catalogErrorText;
        private set { _catalogErrorText = value; OnPropertyChanged(); }
    }
    public bool IsCatalogLoading
    {
        get => _isCatalogLoading;
        private set
        {
            _isCatalogLoading = value;
            OnPropertyChanged();
            RefreshCatalogCommand.RaiseCanExecuteChanged();
        }
    }
    public string SelectedCatalogCategory
    {
        get => _selectedCatalogCategory;
        set
        {
            if (_selectedCatalogCategory == value || !CatalogCategories.Contains(value)) return;
            _selectedCatalogCategory = value;
            OnPropertyChanged();
            FilterCatalog();
        }
    }

    public Task RefreshCatalogAsync() => IsInstrumentCatalogMode && !IsCatalogLoading
        ? StartSelectionLoadAsync() : Task.CompletedTask;

    private void ResetCatalog()
    {
        _catalogInstruments = [];
        _catalogRows = [];
        _selectedCatalogCategory = "All";
        CatalogErrorText = "";
        IsCatalogLoading = false;
        OnPropertyChanged(nameof(SelectedCatalogCategory));
        OnPropertyChanged(nameof(CatalogRows));
        OnPropertyChanged(nameof(CatalogCountText));
    }

    private void FilterCatalog()
    {
        var query = SearchText.Trim();
        _catalogRows = _catalogInstruments.Select(i => new InstrumentCatalogRow(i))
            .Where(row => (SelectedCatalogCategory == "All" || row.Type == SelectedCatalogCategory) &&
                (query.Length == 0 || row.Ticker.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        OnPropertyChanged(nameof(CatalogRows));
        OnPropertyChanged(nameof(CatalogCountText));
    }

    private async Task LoadCatalogAsync(string exchange, CancellationTokenSource source)
    {
        IsCatalogLoading = true;
        CatalogErrorText = "";
        var instruments = await _exchangeService.GetInstrumentsAsync(exchange, source.Token);
        source.Token.ThrowIfCancellationRequested();
        if (!_isReady || !ReferenceEquals(_selectionLoadCts, source) || SelectedExchange != exchange) return;
        _catalogInstruments = instruments;
        FilterCatalog();
    }
}
