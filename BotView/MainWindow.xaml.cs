using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using BotView.Chart;
using BotView.Chart.TechnicalAnalysis;
using BotView.Database;
using BotView.Models;
using BotView.Services;
using BotView.ViewModels;

namespace BotView
{
    /// <summary>Interaction logic for MainWindow.xaml — View adapter for ChartView and dialogs.</summary>
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly System.Windows.Threading.DispatcherTimer _renderTimeUpdateTimer;

        public MainWindow()
        {
            InitializeComponent();

            var databaseService = new DatabaseService();
            var metricsController = new MetricsController(App.ExchangeService);

            _viewModel = new MainWindowViewModel(
                databaseService,
                App.DataProvider,
                App.MarketDataService,
                metricsController);
            DataContext = _viewModel;

            SubscribeViewModelEvents();

            _viewModel.Initialize();

            _renderTimeUpdateTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _renderTimeUpdateTimer.Tick += (_, _) =>
            {
                if (chartView != null)
                {
                    _viewModel.RenderTime = chartView.LastRenderTimeMs;
                }
            };
            _renderTimeUpdateTimer.Start();

            chartView.LeftEdgeApproached += OnLeftEdgeApproached;
            chartView.AddIndicator(new Chart.IndicatorPane.RSIIndicator());
            Loaded += MainWindow_Loaded;
        }

        private void SubscribeViewModelEvents()
        {
            _viewModel.ChartSnapshotReady += OnChartSnapshotReady;
            _viewModel.LiveCandleUpdated += c =>
                Dispatcher.InvokeAsync(() => chartView.UpdateLastCandle(c));
            _viewModel.CandleClosed += (closed, newOpen) =>
                Dispatcher.InvokeAsync(() => chartView.OnCandleClosed(closed, newOpen));
            _viewModel.OlderCandlesLoaded += arr =>
                Dispatcher.InvokeAsync(() => chartView.PrependCandles(arr));
            _viewModel.DrawingToolRequested += OnDrawingToolRequested;
            _viewModel.SnapLastToRightRequested += () => chartView.SnapLastCandleToRightEdge();
            _viewModel.ShowMetricsRequested += OnShowMetricsRequested;
            _viewModel.ExportMetricsRequested += OnExportMetricsRequested;
            _viewModel.ErrorOccurred += OnErrorOccurred;
            _viewModel.SymbolChanged += OnSymbolChanged;
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.IsBusy))
                {
                    Cursor = _viewModel.IsBusy ? Cursors.Wait : Cursors.Arrow;
                }
            };
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await _viewModel.StartAsync();
        }

        private void OnChartSnapshotReady(CandlestickData data, bool useLiveLayout)
        {
            void Apply()
            {
                chartView.SetCandlestickData(data);
                if (useLiveLayout)
                {
                    chartView.ResetTimeScaleToTimeframe();
                    chartView.SnapLastCandleToRightEdge();
                    chartView.ResetPriceScaleToCurrentPrice();
                }
                else
                {
                    chartView.FitToData();
                }
            }

            if (Dispatcher.CheckAccess())
            {
                Apply();
            }
            else
            {
                Dispatcher.Invoke(Apply);
            }
        }

        private async void OnLeftEdgeApproached()
        {
            await _viewModel.LoadOlderAsync();
        }

        private void OnDrawingToolRequested(TechnicalAnalysisToolType toolType)
        {
            TechnicalAnalysisTool.StartCreating(toolType);
            chartView.Cursor = Cursors.Cross;
        }

        private async void OnSymbolChanged(string symbol)
        {
            var taManager = chartView.GetTechnicalAnalysisManager();
            await taManager.SetSymbolAsync(symbol);
        }

        private void OnShowMetricsRequested(string summary)
        {
            var metricsWindow = new Views.MetricsWindow(summary)
            {
                Owner = this
            };
            metricsWindow.ShowDialog();
        }

        private void OnExportMetricsRequested(string csvData)
        {
            var saveFileDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Экспорт метрик производительности",
                Filter = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = "csv",
                FileName = $"performance_metrics_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (saveFileDialog.ShowDialog() != true)
            {
                return;
            }

            System.IO.File.WriteAllText(saveFileDialog.FileName, csvData);
            MessageBox.Show(
                $"Метрики производительности успешно экспортированы в файл:\n{saveFileDialog.FileName}",
                "Экспорт завершен",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void OnErrorOccurred(string title, string message)
        {
            void Show() => MessageBox.Show(message, title, MessageBoxButton.OK,
                title.Contains("бирж", StringComparison.OrdinalIgnoreCase)
                    ? MessageBoxImage.Warning
                    : MessageBoxImage.Error);

            if (Dispatcher.CheckAccess())
            {
                Show();
            }
            else
            {
                Dispatcher.Invoke(Show);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _renderTimeUpdateTimer.Stop();

                if (chartView != null)
                {
                    chartView.GetTechnicalAnalysisManager().SaveTools();
                }

                _viewModel.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during cleanup: {ex.Message}");
            }

            base.OnClosed(e);
        }
    }
}
