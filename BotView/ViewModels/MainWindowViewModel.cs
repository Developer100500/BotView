using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Input;
using BotView.Chart.TechnicalAnalysis;
using BotView.Configuration;
using BotView.Database;
using BotView.Interfaces;
using BotView.Models;
using BotView.Services;

namespace BotView.ViewModels
{
    public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
    {
        private const int HistoryBatchSize = CandleSeries.CandleChunkSize;
        private const int MaxSearchResults = 50;

        private readonly DatabaseService _databaseService;
        private readonly IDataProvider _dataProvider;
        private readonly IExchangeService _exchangeService;
        private readonly IMarketDataService _marketDataService;
        private readonly MetricsController _metricsController;
        private readonly FavoritePairsStore _favoritePairsStore;
        private readonly Timer _metricsTimer;

        private string _selectedExchange = MarketCatalog.DefaultExchangeId;
        private string _selectedSymbol = "BTC/USDT";
        private string _selectedTimeframe = MarketCatalog.DefaultTimeframeId;
        private string _searchText = string.Empty;
        private string _busyMessage = string.Empty;
        private double _renderTime;
        private bool _isBusy;
        private bool _isReady;
        private bool _isSearchDropdownOpen;
        private bool _suppressSelectionReload;
        private bool _suppressSearchUpdate;
        private IMarketDataSubscription? _subscription;
        private CandleCacheKey _currentKey;
        private int _loadingOlder;
        private int _loadOlderPending;
        private CancellationTokenSource? _selectionLoadCts;
        private CancellationTokenSource _historyLoadCts = new();
        private string? _loadedSymbolsExchange;
        private int _seriesVersion;
        private List<string> _allSymbols = new();
        private HashSet<string> _favoriteSymbols = new(StringComparer.OrdinalIgnoreCase);

        public MainWindowViewModel(
            DatabaseService databaseService,
            IDataProvider dataProvider,
            IExchangeService exchangeService,
            IMarketDataService marketDataService,
            MetricsController metricsController,
            FavoritePairsStore? favoritePairsStore = null)
        {
            _databaseService = databaseService;
            _dataProvider = dataProvider;
            _exchangeService = exchangeService;
            _marketDataService = marketDataService;
            _metricsController = metricsController;
            _favoritePairsStore = favoritePairsStore ?? new FavoritePairsStore();

            Exchanges = new ObservableCollection<ExchangeOption>(MarketCatalog.Exchanges
                .Select(exchange => new ExchangeOption(exchange.Id, exchange.DisplayName)));

            Timeframes = new ObservableCollection<string>(
                MarketCatalog.GetExchange(_selectedExchange).SupportedTimeframes);

            TradingPairs = new ObservableCollection<TradingPairModel>();
            SearchResults = new ObservableCollection<string>();

            SnapLastToRightCommand = new RelayCommand(() => SnapLastToRightRequested?.Invoke());
            StartHorizontalLineCommand = new RelayCommand(() =>
                DrawingToolRequested?.Invoke(TechnicalAnalysisToolType.HorizontalLine));
            StartHorizontalRayCommand = new RelayCommand(() =>
                DrawingToolRequested?.Invoke(TechnicalAnalysisToolType.HorizontalRay));
            StartTrendLineCommand = new RelayCommand(() =>
                DrawingToolRequested?.Invoke(TechnicalAnalysisToolType.TrendLine));
            StartTrendChannelCommand = new RelayCommand(() =>
                DrawingToolRequested?.Invoke(TechnicalAnalysisToolType.TrendChannel));
            StartRectangleCommand = new RelayCommand(() =>
                DrawingToolRequested?.Invoke(TechnicalAnalysisToolType.Rectangle));
            ShowMetricsCommand = new RelayCommand(ShowMetrics);
            ExportMetricsCommand = new RelayCommand(ExportMetrics);
            SelectSearchResultCommand = new RelayCommand(SelectSearchResult);
            ToggleFavoriteCommand = new RelayCommand(ToggleFavorite);
            RemoveFavoriteCommand = new RelayCommand(RemoveFavorite);

            _metricsTimer = new Timer(
                _ => LogMetrics(),
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));
        }

