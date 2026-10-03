using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
        private readonly System.Windows.Threading.DispatcherTimer _quoteUpdateTimer;

        public MainWindow()
        {
            InitializeComponent();

            var databaseService = new DatabaseService();
            var metricsController = new MetricsController(App.ExchangeService);

            _viewModel = new MainWindowViewModel(
                databaseService,
                App.DataProvider,
                App.ExchangeService,
                App.MarketDataService,
                metricsController,
                quoteProvider: App.QuoteProvider);
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

            _quoteUpdateTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(1)
            };
            _quoteUpdateTimer.Tick += async (_, _) =>
            {
                if (_viewModel.FuturesQuote is { } quote)
                    await quote.RefreshAsync();
            };

            chartView.LeftEdgeApproached += OnLeftEdgeApproached;
            chartView.VisibleTimeRangeChanged += _viewModel.RequestComparisonHistory;
            chartView.AddIndicator(new Chart.IndicatorPane.RSIIndicator());
            Loaded += MainWindow_Loaded;
        }

        private void SubscribeViewModelEvents()
        {
            _viewModel.ComparisonSeriesReady += (series, symbol, version) =>
            {
                void Apply()
                {
                    if (version == _viewModel.CurrentComparisonVersion &&
                        ReferenceEquals(series, _viewModel.CurrentComparisonSeries))
                        chartView.SetComparison(series, symbol);
                }
                if (Dispatcher.CheckAccess()) Apply();
                else Dispatcher.Invoke(Apply);
            };
            _viewModel.ComparisonSeriesCleared += version =>
            {
                void Clear()
                {
                    if (version == _viewModel.CurrentComparisonVersion)
                        chartView.ClearComparison();
                }
                if (Dispatcher.CheckAccess()) Clear();
                else Dispatcher.Invoke(Clear);
            };
            _viewModel.ComparisonDataChanged += version => Dispatcher.InvokeAsync(() =>
            {
                if (version == _viewModel.CurrentComparisonVersion)
                    chartView.OnComparisonChanged();
            });
            _viewModel.ChartSeriesReady += OnChartSeriesReady;
            _viewModel.ChartSeriesCleared += (timeframe, version) =>
            {
                void Clear()
                {
                    if (version == _viewModel.CurrentSeriesVersion && _viewModel.CurrentSeries == null)
                        chartView.SetSeries(new CandleSeries(), timeframe);
                }
                if (Dispatcher.CheckAccess()) Clear();
                else Dispatcher.Invoke(Clear);
            };
            _viewModel.LiveCandleUpdated += c =>
            {
                var series = _viewModel.CurrentSeries;
                var version = _viewModel.CurrentSeriesVersion;
                Dispatcher.InvokeAsync(() =>
                {
                    if (version == _viewModel.CurrentSeriesVersion && ReferenceEquals(series, _viewModel.CurrentSeries))
                        chartView.OnLiveCandleUpdated();
                });
            };
            _viewModel.CandleClosed += (closed, newOpen) =>
            {
                var series = _viewModel.CurrentSeries;
                var version = _viewModel.CurrentSeriesVersion;
                Dispatcher.InvokeAsync(() =>
                {
                    if (version == _viewModel.CurrentSeriesVersion && ReferenceEquals(series, _viewModel.CurrentSeries))
                        chartView.OnCandleClosed();
                });
            };
            _viewModel.OlderCandlesLoaded += arr =>
            {
                var series = _viewModel.CurrentSeries;
                var version = _viewModel.CurrentSeriesVersion;
                Dispatcher.InvokeAsync(() =>
                {
                    if (version == _viewModel.CurrentSeriesVersion && ReferenceEquals(series, _viewModel.CurrentSeries))
                        chartView.OnHistoryExtended();
                });
            };
            _viewModel.DrawingToolRequested += OnDrawingToolRequested;
            _viewModel.SnapLastToRightRequested += () => chartView.SnapLastCandleToRightEdge();
            _viewModel.ResetVerticalScaleRequested += () => chartView.ResetPriceScaleToCurrentPrice();
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
            if (_viewModel.FuturesQuote is { } quote)
            {
                _quoteUpdateTimer.Start();
                _ = quote.RefreshAsync();
            }
            await _viewModel.StartAsync();
        }

        private void TxtSymbolSearch_GotFocus(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SearchResults.Count > 0)
            {
                _viewModel.IsSearchDropdownOpen = true;
            }
        }

        private void TxtSymbolSearch_LostFocus(object sender, RoutedEventArgs e)
        {
            // Delay so ListBox click can run before popup closes.
            Dispatcher.BeginInvoke(() =>
            {
                if (!txtSymbolSearch.IsKeyboardFocusWithin)
                {
                    _viewModel.IsSearchDropdownOpen = false;
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void SearchResults_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBox listBox)
            {
                return;
            }

            var item = ItemsControl.ContainerFromElement(listBox, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item?.DataContext is string symbol)
            {
                _viewModel.SelectSearchResultCommand.Execute(symbol);
                e.Handled = true;
            }
        }

        private void TxtComparisonSearch_GotFocus(object sender, RoutedEventArgs e)
        {
            if (_viewModel.ComparisonSearchResults.Count > 0)
                _viewModel.IsComparisonDropdownOpen = true;
        }

        private void TxtComparisonSearch_LostFocus(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!txtComparisonSearch.IsKeyboardFocusWithin)
                    _viewModel.IsComparisonDropdownOpen = false;
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ComparisonResults_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBox listBox) return;
            var item = ItemsControl.ContainerFromElement(listBox, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item?.DataContext is string symbol)
            {
                _viewModel.SelectComparisonResultCommand.Execute(symbol);
                e.Handled = true;
            }
        }

        private void OnChartSeriesReady(ICandleSeriesReader series, string timeframe, bool useLiveLayout, int version)
        {
            void Apply()
            {
                if (version != _viewModel.CurrentSeriesVersion || !ReferenceEquals(series, _viewModel.CurrentSeries)) return;
                chartView.SetSeries(series, timeframe);
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
            chartView.CloseToolContextMenu();
            TechnicalAnalysisTool.StartCreating(toolType);
            chartView.Cursor = Cursors.Cross;
        }

        private async void OnSymbolChanged(string symbol)
        {
            chartView.CloseToolContextMenu();
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
                _quoteUpdateTimer.Stop();

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
