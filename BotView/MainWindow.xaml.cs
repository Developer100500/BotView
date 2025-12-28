using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using BotView.Chart;
using BotView.Chart.TechnicalAnalysis;
using BotView.Configuration;
using BotView.Services;
using BotView.Interfaces;
using BotView.Database;

namespace BotView
{
    /// <summary>Interaction logic for MainWindow.xaml</summary>
    public partial class MainWindow : Window
    {
        private readonly IExchangeService _exchangeService;
        private readonly IDataProvider _dataProvider;
        private readonly DatabaseService _databaseService;
        private readonly System.Windows.Threading.DispatcherTimer _metricsTimer;
        private readonly System.Windows.Threading.DispatcherTimer _renderTimeUpdateTimer;
        
        public MainWindow()
        {
            InitializeComponent();
            
            // Initialize database service
            _databaseService = new DatabaseService();
            InitializeDatabase();
            LoadTradingPairsFromDatabase();
            
            // Initialize services with logger
            _dataProvider = new DataProvider();
            var logger = new ConsoleExchangeLogger();
            _exchangeService = new ExchangeService(_dataProvider, logger);
            
            // Set up performance metrics timer (log metrics every 5 minutes)
            _metricsTimer = new System.Windows.Threading.DispatcherTimer();
            _metricsTimer.Interval = TimeSpan.FromMinutes(5);
            _metricsTimer.Tick += MetricsTimer_Tick;
            _metricsTimer.Start();
            
            // Set up render time update timer (update UI every 100ms)
            _renderTimeUpdateTimer = new System.Windows.Threading.DispatcherTimer();
            _renderTimeUpdateTimer.Interval = TimeSpan.FromMilliseconds(100);
            _renderTimeUpdateTimer.Tick += RenderTimeUpdateTimer_Tick;
            _renderTimeUpdateTimer.Start();
            
            // Загружаем демонстрационные данные после инициализации
            this.Loaded += MainWindow_Loaded;
        }

