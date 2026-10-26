using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Input;
using BotView.Chart.TechnicalAnalysis;
using BotView.Chart;
using BotView.Configuration;
using BotView.Database;
using BotView.Interfaces;
using BotView.Models;
using BotView.Services;

namespace BotView.ViewModels
{
    public sealed partial class MainWindowViewModel : INotifyPropertyChanged, IDisposable
    {
        private const int HistoryBatchSize = CandleSeries.CandleChunkSize;
        private const int MaxSearchResults = 50;

        private readonly DatabaseService _databaseService;
        private readonly IDataProvider _dataProvider;
        private readonly IExchangeService _exchangeService;
        private readonly IReadOnlyList<ExchangeDefinition> _exchangeDefinitions;
        private readonly IMarketDataService _marketDataService;
        private readonly MetricsController _metricsController;
        private readonly FavoritePairsStore _favoritePairsStore;
        private readonly Timer _metricsTimer;

        private string _selectedExchange = MarketCatalog.DefaultExchangeId;
        private string _selectedSymbol = "BTC/USDT";
        private string _selectedTimeframe = MarketCatalog.DefaultTimeframeId;
        // The user's search input in the symbol/pair selector UI.
        private string _searchText = string.Empty;
        // Message displayed during background/busy operations (e.g., loading data).
        private string _comparisonSearchText = string.Empty;
        private string? _comparisonSymbol;
        private string _comparisonStatusText = string.Empty;
        private bool _isComparisonDropdownOpen;
        private IMarketDataSubscription? _comparisonSubscription;
        private CandleCacheKey _comparisonKey;
        private CancellationTokenSource? _comparisonSessionCts;
        private int _comparisonVersion;
        private bool _comparisonHistoryLoading;
        private bool _comparisonHistoryExhausted;
        private DateTime _comparisonHistoryTarget = DateTime.MaxValue;
        private string _busyMessage = string.Empty;
        // Last measured render duration for performance metrics, in milliseconds.
        private double _renderTime;
        // UI state flags.
        private bool _isBusy;                   // App is loading or performing a blocking operation.
        private bool _isReady;                  // Set to true after view model startup is finished.
        private bool _isSearchDropdownOpen;     // Controls visibility of the search results dropdown.
        private bool _suppressSelectionReload;  // Prevents automatic reload when changing related values.
        private bool _suppressSearchUpdate;     // Prevents recursive search UI updates.

        // --- Data subscription and loading state ---
        // Active market data subscription for the selected trading pair (feed of candles/etc).
        private IMarketDataSubscription? _subscription;
        // Cache key representing the current subscription (exchange/symbol/timeframe/batch size).
        private CandleCacheKey _currentKey;
        // Guards and tracks "load older data" requests to avoid concurrent/race conditions.
        private int _loadingOlder;       // 0 or 1: indicates if older data is currently being loaded.
        private int _loadOlderPending;   // 0 or 1: signals if another load was requested during an active load.

        // --- Task cancellation and lifecycle control ---
        // Used to signal cancellation for the currently running selection load task (switching symbol, etc.).
        private CancellationTokenSource? _selectionLoadCts;
        // Used to signal cancellation for the currently running history/data load task.
        private CancellationTokenSource _historyLoadCts = new();

        // --- Caching for fast switching and version tracking ---
        // The exchange for which symbols are currently loaded in _allSymbols, for cache coherence.
        private string? _loadedSymbolsExchange;
        // Version number for the active chart series; incremented to tell consumers data has changed.
        private int _seriesVersion;
        // List of all trading pairs currently loaded for the selected exchange, for filtering/search/autocomplete.
        private List<string> _allSymbols = new();
        // Set of user's favorite trading pairs (for marking favorites in UI).
        private HashSet<string> _favoriteSymbols = new(StringComparer.OrdinalIgnoreCase);

