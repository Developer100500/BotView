using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BotView.Chart;
using BotView.Chart.TechnicalAnalysis;
using BotView.Database;
using BotView.ViewModels;
using BotView.Services;

namespace BotView
{
    /// <summary>Interaction logic for MainWindow.xaml</summary>
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly System.Windows.Threading.DispatcherTimer _metricsTimer;
        private readonly System.Windows.Threading.DispatcherTimer _renderTimeUpdateTimer;
        
        public MainWindow()
        {
            InitializeComponent();
            
            // Initialize ViewModel with controllers (services)
            var databaseService = new DatabaseService();
            var metricsController = new MetricsController(App.ExchangeService);
            
            _viewModel = new MainWindowViewModel(databaseService, App.DataProvider, metricsController);
            DataContext = _viewModel;
            
            // Initialize database and load trading pairs
            try
            {
                _viewModel.InitializeDatabase();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Database initialization failed: {ex.Message}");
                MessageBox.Show(
                    $"Не удалось инициализировать базу данных.\n\n{ex.Message}",
                    "Ошибка БД",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            _viewModel.LoadTradingPairs();
            LoadTradingPairsToUI();
            
            // Set up performance metrics timer (log metrics every 5 minutes)
            _metricsTimer = new System.Windows.Threading.DispatcherTimer();
            _metricsTimer.Interval = TimeSpan.FromMinutes(5);
            _metricsTimer.Tick += (s, e) => _viewModel.LogMetrics();
            _metricsTimer.Start();
            
            // Set up render time update timer (update UI every 100ms)
            _renderTimeUpdateTimer = new System.Windows.Threading.DispatcherTimer();
            _renderTimeUpdateTimer.Interval = TimeSpan.FromMilliseconds(100);
            _renderTimeUpdateTimer.Tick += RenderTimeUpdateTimer_Tick;
            _renderTimeUpdateTimer.Start();
            
            this.Loaded += MainWindow_Loaded;
            chartView.AddIndicator(new Chart.IndicatorPane.RSIIndicator());
        }

        private void LoadTradingPairsToUI()
        {
            lstTradingPairs.Items.Clear();
            bool isFirst = true;

            foreach (var pair in _viewModel.TradingPairs)
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
        }

        private void RenderTimeUpdateTimer_Tick(object? sender, EventArgs e)
        {
            if (fpsCounter != null && chartView != null)
            {
                _viewModel.RenderTime = chartView.LastRenderTimeMs;
                fpsCounter.Text = $"Render: {_viewModel.RenderTime:F2} ms";
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var taManager = chartView.GetTechnicalAnalysisManager();
            await taManager.SetSymbolAsync(_viewModel.SelectedSymbol);
            await LoadRealData();
        }

        private async Task LoadRealData()
        {
            try
            {
                this.Cursor = Cursors.Wait;
                
                var candlestickData = await _viewModel.LoadDataAsync();
                
                if (candlestickData.HasValue)
                {
                    var data = candlestickData.Value;
                    Dispatcher.Invoke(() =>
                    {
                        chartView.SetCandlestickData(data);
                        chartView.FitToData();
                    });
                }
                else
                {
                    throw new Exception("Failed to load data from exchange");
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(
                        $"Ошибка загрузки данных с биржи {_viewModel.SelectedExchange}:\n\n{ex.Message}\n\nБудут загружены демонстрационные данные.",
                        "Ошибка подключения к бирже",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    
                    var demoData = _viewModel.LoadDemoData();
                    chartView.SetCandlestickData(demoData);
                    chartView.FitToData();
                });
            }
            finally
            {
                this.Cursor = Cursors.Arrow;
            }
        }

        // === EVENT HANDLERS FOR CONTROL BUTTONS ===

        private void BtnSnapLastToRight_Click(object sender, RoutedEventArgs e)
        {
            chartView.SnapLastCandleToRightEdge();
        }

        private async void CmbExchange_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (chartView == null || lstTradingPairs == null || cmbTimeframe == null) return;
            
            var selectedExchange = cmbExchange.SelectedItem as ComboBoxItem;
            var selectedPair = lstTradingPairs.SelectedItem as ListBoxItem;
            var selectedTimeframe = cmbTimeframe.SelectedItem as ComboBoxItem;
            
            if (selectedExchange?.Tag != null && selectedPair?.Tag != null && selectedTimeframe?.Tag != null)
            {
                _viewModel.SelectedExchange = selectedExchange.Tag.ToString() ?? "binance";
                _viewModel.SelectedSymbol = selectedPair.Tag.ToString() ?? "BTC/USDT";
                _viewModel.SelectedTimeframe = selectedTimeframe.Tag.ToString() ?? "1d";
                
                await LoadRealData();
            }
        }

        private async void CmbTimeframe_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (chartView == null || cmbExchange == null || lstTradingPairs == null) return;
            
            var selectedExchange = cmbExchange.SelectedItem as ComboBoxItem;
            var selectedPair = lstTradingPairs.SelectedItem as ListBoxItem;
            var selectedTimeframe = cmbTimeframe.SelectedItem as ComboBoxItem;
            
            if (selectedExchange?.Tag != null && selectedPair?.Tag != null && selectedTimeframe?.Tag != null)
            {
                _viewModel.SelectedExchange = selectedExchange.Tag.ToString() ?? "binance";
                _viewModel.SelectedSymbol = selectedPair.Tag.ToString() ?? "BTC/USDT";
                _viewModel.SelectedTimeframe = selectedTimeframe.Tag.ToString() ?? "1d";
                
                await LoadRealData();
            }
        }

        private async void LstTradingPairs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (chartView == null || cmbExchange == null || cmbTimeframe == null || lstTradingPairs == null)
                return;
            
            var selectedExchange = cmbExchange.SelectedItem as ComboBoxItem;
            var selectedPair = lstTradingPairs.SelectedItem as ListBoxItem;
            var selectedTimeframe = cmbTimeframe.SelectedItem as ComboBoxItem;
            
            if (selectedExchange?.Tag != null && selectedPair?.Tag != null && selectedTimeframe?.Tag != null)
            {
                _viewModel.SelectedExchange = selectedExchange.Tag.ToString() ?? "binance";
                _viewModel.SelectedSymbol = selectedPair.Tag.ToString() ?? "BTC/USDT";
                _viewModel.SelectedTimeframe = selectedTimeframe.Tag.ToString() ?? "1d";
                
                var taManager = chartView.GetTechnicalAnalysisManager();
                await taManager.SetSymbolAsync(_viewModel.SelectedSymbol);

                await LoadRealData();
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
                var summary = _viewModel.GetFormattedMetrics();
                var metricsWindow = new Views.MetricsWindow(summary)
                {
                    Owner = this
                };
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

        private void BtnExportMetrics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var csvData = _viewModel.ExportMetricsToCSV();
                
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

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _metricsTimer?.Stop();
                _renderTimeUpdateTimer?.Stop();
                
                if (chartView != null)
                {
                    var taManager = chartView.GetTechnicalAnalysisManager();
                    taManager.SaveTools();
                }
                
                _viewModel.LogMetrics();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during cleanup: {ex.Message}");
            }
            
            base.OnClosed(e);
        }
    }
}