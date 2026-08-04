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

        private readonly DatabaseService _databaseService;
        private readonly IDataProvider _dataProvider;
        private readonly IMarketDataService _marketDataService;
        private readonly MetricsController _metricsController;
        private readonly Timer _metricsTimer;

        private string _selectedExchange = "binance";
        private string _selectedSymbol = "BTC/USDT";
        private string _selectedTimeframe = "1d";
        private double _renderTime;
        private bool _isBusy;
        private bool _isReady;
        private bool _suppressSelectionReload;
        private IMarketDataSubscription? _subscription;
        private CandleCacheKey _currentKey;
        private int _loadingOlder;

        public MainWindowViewModel(
            DatabaseService databaseService,
            IDataProvider dataProvider,
            IMarketDataService marketDataService,
            MetricsController metricsController)
        {
            _databaseService = databaseService;
            _dataProvider = dataProvider;
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

            SnapLastToRightCommand = new RelayCommand(() => SnapLastToRightRequested?.Invoke());
            StartHorizontalLineCommand = new RelayCommand(() =>
                DrawingToolRequested?.Invoke(TechnicalAnalysisToolType.HorizontalLine));
            StartTrendLineCommand = new RelayCommand(() =>
                DrawingToolRequested?.Invoke(TechnicalAnalysisToolType.TrendLine));
            StartTrendChannelCommand = new RelayCommand(() =>
                DrawingToolRequested?.Invoke(TechnicalAnalysisToolType.TrendChannel));
            StartRectangleCommand = new RelayCommand(() =>
                DrawingToolRequested?.Invoke(TechnicalAnalysisToolType.Rectangle));
            ShowMetricsCommand = new RelayCommand(ShowMetrics);
            ExportMetricsCommand = new RelayCommand(ExportMetrics);

            _metricsTimer = new Timer(
                _ => LogMetrics(),
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));
        }

        public ObservableCollection<ExchangeOption> Exchanges { get; }
        public ObservableCollection<string> Timeframes { get; }
        public ObservableCollection<TradingPairModel> TradingPairs { get; }

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
                _ = ReloadChartAsync();
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

        public ICommand SnapLastToRightCommand { get; }
        public ICommand StartHorizontalLineCommand { get; }
        public ICommand StartTrendLineCommand { get; }
        public ICommand StartTrendChannelCommand { get; }
        public ICommand StartRectangleCommand { get; }
        public ICommand ShowMetricsCommand { get; }
        public ICommand ExportMetricsCommand { get; }

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

            LoadTradingPairs();
        }

        public async Task StartAsync()
        {
            _isReady = true;
            SymbolChanged?.Invoke(SelectedSymbol);
            await SwitchSubscriptionAsync();
        }

        public async Task LoadOlderAsync()
        {
            if (Interlocked.CompareExchange(ref _loadingOlder, 1, 0) != 0)
            {
                return;
            }

            try
            {
                await _marketDataService.LoadOlderAsync(_currentKey, HistoryBatchSize);
            }
            finally
            {
                Interlocked.Exchange(ref _loadingOlder, 0);
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

        private void LoadTradingPairs()
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
            try
            {
                IsBusy = true;

                _subscription?.Dispose();
                _subscription = null;

                var key = new CandleCacheKey(
                    SelectedExchange,
                    SelectedSymbol,
                    SelectedTimeframe,
                    HistoryBatchSize);
                _currentKey = key;

                var sub = await _marketDataService.SubscribeAsync(key, initialHistory: HistoryBatchSize);
                _subscription = sub;

                var data = _marketDataService.BuildChartData(key);
                ChartSnapshotReady?.Invoke(data, true);

                sub.LiveCandleTicked += c => LiveCandleUpdated?.Invoke(c);
                sub.CandleClosed += (closed, newOpen) => CandleClosed?.Invoke(closed, newOpen);
                sub.OlderCandlesLoaded += arr => OlderCandlesLoaded?.Invoke(arr);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(
                    "Ошибка подключения к бирже",
                    $"Ошибка загрузки данных с биржи {SelectedExchange}:\n\n{ex.Message}\n\nБудут загружены демонстрационные данные.");

                var demoData = _dataProvider.LoadDemoData(SelectedTimeframe);
                ChartSnapshotReady?.Invoke(demoData, false);
            }
            finally
            {
                IsBusy = false;
            }
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
