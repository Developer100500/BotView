using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Input;
using BotView.Chart.TechnicalAnalysis;
using BotView.Database;
using BotView.Interfaces;
using BotView.Models;
using BotView.Services;

namespace BotView.ViewModels
{
    public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
    {
        private const int HistoryBatchSize = 250;
        private const int MaxSearchResults = 50;

        private readonly DatabaseService _databaseService;
        private readonly IDataProvider _dataProvider;
        private readonly IExchangeService _exchangeService;
        private readonly IMarketDataService _marketDataService;
        private readonly MetricsController _metricsController;
        private readonly Timer _metricsTimer;

        private string _selectedExchange = "binance";
        private string _selectedSymbol = "BTC/USDT";
        private string _selectedTimeframe = "1d";
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
        private int _reloadGeneration;
        private List<string> _allSymbols = new();

        public MainWindowViewModel(
            DatabaseService databaseService,
            IDataProvider dataProvider,
            IExchangeService exchangeService,
            IMarketDataService marketDataService,
            MetricsController metricsController)
        {
            _databaseService = databaseService;
            _dataProvider = dataProvider;
            _exchangeService = exchangeService;
            _marketDataService = marketDataService;
            _metricsController = metricsController;

            Exchanges = new ObservableCollection<ExchangeOption>
            {
                new("binance", "Binance"),
                new("bybit", "Bybit"),
                new("okx", "OKX"),
                new("kraken", "Kraken")
            };

            Timeframes = new ObservableCollection<string>
            {
                "1m", "5m", "15m", "1h", "1d", "1w"
            };

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
                _ = OnExchangeChangedAsync();
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
                SymbolChanged?.Invoke(_selectedSymbol);
                _ = ReloadChartAsync();
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
                _ = ReloadChartAsync();
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

        /// <summary> Full chart snapshot ready. Second arg: true = live layout, false = demo fit. </summary>
        public event Action<CandlestickData, bool>? ChartSnapshotReady;

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
            await LoadExchangeSymbolsAsync(SelectedExchange);
            await SwitchSubscriptionAsync();
        }

        public async Task LoadOlderAsync()
        {
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
                    // Capture a consistent pair because search may switch subscriptions while awaiting.
                    var key = _currentKey;
                    var subscription = _subscription;
                    if (subscription == null || subscription.Key != key)
                    {
                        continue;
                    }

                    int loadedCount = await _marketDataService.LoadOlderAsync(key, HistoryBatchSize);
                    if (loadedCount == 0)
                    {
                        // The exchange has no more history; discard repeated edge notifications.
                        Interlocked.Exchange(ref _loadOlderPending, 0);
                        break;
                    }
                }
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
            _metricsTimer.Dispose();
            _subscription?.Dispose();
            _subscription = null;
            LogMetrics();
        }

        private void LoadFavoriteTradingPairs()
        {
            _suppressSelectionReload = true;
            try
            {
                TradingPairs.Clear();
                var pairs = _databaseService.GetTradingPairModels();
                foreach (var pair in pairs)
                {
                    TradingPairs.Add(pair);
                }

                if (TradingPairs.Count > 0 &&
                    TradingPairs.All(p => p.Symbol != SelectedSymbol))
                {
                    _selectedSymbol = TradingPairs[0].Symbol;
                    OnPropertyChanged(nameof(SelectedSymbol));
                }
            }
            finally
            {
                _suppressSelectionReload = false;
            }
        }

        private async Task OnExchangeChangedAsync()
        {
            if (!_isReady || _suppressSelectionReload)
            {
                return;
            }

            await LoadExchangeSymbolsAsync(SelectedExchange);
            await SwitchSubscriptionAsync();
        }

        private async Task LoadExchangeSymbolsAsync(string exchange)
        {
            var generation = Interlocked.Increment(ref _reloadGeneration);

            try
            {
                SetBusy(true, $"Загрузка рынков {exchange}...");
                var symbols = await _exchangeService.GetAvailableSymbolsAsync(exchange);

                if (generation != _reloadGeneration)
                {
                    return;
                }

                _allSymbols = symbols;
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
            catch (Exception ex)
            {
                if (generation != _reloadGeneration)
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
            finally
            {
                if (generation == _reloadGeneration)
                {
                    SetBusy(false);
                }
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

        private async Task ReloadChartAsync()
        {
            if (!_isReady || _suppressSelectionReload)
            {
                return;
            }

            await SwitchSubscriptionAsync();
        }

        private async Task SwitchSubscriptionAsync()
        {
            var generation = Interlocked.Increment(ref _reloadGeneration);

            try
            {
                SetBusy(true, $"Загрузка {SelectedSymbol}...");

                _subscription?.Dispose();
                _subscription = null;

                var key = new CandleCacheKey(
                    SelectedExchange,
                    SelectedSymbol,
                    SelectedTimeframe,
                    HistoryBatchSize);
                _currentKey = key;

                var sub = await _marketDataService.SubscribeAsync(key, initialHistory: HistoryBatchSize);

                if (generation != _reloadGeneration)
                {
                    sub.Dispose();
                    return;
                }

                _subscription = sub;

                sub.LiveCandleTicked += c => LiveCandleUpdated?.Invoke(c);
                sub.CandleClosed += (closed, newOpen) => CandleClosed?.Invoke(closed, newOpen);
                sub.OlderCandlesLoaded += arr => OlderCandlesLoaded?.Invoke(arr);
                
                var data = _marketDataService.BuildChartData(key);
                ChartSnapshotReady?.Invoke(data, true);
            }
            catch (Exception ex)
            {
                if (generation != _reloadGeneration)
                {
                    return;
                }

                ErrorOccurred?.Invoke(
                    "Ошибка подключения к бирже",
                    $"Ошибка загрузки данных с биржи {SelectedExchange}:\n\n{ex.Message}\n\nБудут загружены демонстрационные данные.");

                var demoData = _dataProvider.LoadDemoData(SelectedTimeframe);
                ChartSnapshotReady?.Invoke(demoData, false);
            }
            finally
            {
                if (generation == _reloadGeneration)
                {
                    SetBusy(false);
                }
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
