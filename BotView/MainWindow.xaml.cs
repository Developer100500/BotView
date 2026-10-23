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
        private ContextMenu? _pairContextMenu;
        private readonly Dictionary<(string Exchange, string Symbol), Views.OrderBookWindow> _orderBooks = new();
        private readonly List<Task> _orderBookCleanup = new();

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
                var moversRefresh = _viewModel.MarketMovers.RefreshAsync();
                if (_viewModel.FuturesQuote is { } quote)
                    await quote.RefreshAsync();
                await moversRefresh;
            };

            chartView.LeftEdgeApproached += OnLeftEdgeApproached;
            chartView.VisibleTimeRangeChanged += _viewModel.RequestComparisonHistory;
            chartView.AddIndicator(new Chart.IndicatorPane.RSIIndicator());
            Loaded += MainWindow_Loaded;
        }

        /// <summary>Connects view model updates to the chart through the UI dispatcher and rejects stale series versions.</summary>
        private void SubscribeViewModelEvents()
        {
            _viewModel.ComparisonSeriesReady += (series, symbol, version) =>
            {
                // Attaches the captured series only if this comparison version still owns the chart.
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
                // Clears the overlay only if the captured cancellation version is still current.
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
            _viewModel.CandleClosed += closed =>
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
            _quoteUpdateTimer.Start();
            if (_viewModel.FuturesQuote is { } quote)
            {
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
                if (!txtSymbolSearch.IsKeyboardFocusWithin && !lstSearchResults.IsKeyboardFocusWithin &&
                    _pairContextMenu?.IsOpen != true)
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

        private void Pair_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel.IsInstrumentCatalogMode) return;
            if (sender is not FrameworkElement target) return;
            var menu = Views.CoinInfoContextMenu.Create(target, pair =>
            {
                _viewModel.IsSearchDropdownOpen = false;
                _viewModel.IsComparisonDropdownOpen = false;
                new Views.CoinInfoWindow(pair, App.CoinInfoProvider) { Owner = this }.Show();
            }, OpenOrderBook);
            if (menu is null) return;
            if (_pairContextMenu is not null) _pairContextMenu.IsOpen = false;
            _pairContextMenu = menu;
            menu.Closed += (_, _) =>
            {
                if (ReferenceEquals(_pairContextMenu, menu)) _pairContextMenu = null;
                if (!txtSymbolSearch.IsKeyboardFocusWithin && !lstSearchResults.IsKeyboardFocusWithin)
                    _viewModel.IsSearchDropdownOpen = false;
                if (!txtComparisonSearch.IsKeyboardFocusWithin)
                    _viewModel.IsComparisonDropdownOpen = false;
            };
            // Handle the preview event so right-clicking a row does not switch the chart.
            e.Handled = true;
            target.ContextMenu = menu;
            menu.IsOpen = true;
        }

        private void OpenOrderBook(string symbol)
        {
            if (_viewModel.IsInstrumentCatalogMode) return;
            _viewModel.IsSearchDropdownOpen = false;
            _viewModel.IsComparisonDropdownOpen = false;
            var key = (Exchange: _viewModel.SelectedExchange, Symbol: symbol);
            if (_orderBooks.TryGetValue(key, out var existing))
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }
            var window = new Views.OrderBookWindow(key.Exchange, symbol, App.OrderBookService) { Owner = this };
            _orderBooks.Add(key, window);
            window.Closed += (_, _) =>
            {
                _orderBooks.Remove(key);
                _orderBookCleanup.RemoveAll(t => t.IsCompletedSuccessfully);
                _orderBookCleanup.Add(window.CleanupTask);
            };
            window.Show();
        }

        /// <summary>Opens comparison results on focus; sender identifies the input and e carries its focus event.</summary>
        private void TxtComparisonSearch_GotFocus(object sender, RoutedEventArgs e)
        {
            if (_viewModel.ComparisonSearchResults.Count > 0)
                _viewModel.IsComparisonDropdownOpen = true;
        }

        /// <summary>Closes comparison results after focus leaves; sender identifies the input and e carries the event.</summary>
        private void TxtComparisonSearch_LostFocus(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!txtComparisonSearch.IsKeyboardFocusWithin && _pairContextMenu?.IsOpen != true)
                    _viewModel.IsComparisonDropdownOpen = false;
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>Selects the clicked comparison row from sender and consumes the pointer event e.</summary>
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
            chartView.BeginToolCreation(toolType);
        }

        private async void OnSymbolChanged(string symbol)
        {
            if (Dispatcher.CheckAccess())
                await chartView.SetSymbolAsync(symbol);
            else
                await Dispatcher.InvokeAsync(() => chartView.SetSymbolAsync(symbol)).Task.Unwrap();
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
                foreach (var window in _orderBooks.Values.ToArray()) window.Close();
                Task.WhenAll(_orderBookCleanup).GetAwaiter().GetResult();
                _renderTimeUpdateTimer.Stop();
                _quoteUpdateTimer.Stop();

                if (chartView != null)
                {
                    chartView.SaveTools();
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