        /// <summary>Инициализирует базу данных и заполняет тестовыми данными если нужно</summary>
        private void InitializeDatabase()
        {
            try
            {
                // Тестовое подключение
                if (!_databaseService.TestConnection())
                {
                    MessageBox.Show("Не удалось подключиться к базе данных.", "Ошибка БД", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Инициализируем таблицы
                _databaseService.Initialize();

                // Если торговых пар нет, заполняем тестовыми данными
                if (!_databaseService.HasTradingPairs())
                {
                    _databaseService.SeedTestData();
                    Debug.WriteLine("Database seeded with test data.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при инициализации базы данных:\n{ex.Message}", "Ошибка БД", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Загружает торговые пары из базы данных в ListBox</summary>
        private void LoadTradingPairsFromDatabase()
        {
            try
            {
                lstTradingPairs.Items.Clear();

                var tradingPairs = _databaseService.GetAllTradingPairs();
                bool isFirst = true;

                foreach (var pair in tradingPairs)
                {
                    var item = new ListBoxItem
                    {
                        Content = pair.Symbol,
                        Tag = pair.Symbol,
                        IsSelected = isFirst
                    };
                    lstTradingPairs.Items.Add(item);
                    isFirst = false;
                }

                Debug.WriteLine($"Loaded {tradingPairs.Count} trading pairs from database.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading trading pairs from database: {ex.Message}");
            }
        }

        private void RenderTimeUpdateTimer_Tick(object? sender, EventArgs e)
        {
            // Обновляем счетчик времени отрисовки в StatusBar
            if (fpsCounter != null && chartView != null)
            {
                double renderTime = chartView.LastRenderTimeMs;
                fpsCounter.Text = $"Render: {renderTime:F2} ms";
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Initialize technical analysis manager with the initial symbol
            var taManager = chartView.GetTechnicalAnalysisManager();
            await taManager.SetSymbolAsync("BTC/USDT");

            // Load real data instead of demo data
            await LoadRealData("binance", "BTC/USDT", "1d");
        }

        /// <summary>Loads real data from cryptocurrency exchange</summary>
        /// <param name="exchange">Exchange name (e.g., "binance")</param>
        /// <param name="symbol">Trading pair symbol (e.g., "BTC/USDT")</param>
        /// <param name="timeframe">Timeframe (e.g., "1d")</param>
        /// <param name="limit">Number of candles to load (default: 500)</param>
        private async Task LoadRealData(string exchange, string symbol, string timeframe, int limit = 500)
        {
            try
            {
                // Show loading indicator (you can implement a loading spinner here)
                this.Cursor = Cursors.Wait;
                
                // Load data asynchronously without blocking UI
                var candlestickData = await Task.Run(async () =>
                {
                    return await _exchangeService.GetCandlestickDataAsync(exchange, symbol, timeframe, limit);
                });
                
                // Update chart on UI thread
                Dispatcher.Invoke(() =>
                {
                    chartView.SetCandlestickData(candlestickData);
                    chartView.FitToData();
                });
                
                Debug.WriteLine($"Successfully loaded {candlestickData.candles.Length} candles from {exchange} for {symbol} ({timeframe})");
            }
            catch (Exception ex)
            {
                // Handle errors and show user-friendly messages
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(
                        $"Ошибка загрузки данных с биржи {exchange}:\n\n{ex.Message}\n\nБудут загружены демонстрационные данные.",
                        "Ошибка подключения к бирже",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    
                    // Fallback to demo data
                    LoadDemoData();
                });
                
                Debug.WriteLine($"Failed to load real data: {ex.Message}");
            }
            finally
            {
                // Hide loading indicator
                this.Cursor = Cursors.Arrow;
            }
        }

        private void LoadDemoData()
        {
            // Создаем массив демонстрационных свечей
            DateTime startTime = DateTime.Parse("2025/10/01 00:00:00");
            var demoCandles = new OHLCV[30];
            
            Random random = new Random();
            double basePrice = 100.0;
            
            for (int i = 0; i < demoCandles.Length; i++)
            {
                // Генерируем случайные данные OHLCV
                double open = basePrice + random.NextDouble() * 10 - 5;
                double close = open + random.NextDouble() * 8 - 4;
                double high = Math.Max(open, close) + random.NextDouble() * 3;
                double low = Math.Min(open, close) - random.NextDouble() * 3;
                double volume = 1000 + random.NextDouble() * 2000;
                
                // Calculate proper timestamp for each candle (1 day intervals)
                DateTime candleTime = startTime.AddDays(i);
                long timestamp = ((DateTimeOffset)candleTime).ToUnixTimeMilliseconds();
                
                demoCandles[i] = new OHLCV(timestamp, open, high, low, close, volume);
                basePrice = close; // Следующая свеча начинается с цены закрытия предыдущей
            }
            
            // Создаем структуру данных свечей
            var candlestickData = new CandlestickData(
                timeframe: "1d",
                beginDateTime: startTime,
                endDateTime: startTime.AddDays(demoCandles.Length - 1),
                candles: demoCandles
            );
            
            // Загружаем данные в график
            chartView.SetCandlestickData(candlestickData);
            
            // Подгоняем график под данные
            chartView.FitToData();
        }

        // === EVENT HANDLERS FOR CONTROL BUTTONS ===

        private void BtnPositionToLast_Click(object sender, RoutedEventArgs e)
        {
            chartView.PositionToLastCandle();
        }

        private async void CmbExchange_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_exchangeService == null || chartView == null || lstTradingPairs == null || cmbTimeframe == null) return;
            
            var selectedExchange = cmbExchange.SelectedItem as ComboBoxItem;
            var selectedPair = lstTradingPairs.SelectedItem as ListBoxItem;
            var selectedTimeframe = cmbTimeframe.SelectedItem as ComboBoxItem;
            
            if (selectedExchange?.Tag != null && selectedPair?.Tag != null && selectedTimeframe?.Tag != null)
            {
                string exchange = selectedExchange.Tag.ToString() ?? "binance";
                string symbol = selectedPair.Tag.ToString() ?? "BTC/USDT";
                string timeframe = selectedTimeframe.Tag.ToString() ?? "1d";
                
                await LoadRealData(exchange, symbol, timeframe);
            }
        }

        private async void CmbTimeframe_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_exchangeService == null || chartView == null || cmbExchange == null || lstTradingPairs == null) return;
            
            var selectedExchange = cmbExchange.SelectedItem as ComboBoxItem;
            var selectedPair = lstTradingPairs.SelectedItem as ListBoxItem;
            var selectedTimeframe = cmbTimeframe.SelectedItem as ComboBoxItem;
            
            if (selectedExchange?.Tag != null && selectedPair?.Tag != null && selectedTimeframe?.Tag != null)
            {
                string exchange = selectedExchange.Tag.ToString() ?? "binance";
                string symbol = selectedPair.Tag.ToString() ?? "BTC/USDT";
                string timeframe = selectedTimeframe.Tag.ToString() ?? "1d";
                
                await LoadRealData(exchange, symbol, timeframe);
            }
        }

        private async void LstTradingPairs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Проверяем что все необходимые объекты инициализированы
            if (_exchangeService == null || chartView == null || cmbExchange == null || cmbTimeframe == null || lstTradingPairs == null) return;
            
            var selectedExchange = cmbExchange.SelectedItem as ComboBoxItem;
            var selectedPair = lstTradingPairs.SelectedItem as ListBoxItem;
            var selectedTimeframe = cmbTimeframe.SelectedItem as ComboBoxItem;
            
            if (selectedExchange?.Tag != null && selectedPair?.Tag != null && selectedTimeframe?.Tag != null)
            {
                string exchange = selectedExchange.Tag.ToString() ?? "binance";
                string symbol = selectedPair.Tag.ToString() ?? "BTC/USDT";
                string timeframe = selectedTimeframe.Tag.ToString() ?? "1d";
                
                // Сохраняем инструменты текущей пары и загружаем инструменты новой пары
                var taManager = chartView.GetTechnicalAnalysisManager();
                await taManager.SetSymbolAsync(symbol);

                await LoadRealData(exchange, symbol, timeframe);
            }
        }

        private void BtnHorizontalLine_Click(object sender, RoutedEventArgs e)
        {
            if (chartView == null)
                return;

            // Включаем режим создания горизонтальной линии
            TechnicalAnalysisTool.StartCreating(TechnicalAnalysisToolType.HorizontalLine);
            
            // Меняем курсор на Cross (перекрестие)
            chartView.Cursor = Cursors.Cross;
        }

        private void BtnTrendLine_Click(object sender, RoutedEventArgs e)
        {
            if (chartView == null)
                return;

            // Включаем режим создания трендовой линии
            TechnicalAnalysisTool.StartCreating(TechnicalAnalysisToolType.TrendLine);
            
            // Меняем курсор на Cross (перекрестие)
            chartView.Cursor = Cursors.Cross;
        }

        private void BtnTrendChannel_Click(object sender, RoutedEventArgs e)
        {
            if (chartView == null)
                return;

			chartView.Cursor = Cursors.Cross;
			TechnicalAnalysisTool.StartCreating(TechnicalAnalysisToolType.TrendChannel);
        }

        private void BtnRectangle_Click(object sender, RoutedEventArgs e)
        {
            if (chartView == null)
                return;

            chartView.Cursor = Cursors.Cross;
            TechnicalAnalysisTool.StartCreating(TechnicalAnalysisToolType.Rectangle);
        }

        private void BtnShowMetrics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var performanceMetrics = _exchangeService.GetPerformanceMetrics();
                var summary = performanceMetrics.GetFormattedSummary();
                
                // Create a new window to display performance metrics
                var metricsWindow = new Window
                {
                    Title = "Метрики производительности",
                    Width = 600,
                    Height = 400,
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                
                var scrollViewer = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Margin = new Thickness(10)
                };
                
                var textBlock = new TextBlock
                {
                    Text = summary,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.NoWrap
                };
                
                scrollViewer.Content = textBlock;
                metricsWindow.Content = scrollViewer;
                
                metricsWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Ошибка при получении метрик производительности:\n\n{ex.Message}",
                    "Ошибка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private void LoadDemoDataWithTimeframe(string timeframe)
        {
            // Создаем демонстрационные данные для выбранного timeframe
            DateTime startTime = DateTime.Parse("2025/10/01 00:00:00");
            int candleCount = timeframe switch
            {
                "1m" => 1440, // 1 день в минутах
                "5m" => 288,  // 1 день в 5-минутках
                "15m" => 96,  // 1 день в 15-минутках
                "1h" => 24,   // 1 день в часах
                "1d" => 30,   // 30 дней
                "1w" => 12,   // 12 недель
                _ => 30
            };
            
            var demoCandles = new OHLCV[candleCount];
            Random random = new Random();
            double basePrice = 100.0;
            
            // Calculate timeframe interval for proper timestamps
            TimeSpan timeframeInterval = timeframe switch
            {
                "1m" => TimeSpan.FromMinutes(1),
                "5m" => TimeSpan.FromMinutes(5),
                "15m" => TimeSpan.FromMinutes(15),
                "1h" => TimeSpan.FromHours(1),
                "1d" => TimeSpan.FromDays(1),
                "1w" => TimeSpan.FromDays(7),
                _ => TimeSpan.FromDays(1)
            };
            
            for (int i = 0; i < demoCandles.Length; i++)
            {
                double open = basePrice + random.NextDouble() * 10 - 5;
                double close = open + random.NextDouble() * 8 - 4;
                double high = Math.Max(open, close) + random.NextDouble() * 3;
                double low = Math.Min(open, close) - random.NextDouble() * 3;
                double volume = 1000 + random.NextDouble() * 2000;
                
                // Calculate proper timestamp for each candle based on timeframe
                DateTime candleTime = startTime.Add(TimeSpan.FromTicks(timeframeInterval.Ticks * i));
                long timestamp = ((DateTimeOffset)candleTime).ToUnixTimeMilliseconds();
                
                demoCandles[i] = new OHLCV(timestamp, open, high, low, close, volume);
                basePrice = close;
            }
            
            // Вычисляем конечное время на основе timeframe
            TimeSpan timeframeSpan = timeframe switch
            {
                "1m" => TimeSpan.FromMinutes(candleCount),
                "5m" => TimeSpan.FromMinutes(candleCount * 5),
                "15m" => TimeSpan.FromMinutes(candleCount * 15),
                "1h" => TimeSpan.FromHours(candleCount),
                "1d" => TimeSpan.FromDays(candleCount),
                "1w" => TimeSpan.FromDays(candleCount * 7),
                _ => TimeSpan.FromDays(candleCount)
            };
            
            var candlestickData = new CandlestickData(
                timeframe: timeframe,
                beginDateTime: startTime,
                endDateTime: startTime.Add(timeframeSpan),
                candles: demoCandles
            );
            
            chartView.SetCandlestickData(candlestickData);
            chartView.FitToData();
        }

        private void BtnExportMetrics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var performanceMetrics = _exchangeService.GetPerformanceMetrics();
                var csvData = performanceMetrics.ExportMetricsToCSV();
                
                // Create save file dialog
                var saveFileDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Экспорт метрик производительности",
                    Filter = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                    DefaultExt = "csv",
                    FileName = $"performance_metrics_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
                };
                
                if (saveFileDialog.ShowDialog() == true)
                {
                    System.IO.File.WriteAllText(saveFileDialog.FileName, csvData);
                    MessageBox.Show(
                        $"Метрики производительности успешно экспортированы в файл:\n{saveFileDialog.FileName}",
                        "Экспорт завершен",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Ошибка при экспорте метрик производительности:\n\n{ex.Message}",
                    "Ошибка экспорта",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private void MetricsTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // Log detailed performance metrics periodically
                _exchangeService.LogDetailedPerformanceMetrics();
                
                // Clear expired cache entries to maintain performance
                _exchangeService.ClearExpiredCache();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error logging performance metrics: {ex.Message}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                // Stop the timers
                _metricsTimer?.Stop();
                _renderTimeUpdateTimer?.Stop();
                
                // Save technical analysis tools before closing
                if (chartView != null)
                {
                    var taManager = chartView.GetTechnicalAnalysisManager();
                    taManager.SaveTools();
                }
                
                // Log final performance metrics before closing
                _exchangeService?.LogDetailedPerformanceMetrics();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during cleanup: {ex.Message}");
            }
            
            base.OnClosed(e);
        }
    }
}