        public ObservableCollection<ExchangeOption> Exchanges { get; }
        public ObservableCollection<string> Timeframes { get; }
        public ObservableCollection<TradingPairModel> TradingPairs { get; }
        public ObservableCollection<string> SearchResults { get; }

        public string SelectedExchange
        {
            get => _selectedExchange;
            set
            {
                if (_selectedExchange == value || string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                _selectedExchange = value;
                OnPropertyChanged();
                UpdateTimeframesForExchange();
                OnPropertyChanged(nameof(IsCurrentPairFavorite));
                OnPropertyChanged(nameof(FavoriteActionText));
                if (_isReady)
                {
                    LoadFavoriteTradingPairs();
                }
                _ = StartSelectionLoadAsync();
            }
        }

        public string SelectedSymbol
        {
            get => _selectedSymbol;
            set
            {
                if (_selectedSymbol == value || string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                _selectedSymbol = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCurrentPairFavorite));
                OnPropertyChanged(nameof(FavoriteActionText));
                SymbolChanged?.Invoke(_selectedSymbol);
                _ = StartSelectionLoadAsync();
            }
        }

        private void UpdateTimeframesForExchange()
        {
            if (!MarketCatalog.TryGetExchange(_selectedExchange, out var exchange))
                return;

            if (!Timeframes.SequenceEqual(exchange!.SupportedTimeframes))
            {
                Timeframes.Clear();
                foreach (var timeframe in exchange.SupportedTimeframes)
                    Timeframes.Add(timeframe);
            }

            if (!exchange.SupportsTimeframe(_selectedTimeframe))
            {
                _selectedTimeframe = exchange.SupportedTimeframes[0];
                OnPropertyChanged(nameof(SelectedTimeframe));
            }
        }

        public string SelectedTimeframe
        {
            get => _selectedTimeframe;
            set
            {
                if (_selectedTimeframe == value || string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                _selectedTimeframe = value;
                OnPropertyChanged();
                _ = StartSelectionLoadAsync();
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText == value)
                {
                    return;
                }

                _searchText = value ?? string.Empty;
                OnPropertyChanged();

                if (!_suppressSearchUpdate)
                {
                    UpdateSearchResults();
                }
            }
        }

        public bool IsSearchDropdownOpen
        {
            get => _isSearchDropdownOpen;
            set
            {
                if (_isSearchDropdownOpen == value)
                {
                    return;
                }

                _isSearchDropdownOpen = value;
                OnPropertyChanged();
            }
        }