        public MainWindowViewModel(
            DatabaseService databaseService,
            IDataProvider dataProvider,
            IExchangeService exchangeService,
            IMarketDataService marketDataService,
            MetricsController metricsController,
            FavoritePairsStore? favoritePairsStore = null,
            IQuoteProvider? quoteProvider = null)
        {
            _databaseService = databaseService;
            _dataProvider = dataProvider;
            _exchangeService = exchangeService;
            var registered = exchangeService.GetExchanges();
            _exchangeDefinitions = registered is { Count: > 0 } ? registered : MarketCatalog.Exchanges;
            if (!_exchangeDefinitions.Any(d => d.Id == _selectedExchange))
                _selectedExchange = _exchangeDefinitions[0].Id;
            var selectedDefinition = _exchangeDefinitions.First(d => d.Id == _selectedExchange);
            if (selectedDefinition.SupportedTimeframes.Count > 0 && !selectedDefinition.SupportsTimeframe(_selectedTimeframe))
                _selectedTimeframe = selectedDefinition.SupportedTimeframes[0];
            _marketDataService = marketDataService;
            _metricsController = metricsController;
            _favoritePairsStore = favoritePairsStore ?? new FavoritePairsStore();
            FuturesQuote = quoteProvider is null ? null : new FuturesQuoteViewModel(quoteProvider);
            MarketMovers = new MarketMoversViewModel(exchangeService);

            Exchanges = new ObservableCollection<ExchangeOption>(_exchangeDefinitions
                .Select(exchange => new ExchangeOption(exchange.Id, exchange.DisplayName)));

            Timeframes = new ObservableCollection<string>(
                _exchangeDefinitions.First(d => d.Id == _selectedExchange).SupportedTimeframes);

            favTradingPairs = new ObservableCollection<TradingPairModel>();
            SearchResults = new ObservableCollection<string>();
            ComparisonSearchResults = new ObservableCollection<string>();

            SnapLastToRightCommand = new RelayCommand(() => SnapLastToRightRequested?.Invoke());
            ResetVerticalScaleCommand = new RelayCommand(() => ResetVerticalScaleRequested?.Invoke());
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
            StartMeasureCommand = new RelayCommand(() =>
                DrawingToolRequested?.Invoke(TechnicalAnalysisToolType.Measure));
            ShowMetricsCommand = new RelayCommand(ShowMetrics);
            ExportMetricsCommand = new RelayCommand(ExportMetrics);
            SelectSearchResultCommand = new RelayCommand(SelectSearchResult);
            SelectComparisonResultCommand = new RelayCommand(SelectComparisonResult);
            RemoveComparisonCommand = new RelayCommand(RemoveComparison);
            ToggleFavoriteCommand = new RelayCommand(ToggleFavorite);
            RemoveFavoriteCommand = new RelayCommand(RemoveFavorite);
            RefreshCatalogCommand = new RelayCommand(_ => RefreshCatalogAsync(), _ => IsInstrumentCatalogMode && !IsCatalogLoading);

            _metricsTimer = new Timer(
                _ => LogMetrics(),
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));
        }

        public ObservableCollection<ExchangeOption> Exchanges { get; }
        public ObservableCollection<string> Timeframes { get; }
        public ObservableCollection<TradingPairModel> favTradingPairs { get; }
        public ObservableCollection<string> SearchResults { get; }
        public ObservableCollection<string> ComparisonSearchResults { get; }
        public FuturesQuoteViewModel? FuturesQuote { get; }
        public MarketMoversViewModel MarketMovers { get; }