        public double RenderTime
        {
            get => _renderTime;
            set
            {
                if (Math.Abs(_renderTime - value) <= 0.01)
                {
                    return;
                }

                _renderTime = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RenderTimeText));
            }
        }

        public string RenderTimeText => $"Render: {_renderTime:F2} ms";

        public bool IsCurrentPairFavorite =>
            _favoriteSymbols.Contains(SelectedSymbol);

        public string FavoriteActionText => IsCurrentPairFavorite
            ? "★ В избранном"
            : "☆ В избранное";

        public string FavoritesFilePath => _favoritePairsStore.FilePath;

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (_isBusy == value)
                {
                    return;
                }

                _isBusy = value;
                OnPropertyChanged();
            }
        }

        public string BusyMessage
        {
            get => _busyMessage;
            private set
            {
                if (_busyMessage == value)
                {
                    return;
                }

                _busyMessage = value;
                OnPropertyChanged();
            }
        }

        public ICommand SnapLastToRightCommand { get; }
        public ICommand StartHorizontalLineCommand { get; }
        public ICommand StartHorizontalRayCommand { get; }
        public ICommand StartTrendLineCommand { get; }
        public ICommand StartTrendChannelCommand { get; }
        public ICommand StartRectangleCommand { get; }
        public ICommand ShowMetricsCommand { get; }
        public ICommand ExportMetricsCommand { get; }
        public ICommand SelectSearchResultCommand { get; }
        public ICommand ToggleFavoriteCommand { get; }
        public ICommand RemoveFavoriteCommand { get; }

        /// <summary>Shared chart series ready. Last arg selects live layout or demo fit.</summary>
        public event Action<ICandleSeriesReader, string, bool, int>? ChartSeriesReady;
        public event Action<string, int>? ChartSeriesCleared;
        public ICandleSeriesReader? CurrentSeries { get; private set; }
        public int CurrentSeriesVersion => Volatile.Read(ref _seriesVersion);

        public event Action<OHLCV>? LiveCandleUpdated;
        public event Action<OHLCV, OHLCV>? CandleClosed;
        public event Action<OHLCV[]>? OlderCandlesLoaded;
        public event Action<TechnicalAnalysisToolType>? DrawingToolRequested;
        public event Action? SnapLastToRightRequested;
        public event Action<string>? ShowMetricsRequested;
        public event Action<string>? ExportMetricsRequested;
        public event Action<string, string>? ErrorOccurred;
        public event Action<string>? SymbolChanged;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Initialize()
        {
            try
            {
                _databaseService.EnsureInitialized();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Database initialization failed: {ex.Message}");
                ErrorOccurred?.Invoke("Ошибка БД",
                    $"Не удалось инициализировать базу данных.\n\n{ex.Message}");
            }

            LoadFavoriteTradingPairs();
        }

        public async Task StartAsync()
        {
            _isReady = true;
            SymbolChanged?.Invoke(SelectedSymbol);
            await StartSelectionLoadAsync();
        }

        public async Task LoadOlderAsync()
        {
            if (!_isReady)
                return;

            var ct = _historyLoadCts.Token;
            if (ct.IsCancellationRequested)
                return;

            // Remember requests raised while another history batch is still loading.
            Interlocked.Exchange(ref _loadOlderPending, 1);

            // Only one worker may drain pending history requests at a time.
            if (Interlocked.CompareExchange(ref _loadingOlder, 1, 0) != 0)
            {
                return;
            }

            try
            {
                while (Interlocked.Exchange(ref _loadOlderPending, 0) != 0)
                {
                    ct.ThrowIfCancellationRequested();
                    // Capture a consistent pair because search may switch subscriptions while awaiting.
                    var key = _currentKey;
                    var subscription = _subscription;
                    if (subscription == null || subscription.Key != key)
                    {
                        continue;
                    }

                    int loadedCount = await _marketDataService.LoadOlderAsync(key, HistoryBatchSize, ct);
                    if (loadedCount == 0)
                    {
                        // The exchange has no more history; discard repeated edge notifications.
                        Interlocked.Exchange(ref _loadOlderPending, 0);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Selection changed while history was loading.
            }
            finally
            {
                Interlocked.Exchange(ref _loadingOlder, 0);

                // Close the race where a request arrives after the loop exits but before the worker flag resets.
                if (Interlocked.Exchange(ref _loadOlderPending, 0) != 0)
                {
                    _ = LoadOlderAsync();
                }
            }
        }

        public void LogMetrics() => _metricsController.LogMetrics();

        public void Dispose()
        {
            _isReady = false;
            var selectionLoad = _selectionLoadCts;
            _selectionLoadCts = null;
            selectionLoad?.Cancel();
            _historyLoadCts.Cancel();
            _historyLoadCts.Dispose();
            _metricsTimer.Dispose();
            _subscription?.Dispose();
            _subscription = null;
            CurrentSeries = null;
            Interlocked.Increment(ref _seriesVersion);
            LogMetrics();
        }

        private void LoadFavoriteTradingPairs()
        {
            try
            {
                _favoriteSymbols = _favoritePairsStore
                    .GetFavorites(SelectedExchange)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                TradingPairs.Clear();
                foreach (var symbol in _favoriteSymbols.OrderBy(
                             value => value,
                             StringComparer.OrdinalIgnoreCase))
                {
                    TradingPairs.Add(new TradingPairModel(symbol));
                }

                OnPropertyChanged(nameof(IsCurrentPairFavorite));
                OnPropertyChanged(nameof(FavoriteActionText));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load favorites: {ex.Message}");
                ErrorOccurred?.Invoke(
                    "Ошибка избранного",
                    $"Не удалось прочитать файл избранного.\n\n{ex.Message}");
            }
        }

        private Task StartSelectionLoadAsync()
        {
            if (!_isReady || _suppressSelectionReload)
            {
                return Task.CompletedTask;
            }

            var previous = _selectionLoadCts;
            var current = new CancellationTokenSource();
            _selectionLoadCts = current;
            var previousHistory = _historyLoadCts;
            _historyLoadCts = new CancellationTokenSource();
            previousHistory.Cancel();
            previousHistory.Dispose();
            previous?.Cancel();
            var seriesVersion = Interlocked.Increment(ref _seriesVersion);
            CurrentSeries = null;
            _subscription?.Dispose();
            _subscription = null;
            ChartSeriesCleared?.Invoke(SelectedTimeframe, seriesVersion);
            return LoadSelectionAsync(current);
        }

        private async Task LoadSelectionAsync(CancellationTokenSource source)
        {
            var ct = source.Token;
            SetBusy(true, $"Загрузка {SelectedSymbol}...");
            try
            {
                ct.ThrowIfCancellationRequested();
                var exchange = SelectedExchange;
                if (!string.Equals(_loadedSymbolsExchange, exchange, StringComparison.OrdinalIgnoreCase))
                {
                    await LoadExchangeSymbolsAsync(exchange, ct);
                }

                ct.ThrowIfCancellationRequested();
                var key = new CandleCacheKey(
                    SelectedExchange, SelectedSymbol, SelectedTimeframe, HistoryBatchSize);
                await SwitchSubscriptionAsync(key, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // A newer selection owns the UI state.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load selection: {ex}");
                if (_isReady && ReferenceEquals(_selectionLoadCts, source))
                    ErrorOccurred?.Invoke("Ошибка загрузки", $"Не удалось загрузить выбранный рынок.\n\n{ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_selectionLoadCts, source))
                {
                    _selectionLoadCts = null;
                    SetBusy(false);
                }
                source.Dispose();
            }
        }

        private void ToggleFavorite()
        {
            try
            {
                if (IsCurrentPairFavorite)
                {
                    _favoritePairsStore.Remove(SelectedExchange, SelectedSymbol);
                }
                else
                {
                    _favoritePairsStore.Add(SelectedExchange, SelectedSymbol);
                }

                LoadFavoriteTradingPairs();
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(
                    "Ошибка избранного",
                    $"Не удалось сохранить избранное.\n\n{ex.Message}");
            }
        }

        private void RemoveFavorite(object? parameter)
        {
            string? symbol = parameter switch
            {
                TradingPairModel pair => pair.Symbol,
                string value => value,
                _ => null
            };

            if (string.IsNullOrWhiteSpace(symbol))
            {
                return;
            }

            try
            {
                _favoritePairsStore.Remove(SelectedExchange, symbol);
                LoadFavoriteTradingPairs();
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(
                    "Ошибка избранного",
                    $"Не удалось удалить пару из избранного.\n\n{ex.Message}");
            }
        }

        private async Task LoadExchangeSymbolsAsync(string exchange, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                SetBusy(true, $"Загрузка рынков {exchange}...");
                var symbols = await _exchangeService.GetAvailableSymbolsAsync(exchange, ct);
                ct.ThrowIfCancellationRequested();

                if (!_isReady || !exchange.Equals(SelectedExchange, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _allSymbols = symbols;
                _loadedSymbolsExchange = exchange;
                UpdateSearchResults();

                if (_allSymbols.Count > 0 &&
                    !_allSymbols.Contains(SelectedSymbol, StringComparer.OrdinalIgnoreCase))
                {
                    var fallback = _allSymbols.FirstOrDefault(s =>
                                       s.Equals("BTC/USDT", StringComparison.OrdinalIgnoreCase))
                                   ?? _allSymbols[0];

                    _suppressSelectionReload = true;
                    try
                    {
                        _selectedSymbol = fallback;
                        OnPropertyChanged(nameof(SelectedSymbol));
                        SymbolChanged?.Invoke(_selectedSymbol);
                    }
                    finally
                    {
                        _suppressSelectionReload = false;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ct.ThrowIfCancellationRequested();
                if (!_isReady || !exchange.Equals(SelectedExchange, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _allSymbols = new List<string>();
                SearchResults.Clear();
                IsSearchDropdownOpen = false;

                Debug.WriteLine($"Failed to load markets for {exchange}: {ex.Message}");
                ErrorOccurred?.Invoke(
                    "Ошибка загрузки рынков",
                    $"Не удалось загрузить торговые пары с биржи {exchange}:\n\n{ex.Message}");
            }
        }

        private void UpdateSearchResults()
        {
            SearchResults.Clear();

            var query = _searchText.Trim();
            if (query.Length == 0 || _allSymbols.Count == 0)
            {
                IsSearchDropdownOpen = false;
                return;
            }

            // Exact match after selecting from dropdown — keep field filled, hide popup.
            if (_allSymbols.Any(s => s.Equals(query, StringComparison.OrdinalIgnoreCase)))
            {
                IsSearchDropdownOpen = false;
                return;
            }

            foreach (var symbol in _allSymbols
                         .Where(s => s.Contains(query, StringComparison.OrdinalIgnoreCase))
                         .Take(MaxSearchResults))
            {
                SearchResults.Add(symbol);
            }

            IsSearchDropdownOpen = SearchResults.Count > 0;
        }

        private void SelectSearchResult(object? parameter)
        {
            if (parameter is not string symbol || string.IsNullOrWhiteSpace(symbol))
            {
                return;
            }

            _suppressSearchUpdate = true;
            try
            {
                SearchText = symbol;
            }
            finally
            {
                _suppressSearchUpdate = false;
            }

            SearchResults.Clear();
            IsSearchDropdownOpen = false;
            SelectedSymbol = symbol;
        }

        private async Task SwitchSubscriptionAsync(CandleCacheKey key, CancellationToken ct)
        {
            var seriesVersion = CurrentSeriesVersion;

            try
            {
                ct.ThrowIfCancellationRequested();
                SetBusy(true, $"Загрузка {SelectedSymbol}...");

                _currentKey = key;

                var sub = await _marketDataService.SubscribeAsync(key, initialHistory: HistoryBatchSize, ct: ct);

                if (ct.IsCancellationRequested || !_isReady || key != new CandleCacheKey(
                        SelectedExchange, SelectedSymbol, SelectedTimeframe, HistoryBatchSize))
                {
                    sub.Dispose();
                    ct.ThrowIfCancellationRequested();
                    return;
                }

                _subscription = sub;
                CurrentSeries = sub.Series;
                ChartSeriesReady?.Invoke(sub.Series, key.Timeframe, true, seriesVersion);

                sub.LiveCandleTicked += c =>
                {
                    if (ReferenceEquals(_subscription, sub)) LiveCandleUpdated?.Invoke(c);
                };
                sub.CandleClosed += (closed, newOpen) =>
                {
                    if (ReferenceEquals(_subscription, sub)) CandleClosed?.Invoke(closed, newOpen);
                };
                sub.OlderCandlesLoaded += arr =>
                {
                    if (ReferenceEquals(_subscription, sub)) OlderCandlesLoaded?.Invoke(arr);
                };

            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ct.ThrowIfCancellationRequested();
                if (!_isReady || key != new CandleCacheKey(
                        SelectedExchange, SelectedSymbol, SelectedTimeframe, HistoryBatchSize))
                {
                    return;
                }

                _subscription?.Dispose();
                _subscription = null;
                CurrentSeries = null;

                ErrorOccurred?.Invoke(
                    "Ошибка подключения к бирже",
                    $"Ошибка загрузки данных с биржи {SelectedExchange}:\n\n{ex.Message}\n\nБудут загружены демонстрационные данные.");

                var demoData = _dataProvider.LoadDemoData(SelectedTimeframe);
                var demoSeries = new CandleSeries();
                demoSeries.LoadInitial(demoData.candles);
                CurrentSeries = demoSeries;
                ChartSeriesReady?.Invoke(demoSeries, demoData.timeframe, false, seriesVersion);
            }
        }

        private void SetBusy(bool isBusy, string message = "")
        {
            BusyMessage = isBusy ? message : string.Empty;
            IsBusy = isBusy;
        }

        private void ShowMetrics()
        {
            try
            {
                ShowMetricsRequested?.Invoke(_metricsController.GetFormattedMetrics());
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke("Ошибка",
                    $"Ошибка при получении метрик производительности:\n\n{ex.Message}");
            }
        }

        private void ExportMetrics()
        {
            try
            {
                ExportMetricsRequested?.Invoke(_metricsController.ExportMetricsToCSV());
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke("Ошибка экспорта",
                    $"Ошибка при экспорте метрик производительности:\n\n{ex.Message}");
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