        public string SelectedExchange
        {
            get => _selectedExchange;
            set
            {
                if (_selectedExchange == value || string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                RemoveComparison();
                _selectedExchange = value;
                _loadedSymbolsExchange = null;
                _allSymbols = [];
                _searchText = "";
                IsSearchDropdownOpen = false;
                ResetCatalog();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SearchText));
                OnPropertyChanged(nameof(IsInstrumentCatalogMode));
                OnPropertyChanged(nameof(IsChartMode));
                OnPropertyChanged(nameof(IsBlockingBusy));
                OnPropertyChanged(nameof(SearchPlaceholder));
                UpdateTimeframesForExchange();
                OnPropertyChanged(nameof(IsCurrentPairFavorite));
                OnPropertyChanged(nameof(FavoriteActionText));
                if (_isReady)
                {
                    if (IsInstrumentCatalogMode) favTradingPairs.Clear();
                    else LoadFavoriteTradingPairs();
                    _ = MarketMovers.SetExchangeAsync(_selectedExchange);
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
                if (string.Equals(_comparisonSymbol, value, StringComparison.OrdinalIgnoreCase))
                    RemoveComparison();
                UpdateComparisonSearchResults();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCurrentPairFavorite));
                OnPropertyChanged(nameof(FavoriteActionText));
                if (IsChartMode) SymbolChanged?.Invoke(_selectedSymbol);
                _ = StartSelectionLoadAsync();
            }
        }

        private void UpdateTimeframesForExchange()
        {
            var exchange = _exchangeDefinitions.FirstOrDefault(d => d.Id == _selectedExchange);
            if (exchange is null)
                return;

            if (!Timeframes.SequenceEqual(exchange!.SupportedTimeframes))
            {
                Timeframes.Clear();
                foreach (var timeframe in exchange.SupportedTimeframes)
                    Timeframes.Add(timeframe);
            }

            if (exchange.SupportedTimeframes.Count > 0 && !exchange.SupportsTimeframe(_selectedTimeframe))
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

        public string ComparisonSearchText
        {
            get => _comparisonSearchText;
            set
            {
                if (_comparisonSearchText == value) return;
                _comparisonSearchText = value ?? string.Empty;
                OnPropertyChanged();
                UpdateComparisonSearchResults();
            }
        }

        public bool IsComparisonDropdownOpen
        {
            get => _isComparisonDropdownOpen;
            set
            {
                if (_isComparisonDropdownOpen == value) return;
                _isComparisonDropdownOpen = value;
                OnPropertyChanged();
            }
        }

        public string? ComparisonSymbol
        {
            get => _comparisonSymbol;
            private set
            {
                if (_comparisonSymbol == value) return;
                _comparisonSymbol = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasComparison));
            }
        }

        public bool HasComparison => ComparisonSymbol != null;

        public string ComparisonStatusText
        {
            get => _comparisonStatusText;
            private set
            {
                if (_comparisonStatusText == value) return;
                _comparisonStatusText = value;
                OnPropertyChanged();
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

                _isSearchDropdownOpen = value && IsChartMode;
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
                OnPropertyChanged(nameof(IsBlockingBusy));
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
        public ICommand ResetVerticalScaleCommand { get; }
        public ICommand StartHorizontalLineCommand { get; }
        public ICommand StartHorizontalRayCommand { get; }
        public ICommand StartTrendLineCommand { get; }
        public ICommand StartTrendChannelCommand { get; }
        public ICommand StartRectangleCommand { get; }
        public ICommand StartMeasureCommand { get; }
        public ICommand ShowMetricsCommand { get; }
        public ICommand ExportMetricsCommand { get; }
        public ICommand SelectSearchResultCommand { get; }
        public ICommand SelectComparisonResultCommand { get; }
        public ICommand RemoveComparisonCommand { get; }
        public ICommand ToggleFavoriteCommand { get; }
        public ICommand RemoveFavoriteCommand { get; }

        /// <summary>Shared chart series ready. Last arg selects live layout or demo fit.</summary>
        public event Action<ICandleSeriesReader, string, bool, int>? ChartSeriesReady;
        public event Action<string, int>? ChartSeriesCleared;
        public ICandleSeriesReader? CurrentSeries { get; private set; }
        public int CurrentSeriesVersion => Volatile.Read(ref _seriesVersion);
        public ICandleSeriesReader? CurrentComparisonSeries { get; private set; }
        public int CurrentComparisonVersion => Volatile.Read(ref _comparisonVersion);
        public event Action<ICandleSeriesReader, string, int>? ComparisonSeriesReady;
        public event Action<int>? ComparisonSeriesCleared;
        public event Action<int>? ComparisonDataChanged;

        public event Action<Candle>? LiveCandleUpdated;
        public event Action<Candle>? CandleClosed;
        public event Action<Candle[]>? OlderCandlesLoaded;
        public event Action<TechnicalAnalysisToolType>? DrawingToolRequested;
        public event Action? SnapLastToRightRequested;
        public event Action? ResetVerticalScaleRequested;
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

        /// <summary>
        /// Initializes the main window view model by ensuring the local database is initialized
        /// and loads the user's favorite trading pairs into memory.
        /// </summary>
        public async Task StartAsync()
        {
            _isReady = true;
            _ = MarketMovers.SetExchangeAsync(SelectedExchange);
            if (IsChartMode) SymbolChanged?.Invoke(SelectedSymbol);
            await StartSelectionLoadAsync();
        }

        /// <summary>
        /// Restarts candle loading for the current selection:
        /// <see cref="SelectedExchange"/>, <see cref="SelectedSymbol"/>, and <see cref="SelectedTimeframe"/>.
        /// Called on startup and whenever any of those three values changes.
        /// </summary>
        private Task StartSelectionLoadAsync()
        {
            // Startup has not finished, or a nested change (for example a fallback symbol)
            // is already covered by the load that is in progress.
            if (!_isReady || _suppressSelectionReload)
            {
                return Task.CompletedTask;
            }

            // A newer selection replaces any in-flight load of the previous market.
            var previous = _selectionLoadCts;
            var current = new CancellationTokenSource();
            _selectionLoadCts = current;
            // Older-history paging belongs to the previous series; drop it with the selection.
            var previousHistory = _historyLoadCts;
            _historyLoadCts = new CancellationTokenSource();
            previousHistory.Cancel();
            previousHistory.Dispose();
            previous?.Cancel();
            StopComparisonSubscription();
            // Chart callbacks compare this version so results from a cancelled selection are ignored.
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
            var exchange = SelectedExchange;
            var catalogMode = IsInstrumentCatalogMode;
            SetBusy(!catalogMode, catalogMode ? "" : $"Загрузка {SelectedSymbol}...");
            try
            {
                ct.ThrowIfCancellationRequested();
                if (catalogMode)
                {
                    await LoadCatalogAsync(exchange, source);
                    return;
                }
                if (!string.Equals(_loadedSymbolsExchange, exchange, StringComparison.OrdinalIgnoreCase))
                {
                    await LoadExchangeSymbolsAsync(exchange, ct);
                }

                ct.ThrowIfCancellationRequested();
                var key = new CandleCacheKey(
                    SelectedExchange, SelectedSymbol, SelectedTimeframe);
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
                {
                    if (catalogMode)
                        CatalogErrorText = (_catalogInstruments.Count > 0 ? "Список не обновлён. " : "") + ex.Message;
                    else ErrorOccurred?.Invoke("Ошибка загрузки", $"Не удалось загрузить выбранный рынок.\n\n{ex.Message}");
                }
            }
            finally
            {
                if (ReferenceEquals(_selectionLoadCts, source))
                {
                    _selectionLoadCts = null;
                    SetBusy(false);
                    IsCatalogLoading = false;
                }
                source.Dispose();
            }
        }

        public async Task LoadOlderAsync()
        {
            if (!_isReady || IsInstrumentCatalogMode)
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

        private void LoadFavoriteTradingPairs()
        {
            try
            {
                favTradingPairs.Clear();
                _favoriteSymbols = _favoritePairsStore.GetFavorites(SelectedExchange)
                                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string symbol in _favoriteSymbols)
                {
                    favTradingPairs.Add(new TradingPairModel(symbol));
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

        private void ToggleFavorite()
        {
            if (IsInstrumentCatalogMode) return;
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

        /// <summary>Moves a favorite to a new index and stores that order for the selected exchange.</summary>
        public void MoveFavorite(int oldIndex, int newIndex)
        {
            if (oldIndex < 0 || newIndex < 0 ||
                oldIndex >= favTradingPairs.Count || newIndex >= favTradingPairs.Count ||
                oldIndex == newIndex)
            {
                return;
            }

            favTradingPairs.Move(oldIndex, newIndex);
            try
            {
                _favoritePairsStore.SetOrder(SelectedExchange, favTradingPairs.Select(pair => pair.Symbol));
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(
                    "Ошибка избранного",
                    $"Не удалось сохранить порядок избранного.\n\n{ex.Message}");
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
                UpdateComparisonSearchResults();

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
                ComparisonSearchResults.Clear();
                IsComparisonDropdownOpen = false;

                Debug.WriteLine($"Failed to load markets for {exchange}: {ex.Message}");
                ErrorOccurred?.Invoke(
                    "Ошибка загрузки рынков",
                    $"Не удалось загрузить торговые пары с биржи {exchange}:\n\n{ex.Message}");
            }
        }

        private void UpdateSearchResults()
        {
            SearchResults.Clear();
            if (IsInstrumentCatalogMode)
            {
                IsSearchDropdownOpen = false;
                FilterCatalog();
                return;
            }

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
            if (IsInstrumentCatalogMode) return;
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

        /// <summary>Filters available comparison pairs by the current query, excluding the primary symbol.</summary>
        private void UpdateComparisonSearchResults()
        {
            ComparisonSearchResults.Clear();
            if (IsInstrumentCatalogMode)
            {
                IsComparisonDropdownOpen = false;
                return;
            }
            var query = _comparisonSearchText.Trim();
            if (query.Length == 0 || _allSymbols.Count == 0)
            {
                IsComparisonDropdownOpen = false;
                return;
            }
            foreach (var symbol in _allSymbols
                .Where(s => !s.Equals(SelectedSymbol, StringComparison.OrdinalIgnoreCase) &&
                            s.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(MaxSearchResults))
                ComparisonSearchResults.Add(symbol);
            IsComparisonDropdownOpen = ComparisonSearchResults.Count > 0;
        }

        /// <summary>Selects the symbol supplied in parameter and starts an independent comparison subscription.</summary>
        private void SelectComparisonResult(object? parameter)
        {
            if (IsInstrumentCatalogMode) return;
            if (parameter is not string symbol ||
                symbol.Equals(SelectedSymbol, StringComparison.OrdinalIgnoreCase) ||
                !_allSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase)) return;
            ComparisonSymbol = symbol;
            _comparisonSearchText = symbol;
            OnPropertyChanged(nameof(ComparisonSearchText));
            ComparisonSearchResults.Clear();
            IsComparisonDropdownOpen = false;
            _ = StartComparisonLoadAsync();
        }

        /// <summary>Clears comparison selection, cancels pending loads and releases the secondary subscription.</summary>
        private void RemoveComparison()
        {
            StopComparisonSubscription();
            ComparisonSymbol = null;
            ComparisonSearchText = string.Empty;
            ComparisonStatusText = string.Empty;
        }

        /// <summary>Invalidates comparison callbacks, cancels the current session and releases its subscription.</summary>
        private void StopComparisonSubscription()
        {
            _comparisonSessionCts?.Cancel();
            _comparisonSessionCts?.Dispose();
            _comparisonSessionCts = null;
            _comparisonSubscription?.Dispose();
            _comparisonSubscription = null;
            CurrentComparisonSeries = null;
            _comparisonHistoryLoading = false;
            _comparisonHistoryTarget = DateTime.MaxValue;
            _comparisonHistoryExhausted = false;
            int version = Interlocked.Increment(ref _comparisonVersion);
            ComparisonSeriesCleared?.Invoke(version);
        }

        /// <summary>Subscribes to the selected secondary pair and ignores results superseded by a selection change.</summary>
        private async Task StartComparisonLoadAsync()
        {
            StopComparisonSubscription();
            if (!_isReady || CurrentSeries == null || ComparisonSymbol is not { } symbol)
                return;
            var key = new CandleCacheKey(SelectedExchange, symbol, SelectedTimeframe);
            var source = new CancellationTokenSource();
            _comparisonSessionCts = source;
            var token = source.Token;
            int version = CurrentComparisonVersion;
            ComparisonStatusText = $"Загрузка {symbol}...";
            try
            {
                var sub = await _marketDataService.SubscribeAsync(key, HistoryBatchSize, token);
                if (source.IsCancellationRequested || !_isReady || version != CurrentComparisonVersion ||
                    CurrentSeries == null || ComparisonSymbol != symbol ||
                    key.Exchange != SelectedExchange || key.Timeframe != SelectedTimeframe)
                {
                    sub.Dispose();
                    return;
                }
                _comparisonKey = key;
                _comparisonSubscription = sub;
                CurrentComparisonSeries = sub.Series;
                ComparisonStatusText = string.Empty;
                sub.LiveCandleTicked += _ => RaiseComparisonDataChanged(sub, version);
                sub.CandleClosed += _ => RaiseComparisonDataChanged(sub, version);
                sub.OlderCandlesLoaded += _ => RaiseComparisonDataChanged(sub, version);
                ComparisonSeriesReady?.Invoke(sub.Series, symbol, version);
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!source.IsCancellationRequested && version == CurrentComparisonVersion)
                    ComparisonStatusText = $"Не удалось загрузить {symbol}: {ex.Message}";
            }
        }

        /// <summary>Publishes an update for sub only when its captured version still belongs to the current session.</summary>
        private void RaiseComparisonDataChanged(IMarketDataSubscription sub, int version)
        {
            if (ReferenceEquals(_comparisonSubscription, sub) && version == CurrentComparisonVersion)
                ComparisonDataChanged?.Invoke(version);
        }

        /// <summary>Extends secondary history to cover visibleFrom, the left edge of the chart viewport.</summary>
        public void RequestComparisonHistory(DateTime visibleFrom)
        {
            if (_comparisonSubscription == null || _comparisonHistoryExhausted) return;
            if (visibleFrom < _comparisonHistoryTarget) _comparisonHistoryTarget = visibleFrom;
            if (!_comparisonHistoryLoading) _ = LoadComparisonHistoryAsync();
        }

        /// <summary>Loads older secondary candles until the requested visible range is covered or history is exhausted.</summary>
        private async Task LoadComparisonHistoryAsync()
        {
            var sub = _comparisonSubscription;
            var source = _comparisonSessionCts;
            if (sub == null || source == null) return;
            var token = source.Token;
            var key = _comparisonKey;
            int version = CurrentComparisonVersion;
            _comparisonHistoryLoading = true;
            try
            {
                while (!source.IsCancellationRequested && ReferenceEquals(sub, _comparisonSubscription))
                {
                    var oldest = sub.Series.GetSnapshot().OldestTimestamp;
                    if (!oldest.HasValue || oldest.Value <= ComparisonSeriesMath.ToUnixMilliseconds(_comparisonHistoryTarget))
                        break;
                    int loaded = await _marketDataService.LoadOlderAsync(key, HistoryBatchSize, token);
                    if (token.IsCancellationRequested || version != CurrentComparisonVersion ||
                        !ReferenceEquals(sub, _comparisonSubscription)) return;
                    if (loaded == 0)
                    {
                        _comparisonHistoryExhausted = true;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (ReferenceEquals(sub, _comparisonSubscription))
                {
                    _comparisonHistoryExhausted = true;
                    ComparisonStatusText = $"Не удалось загрузить историю сравнения: {ex.Message}";
                }
            }
            finally
            {
                if (version == CurrentComparisonVersion)
                {
                    _comparisonHistoryLoading = false;
                    if (!source.IsCancellationRequested && ReferenceEquals(sub, _comparisonSubscription) &&
                        !_comparisonHistoryExhausted && sub.Series.GetSnapshot().OldestTimestamp is { } oldest &&
                        oldest > ComparisonSeriesMath.ToUnixMilliseconds(_comparisonHistoryTarget))
                        _ = LoadComparisonHistoryAsync();
                }
            }
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
                        SelectedExchange, SelectedSymbol, SelectedTimeframe))
                {
                    sub.Dispose();
                    ct.ThrowIfCancellationRequested();
                    return;
                }

                _subscription = sub;
                CurrentSeries = sub.Series;
                ChartSeriesReady?.Invoke(sub.Series, key.Timeframe, true, seriesVersion);
                if (ComparisonSymbol != null) _ = StartComparisonLoadAsync();

                sub.LiveCandleTicked += c =>
                {
                    if (ReferenceEquals(_subscription, sub)) LiveCandleUpdated?.Invoke(c);
                };
                sub.CandleClosed += closed =>
                {
                    if (ReferenceEquals(_subscription, sub)) CandleClosed?.Invoke(closed);
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
                        SelectedExchange, SelectedSymbol, SelectedTimeframe))
                {
                    return;
                }

                _subscription?.Dispose();
                _subscription = null;
                CurrentSeries = null;

                ErrorOccurred?.Invoke(
                    "Ошибка подключения к бирже",
                    $"Ошибка загрузки данных с биржи {SelectedExchange}:\n\n{ex.Message}\n\nБудут загружены демонстрационные данные.");

                var demoData = _dataProvider.LoadDemoData(key.Timeframe);
                var demoSeries = new CandleSeries();
                demoSeries.LoadInitial(demoData);
                CurrentSeries = demoSeries;
                ChartSeriesReady?.Invoke(demoSeries, key.Timeframe, false, seriesVersion);
                if (ComparisonSymbol != null)
                    ComparisonStatusText = "Сравнение недоступно для демонстрационных данных.";
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

        public void LogMetrics() => _metricsController.LogMetrics();

        /// <summary>Stops background work and releases primary, comparison, market movers and quote subscriptions.</summary>
        public void Dispose()
        {
            FuturesQuote?.Dispose();
            MarketMovers.Dispose();
            _isReady = false;
            StopComparisonSubscription();
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
    }
}
