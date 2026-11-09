# Архив торговой реализации до упрощения

Дата создания: 3 ноября 2026 года.

Здесь сохранены прежние версии файлов торгового слоя, удалённых или изменённых при рефакторинге, а не только удалённые строки. Поэтому методы, их зависимости, комментарии и проверки можно изучать вместе. В конце приведён Git diff, включая новые файлы и замены методов, за исключением файлов unit-тестов.

- Исходный коммит: `a3d8986546d39966f925c3e9fce6df0719ae232e` — Add USDT futures trading and reorganize services and windows.
- Коммит рефакторинга: `57f93c947d1cd14e46ba94058b3851eba5fc1dfd` — Refactoring of trading layer code.
- Источник: Git-объекты этих коммитов; более поздние изменения в архив автоматически не попадают.

Архив включает прежние интерфейсы, адаптер CCXT, торговый сервис, журнал намерений, валидатор, менеджер сессий, модели, код стакана и документацию. Сохранены проверки точности, лимитов, средств и комиссии, фьючерсной маржи и режима позиций, обработка неопределённых результатов, восстановление журнала и сверка биржевых данных. Это исторический код: наличие проверки не означает, что она была безошибочной или актуальна для будущих версий API.

Файл служит справочником и не участвует в сборке. Для извлечения конкретной версии: `git show a3d8986:BotView/Services/Integrations/Ccxt/CcxtTradingService.cs`. Незакоммиченные промежуточные версии, отсутствующие в истории Git, этот снимок не восстанавливает.

## Содержание прежних файлов

| № | Файл | Изменение при рефакторинге |
|---|---|---|
| 5 | [BotView/MainWindow.xaml.cs](#file-05) | Изменён; сохранена полная прежняя версия |
| 6 | [BotView/Models/SpotTrading.cs](#file-06) | Удалён |
| 7 | [BotView/Models/Trading.cs](#file-07) | Изменён; сохранена полная прежняя версия |
| 8 | [BotView/Services/Integrations/Ccxt/CcxtSpotTradingClient.cs](#file-08) | Удалён |
| 9 | [BotView/Services/Integrations/Ccxt/CcxtTradingService.cs](#file-09) | Изменён; сохранена полная прежняя версия |
| 10 | [BotView/Services/Integrations/Ccxt/OrderBookService.cs](#file-10) | Изменён; сохранена полная прежняя версия |
| 11 | [BotView/Services/Trading/ITradingService.cs](#file-11) | Изменён; сохранена полная прежняя версия |
| 12 | [BotView/Services/Trading/SpotTradingClient.cs](#file-12) | Удалён |
| 13 | [BotView/Services/Trading/TradingJournal.cs](#file-13) | Удалён |
| 14 | [BotView/Services/Trading/TradingOrderValidator.cs](#file-14) | Удалён |
| 15 | [BotView/Services/Trading/TradingSessionManager.cs](#file-15) | Изменён; сохранена полная прежняя версия |
| 16 | [BotView/ViewModels/OrderBookTradingViewModel.cs](#file-16) | Изменён; сохранена полная прежняя версия |
| 17 | [BotView/ViewModels/OrderBookViewModel.cs](#file-17) | Изменён; сохранена полная прежняя версия |
| 18 | [BotView/Windows/OrderBookWindow.xaml](#file-18) | Изменён; сохранена полная прежняя версия |
| 19 | [docs/FuturesTrading.md](#file-19) | Изменён; сохранена полная прежняя версия |
| 20 | [docs/TradingService.md](#file-20) | Изменён; сохранена полная прежняя версия |

<a id="file-05"></a>

## 5. BotView/MainWindow.xaml.cs

Полная версия из `a3d8986`. SHA-256 исходного Git blob: `8cfd6d39c3caf0b162bd7c4ad622f55f057ef4b6e6e1b74d7638b1afaed7ecdc`.

```csharp
﻿using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BotView.Chart;
using BotView.Chart.TechnicalAnalysis;
using BotView.Database;
using BotView.Models;
using BotView.Services;
using BotView.Services.Integrations.Ccxt;
using BotView.ViewModels;
using System.Windows.Threading;

namespace BotView
{
    /// <summary>Interaction logic for MainWindow.xaml — View adapter for ChartView and dialogs.</summary>
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly DispatcherTimer _renderTimeUpdateTimer;
        private readonly DispatcherTimer _quoteUpdateTimer;
        private ContextMenu? _pairContextMenu;
        private Point _favoriteDragStart;
        private TradingPairModel? _draggedFavorite;
        private readonly Dictionary<(string Exchange, string Symbol, TradingEnvironment Environment, TradingMarket Market), Windows.OrderBookWindow> _orderBooks = new();
        private readonly List<Task> _orderBookCleanup = new();
        private Windows.TradingAccountsWindow? _tradingAccountsWindow;

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

        private void OpenTradingAccounts(object sender, RoutedEventArgs e)
        {
            if (_tradingAccountsWindow is not null)
            {
                if (_tradingAccountsWindow.WindowState == WindowState.Minimized) _tradingAccountsWindow.WindowState = WindowState.Normal;
                _tradingAccountsWindow.Activate();
                return;
            }
            var window = new Windows.TradingAccountsWindow(App.TradingAccounts, App.TradingCredentials, App.AccountVerifier) { Owner = this };
            _tradingAccountsWindow = window;
            window.Closed += (_, _) => _tradingAccountsWindow = null;
            window.Show();
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

        private void lstTradingPairs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _draggedFavorite = null;
            if (e.OriginalSource is not DependencyObject source || FindAncestor<Button>(source) != null)
            {
                return;
            }

            var item = ItemsControl.ContainerFromElement(lstTradingPairs, source) as ListBoxItem;
            if (item?.DataContext is not TradingPairModel pair)
            {
                return;
            }

            _favoriteDragStart = e.GetPosition(null);
            _draggedFavorite = pair;
        }

        private void lstTradingPairs_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _draggedFavorite is null)
            {
                return;
            }

            Point position = e.GetPosition(null);
            if (Math.Abs(position.Y - _favoriteDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            TradingPairModel dragged = _draggedFavorite;
            _draggedFavorite = null;
            DragDrop.DoDragDrop(lstTradingPairs, dragged, DragDropEffects.Move);
        }

        private void lstTradingPairs_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(TradingPairModel))
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void lstTradingPairs_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(TradingPairModel)) is not TradingPairModel dragged)
            {
                return;
            }

            int oldIndex = _viewModel.favTradingPairs.IndexOf(dragged);
            if (oldIndex < 0)
            {
                return;
            }

            int insertIndex = _viewModel.favTradingPairs.Count;
            if (e.OriginalSource is DependencyObject source &&
                ItemsControl.ContainerFromElement(lstTradingPairs, source) is ListBoxItem container &&
                container.DataContext is TradingPairModel target)
            {
                insertIndex = _viewModel.favTradingPairs.IndexOf(target);
                if (e.GetPosition(container).Y > container.ActualHeight / 2)
                {
                    insertIndex++;
                }
            }

            if (oldIndex < insertIndex)
            {
                insertIndex--;
            }

            insertIndex = Math.Clamp(insertIndex, 0, _viewModel.favTradingPairs.Count - 1);
            if (oldIndex != insertIndex)
            {
                _viewModel.MoveFavorite(oldIndex, insertIndex);
            }

            e.Handled = true;
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match)
                {
                    return match;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private void Pair_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel.IsInstrumentCatalogMode) return;
            if (sender is not FrameworkElement target) return;
            var menu = Views.CoinInfoContextMenu.Create(target, pair =>
            {
                _viewModel.IsSearchDropdownOpen = false;
                _viewModel.IsComparisonDropdownOpen = false;
                new Windows.CoinInfoWindow(pair, App.CoinInfoProvider) { Owner = this }.Show();
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

        /// <summary>Opens one book per venue/pair/network, using the active profile's environment.</summary>
        private async void OpenOrderBook(string symbol, TradingMarket market)
        {
            if (_viewModel.IsInstrumentCatalogMode) return;
            _viewModel.IsSearchDropdownOpen = false;
            _viewModel.IsComparisonDropdownOpen = false;
            var environment = TradingEnvironment.Production;
            try
            {
                var settings = App.TradingAccounts.Load();
                var active = settings.Profiles.FirstOrDefault(p => p.Id == settings.ActiveProfiles.GetValueOrDefault(_viewModel.SelectedExchange));
                environment = active?.Environment ?? environment;
            }
            catch (Exception) { }
            var exchange = _viewModel.SelectedExchange;
            if (market == TradingMarket.UsdtFutures)
            {
                try
                {
                    var profile = new TradingAccountProfile(Guid.NewGuid(), "Catalog", exchange, environment, TradingMarket.UsdtFutures);
                    using var placeholder = new AccountCredentials("unused", "unused");
                    await using var client = new CcxtSpotTradingClient(profile, placeholder);
                    // Public catalog loading does not require a trading key.
                    client.Transport.apiKey = "";
                    client.Transport.secret = "";

                    var contracts = await client.LoadTradingPairsAsync(symbol);

                    if (!this.IsLoaded)
                    {
                        return;
                    }

                    if (contracts.Count == 0)
                    {
                        MessageBox.Show(this, "No active USDT contracts are available for this pair on the selected network.");
                        return;
                    }

                    var selection = new Windows.ContractSelectionWindow(contracts) { Owner = this };
                    if (selection.ShowDialog() != true)
                    {
                        return;
                    }

                    symbol = selection.SelectedContract!;
                }
                catch (Exception)
                {
                    MessageBox.Show(this, "Could not load the futures catalog. Check the exchange and network.");
                    return;
                }
            }

            var key = (Exchange: exchange, Symbol: symbol, Environment: environment, Market: market);
            if (_orderBooks.TryGetValue(key, out var existing))
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }
            var window = new Windows.OrderBookWindow(key.Exchange, symbol, App.OrderBookService, App.TradingSessions, environment, market) { Owner = this };
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
            var metricsWindow = new Windows.MetricsWindow(summary)
            {
                Owner = this
            };
            metricsWindow.ShowDialog();
        }

        private void OnExportMetricsRequested(string csvData)
        {
            var saveFileDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export performance metrics",
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
                $"Performance metrics successfully exported to:\n{saveFileDialog.FileName}",
                "Export complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void OnErrorOccurred(string title, string message)
        {
            void Show() => MessageBox.Show(message, title, MessageBoxButton.OK,
                title.Contains("exchange", StringComparison.OrdinalIgnoreCase)
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
```

<a id="file-06"></a>

## 6. BotView/Models/SpotTrading.cs

Полная версия из `a3d8986`. SHA-256 исходного Git blob: `cb1506102baa497f257c37a1f08b16b2509027d7948092b1bfa4fcb9754a5dde`.

```csharp
namespace BotView.Models;

public enum OrderQuantityCurrency { Base, Quote }
public enum TradingOperationPhase { Prepared, Dispatching, Unknown, Resolved }

/// <summary>Durable intent, scoped to the verified exchange identity and network; contains no credentials.</summary>
public sealed record TradingOperation(string Id, string AccountKey, PlaceOrderRequest Request,
    TradingOperationPhase Phase, DateTimeOffset CreatedAt, OrderReference? CancelTarget = null,
    string? ExchangeOrderId = null);

/// <summary>Detached account state shared by all windows. Uncertainty is separate from an order's exchange status.</summary>
public sealed record SpotTradingState(TradingConnectionState Connection, string Message,
    IReadOnlyList<OrderSnapshot> Orders, IReadOnlyList<ExecutionSnapshot> Executions,
    AccountSnapshot? Balance, IReadOnlyList<TradingOperation> Pending, bool Ready);

public sealed record CancelBatch(TradingAccountContext Account, InstrumentKey Instrument,
    IReadOnlyList<OrderReference> Orders);
```

<a id="file-07"></a>

## 7. BotView/Models/Trading.cs

Полная версия из `a3d8986`. SHA-256 исходного Git blob: `29fa5ba110a487b1c6d2f1ef66cd2078bacd9a81b6ed41d89e37a4283cfb6722`.

```csharp
namespace BotView.Models;

/// <summary>A service instance is permanently bound to this connection, account and environment.
/// Provider routing (QUIK firm/client codes or crypto account type) belongs inside its adapter.</summary>
public sealed record TradingAccountContext(
                    string ConnectionId, string AccountId,
                    string Venue, TradingEnvironment Environment, TradingMarket Market = TradingMarket.Spot);

public enum OrderType { Market, Limit, Stop, StopLimit }
public enum OrderSide { Buy, Sell }
public enum OrderTimeInForce { Gtc, Day, Ioc, Fok, Gtd }
public enum TriggerCondition { AtOrAbove, AtOrBelow }
public enum PositionSide { Long, Short }
public enum PlaceOrderStatus { Submitted, Accepted, Rejected, Unknown }
public enum CancelOrderStatus { Submitted, Canceled, AlreadyClosed, Rejected, Unknown }
public enum OrderState { Pending, Open, Filled, Canceled, Expired, Rejected, Unknown }

/// <summary>Generate and record ClientOrderId before dispatch; never reuse it for a new intent.
/// Quantity uses the instrument's declared unit. Limit and trigger prices are separate.
/// Client-ID lookup/idempotency depends on provider capabilities.</summary>
public sealed record PlaceOrderRequest(
    InstrumentKey Instrument, OrderSide Side, OrderType Type, decimal Quantity, string ClientOrderId,
    decimal? Price = null, decimal? TriggerPrice = null, TriggerCondition? TriggerCondition = null,
    OrderTimeInForce TimeInForce = OrderTimeInForce.Gtc, DateTimeOffset? ExpiresAt = null,
    bool PostOnly = false, bool ReduceOnly = false, PositionSide? PositionSide = null);

/// <summary>Exchange order identity is scoped by the bound account and full listing key.</summary>
public sealed record OrderReference(InstrumentKey Instrument, string ExchangeOrderId);

/// <summary>All quantities use the same unit. Open orders can be partially filled; canceled/expired
/// orders can retain fills. Unknown fields remain null, not zero.</summary>
public sealed record OrderSnapshot(
    OrderReference Reference, string? ClientOrderId, OrderSide Side, OrderType Type, OrderState State,
    decimal Quantity, decimal? FilledQuantity, decimal? RemainingQuantity, decimal? Price,
    decimal? AverageFillPrice, DateTimeOffset? CreatedAt, DateTimeOffset UpdatedAt, string? StatusReason = null,
    PositionSide? PositionSide = null, bool? ReduceOnly = null);

/// <summary>Sanitized application error. Never include raw provider responses or credentials.</summary>
public sealed record TradingError(string Code, string Message);

/// <summary>Submitted confirms dispatch; Accepted confirms acceptance, not execution. ProviderRequestId
/// identifies e.g. a QUIK transaction, independently of ExchangeOrderId. Unknown requires reconciliation.</summary>
public sealed record PlaceOrderResult(
    PlaceOrderStatus Status, string ClientOrderId, InstrumentKey Instrument,
    string? ExchangeOrderId = null, string? ProviderRequestId = null,
    OrderSnapshot? Order = null, TradingError? Error = null);

/// <summary>Submitted is a cancel acknowledgement. Canceled requires confirmed final state.
/// AlreadyClosed includes the observed terminal order when available; partial fills remain authoritative.</summary>
public sealed record CancelOrderResult(
    CancelOrderStatus Status, OrderReference Reference, string? ProviderRequestId = null,
    OrderSnapshot? Order = null, TradingError? Error = null);

public sealed record Money(decimal Amount, string Currency);
public sealed record AccountBalance(string Currency, decimal? Total, decimal? Available, decimal? Reserved);

/// <summary>Quantity is nonnegative; Side carries direction. Hedged accounts may contain both sides.
/// Position value and collateral are distinct. Unknown financial values remain null.</summary>
public sealed record PositionSnapshot(
    InstrumentKey Instrument, PositionSide Side, decimal Quantity, QuantityUnit QuantityUnit,
    decimal? AveragePrice = null, Money? UnrealizedPnl = null, Money? InitialMargin = null,
    decimal? Leverage = null, bool? Hedged = null, string? MarginMode = null);

public sealed record MarginSnapshot(
    string Currency, decimal? Equity = null, decimal? AvailableFunds = null,
    decimal? InitialMargin = null, decimal? MaintenanceMargin = null, decimal? VariationMargin = null);

/// <summary>A null section is unavailable/unknown; an empty section confirms there are no entries.</summary>
public sealed record AccountSnapshot(
    TradingAccountContext Account, IReadOnlyList<AccountBalance>? Balances,
    IReadOnlyList<PositionSnapshot>? Positions, IReadOnlyList<MarginSnapshot>? Margins, DateTimeOffset AsOf);

/// <summary>One actual fill, identified by account + listing + ExecutionId for deduplication. Fee currency may differ and rebates can
/// be negative. Execution quantity uses the same instrument unit as the order.</summary>
public sealed record ExecutionSnapshot(
    string ExecutionId, OrderReference Order, OrderSide Side, decimal Quantity, decimal Price,
    DateTimeOffset ExecutedAt, Money? Fee = null);

/// <summary>UTC bounds and an opaque continuation cursor. Reject unsupported filters instead of
/// ignoring them. A null instrument means all listings only where supported by the provider.</summary>
public sealed record TradingQuery(
    InstrumentKey? Instrument = null, DateTimeOffset? Since = null, DateTimeOffset? Until = null,
    int Limit = 100, string? Cursor = null);
public sealed record TradingPage<T>(IReadOnlyList<T> Items, string? NextCursor = null);

/// <summary>Capabilities are listing/account-specific, not inferred from available enum values.
/// Unsupported methods/options must fail explicitly rather than return an empty success.</summary>
public sealed record TradingCapabilities(
    IReadOnlySet<OrderType> OrderTypes, IReadOnlySet<OrderTimeInForce> TimeInForces,
    bool SupportsPostOnly = false, bool SupportsReduceOnly = false, bool SupportsPositionSide = false,
    bool SupportsLookupByClientOrderId = false, bool SupportsOrderHistory = false,
    bool SupportsExecutions = false, bool SupportsPositions = false);

public sealed record OrderValidationResult(IReadOnlyList<TradingError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public enum TradingConnectionState { Connecting, Connected, Reconnecting, Disconnected }
public abstract record TradingUpdate(DateTimeOffset AsOf);
public sealed record OrderChanged(OrderSnapshot Order) : TradingUpdate(Order.UpdatedAt);
public sealed record ExecutionReceived(ExecutionSnapshot Execution) : TradingUpdate(Execution.ExecutedAt);
public sealed record AccountChanged(AccountSnapshot Account) : TradingUpdate(Account.AsOf);
/// <summary>RequiresResync signals that consumers must refresh snapshots and reconcile history.
/// A reconnected stream is not proof that no order or execution updates were missed.</summary>
public sealed record TradingConnectionChanged(
    TradingConnectionState State, DateTimeOffset Timestamp, bool RequiresResync = false, TradingError? Error = null)
    : TradingUpdate(Timestamp);
```

<a id="file-08"></a>

## 8. BotView/Services/Integrations/Ccxt/CcxtSpotTradingClient.cs

Полная версия из `a3d8986`. SHA-256 исходного Git blob: `bec919c241a8d2e0932b69eb2c570e5c575b501efb3fd3fb01dfc13106777160`.

```csharp
﻿using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BotView.Models;
using BotView.Services;

namespace BotView.Services.Integrations.Ccxt;

/// <summary>Binance/Bybit spot and USDT contract mapping. Raw CCXT values and signed errors never escape this boundary.</summary>
internal sealed class CcxtSpotTradingClient : ISpotTradingClient
{
    private readonly ccxt.Exchange _client;
    private readonly TradingAccountProfile _profile;
    private readonly string _keyIdentity;
    private readonly Dictionary<string, SpotMarket> _markets = new();
    private readonly Dictionary<string, DateTimeOffset> _marketTimes = new();
    private string _bybitAccountType = "UNIFIED";
    private readonly Dictionary<string, ccxt.MarketInterface> _rawMarkets = new();
    internal ccxt.Exchange Transport => _client;
    private readonly ConcurrentDictionary<string, string> _bybitOrderFilters = new();
    private readonly ConcurrentDictionary<string, (PlaceOrderRequest Request, bool Hedged)> _validatedModes = new();
    public bool IsFutures => _profile.Market == TradingMarket.UsdtFutures;
    private string Category => IsFutures ? "linear" : "spot";
    private string Listing => IsFutures ? "futures" : "spot";

    /// <summary>Creates an isolated Pro client and selects sandbox before any request.</summary>
    internal CcxtSpotTradingClient(TradingAccountProfile profile, AccountCredentials credentials,
        ccxt.Exchange? transport = null)
    {
        _profile = profile;
        var settings = ProOrderBookClient.CreateSettings();
        settings["apiKey"] = credentials.ApiKey;
        settings["secret"] = credentials.Secret;
        settings["verbose"] = false;
        _client = transport ?? (IsFutures ? CreateFuturesExchange(profile, settings) : CreateSpotExchange(profile, settings));
        _keyIdentity = "key:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentials.ApiKey)));
        if (profile.Environment == TradingEnvironment.Testnet) _client.setSandboxMode(true);
    }

    /// <summary>Loads active USDT-margined contract symbols that match the spot pair.</summary>
    public async Task<IReadOnlyList<string>> LoadTradingPairsAsync(string spotSymbol, CancellationToken ct = default)
    {
        var markets = await _client.LoadMarkets().WaitAsync(ct).ConfigureAwait(false);
        var pair = spotSymbol.Split(':')[0];
        return markets.Values
            .Where(m => IsLinearUsdtContract(m) && m.active != false && m.symbol!.Split(':')[0] == pair)
            .OrderBy(m => m.expiry.HasValue)
            .ThenBy(m => m.expiry)
            .Select(m => m.symbol!)
            .ToArray();
    }

    /// <summary>Authenticates and obtains a stable owner ID; a key fingerprint isolates recovery when no owner ID is returned.</summary>
    public Task<(string Identity, bool? CanTrade)> ConnectAsync(CancellationToken ct) =>
        IsFutures ? ConnectFuturesAsync(ct) : ConnectSpotAsync(ct);

    /// <summary>Confirms futures access and rejects Binance multi-asset margin, which this adapter does not settle.</summary>
    private async Task<(string Identity, bool? CanTrade)> ConnectFuturesAsync(CancellationToken ct)
    {
        await _client.FetchBalance(Parameters()).WaitAsync(ct).ConfigureAwait(false);
        if (_client is ccxt.binance futuresBinance)
        {
            var mode = await futuresBinance.fapiPrivateGetMultiAssetsMargin().WaitAsync(ct).ConfigureAwait(false);
            if (mode.TryGetValue("multiAssetsMargin", out var enabled) && Convert.ToBoolean(enabled, CultureInfo.InvariantCulture))
            {
                throw new NotSupportedException("USDT futures currently require single-asset margin mode.");
            }
        }

        if (_client is ccxt.bybit futuresBybit)
        {
            var futuresKeyResponse = await futuresBybit.privateGetV5UserQueryApi().WaitAsync(ct).ConfigureAwait(false);
            var keyInfo = (IDictionary<string, object>)futuresKeyResponse["result"];
            bool? allowed = Text(keyInfo, "readOnly") == "1" ? false : null;
            if (keyInfo.TryGetValue("permissions", out var futuresPermissions) && futuresPermissions is IDictionary<string, object> permissionMap &&
                permissionMap.TryGetValue("ContractTrade", out var contractRights) && contractRights is IEnumerable rights)
            {
                allowed = allowed == false ? false : rights.Cast<object>().Any(p => p?.ToString() == "Order");
            }

            var owner = Text(keyInfo, "userID");
            return (string.IsNullOrWhiteSpace(owner) ? _keyIdentity : "uid:" + owner, allowed);
        }

        return (_keyIdentity, null);
    }

    /// <summary>Reads the spot account owner and whether this key itself may trade.</summary>
    private async Task<(string Identity, bool? CanTrade)> ConnectSpotAsync(CancellationToken ct)
    {
        if (_client is ccxt.binance binance)
        {
            var account = await binance.privateGetAccount().WaitAsync(ct).ConfigureAwait(false);
            var uid = Text(account, "uid");
            bool? canTrade = account.TryGetValue("canTrade", out var flag) && flag is bool allowed ? allowed : null;
            bool? keyCanTrade = null;
            if (_profile.Environment == TradingEnvironment.Production)
            {
                try { keyCanTrade = CcxtAccountVerificationClient.ParseBinancePermissions(await binance.sapiGetAccountApiRestrictions().WaitAsync(ct).ConfigureAwait(false)).CanTrade; }
                catch (ccxt.ExchangeError) { }
            }
            // Account-wide canTrade is not proof that this particular API key has trading permission.
            return (string.IsNullOrWhiteSpace(uid) ? _keyIdentity : "uid:" + uid, canTrade == false || keyCanTrade == false ? false : keyCanTrade);
        }
        var response = await ((ccxt.bybit)_client).privateGetV5UserQueryApi().WaitAsync(ct).ConfigureAwait(false);
        var result = response["result"] as IDictionary<string, object> ?? throw new NotSupportedException("Account identity unavailable.");
        var permissions = CcxtAccountVerificationClient.ParseBybitPermissions(result);
        _bybitAccountType = Text(result, "uta") == "0" ? "SPOT" : "UNIFIED";
        return (string.IsNullOrWhiteSpace(Text(result, "userID")) ? _keyIdentity : "uid:" + Text(result, "userID"), permissions.CanTrade);
    }

    /// <summary>Loads spot filters and account fees, retaining only detached application metadata.</summary>
    public async Task<SpotMarket> MarketAsync(InstrumentKey key, CancellationToken ct)
    {
        if (key.Venue != _profile.Exchange || key.Market != Listing || key.Board != "") throw new NotSupportedException("Only this account's spot listings are supported.");
        if (_markets.TryGetValue(key.SecurityId, out var cached) && DateTimeOffset.UtcNow - _marketTimes[key.SecurityId] < TimeSpan.FromMinutes(5)) return cached;
        if (_markets.Count > 0) await _client.loadMarkets(true).WaitAsync(ct).ConfigureAwait(false);
        var markets = await _client.LoadMarkets().WaitAsync(ct).ConfigureAwait(false);
        if (!markets.TryGetValue(key.SecurityId, out var market) || !IsTradable(market) || market.active == false)
            throw new NotSupportedException("The spot instrument is unavailable.");
        decimal? fee = null;
        if (_client.has.GetValueOrDefault("fetchTradingFee") is true && !(_profile.Exchange == "binance" && _profile.Environment == TradingEnvironment.Testnet))
        {
            try
            {
                var rates = await _client.FetchTradingFee(key.SecurityId).WaitAsync(ct).ConfigureAwait(false);
                if (rates.percentage != false && rates.maker.HasValue && rates.taker.HasValue)
                    fee = Math.Max(0, Math.Max(SpotOrderMath.Number(rates.maker)!.Value, SpotOrderMath.Number(rates.taker)!.Value));
            }
            catch (ccxt.ExchangeError) { }
        }
        if (fee is null && market.percentage == true && market.maker.HasValue && market.taker.HasValue)
            fee = Math.Max(0, Math.Max(SpotOrderMath.Number(market.maker)!.Value, SpotOrderMath.Number(market.taker)!.Value));
        var instrument = DescribeInstrument(CcxtMarketDataProvider.MapInstrument(_profile.Exchange, market, Convert.ToInt32(_client.precisionMode)), market, key);

        cached = new(instrument, market.baseCurrency!, market.quote!, fee, market.feeSide);
        _rawMarkets[key.SecurityId] = market;
        _markets[key.SecurityId] = cached;
        _marketTimes[key.SecurityId] = DateTimeOffset.UtcNow;
        return cached;
    }

    /// <summary>Truncates quantities with CCXT's actual precision mode rather than assuming decimal places.</summary>
    public decimal NormalizeAmount(string symbol, decimal amount) => decimal.Parse(_client.amountToPrecision(symbol, amount.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);

    /// <summary>Normalizes prices for equality checks; callers reject any changed entered price.</summary>
    public decimal NormalizePrice(string symbol, decimal price) => decimal.Parse(_client.priceToPrecision(symbol, price.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);

    /// <summary>Verifies both precision and the typed CCXT double boundary before dispatch.</summary>
    internal double WireNumber(string symbol, decimal value, bool amount)
    {
        var wire = (double)value;
        var text = wire.ToString("R", CultureInfo.InvariantCulture);
        var roundTrip = decimal.Parse((amount ? _client.amountToPrecision(symbol, text) : _client.priceToPrecision(symbol, text))!, CultureInfo.InvariantCulture);
        if (roundTrip != value) throw new ArgumentException("The value cannot be represented at the exchange precision.");
        return wire;
    }

    /// <summary>Maps Post-only explicitly, never downgrading a rejected maker request to an ordinary limit.</summary>
    internal static Dictionary<string, object> OrderParameters(string exchange, PlaceOrderRequest request)
    {
        var parameters = new Dictionary<string, object> { ["clientOrderId"] = request.ClientOrderId };
        if (exchange == "binance")
        {
            if (request.PostOnly) parameters["postOnly"] = true;
            else parameters["timeInForce"] = "GTC";
            parameters["newOrderRespType"] = "FULL";
        }
        else
        {
            parameters["category"] = "spot";
            parameters["isLeverage"] = 0;
            parameters["timeInForce"] = request.PostOnly ? "PostOnly" : "GTC";
        }
        return parameters;
    }

    /// <summary>Sends exactly once; transport uncertainty is classified by the account service.</summary>
    public async Task<OrderSnapshot> PlaceAsync(PlaceOrderRequest request, CancellationToken ct)
    {
        var symbol = request.Instrument.SecurityId;
        var amount = WireNumber(symbol, request.Quantity, true);
        var price = WireNumber(symbol, request.Price!.Value, false);
        var parameters = await CreateOrderParametersAsync(request, ct).ConfigureAwait(false);

        var order = await _client.CreateOrder(symbol, "limit", request.Side == OrderSide.Buy ? "buy" : "sell", amount, price,
            parameters).WaitAsync(ct).ConfigureAwait(false);
        return MapOrder(order, request.Instrument) with { ClientOrderId = request.ClientOrderId,
            Quantity = request.Quantity, Price = request.Price, Side = request.Side,
            PositionSide = request.PositionSide, ReduceOnly = request.ReduceOnly };
    }

    /// <summary>An acknowledgement of cancellation is not assumed to be a final canceled state.</summary>
    public async Task<OrderSnapshot?> CancelAsync(OrderReference order, CancellationToken ct)
    {
        if (_client is ccxt.bybit bybit)
        {
            await bybit.privatePostV5OrderCancel(BybitCancelParameters(order)).WaitAsync(ct).ConfigureAwait(false);
            return null;
        }
        return MapOrder(await _client.CancelOrder(order.ExchangeOrderId, order.Instrument.SecurityId, Parameters()).WaitAsync(ct).ConfigureAwait(false), order.Instrument);
    }

    /// <summary>Returns null only for the provider's explicit order-not-found response.</summary>
    public async Task<OrderSnapshot?> OrderAsync(OrderReference order, CancellationToken ct)
    {
        if (_profile.Exchange == "bybit")
        {
            foreach (var open in new[] { true, false })
            {
                var page = await BybitOrdersAsync(new(order.Instrument), open, ct, exchangeId: order.ExchangeOrderId).ConfigureAwait(false);
                var found = page.Items.FirstOrDefault(o => o.Reference == order);
                if (found is not null) return found;
            }
            return null;
        }
        try { return MapOrder(await _client.FetchOrder(order.ExchangeOrderId, order.Instrument.SecurityId, Parameters()).WaitAsync(ct).ConfigureAwait(false), order.Instrument); }
        catch (ccxt.OrderNotFound) { return null; }
    }

    /// <summary>Looks up the original client ID, including Bybit history after an order leaves the realtime cache.</summary>
    public async Task<OrderSnapshot?> FindAsync(InstrumentKey key, string clientId, CancellationToken ct)
    {
        if (_profile.Exchange == "binance")
            return await (IsFutures ? FindBinanceFuturesOrderAsync(key, clientId, ct) : FindBinanceSpotOrderAsync(key, clientId, ct)).ConfigureAwait(false);
        foreach (var open in new[] { true, false })
        {
            var page = await BybitOrdersAsync(new(key), open, ct, clientId).ConfigureAwait(false);
            foreach (var order in page.Items) if (order.ClientOrderId == clientId) return order;
        }
        return null;
    }

    /// <summary>Uses provider continuation: Bybit cursor, Binance increasing order ID for history.</summary>
    public async Task<TradingPage<OrderSnapshot>> OrdersAsync(TradingQuery query, bool open, CancellationToken ct)
    {
        var key = query.Instrument ?? throw new NotSupportedException("A spot instrument is required.");
        if (_profile.Exchange == "bybit") return await BybitOrdersAsync(query, open, ct).ConfigureAwait(false);
        var parameters = Parameters();
        if (query.Until.HasValue) parameters[_profile.Exchange == "binance" ? "endTime" : "endTime"] = query.Until.Value.ToUnixTimeMilliseconds();
        if (query.Cursor is not null) parameters[_profile.Exchange == "binance" ? "orderId" : "cursor"] = query.Cursor;
        var limit = _profile.Exchange == "binance" && open ? (int?)null : Math.Clamp(query.Limit, 1, _profile.Exchange == "bybit" ? 50 : 1000);
        var orders = open
            ? await _client.FetchOpenOrders(key.SecurityId, null, limit, parameters).WaitAsync(ct).ConfigureAwait(false)
            : await _client.FetchOrders(key.SecurityId, query.Since?.ToUnixTimeMilliseconds(), limit, parameters).WaitAsync(ct).ConfigureAwait(false);
        string? next = null;
        if (_profile.Exchange == "binance" && !open && orders.Count == limit && long.TryParse(orders[^1].id, out var id)) next = (id + 1).ToString(CultureInfo.InvariantCulture);
        return new(orders.Select(o => MapOrder(o, key)).ToArray(), string.IsNullOrEmpty(next) ? null : next);
    }

    /// <summary>Fetches paged actual executions; the caller persists identities and overlaps its watermark.</summary>
    public async Task<TradingPage<ExecutionSnapshot>> ExecutionsAsync(TradingQuery query, CancellationToken ct)
    {
        var key = query.Instrument ?? throw new NotSupportedException("A spot instrument is required.");
        if (_profile.Exchange == "bybit")
        {
            var request = BybitQuery(query);
            var response = await ((ccxt.bybit)_client).privateGetV5ExecutionList(request).WaitAsync(ct).ConfigureAwait(false);
            return ParseBybitExecutions(response, key);
        }
        var parameters = Parameters();
        if (query.Cursor is not null) parameters[_profile.Exchange == "binance" ? "fromId" : "cursor"] = query.Cursor;
        if (query.Until.HasValue) parameters["endTime"] = query.Until.Value.ToUnixTimeMilliseconds();
        var limit = Math.Clamp(query.Limit, 1, _profile.Exchange == "bybit" ? 100 : 1000);
        var trades = await _client.FetchMyTrades(key.SecurityId, query.Cursor is null ? query.Since?.ToUnixTimeMilliseconds() : null, limit, parameters).WaitAsync(ct).ConfigureAwait(false);
        string? next = trades.Count > 0 ? ResponseCursor(trades[^1].info) : null;
        if (_profile.Exchange == "binance" && trades.Count == limit && long.TryParse(trades[^1].id, out var id)) next = (id + 1).ToString(CultureInfo.InvariantCulture);
        return new(trades.Select(t => MapExecution(t, key)).ToArray(), string.IsNullOrEmpty(next) ? null : next);
    }

    /// <summary>Copies balances, preserving unknown free/used values instead of manufacturing zeroes.</summary>
    public Task<AccountSnapshot> BalanceAsync(TradingAccountContext account, CancellationToken ct) =>
        IsFutures ? FuturesBalanceAsync(account, ct) : SpotBalanceAsync(account, ct);

    /// <summary>Copies spot wallet balances, preserving unknown free/used values instead of manufacturing zeroes.</summary>
    private async Task<AccountSnapshot> SpotBalanceAsync(TradingAccountContext account, CancellationToken ct)
    {
        if (_client is ccxt.bybit bybit)
        {
            var raw = await bybit.privateGetV5AccountWalletBalance(new Dictionary<string, object> { ["accountType"] = _bybitAccountType }).WaitAsync(ct).ConfigureAwait(false);
            var walletBalances = new List<AccountBalance>();
            var result = (IDictionary<string, object>)raw["result"];
            foreach (var wallet in ((IEnumerable)result["list"]).Cast<IDictionary<string, object>>())
                foreach (var coin in ((IEnumerable)wallet["coin"]).Cast<IDictionary<string, object>>())
                {
                    var total = DecimalField(coin, "walletBalance");
                    var locked = DecimalField(coin, "locked");
                    var borrow = DecimalField(coin, "spotBorrow") ?? DecimalField(coin, "borrowAmount");
                    // Wallet holdings, less locked/borrowed assets; available margin is not spot cash.
                    var free = DecimalField(coin, "free") ?? (total.HasValue && locked.HasValue ? Math.Max(0, total.Value - locked.Value - (borrow ?? 0)) : (decimal?)null);
                    walletBalances.Add(new(Text(coin, "coin")!, total, free, locked));
                }
            return new(account, walletBalances, null, null, DateTimeOffset.UtcNow);
        }
        var balances = await _client.FetchBalance(Parameters()).WaitAsync(ct).ConfigureAwait(false);
        return MapBalance(balances, account);
    }

    /// <summary>Checks runtime capabilities because private-stream support differs by exchange and version.</summary>
    public bool SupportsStream(string kind) => _client.has.GetValueOrDefault(kind) is true;

    /// <summary>Copies a private stream batch before calling watch again.</summary>
    public async Task<IReadOnlyList<TradingUpdate>> WatchAsync(string kind, InstrumentKey key, TradingAccountContext account, CancellationToken ct)
    {
        if (kind == "watchOrders") return (await _client.WatchOrders(key.SecurityId, null, null, Parameters()).WaitAsync(ct).ConfigureAwait(false))
            .Where(o => o.symbol == key.SecurityId).Select(o => (TradingUpdate)new OrderChanged(MapAccountOrder(o, key))).ToArray();
        if (kind == "watchMyTrades") return (await _client.WatchMyTrades(key.SecurityId, null, null, Parameters()).WaitAsync(ct).ConfigureAwait(false))
            .Where(t => t.symbol == key.SecurityId).Select(t => (TradingUpdate)new ExecutionReceived(MapExecution(t, key))).ToArray();
        return [new AccountChanged(MapBalance(await _client.WatchBalance(Parameters()).WaitAsync(ct).ConfigureAwait(false), account))];
    }

    /// <summary>Routes unified calls without leaking derivative-only options into spot HTTP requests.
    /// The linear subtype is configured on the futures client, not forwarded to individual endpoints.</summary>
    private Dictionary<string, object> Parameters() => IsFutures ? FuturesParameters() : SpotParameters();

    /// <summary>Spot requests stay free of derivative routing options.</summary>
    private static Dictionary<string, object> SpotParameters() => new();

    /// <summary>Selects the USDT-M route. Linearity stays on the futures client, not on each request.</summary>
    private Dictionary<string, object> FuturesParameters() => new()
    {
        ["type"] = _profile.Exchange == "binance" ? "future" : "swap"
    };

    /// <summary>Preserves provider status rather than treating an asynchronous acknowledgement as an open order.</summary>
    internal static OrderSnapshot MapOrder(ccxt.Order order, InstrumentKey key) => new(
        new(key, order.id ?? ""), order.clientOrderId, order.side == "sell" ? OrderSide.Sell : OrderSide.Buy,
        order.type switch { "market" => OrderType.Market, "stop" or "stop_market" => OrderType.Stop,
            "stop_limit" => OrderType.StopLimit, _ => OrderType.Limit }, order.status switch { "open" => OrderState.Open, "closed" => OrderState.Filled,
            "canceled" or "cancelled" => OrderState.Canceled, "expired" => OrderState.Expired, "rejected" => OrderState.Rejected, _ => OrderState.Pending },
        SpotOrderMath.Number(order.amount) ?? 0, SpotOrderMath.Number(order.filled), SpotOrderMath.Number(order.remaining),
        SpotOrderMath.Number(order.price), SpotOrderMath.Number(order.average), At(order.timestamp),
        At(order.lastUpdateTimestamp ?? order.lastTradeTimestamp ?? order.timestamp) ?? DateTimeOffset.MinValue, StatusReason(order),
        GetPositionSide(order), order.reduceOnly);

    /// <summary>Retains the hedge leg for exchange orders created by this or another application.</summary>
    private static PositionSide? GetPositionSide(ccxt.Order order)
    {
        if (order.info is null)
        {
            return null;
        }

        return Text(order.info, "positionSide") switch
        {
            "LONG" => PositionSide.Long,
            "SHORT" => PositionSide.Short,
            _ => Text(order.info, "positionIdx") switch
            {
                "1" => PositionSide.Long,
                "2" => PositionSide.Short,
                _ => null
            }
        };
    }

    /// <summary>Maps an allowlist of provider reason codes; raw rejection text is never shown or persisted.</summary>
    private static string? StatusReason(ccxt.Order order)
    {
        var reason = order.info is null ? null : Text(order.info, "rejectReason") ?? Text(order.info, "cancelType") ?? Text(order.info, "expiryReason");
        if (reason is "EC_PostOnlyWillTakeLiquidity" or "CancelByPostOnly") return "Post-only would execute immediately.";
        if (reason is "EC_NoEnoughQtyToFill" or "INSUFFICIENT_LIQUIDITY") return "Insufficient exchange liquidity.";
        return order.status switch { "canceled" or "cancelled" => "Canceled by the exchange.", "rejected" => "Rejected by the exchange.",
            "expired" => "Expired at the exchange.", _ => null };
    }

    /// <summary>Maps a fill independently of its order's current state, retaining the actual commission currency.</summary>
    private static ExecutionSnapshot MapExecution(ccxt.Trade trade, InstrumentKey key) => new(trade.id ?? "",
        new(key, trade.order ?? trade.orderId ?? ""), trade.side == "sell" ? OrderSide.Sell : OrderSide.Buy,
        SpotOrderMath.Number(trade.amount) ?? 0, SpotOrderMath.Number(trade.price) ?? 0,
        At(trade.timestamp) ?? DateTimeOffset.MinValue,
        trade.fee is { } fee && fee.currency is not null && SpotOrderMath.Number(fee.cost) is { } cost ? new(cost, fee.currency) : null);

    /// <summary>Copies all currency balances out of the mutable CCXT cache.</summary>
    private static AccountSnapshot MapBalance(ccxt.Balances balance, TradingAccountContext account) => new(account,
        balance.balances.Select(p => new AccountBalance(p.Key, SpotOrderMath.Number(p.Value.total),
            SpotOrderMath.Number(p.Value.free), SpotOrderMath.Number(p.Value.used))).ToArray(), null, null, DateTimeOffset.UtcNow);

    /// <summary>Reads optional detached provider metadata.</summary>
    private static string? Text(IDictionary<string, object> data, string key) => data.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    /// <summary>Extracts a cursor if a provider preserves it in unified item metadata.</summary>
    private static string? ResponseCursor(IDictionary<string, object>? data) => data is null ? null : Text(data, "nextPageCursor");

    /// <summary>Retains Bybit's envelope cursor, which unified CCXT order lists do not expose.</summary>
    private async Task<TradingPage<OrderSnapshot>> BybitOrdersAsync(TradingQuery query, bool open, CancellationToken ct, string? clientId = null, string? exchangeId = null)
    {
        var request = BybitQuery(query);
        if (clientId is not null) request["orderLinkId"] = clientId;
        if (exchangeId is not null) request["orderId"] = exchangeId;
        var bybit = (ccxt.bybit)_client;
        var response = open ? await bybit.privateGetV5OrderRealtime(request).WaitAsync(ct).ConfigureAwait(false)
            : await bybit.privateGetV5OrderHistory(request).WaitAsync(ct).ConfigureAwait(false);
        return ParseBybitOrders(response, query.Instrument!);
    }

    /// <summary>Retains the cursor from the response envelope while parsing detached spot orders.</summary>
    internal TradingPage<OrderSnapshot> ParseBybitOrders(IDictionary<string, object> response, InstrumentKey key)
    {
        var result = (IDictionary<string, object>)response["result"];
        var items = ((IEnumerable)result["list"]).Cast<object>()
            .Select(raw => MapAccountOrder(new ccxt.Order(_client.parseOrder(raw, _client.market(key.SecurityId))), key)).ToArray();
        return new(items, EmptyCursor(Text(result, "nextPageCursor")));
    }

    /// <summary>Parses actual fill IDs, commissions and the execution endpoint's independent cursor.</summary>
    internal TradingPage<ExecutionSnapshot> ParseBybitExecutions(IDictionary<string, object> response, InstrumentKey key)
    {
        var result = (IDictionary<string, object>)response["result"];
        var items = ((IEnumerable)result["list"]).Cast<object>().Select(raw => MapExecution(new ccxt.Trade(_client.parseTrade(raw, _client.market(key.SecurityId))), key)).ToArray();
        return new(items, EmptyCursor(Text(result, "nextPageCursor")));
    }

    /// <summary>Routes an external conditional order's cancellation using its original spot order filter.</summary>
    internal Dictionary<string, object> BybitCancelParameters(OrderReference order) =>
        IsFutures ? FuturesBybitCancelParameters(order) : SpotBybitCancelParameters(order);

    /// <summary>Keeps provider routing for externally created conditional spot orders inside the adapter.</summary>
    private OrderSnapshot MapAccountOrder(ccxt.Order order, InstrumentKey key) =>
        IsFutures ? MapOrder(order, key) : MapSpotAccountOrder(order, key);

    /// <summary>Builds an explicitly spot-only query and preserves caller bounds and cursor.</summary>
    private Dictionary<string, object> BybitQuery(TradingQuery query)
    {
        if (query.Since.HasValue && query.Until.HasValue && query.Until - query.Since > TimeSpan.FromDays(7))
            throw new NotSupportedException("Bybit history requests must use windows of at most seven days.");
        var request = new Dictionary<string, object> { ["category"] = Category, ["symbol"] = _client.Market(query.Instrument!.SecurityId).id!, ["limit"] = Math.Clamp(query.Limit, 1, 50) };
        if (query.Cursor is not null) request["cursor"] = query.Cursor;
        if (query.Since.HasValue) request["startTime"] = query.Since.Value.ToUnixTimeMilliseconds();
        if (query.Until.HasValue) request["endTime"] = query.Until.Value.ToUnixTimeMilliseconds();
        return request;
    }

    /// <summary>Checks Binance's moving price bands with a fresh reference instead of stale depth.</summary>
    public Task<IReadOnlyList<TradingError>> ValidateMarketAsync(PlaceOrderRequest request, CancellationToken ct) =>
        IsFutures ? Task.FromResult<IReadOnlyList<TradingError>>([]) : ValidateSpotMarketAsync(request, ct);

    /// <summary>Rejects a spot limit that sits outside the exchange's current price band.</summary>
    private async Task<IReadOnlyList<TradingError>> ValidateSpotMarketAsync(PlaceOrderRequest request, CancellationToken ct)
    {
        WireNumber(request.Instrument.SecurityId, request.Quantity, true);
        WireNumber(request.Instrument.SecurityId, request.Price!.Value, false);
        if (_client is ccxt.bybit bybit)
        {
            var response = await bybit.publicGetV5MarketPriceLimit(new Dictionary<string, object>
                { ["category"] = Category, ["symbol"] = _client.Market(request.Instrument.SecurityId).id! }).WaitAsync(ct).ConfigureAwait(false);
            var limits = (IDictionary<string, object>)response["result"];
            var maximumBuy = DecimalField(limits, "buyLmt");
            var minimumSell = DecimalField(limits, "sellLmt");
            if (maximumBuy is null || minimumSell is null) return [new("PriceBandUnavailable", "The current spot price band is unavailable.")];
            if (request.Side == OrderSide.Buy && request.Price > maximumBuy || request.Side == OrderSide.Sell && request.Price < minimumSell)
                return [new("PriceBand", "Price is outside the exchange's current allowed band.")];
            return [];
        }
        if (_client is not ccxt.binance binance || !_rawMarkets.TryGetValue(request.Instrument.SecurityId, out var market) ||
            market.info is null || !market.info.TryGetValue("filters", out var raw) || raw is not IEnumerable filters) return [];
        decimal? referencePrice = null;
        try
        {
            var reference = await binance.publicGetReferencePrice(new Dictionary<string, object> { ["symbol"] = market.id! }).WaitAsync(ct).ConfigureAwait(false);
            referencePrice = DecimalField(reference, "referencePrice");
        }
        catch (ccxt.NotSupported) { }
        catch (ccxt.BadRequest) { }
        foreach (var filter in filters.Cast<IDictionary<string, object>>().Where(f => Text(f, "filterType") is "PERCENT_PRICE" or "PERCENT_PRICE_BY_SIDE"))
        {
            var parameters = new Dictionary<string, object> { ["symbol"] = market.id! };
            var zeroMinutes = Text(filter, "avgPriceMins") == "0";
            var average = referencePrice;
            if (average is null)
            {
                var reference = zeroMinutes ? await binance.publicGetTickerPrice(parameters).WaitAsync(ct).ConfigureAwait(false)
                    : await binance.publicGetAvgPrice(parameters).WaitAsync(ct).ConfigureAwait(false);
                average = DecimalField((IDictionary<string, object>)reference, "price");
            }
            var prefix = Text(filter, "filterType") == "PERCENT_PRICE_BY_SIDE" ? request.Side == OrderSide.Buy ? "bidMultiplier" : "askMultiplier" : "multiplier";
            var lower = DecimalField(filter, prefix + "Down");
            var upper = DecimalField(filter, prefix + "Up");
            if (average is null || lower is null || upper is null) return [new("PriceBandUnavailable", "The exchange price band could not be checked.")];
            if (request.Price < average * lower || request.Price > average * upper) return [new("PriceBand", "Price is outside the exchange's current allowed band.")];
        }
        return [];
    }

    /// <summary>Parses optional provider decimal strings without passing through binary floating point.</summary>
    private static decimal? DecimalField(IDictionary<string, object> value, string key) => decimal.TryParse(Text(value, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    /// <summary>Normalizes an empty provider cursor to the end of pagination.</summary>
    private static string? EmptyCursor(string? cursor) => string.IsNullOrWhiteSpace(cursor) ? null : cursor;

    /// <summary>Converts only actual exchange timestamps; unknown timestamps do not get invented.</summary>
    private static DateTimeOffset? At(long? timestamp) => timestamp.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value) : null;

    /// <summary>Closes transport and removes client-owned credential references without canceling any order.</summary>
    public async ValueTask DisposeAsync()
    {
        try { await _client.Close().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        finally { _client.apiKey = ""; _client.secret = ""; }
    }

    /// <summary>Opens a public spot client. Futures options are never applied here.</summary>
    private static ccxt.Exchange CreateSpotExchange(TradingAccountProfile profile, Dictionary<string, object> settings) => profile.Exchange switch
    {
        "binance" => new ccxt.pro.binance(settings),
        "bybit" => new ccxt.pro.bybit(settings),
        _ => throw new NotSupportedException("Spot trading supports Binance and Bybit only.")
    };

    /// <summary>Opens a USDT-M client and limits market loading to linear contracts.</summary>
    private static ccxt.Exchange CreateFuturesExchange(TradingAccountProfile profile, Dictionary<string, object> settings)
    {
        var options = (Dictionary<string, object>)settings["options"];
        options["defaultType"] = profile.Exchange == "binance" ? "future" : "swap";
        options["defaultSubType"] = "linear";
        options["fetchMarkets"] = new Dictionary<string, object> { ["types"] = new List<object> { "linear" } };
        return profile.Exchange switch
        {
            "binance" => new ccxt.pro.binanceusdm(settings),
            "bybit" => new ccxt.pro.bybit(settings),
            _ => throw new NotSupportedException("Spot trading supports Binance and Bybit only.")
        };
    }

    private bool IsTradable(ccxt.MarketInterface market) => IsFutures ? IsLinearUsdtContract(market) : market.spot == true;

    private static bool IsLinearUsdtContract(ccxt.MarketInterface market) =>
        market.contract == true && market.linear == true && market.settle == "USDT";

    private Instrument DescribeInstrument(Instrument instrument, ccxt.MarketInterface market, InstrumentKey key) =>
        IsFutures ? DescribeFuturesInstrument(instrument, market, key) : instrument;

    /// <summary>Records contract size and USDT settlement on top of the shared market metadata.</summary>
    private static Instrument DescribeFuturesInstrument(Instrument instrument, ccxt.MarketInterface market, InstrumentKey key) => instrument with
    {
        Key = key,
        Expiry = market.expiry.HasValue ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(market.expiry.Value).UtcDateTime) : null,
        TradingRules = instrument.TradingRules! with
        {
            QuantityUnit = QuantityUnit.Contracts,
            ContractSize = SpotOrderMath.Number(market.contractSize),
            SettlementCurrency = "USDT"
        }
    };

    private Task<Dictionary<string, object>> CreateOrderParametersAsync(PlaceOrderRequest request, CancellationToken ct) =>
        IsFutures ? FuturesParametersAsync(request, ct) : Task.FromResult(OrderParameters(_profile.Exchange, request));

    /// <summary>Resolves a spot order by the original client id. Implicit Binance endpoints ignore CCXT routing options.</summary>
    private async Task<OrderSnapshot?> FindBinanceSpotOrderAsync(InstrumentKey key, string clientId, CancellationToken ct)
    {
        try
        {
            var raw = await ((ccxt.binance)_client).privateGetOrder(BinanceClientOrderQuery(key, clientId)).WaitAsync(ct).ConfigureAwait(false);
            return MapOrder(new ccxt.Order(_client.parseOrder(raw, _client.market(key.SecurityId))), key);
        }
        catch (ccxt.OrderNotFound) { return null; }
    }

    /// <summary>Resolves a USDT-M order by the original client id through the futures endpoint.</summary>
    private async Task<OrderSnapshot?> FindBinanceFuturesOrderAsync(InstrumentKey key, string clientId, CancellationToken ct)
    {
        try
        {
            var raw = await ((ccxt.binance)_client).fapiPrivateGetOrder(BinanceClientOrderQuery(key, clientId)).WaitAsync(ct).ConfigureAwait(false);
            return MapOrder(new ccxt.Order(_client.parseOrder(raw, _client.market(key.SecurityId))), key);
        }
        catch (ccxt.OrderNotFound) { return null; }
    }

    private Dictionary<string, object> BinanceClientOrderQuery(InstrumentKey key, string clientId) => new()
    {
        ["origClientOrderId"] = clientId,
        ["symbol"] = _client.Market(key.SecurityId).id!
    };

    private Dictionary<string, object> SpotBybitCancelParameters(OrderReference order)
    {
        var parameters = BybitCancelRequest(order);
        parameters["orderFilter"] = _bybitOrderFilters.GetValueOrDefault(order.ExchangeOrderId, "Order");
        return parameters;
    }

    private Dictionary<string, object> FuturesBybitCancelParameters(OrderReference order) => BybitCancelRequest(order);

    private Dictionary<string, object> BybitCancelRequest(OrderReference order) => new()
    {
        ["category"] = Category,
        ["symbol"] = _client.Market(order.Instrument.SecurityId).id!,
        ["orderId"] = order.ExchangeOrderId
    };

    /// <summary>Remembers Bybit's order filter so a later spot cancel reaches the same conditional order.</summary>
    private OrderSnapshot MapSpotAccountOrder(ccxt.Order order, InstrumentKey key)
    {
        if (_profile.Exchange == "bybit" && order.id is not null && order.info is { } info)
        {
            var filter = Text(info, "orderFilter");
            if (filter is not ("Order" or "StopOrder" or "tpslOrder" or "OcoOrder" or "BidirectionalTpslOrder"))
                filter = Text(info, "stopOrderType") switch { "Stop" => "StopOrder", "TakeProfit" or "StopLoss" => "tpslOrder",
                    "OcoOrder" => "OcoOrder", "BidirectionalTpslOrder" => "BidirectionalTpslOrder", _ => "Order" };
            _bybitOrderFilters[order.id] = filter;
        }
        return MapOrder(order, key);
    }

    /// <summary>Spot accounts use balances instead of contract positions.</summary>
    public Task<IReadOnlyList<PositionSnapshot>> PositionsAsync(InstrumentKey? key, CancellationToken ct) =>
        IsFutures ? LoadFuturesPositionsAsync(key, ct) : throw new NotSupportedException("Spot accounts use balances instead of contract positions.");

    /// <summary>Reads position size, actual leverage and position mode without changing exchange settings.</summary>
    private async Task<IReadOnlyList<PositionSnapshot>> LoadFuturesPositionsAsync(InstrumentKey? key, CancellationToken ct)
    {
        var parameters = Parameters();
        parameters["settleCoin"] = "USDT";
        var positions = await _client.FetchPositions(key is null ? null! : new List<object> { key.SecurityId }, parameters)
            .WaitAsync(ct).ConfigureAwait(false);
        var result = new List<PositionSnapshot>();

        foreach (var position in positions)
        {
            var market = _client.Market(position.symbol!);
            if (market.linear != true || market.settle != "USDT")
            {
                continue;
            }

            var index = position.info is null ? null : Text(position.info, "positionIdx");
            var side = index == "2" || position.side == "short" ? PositionSide.Short : PositionSide.Long;
            var hedged = index is "1" or "2" ? true : index == "0" ? false : position.hedged;
            result.Add(new PositionSnapshot(
                new(_profile.Exchange, "futures", "", position.symbol!), side,
                Math.Abs(SpotOrderMath.Number(position.contracts) ?? 0), QuantityUnit.Contracts,
                SpotOrderMath.Number(position.entryPrice),
                SpotOrderMath.Number(position.unrealizedPnl) is { } pnl ? new(pnl, "USDT") : null,
                SpotOrderMath.Number(position.initialMargin) is { } margin ? new(margin, "USDT") : null,
                SpotOrderMath.Number(position.leverage), hedged, position.marginMode));
        }

        if (_client is ccxt.binance binance && key is not null)
        {
            var mode = await binance.fapiPrivateGetPositionSideDual().WaitAsync(ct).ConfigureAwait(false);
            var hedged = mode.TryGetValue("dualSidePosition", out var flag) && Convert.ToBoolean(flag, CultureInfo.InvariantCulture);
            var configuration = await binance.fapiPrivateGetSymbolConfig(new Dictionary<string, object>
            {
                ["symbol"] = _client.Market(key.SecurityId).id!
            }).WaitAsync(ct).ConfigureAwait(false);
            var config = configuration.OfType<IDictionary<string, object>>().FirstOrDefault();
            var leverage = config is null ? null : DecimalField(config, "leverage");

            for (var index = 0; index < result.Count; index++)
            {
                result[index] = result[index] with { Leverage = leverage, Hedged = hedged };
            }

            foreach (var side in hedged ? new[] { PositionSide.Long, PositionSide.Short } : new[] { PositionSide.Long })
            {
                if (!result.Any(p => p.Side == side))
                {
                    result.Add(new(key, side, 0, QuantityUnit.Contracts, Leverage: leverage, Hedged: hedged,
                        MarginMode: config is null ? null : Text(config, "marginType")));
                }
            }
        }

        return result;
    }

    /// <summary>Maps hedge and one-way semantics, including Binance's prohibition on the reduceOnly flag in hedge mode.</summary>
    internal static Dictionary<string, object> FuturesOrderParameters(string exchange, PlaceOrderRequest request, bool hedged)
    {
        var parameters = new Dictionary<string, object>
        {
            ["clientOrderId"] = request.ClientOrderId,
            ["timeInForce"] = request.PostOnly ? exchange == "binance" ? "GTX" : "PostOnly" : "GTC"
        };

        if (exchange == "binance")
        {
            parameters["positionSide"] = hedged
                ? request.PositionSide == PositionSide.Long ? "LONG" : "SHORT"
                : "BOTH";

            if (!hedged)
            {
                parameters["reduceOnly"] = request.ReduceOnly;
            }
        }
        else
        {
            parameters["category"] = "linear";
            parameters["positionIdx"] = hedged ? request.PositionSide == PositionSide.Long ? 1 : 2 : 0;
            parameters["reduceOnly"] = request.ReduceOnly;
        }

        return parameters;
    }

    /// <summary>Obtains the current mode immediately before building the exchange request.</summary>
    private Task<Dictionary<string, object>> FuturesParametersAsync(PlaceOrderRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_validatedModes.TryRemove(request.ClientOrderId, out var validated) || validated.Request != request)
        {
            throw new TradingServiceException(new("ValidationRequired", "The exact futures request must be validated before dispatch."));
        }

        return Task.FromResult(FuturesOrderParameters(_profile.Exchange, request, validated.Hedged));
    }

    /// <summary>Rejects ambiguous mode/side combinations and closing requests that could increase exposure.</summary>
    internal static IReadOnlyList<TradingError> ValidatePositionIntent(PlaceOrderRequest request, IReadOnlyList<PositionSnapshot> positions)
    {
        if (positions.Count == 0 || positions.Any(p => p.Hedged is null))
        {
            return [new("PositionModeUnknown", "The exchange position mode could not be established.")];
        }

        var hedged = positions.Any(p => p.Hedged == true);
        if (hedged != request.PositionSide.HasValue)
        {
            return [new("PositionSide", hedged ? "Select Long or Short for hedge mode." : "Use Auto for one-way mode.")];
        }

        if (request.ReduceOnly)
        {
            var target = hedged ? request.PositionSide!.Value
                : request.Side == OrderSide.Sell ? PositionSide.Long : PositionSide.Short;
            var available = positions.Where(p => p.Side == target).Sum(p => p.Quantity);
            var closingSide = target == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            if (request.Side != closingSide || request.Quantity > available)
            {
                return [new("ReduceOnly", "Reduce-only must close an existing position without exceeding its size.")];
            }
        }

        return [];
    }

    /// <summary>Checks real available collateral and current leverage; spot cash checks are not reused for contracts.</summary>
    public async Task<IReadOnlyList<TradingError>> ValidateFuturesAsync(
        PlaceOrderRequest request, SpotMarket market, AccountSnapshot balance, CancellationToken ct)
    {
        var positions = await PositionsAsync(request.Instrument, ct).ConfigureAwait(false);
        var errors = ValidatePositionIntent(request, positions);
        if (errors.Count > 0)
        {
            return errors;
        }

        if (request.ReduceOnly)
        {
            _validatedModes[request.ClientOrderId] = (request, positions.Any(p => p.Hedged == true));
            return [];
        }

        var leverage = positions.Where(p => !request.PositionSide.HasValue || p.Side == request.PositionSide)
            .Select(p => p.Leverage).FirstOrDefault();
        var collateral = balance.Margins?.FirstOrDefault(m => m.Currency == "USDT")?.AvailableFunds;
        var contractSize = market.Instrument.TradingRules?.ContractSize;

        if (leverage is not > 0 || collateral is null || contractSize is not > 0 || market.FeeRate is null)
        {
            return [new("MarginUnavailable", "Available USDT margin, leverage or contract metadata is unknown.")];
        }

        var ticker = await _client.FetchTicker(request.Instrument.SecurityId, Parameters()).WaitAsync(ct).ConfigureAwait(false);
        var reference = SpotOrderMath.Number(ticker.last);
        if (reference is not > 0)
        {
            return [new("PriceUnavailable", "A fresh contract price is required for the margin check.")];
        }

        var required = RequiredMargin(request.Quantity, contractSize.Value, request.Price!.Value,
            reference.Value, leverage.Value, market.FeeRate.Value, request.Side);
        if (collateral < required)
        {
            return [new("InsufficientMargin", "Insufficient available USDT margin, including commission coverage.")];
        }

        _validatedModes[request.ClientOrderId] = (request, positions.Any(p => p.Hedged == true));
        return [];
    }

    /// <summary>Reserves initial margin plus opening/closing commission at the higher current or limit price.</summary>
    internal static decimal RequiredMargin(decimal quantity, decimal contractSize, decimal price,
        decimal reference, decimal leverage, decimal fee, OrderSide side = OrderSide.Buy)
    {
        if (quantity <= 0 || contractSize <= 0 || price <= 0 || reference <= 0 || leverage <= 0 || fee < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        var notional = quantity * contractSize * Math.Max(price, reference);
        var openingLoss = quantity * contractSize * Math.Max(0, side == OrderSide.Buy ? price - reference : reference - price);
        return notional / leverage + notional * fee * 2 + openingLoss;
    }

    /// <summary>Reads derivative collateral; Bybit multi-coin and portfolio margin remain explicitly unavailable.</summary>
    private async Task<AccountSnapshot> FuturesBalanceAsync(TradingAccountContext account, CancellationToken ct)
    {
        var balances = await _client.FetchBalance(Parameters()).WaitAsync(ct).ConfigureAwait(false);
        decimal? available = null;

        if (_profile.Exchange == "binance")
        {
            available = SpotOrderMath.Number(balances.free.GetValueOrDefault("USDT"));
        }
        else if (balances.info?.GetValueOrDefault("result") is IDictionary<string, object> result &&
                 result.TryGetValue("list", out var walletList) && walletList is IEnumerable wallets)
        {
            foreach (var wallet in wallets.Cast<IDictionary<string, object>>())
            {
                var coins = ((IEnumerable)wallet["coin"]).Cast<IDictionary<string, object>>().ToArray();
                var usdt = coins.FirstOrDefault(c => Text(c, "coin") == "USDT");
                var otherCollateral = coins.Any(c => Text(c, "coin") != "USDT" && DecimalField(c, "equity") is not (null or 0) &&
                    (!bool.TryParse(Text(c, "collateralSwitch"), out var enabled) || enabled));

                if (usdt is not null && !otherCollateral && DecimalField(usdt, "equity") is > 0 &&
                    DecimalField(usdt, "usdValue") is > 0 && DecimalField(wallet, "totalAvailableBalance") is { } dollars)
                {
                    available = dollars * DecimalField(usdt, "equity") / DecimalField(usdt, "usdValue");
                }
            }
        }

        var snapshot = MapBalance(balances, account);
        return snapshot with
        {
            Margins = [new MarginSnapshot("USDT", AvailableFunds: available)],
            Positions = null
        };
    }
}
```

<a id="file-09"></a>

## 9. BotView/Services/Integrations/Ccxt/CcxtTradingService.cs

Полная версия из `a3d8986`. SHA-256 исходного Git blob: `057d7561e389b1dc199393e504f22debf6356e238fe74c688b2beafd8ee6d4ab`.

```csharp
﻿using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BotView.Models;
using BotView.Services;

namespace BotView.Services.Integrations.Ccxt;

/// <summary>One account and market's session. Owns recovery, validation and serialized mutations.</summary>
public sealed class CcxtTradingService : ITradingService
{
    private static readonly TradingCapabilities SpotCapabilities = new(new HashSet<OrderType> { OrderType.Limit },
        new HashSet<OrderTimeInForce> { OrderTimeInForce.Gtc }, SupportsPostOnly: true,
        SupportsLookupByClientOrderId: true, SupportsOrderHistory: true, SupportsExecutions: true);
    private TradingCapabilities Capabilities => _client.IsFutures
        ? SpotCapabilities with { SupportsReduceOnly = true, SupportsPositionSide = true, SupportsPositions = true }
        : SpotCapabilities;

    private readonly ISpotTradingClient _client;
    private readonly TradingJournal _journal;
    private readonly string _accountKey;
    private readonly bool? _canTrade;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<InstrumentKey, byte> _tracked = new();
    private readonly ConcurrentDictionary<InstrumentKey, bool> _ready = new();
    private readonly ConcurrentDictionary<InstrumentKey, SpotMarket> _markets = new();
    private readonly ConcurrentDictionary<InstrumentKey, DateTimeOffset> _marketTimes = new();
    private readonly Dictionary<OrderReference, OrderSnapshot> _orders = new();
    private readonly Dictionary<(InstrumentKey, string), ExecutionSnapshot> _fills = new();
    private readonly Dictionary<string, TradingOperation> _operations = new();
    private readonly ConcurrentDictionary<Guid, Channel<TradingUpdate>> _subscribers = new();
    private readonly List<Task> _streams = new();
    private readonly Task _worker;
    private AccountSnapshot? _balance;
    private string _message = "Synchronizing account…";
    private TradingConnectionState _connection = TradingConnectionState.Connecting;
    private bool _balanceDirty = true;
    private bool _disposed;
    private Task? _disposeTask;
    private int _resyncGeneration;

    public TradingAccountContext Account { get; }
    internal bool? KeyCanTrade => _canTrade;

    /// <summary>Immediately prevents further mutations when account credentials change.</summary>
    internal void Suspend() { _disposed = true; _stop.Cancel(); }

    /// <summary>Checks pair readiness independently of other windows' tracked markets.</summary>
    public bool IsReady(InstrumentKey key) => !_disposed && _ready.GetValueOrDefault(key) && Current.Ready;

    /// <summary>Validates cached limits and precision for immediate input feedback without network requests.</summary>
    public OrderValidationResult ValidateInput(PlaceOrderRequest request)
    {
        if (!_markets.TryGetValue(request.Instrument, out var market)) return new([new("MetadataRequired", "Wait for trading metadata.")]);
        var errors = TradingOrderValidator.Validate(request, market.Instrument, Capabilities, Account, DateTimeOffset.UtcNow).Errors.ToList();
        try
        {
            if (request.Quantity > 0 && _client.NormalizeAmount(market.Instrument.Symbol, request.Quantity) != request.Quantity) errors.Add(new("QuantityPrecision", "Quantity does not match exchange precision."));
            if (request.Price is > 0 && _client.NormalizePrice(market.Instrument.Symbol, request.Price.Value) != request.Price) errors.Add(new("PricePrecision", "Price does not match exchange precision."));
        }
        catch (Exception) { errors.Add(new("Precision", "The entered values cannot be represented at the exchange precision.")); }
        if (_canTrade == false) errors.Add(new("TradingDenied", "Trading is denied for this API key."));
        return new(errors);
    }

    /// <summary>Restores durable state only after the provider has authenticated this exact account/network.</summary>
    internal CcxtTradingService(TradingAccountProfile profile, ISpotTradingClient client, TradingJournal journal, string identity, bool? canTrade)
    {
        _client = client;
        _journal = journal;
        _accountKey = profile.Market == TradingMarket.Spot
            ? $"{profile.Exchange}:{profile.Environment}:{identity}"
            : $"{profile.Exchange}:{profile.Environment}:{profile.Market}:{identity}";
        Account = new(profile.Id.ToString(), identity, profile.Exchange, profile.Environment, profile.Market);
        _canTrade = canTrade;
        foreach (var order in journal.Orders(_accountKey)) _orders[order.Reference] = order;
        foreach (var fill in journal.Executions(_accountKey)) _fills[(fill.Order.Instrument, fill.ExecutionId)] = fill;
        foreach (var operation in journal.Operations(_accountKey))
        {
            var restored = operation.Phase == TradingOperationPhase.Dispatching ? operation with { Phase = TradingOperationPhase.Unknown } : operation;
            _operations[operation.Id] = restored;
            if (restored.Phase == TradingOperationPhase.Unknown) _tracked.TryAdd(restored.Request.Instrument, 0);
        }
        foreach (var order in _orders.Values.Where(o => !Terminal(o.State))) _tracked.TryAdd(order.Reference.Instrument, 0);
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Returns a detached state; views can coalesce updates on their existing 50 ms timer.</summary>
    public SpotTradingState Current
    {
        get
        {
            lock (_sync) return new(_connection, _message, _orders.Values.ToArray(), _fills.Values.OrderByDescending(f => f.ExecutedAt).ToArray(),
                _balance, _operations.Values.Where(Pending).ToArray(), !_disposed && !_balanceDirty && _connection == TradingConnectionState.Connected);
        }
    }

    /// <summary>Adds a pair for ongoing supervision; window closure deliberately does not untrack live orders.</summary>
    public async Task TrackAsync(InstrumentKey key, CancellationToken ct = default)
    {
        _tracked.TryAdd(key, 0);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await MarketCoreAsync(key, ct).ConfigureAwait(false);
            StartStreams(key);
            await ReconcileCoreAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { MarkDisconnected(ex); throw Safe(ex); }
        finally { _gate.Release(); }
    }

    /// <summary>Converts quote input through the exchange's quantity precision without rounding the price.</summary>
    public decimal ConvertQuantity(InstrumentKey key, decimal input, decimal price, OrderQuantityCurrency currency) =>
        SpotOrderMath.Quantity(input, price * (_markets.GetValueOrDefault(key)?.Instrument.TradingRules?.ContractSize ?? 1m), currency, value => _client.NormalizeAmount(key.SecurityId, value));

    /// <summary>Values native contract quantities using the exchange contract multiplier.</summary>
    public decimal OrderNotional(InstrumentKey key, decimal quantity, decimal price) =>
        quantity * price * (_markets.GetValueOrDefault(key)?.Instrument.TradingRules?.ContractSize ?? 1m);

    /// <summary>Loads market metadata within the account request gate.</summary>
    public async Task<Instrument> GetTradingInstrumentAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return (await MarketCoreAsync(instrument, ct).ConfigureAwait(false)).Instrument; }
        catch (Exception ex) { throw Safe(ex); }
        finally { _gate.Release(); }
    }

    /// <summary>Advertises only the spot limit functionality implemented by this adapter.</summary>
    public async Task<TradingCapabilities> GetCapabilitiesAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        await GetTradingInstrumentAsync(instrument, ct).ConfigureAwait(false);
        return Capabilities;
    }

    /// <summary>Runs a preview check; placement repeats it under the same account mutation gate.</summary>
    public async Task<OrderValidationResult> ValidateOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ValidateCoreAsync(request, ct).ConfigureAwait(false); }
        catch (Exception ex) { throw Safe(ex); }
        finally { _gate.Release(); }
    }

    /// <summary>Records a unique intent durably, then dispatches once; uncertainty never causes automatic resubmission.</summary>
    public async Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        ct = lifetime.Token;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        TradingOperation? operation = null;
        var dispatched = false;
        var responded = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_sync) if (_operations.Values.Any(o => o.Request.ClientOrderId == request.ClientOrderId))
                return new(PlaceOrderStatus.Rejected, request.ClientOrderId, request.Instrument, Error: new("DuplicateIntent", "This client order ID has already been used."));
            var validation = await ValidateCoreAsync(request, ct).ConfigureAwait(false);
            if (!validation.IsValid) return new(PlaceOrderStatus.Rejected, request.ClientOrderId, request.Instrument, Error: validation.Errors[0]);
            operation = new(request.ClientOrderId, _accountKey, request, TradingOperationPhase.Prepared, DateTimeOffset.UtcNow);
            Save(operation);
            ct.ThrowIfCancellationRequested();
            operation = operation with { Phase = TradingOperationPhase.Dispatching };
            Save(operation);
            lock (_sync) _balanceDirty = true;
            dispatched = true;
            var order = await _client.PlaceAsync(request, ct).ConfigureAwait(false);
            responded = true;
            if (!string.IsNullOrWhiteSpace(order.Reference.ExchangeOrderId)) Merge(order);
            operation = operation with { ExchangeOrderId = order.Reference.ExchangeOrderId,
                Phase = order.State is OrderState.Open || Terminal(order.State) ? TradingOperationPhase.Resolved : TradingOperationPhase.Unknown };
            Save(operation);
            // Only an authoritative balance fetched after the order observation can release the local spend barrier.
            try { await RefreshBalanceCoreAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception) { MarkDisconnected(null); }
            return new(order.State == OrderState.Rejected ? PlaceOrderStatus.Rejected : operation.Phase == TradingOperationPhase.Resolved ? PlaceOrderStatus.Accepted : PlaceOrderStatus.Submitted,
                request.ClientOrderId, request.Instrument, order.Reference.ExchangeOrderId, Order: order);
        }
        catch (Exception ex)
        {
            if (dispatched && operation is not null)
            {
                var rejected = !responded && DefiniteRejection(ex);
                RecordFailure(operation, rejected);
                return new(rejected ? PlaceOrderStatus.Rejected : PlaceOrderStatus.Unknown, request.ClientOrderId, request.Instrument,
                    Error: rejected ? Error(ex) : new("ResultUnknown", "No definitive order outcome. Reconciling the original client ID; no automatic resend."));
            }
            if (ex is OperationCanceledException) throw;
            return new(PlaceOrderStatus.Rejected, request.ClientOrderId, request.Instrument, Error: Error(ex));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Persists cancellation uncertainty independently of the order's partial executions.</summary>
    public async Task<CancelOrderResult> CancelOrderAsync(OrderReference order, CancellationToken ct = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        ct = lifetime.Token;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        TradingOperation? operation = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await MarketCoreAsync(order.Instrument, ct).ConfigureAwait(false);
            if (_canTrade == false) return new(CancelOrderStatus.Rejected, order, Error: new("TradingDenied", "This API key cannot cancel orders."));
            var known = await _client.OrderAsync(order, ct).ConfigureAwait(false);
            if (known is not null)
            {
                Merge(known);
                if (Terminal(known.State)) return new(CancelOrderStatus.AlreadyClosed, order, Order: known);
            }
            lock (_sync) if (_operations.Values.Any(o => Pending(o) && o.CancelTarget == order))
                return new(CancelOrderStatus.Unknown, order, Error: new("CancelPending", "An earlier cancellation is still being reconciled."));
            var request = new PlaceOrderRequest(order.Instrument, known?.Side ?? OrderSide.Buy, OrderType.Limit,
                known?.Quantity ?? 0, known?.ClientOrderId ?? "", known?.Price);
            operation = new("cancel:" + Guid.NewGuid().ToString("N"), _accountKey, request, TradingOperationPhase.Prepared, DateTimeOffset.UtcNow, order);
            Save(operation);
            ct.ThrowIfCancellationRequested();
            operation = operation with { Phase = TradingOperationPhase.Dispatching };
            Save(operation);
            var result = await _client.CancelAsync(order, ct).ConfigureAwait(false);
            if (result is not null && result.Quantity > 0) Merge(result);
            var final = result is not null && Terminal(result.State);
            Save(operation with { Phase = final ? TradingOperationPhase.Resolved : TradingOperationPhase.Unknown });
            lock (_sync) _balanceDirty = true;
            return new(final ? result!.State == OrderState.Filled ? CancelOrderStatus.AlreadyClosed : CancelOrderStatus.Canceled : CancelOrderStatus.Submitted, order, Order: result);
        }
        catch (Exception ex)
        {
            if (operation?.Phase == TradingOperationPhase.Dispatching)
            {
                if (ex is ccxt.PermissionDenied or ccxt.AuthenticationError)
                {
                    RecordFailure(operation, true);
                    return new(CancelOrderStatus.Rejected, order, Error: Error(ex));
                }
                RecordFailure(operation, false);
                return new(CancelOrderStatus.Unknown, order, Error: new("CancelUnknown", "Cancellation outcome is unknown; orders and executions will be reconciled."));
            }
            if (ex is OperationCanceledException) throw;
            return new(CancelOrderStatus.Rejected, order, Error: Error(ex));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Captures every current order ID before the view requests confirmation.</summary>
    public async Task<CancelBatch> PrepareCancelAllAsync(InstrumentKey key, CancellationToken ct = default)
    {
        var orders = new List<OrderSnapshot>();
        string? cursor = null;
        var cursors = new HashSet<string>();
        do
        {
            var page = await GetOpenOrdersAsync(new(key, Cursor: cursor), ct).ConfigureAwait(false);
            orders.AddRange(page.Items);
            cursor = page.NextCursor;
            if (cursor is not null && !cursors.Add(cursor)) throw new TradingServiceException(new("Pagination", "Could not capture a complete cancellation snapshot."));
        } while (cursor is not null);
        if (orders.Any(o => string.IsNullOrWhiteSpace(o.Reference.ExchangeOrderId)))
            throw new TradingServiceException(new("OrderIdentityMissing", "An exchange order has no usable identity; cancellation snapshot is incomplete."));
        return new(Account, key, orders.Where(o => !Terminal(o.State)).Select(o => o.Reference).Distinct().ToArray());
    }

    /// <summary>Cancels only confirmed snapshot IDs and returns every individual outcome, including partial failure.</summary>
    public async Task<IReadOnlyList<CancelOrderResult>> CancelBatchAsync(CancelBatch batch, CancellationToken ct = default)
    {
        if (batch.Account != Account || batch.Orders.Any(o => o.Instrument != batch.Instrument)) throw new ArgumentException("Cancel batch account mismatch.");
        var results = new List<CancelOrderResult>();
        foreach (var order in batch.Orders) results.Add(await CancelOrderAsync(order, ct).ConfigureAwait(false));
        return results;
    }

    /// <summary>Reads an authoritative order without leaking provider exceptions.</summary>
    public Task<OrderSnapshot?> GetOrderAsync(OrderReference order, CancellationToken ct = default) => ReadInstrumentAsync(order.Instrument, () => _client.OrderAsync(order, ct), ct);

    /// <summary>Missing history is not used as proof that an uncertain placement failed.</summary>
    public Task<OrderSnapshot?> FindOrderByClientIdAsync(InstrumentKey instrument, string clientOrderId, CancellationToken ct = default) => ReadInstrumentAsync(instrument, () => _client.FindAsync(instrument, clientOrderId, ct), ct);

    /// <summary>Exposes provider continuation rather than silently truncating open orders.</summary>
    public Task<TradingPage<OrderSnapshot>> GetOpenOrdersAsync(TradingQuery query, CancellationToken ct = default) => ReadInstrumentAsync(query.Instrument, () => _client.OrdersAsync(query, true, ct), ct);

    /// <summary>Exposes paged order history in application models.</summary>
    public Task<TradingPage<OrderSnapshot>> GetOrderHistoryAsync(TradingQuery query, CancellationToken ct = default) => ReadInstrumentAsync(query.Instrument, () => _client.OrdersAsync(query, false, ct), ct);

    /// <summary>Exposes paged executions with stable fill identities.</summary>
    public Task<TradingPage<ExecutionSnapshot>> GetExecutionsAsync(TradingQuery query, CancellationToken ct = default) => ReadInstrumentAsync(query.Instrument, () => _client.ExecutionsAsync(query, ct), ct);

    /// <summary>Returns a fresh account snapshot rather than substituting an unavailable section with zeroes.</summary>
    public Task<AccountSnapshot> GetAccountStateAsync(CancellationToken ct = default) => ReadAsync(() => _client.BalanceAsync(Account, ct), ct);

    /// <summary>Spot holdings are balances; derivative position operations are not supported.</summary>
    public Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(InstrumentKey? instrument = null, CancellationToken ct = default) => ReadAsync(() => _client.PositionsAsync(instrument, ct), ct);

    /// <summary>Broadcasts independent bounded streams; a slow reader is instructed to resynchronize after overflow.</summary>
    public async IAsyncEnumerable<TradingUpdate> WatchUpdatesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<TradingUpdate>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.Wait });
        _subscribers[id] = channel;
        channel.Writer.TryWrite(new TradingConnectionChanged(Current.Connection, DateTimeOffset.UtcNow, true));
        try { await foreach (var update in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return update; }
        finally { _subscribers.TryRemove(id, out _); }
    }

    /// <summary>Serializes REST operations while allowing independent private-stream waits.</summary>
    private async Task<T> ReadAsync<T>(Func<Task<T>> read, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); return await read().ConfigureAwait(false); }
        catch (Exception ex) { throw Safe(ex); }
        finally { _gate.Release(); }
    }

    /// <summary>Ensures listing metadata exists even when a contract method is used before a book is opened.</summary>
    private Task<T> ReadInstrumentAsync<T>(InstrumentKey? key, Func<Task<T>> read, CancellationToken ct) => ReadAsync(async () =>
    {
        if (key is null) throw new NotSupportedException("A spot instrument is required.");
        await MarketCoreAsync(key, ct).ConfigureAwait(false);
        return await read().ConfigureAwait(false);
    }, ct);

    /// <summary>Caches spot metadata after successful loading under the account gate.</summary>
    private async Task<SpotMarket> MarketCoreAsync(InstrumentKey key, CancellationToken ct)
    {
        if (_markets.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - _marketTimes[key] < TimeSpan.FromMinutes(5)) return cached;
        var market = await _client.MarketAsync(key, ct).ConfigureAwait(false);
        _markets[key] = market;
        _marketTimes[key] = DateTimeOffset.UtcNow;
        return market;
    }

    /// <summary>Checks limits, precision, permission and fresh funds immediately before dispatch.</summary>
    private async Task<OrderValidationResult> ValidateCoreAsync(PlaceOrderRequest request, CancellationToken ct)
    {
        var market = await MarketCoreAsync(request.Instrument, ct).ConfigureAwait(false);
        var errors = ValidateInput(request).Errors.ToList();
        if (request.ClientOrderId.Length > 36 || request.ClientOrderId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) errors.Add(new("ClientOrderId", "Use a unique client ID of at most 36 ASCII characters."));
        if (!_ready.GetValueOrDefault(request.Instrument) || _disposed) errors.Add(new("Synchronizing", "Wait for the account and this pair to synchronize."));
        TradingOperation[] pending;
        lock (_sync) pending = _operations.Values.Where(Pending).ToArray();
        if (pending.Any(o => o.Request.Instrument == request.Instrument)) errors.Add(new("UnknownOperation", "An operation on this pair is still being reconciled; it will not be resent."));
        var spendCurrency = _client.IsFutures ? "USDT" : request.Side == OrderSide.Buy ? market.Quote : market.Base;
        // When an unknown order may already be reflected in free balance, subtraction could count it twice.
        // Block competing spending in its currency until order+balance reconciliation establishes the truth.
        if (pending.Any(o => _client.IsFutures ||
            _markets.TryGetValue(o.Request.Instrument, out var m) &&
            (o.Request.Side == OrderSide.Buy ? m.Quote : m.Base) == spendCurrency))
            errors.Add(new("FundsUncertain", "Funds in this currency are awaiting reconciliation of an earlier operation."));
        if (errors.Count > 0) return new(errors);
        errors.AddRange(await _client.ValidateMarketAsync(request, ct).ConfigureAwait(false));
        if (errors.Count > 0) return new(errors);
        if (_balanceDirty || _balance is null || DateTimeOffset.UtcNow - _balance.AsOf > TimeSpan.FromSeconds(5))
            await RefreshBalanceCoreAsync(ct).ConfigureAwait(false);
        if (_client.IsFutures)
        {
            errors.AddRange(await _client.ValidateFuturesAsync(request, market, _balance!, ct).ConfigureAwait(false));
            return new(errors);
        }

        if (market.FeeRate is null) errors.Add(new("FeeUnavailable", "Trading fees could not be established."));
        else
        {
            // Conservative coverage permits commissions in either input asset without assuming discounts or borrowing.
            var feeInSpend = market.FeeSide switch { "get" => false, "base" => request.Side == OrderSide.Sell,
                "quote" => request.Side == OrderSide.Buy, _ => true };
            var required = (request.Side == OrderSide.Buy ? request.Quantity * request.Price!.Value : request.Quantity) * (1 + (feeInSpend ? market.FeeRate.Value : 0));
            var available = _balance?.Balances?.FirstOrDefault(b => b.Currency == spendCurrency)?.Available;
            if (available is null) errors.Add(new("BalanceUnavailable", $"Available {spendCurrency} balance is unknown."));
            else if (available < required) errors.Add(new("InsufficientFunds", $"Insufficient available {spendCurrency}, including commission coverage."));
        }
        return new(errors);
    }

    /// <summary>Refreshes all tracked pairs after stream disruption, with capped exponential reconnect delay.</summary>
    private async Task RunAsync()
    {
        var retry = 1;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                foreach (var key in _tracked.Keys)
                {
                    await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
                    try
                    {
                        await MarketCoreAsync(key, _stop.Token).ConfigureAwait(false);
                        StartStreams(key);
                        await ReconcileCoreAsync(key, _stop.Token).ConfigureAwait(false);
                    }
                    finally { _gate.Release(); }
                }
                retry = 1;
                await Task.Delay(2000, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                MarkDisconnected(ex);
                try { await Task.Delay(TimeSpan.FromSeconds(retry), _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                retry = Math.Min(30, retry * 2);
            }
        }
    }

    private readonly HashSet<(string, InstrumentKey?)> _started = new();

    /// <summary>Starts private watchers before REST snapshots; reducers safely merge buffered and later events.</summary>
    private void StartStreams(InstrumentKey key)
    {
        foreach (var kind in new[] { "watchOrders", "watchMyTrades", "watchBalance" })
        {
            var identity = (kind, kind == "watchBalance" ? null : key);
            if (!_client.SupportsStream(kind) || !_started.Add(identity)) continue;
            _streams.Add(Task.Run(() => WatchCoreAsync(kind, key)));
        }
    }

    /// <summary>Retries failed streams while REST reconciliation remains the authority for readiness.</summary>
    private async Task WatchCoreAsync(string kind, InstrumentKey key)
    {
        var retry = 1;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                foreach (var update in await _client.WatchAsync(kind, key, Account, _stop.Token).ConfigureAwait(false))
                {
                    if (update is OrderChanged order) Merge(order.Order);
                    else if (update is ExecutionReceived fill) Merge(fill.Execution);
                    else if (update is AccountChanged balance)
                    {
                        lock (_sync)
                        {
                            if (_balance is null || balance.AsOf >= _balance.AsOf) _balance = balance.Account;
                            _balanceDirty = true;
                        }
                        Publish(update);
                    }
                }
                retry = 1;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (ccxt.NotSupported) { break; }
            catch (Exception ex)
            {
                MarkDisconnected(ex);
                try { await Task.Delay(TimeSpan.FromSeconds(retry), _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                retry = Math.Min(30, retry * 2);
            }
        }
    }

    /// <summary>Combines complete open-order pages, point lookups, unresolved intents, executions and a final balance.</summary>
    private async Task ReconcileCoreAsync(InstrumentKey key, CancellationToken ct)
    {
        var generation = Volatile.Read(ref _resyncGeneration);
        string? cursor = null;
        var seen = new HashSet<string>();
        var observedOpen = new HashSet<OrderReference>();
        do
        {
            var page = await _client.OrdersAsync(new(key, Limit: 1000, Cursor: cursor), true, ct).ConfigureAwait(false);
            foreach (var order in page.Items) { observedOpen.Add(order.Reference); Merge(order); }
            cursor = page.NextCursor;
            if (cursor is not null && !seen.Add(cursor)) throw new InvalidOperationException("Repeated history cursor.");
        } while (cursor is not null);
        OrderSnapshot[] known;
        TradingOperation[] pending;
        lock (_sync)
        {
            known = _orders.Values.Where(o => o.Reference.Instrument == key && !Terminal(o.State)).ToArray();
            pending = _operations.Values.Where(o => Pending(o) && o.Request.Instrument == key).ToArray();
        }
        foreach (var order in known.Where(o => !observedOpen.Contains(o.Reference)))
        {
            var current = await _client.OrderAsync(order.Reference, ct).ConfigureAwait(false);
            if (current is not null) Merge(current);
        }
        foreach (var operation in pending)
        {
            var order = operation.CancelTarget is { } target ? await _client.OrderAsync(target, ct).ConfigureAwait(false)
                : await _client.FindAsync(key, operation.Request.ClientOrderId, ct).ConfigureAwait(false);
            if (order is null && operation.CancelTarget is null && operation.CreatedAt < DateTimeOffset.UtcNow.AddDays(-6))
                order = await FindInHistoryCoreAsync(operation, ct).ConfigureAwait(false);
            if (order is not null)
            {
                Merge(order);
                if (operation.CancelTarget is null && order.State is not (OrderState.Pending or OrderState.Unknown) || operation.CancelTarget is not null && Terminal(order.State))
                    Save(operation with { Phase = TradingOperationPhase.Resolved, ExchangeOrderId = order.Reference.ExchangeOrderId });
            }
        }
        DateTimeOffset since;
        var recoveryStart = known.Where(o => o.CreatedAt.HasValue).Select(o => o.CreatedAt!.Value)
            .Concat(pending.Select(o => o.CreatedAt)).DefaultIfEmpty(DateTimeOffset.UtcNow.AddDays(-1)).Min();
        lock (_sync) since = _fills.Values.Where(f => f.Order.Instrument == key).Select(f => f.ExecutedAt).DefaultIfEmpty(recoveryStart).Max().AddMinutes(-5);
        var until = DateTimeOffset.UtcNow;
        while (since < until)
        {
            var end = HistoryEnd(since, until);
            cursor = null; seen.Clear();
            do
            {
                var page = await _client.ExecutionsAsync(new(key, Since: since, Until: end, Limit: 1000, Cursor: cursor), ct).ConfigureAwait(false);
                foreach (var fill in page.Items) Merge(fill);
                cursor = page.NextCursor;
                if (cursor is not null && !seen.Add(cursor)) throw new InvalidOperationException("Repeated execution cursor.");
            } while (cursor is not null);
            since = end;
        }
        await RefreshBalanceCoreAsync(ct).ConfigureAwait(false);
        if (generation != Volatile.Read(ref _resyncGeneration)) return;
        _ready[key] = true;
        lock (_sync)
        {
            _connection = TradingConnectionState.Connected;
            _message = _canTrade is null ? "Connected · trading permissions unknown" : _canTrade == false ? "Connected · trading denied" : "Connected";
        }
        Publish(new TradingConnectionChanged(TradingConnectionState.Connected, DateTimeOffset.UtcNow));
    }

    /// <summary>Keeps one history request inside the venue limit. Binance rejects startTime and endTime more than 24 hours apart; Bybit allows seven days.</summary>
    private DateTimeOffset HistoryEnd(DateTimeOffset since, DateTimeOffset until)
    {
        var window = Account.Venue == "bybit" ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24) - TimeSpan.FromMilliseconds(1);
        var end = since + window;
        return end < until ? end : until;
    }

    /// <summary>Searches older unresolved client IDs in bounded history windows without guessing or resending.</summary>
    private async Task<OrderSnapshot?> FindInHistoryCoreAsync(TradingOperation operation, CancellationToken ct)
    {
        var since = operation.CreatedAt.AddMinutes(-5);
        var until = DateTimeOffset.UtcNow;
        while (since < until)
        {
            var end = HistoryEnd(since, until);
            string? cursor = null;
            var seen = new HashSet<string>();
            do
            {
                var page = await _client.OrdersAsync(new(operation.Request.Instrument, since, end, 1000, cursor), false, ct).ConfigureAwait(false);
                var found = page.Items.FirstOrDefault(o => o.ClientOrderId == operation.Request.ClientOrderId);
                if (found is not null) return found;
                cursor = page.NextCursor;
                if (cursor is not null && !seen.Add(cursor)) throw new InvalidOperationException("Repeated order history cursor.");
            } while (cursor is not null);
            since = end;
        }
        return null;
    }

    /// <summary>Fetches authoritative free balances after order state, avoiding double local reservations.</summary>
    private async Task RefreshBalanceCoreAsync(CancellationToken ct)
    {
        var balance = await _client.BalanceAsync(Account, ct).ConfigureAwait(false);
        if (_client.IsFutures)
        {
            balance = balance with { Positions = await _client.PositionsAsync(null, ct).ConfigureAwait(false) };
        }
        lock (_sync) { _balance = balance; _balanceDirty = false; }
        Publish(new AccountChanged(balance));
    }

    /// <summary>Reduces duplicate/out-of-order snapshots without losing fills or reopening terminal orders.</summary>
    internal void Merge(OrderSnapshot order)
    {
        if (string.IsNullOrWhiteSpace(order.Reference.ExchangeOrderId)) return;
        lock (_sync)
        {
            if (_orders.TryGetValue(order.Reference, out var old)) order = Reduce(old, order);
            _orders[order.Reference] = order;
            _journal.Save(_accountKey, order);
            foreach (var operation in _operations.Values.Where(o => Pending(o) &&
                (o.CancelTarget == order.Reference || o.CancelTarget is null && o.Request.Instrument == order.Reference.Instrument && o.Request.ClientOrderId == order.ClientOrderId)).ToArray())
                if (operation.CancelTarget is null && order.State is not (OrderState.Pending or OrderState.Unknown) || operation.CancelTarget is not null && Terminal(order.State))
                    Save(operation with { Phase = TradingOperationPhase.Resolved, ExchangeOrderId = order.Reference.ExchangeOrderId });
        }
        Publish(new OrderChanged(order));
    }

    /// <summary>Preserves monotonic filled quantity and terminal state under delayed stream messages.</summary>
    internal static OrderSnapshot Reduce(OrderSnapshot old, OrderSnapshot next)
    {
        var filled = old.FilledQuantity.HasValue && next.FilledQuantity.HasValue ? Math.Max(old.FilledQuantity.Value, next.FilledQuantity.Value) : next.FilledQuantity ?? old.FilledQuantity;
        var newer = next.UpdatedAt >= old.UpdatedAt || (next.FilledQuantity ?? 0) > (old.FilledQuantity ?? 0);
        var result = newer ? next : old;
        if (Terminal(old.State) && !Terminal(result.State)) result = result with { State = old.State };
        if (old.State == OrderState.Filled) result = result with { State = OrderState.Filled };
        var quantity = result.Quantity > 0 ? result.Quantity : old.Quantity;
        return result with { Quantity = quantity, FilledQuantity = filled,
            RemainingQuantity = filled.HasValue ? Math.Max(0, quantity - filled.Value) : result.RemainingQuantity ?? old.RemainingQuantity,
            Price = result.Price ?? old.Price, ClientOrderId = result.ClientOrderId ?? old.ClientOrderId,
            StatusReason = result.StatusReason ?? old.StatusReason,
            PositionSide = result.PositionSide ?? old.PositionSide,
            ReduceOnly = result.ReduceOnly ?? old.ReduceOnly,
            AverageFillPrice = result.AverageFillPrice ?? old.AverageFillPrice, CreatedAt = result.CreatedAt ?? old.CreatedAt,
            UpdatedAt = next.UpdatedAt > old.UpdatedAt ? next.UpdatedAt : old.UpdatedAt };
    }

    /// <summary>Persists a fill once, allowing a later event to enrich a previously unknown commission.</summary>
    private void Merge(ExecutionSnapshot fill)
    {
        if (string.IsNullOrEmpty(fill.ExecutionId)) return;
        lock (_sync)
        {
            var key = (fill.Order.Instrument, fill.ExecutionId);
            if (_fills.TryGetValue(key, out var old) && (old.Fee is not null || fill.Fee is null)) return;
            _fills[key] = fill;
            _journal.Save(_accountKey, fill);
        }
        Publish(new ExecutionReceived(fill));
    }

    /// <summary>Updates durable and in-memory intent state together before returning to a caller.</summary>
    private void Save(TradingOperation operation)
    {
        lock (_sync)
        {
            if (_operations.TryGetValue(operation.Id, out var old) && old.Phase == TradingOperationPhase.Resolved && operation.Phase != TradingOperationPhase.Resolved) return;
            _journal.Save(operation); _operations[operation.Id] = operation;
        }
    }

    /// <summary>Retains uncertainty in RAM even if storage fails after dispatch; the earlier durable Dispatching remains recoverable.</summary>
    private void RecordFailure(TradingOperation operation, bool rejected)
    {
        var updated = operation with { Phase = rejected ? TradingOperationPhase.Resolved : TradingOperationPhase.Unknown };
        try { Save(updated); }
        catch (Exception)
        {
            lock (_sync)
            {
                if (!_operations.TryGetValue(updated.Id, out var old) || old.Phase != TradingOperationPhase.Resolved) _operations[updated.Id] = updated;
                _balanceDirty = true;
            }
        }
    }

    /// <summary>Invalidates placement readiness on any stream/REST gap until reconciliation succeeds.</summary>
    private void MarkDisconnected(Exception? ex)
    {
        Interlocked.Increment(ref _resyncGeneration);
        foreach (var key in _tracked.Keys) _ready[key] = false;
        lock (_sync) { _connection = TradingConnectionState.Reconnecting; _balanceDirty = true; _message = ex is null ? "Balance synchronization required" : Error(ex).Message; }
        Publish(new TradingConnectionChanged(TradingConnectionState.Reconnecting, DateTimeOffset.UtcNow, true, ex is null ? null : Error(ex)));
    }

    /// <summary>Broadcasts detached events and marks an overflowed subscriber as requiring a full resync.</summary>
    private void Publish(TradingUpdate update)
    {
        foreach (var channel in _subscribers.Values)
            if (!channel.Writer.TryWrite(update))
            {
                while (channel.Reader.TryRead(out _)) { }
                channel.Writer.TryWrite(new TradingConnectionChanged(TradingConnectionState.Reconnecting, DateTimeOffset.UtcNow, true));
            }
    }

    /// <summary>Identifies only states whose exchange lifecycle has ended.</summary>
    internal static bool Terminal(OrderState state) => state is OrderState.Filled or OrderState.Canceled or OrderState.Expired or OrderState.Rejected;

    /// <summary>Prepared intents have not crossed the network boundary and must never be replayed.</summary>
    private static bool Pending(TradingOperation operation) => operation.Phase is TradingOperationPhase.Dispatching or TradingOperationPhase.Unknown;

    /// <summary>Only explicit business rejections prove that placement failed; timeouts and duplicates remain uncertain.</summary>
    private static bool DefiniteRejection(Exception ex) => ex is ccxt.InsufficientFunds or ccxt.PermissionDenied or ccxt.AuthenticationError ||
        ex is ccxt.InvalidOrder && ex is not ccxt.OrderNotFound && !ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);

    /// <summary>Produces stable user-facing errors without provider payloads or signed URLs.</summary>
    private static TradingError Error(Exception ex) => ex is TradingServiceException service ? service.Error : ex switch
    {
        ccxt.InsufficientFunds => new("InsufficientFunds", "The exchange rejected the order: insufficient funds."),
        ccxt.InvalidOrder => new("InvalidOrder", "The exchange rejected the order parameters or Post-only condition."),
        OperationCanceledException or ccxt.NetworkError => new("ConnectionLost", "Connection interrupted. Any dispatched operation will be reconciled, not resent."),
        NotSupportedException => new("Unsupported", "This operation or spot instrument is unavailable."),
        _ => new("ExchangeFailure", AccountConnectionVerifier.SafeError(ex))
    };

    /// <summary>Normalizes provider read failures while preserving caller cancellation.</summary>
    private static Exception Safe(Exception ex) => ex is OperationCanceledException or NotSupportedException ? ex : new TradingServiceException(Error(ex));

    /// <summary>Closes transports and workers without issuing exchange cancellation requests.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync) return new(_disposeTask ??= DisposeCoreAsync());
    }

    /// <summary>Stops stream waits, drains the current bounded request and retains uncertain journal entries.</summary>
    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        await _stop.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _client.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
        await _worker.ConfigureAwait(false);
        await Task.WhenAll(_streams).ConfigureAwait(false);
        foreach (var channel in _subscribers.Values) channel.Writer.TryComplete();
        lock (_sync) { _connection = TradingConnectionState.Disconnected; _message = "Disconnected"; }
    }
}
```

<a id="file-10"></a>

## 10. BotView/Services/Integrations/Ccxt/OrderBookService.cs

Полная версия из `a3d8986`. SHA-256 исходного Git blob: `8c50015e41c640f597c85dec47efc62d85f6e5367124a09b8b4e1eff72f8ee7f`.

```csharp
using System.Collections;
using System.Globalization;
using BotView.Configuration;
using BotView.Services;
using BotView.Models;

namespace BotView.Services.Integrations.Ccxt;

public sealed class OrderBookService : IOrderBookService
{
    private readonly Func<string, IOrderBookClient> _create;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public OrderBookService() : this(id => new ProOrderBookClient(id), Task.Delay) { }

    internal OrderBookService(Func<string, IOrderBookClient> create,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _create = create;
        _delay = delay ?? Task.Delay;
    }

    public Task<IOrderBookSubscription> SubscribeAsync(string exchange, string symbol, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var id = MarketCatalog.GetExchange(exchange).Id;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IOrderBookSubscription>(new Subscription(id, symbol, _create, _delay, ct));
    }

    /// <summary>Creates public depth in the account's network, independently of the chart connection.</summary>
    public Task<IOrderBookSubscription> SubscribeAsync(string exchange, string symbol, TradingEnvironment environment, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(environment)) throw new ArgumentOutOfRangeException(nameof(environment));
        if (environment == TradingEnvironment.Production) return SubscribeAsync(exchange, symbol, ct);
        var id = MarketCatalog.GetExchange(exchange).Id;
        if (id is not ("binance" or "bybit")) throw new NotSupportedException("Testnet depth supports Binance and Bybit only.");
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IOrderBookSubscription>(new Subscription(id, symbol, value => new ProOrderBookClient(value, environment), _delay, ct));
    }

    /// <summary>Creates depth for the exact account market and network.</summary>
    public Task<IOrderBookSubscription> SubscribeAsync(string exchange, string symbol, TradingEnvironment environment,
        TradingMarket market, CancellationToken ct = default)
    {
        if (market == TradingMarket.Spot)
        {
            return SubscribeAsync(exchange, symbol, environment, ct);
        }

        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IOrderBookSubscription>(new Subscription(exchange, symbol,
            id => new ProOrderBookClient(id, environment, market), _delay, ct));
    }

    private sealed class Subscription : IOrderBookSubscription
    {
        private readonly CancellationTokenSource _stop;
        private readonly Task _worker;
        private readonly object _disposeSync = new();
        private Task? _disposeTask;
        private OrderBookUpdate _current = new(null, OrderBookConnection.Connecting, "Connecting…");
        public OrderBookUpdate Current => Volatile.Read(ref _current);

        public Subscription(string exchange, string symbol, Func<string, IOrderBookClient> create,
            Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _worker = Task.Run(() => RunAsync(exchange, symbol, create, delay, _stop.Token));
        }

        private void Publish(OrderBookConnection connection, string message, OrderBookSnapshot? snapshot = null) =>
            Volatile.Write(ref _current, new(snapshot ?? Current.Snapshot, connection, message));

        private async Task RunAsync(string exchange, string symbol, Func<string, IOrderBookClient> create,
            Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
        {
            var retry = 1;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    IOrderBookClient? client = null;
                    var fatal = false;
                    try
                    {
                        client = create(exchange);
                        var market = await client.LoadMarketAsync(symbol, ct).ConfigureAwait(false);
                        while (!ct.IsCancellationRequested)
                        {
                            var snapshot = await client.WatchAsync(market, ct).ConfigureAwait(false);
                            ct.ThrowIfCancellationRequested();
                            Publish(OrderBookConnection.Live, "Connected", snapshot);
                            retry = 1;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex) when (ex is NotSupportedException or ccxt.BadRequest or ccxt.NotSupported)
                    {
                        Publish(OrderBookConnection.Error, ex.Message);
                        fatal = true;
                    }
                    catch (Exception ex)
                    {
                        Publish(OrderBookConnection.Reconnecting, $"Data is stale. Reconnecting: {ex.Message}");
                    }
                    finally
                    {
                        if (client is not null)
                        {
                            try { await client.DisposeAsync().ConfigureAwait(false); }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Order book close: {ex.Message}"); }
                        }
                    }
                    if (fatal) return;
                    await delay(TimeSpan.FromSeconds(retry), ct).ConfigureAwait(false);
                    retry = Math.Min(30, retry * 2);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally
            {
                if (ct.IsCancellationRequested) Publish(OrderBookConnection.Closed, "Disconnected");
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_disposeSync) return new(_disposeTask ??= StopAsync());
        }

        private async Task StopAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await _worker.ConfigureAwait(false);
            _stop.Dispose();
        }
    }
}

internal interface IOrderBookClient : IAsyncDisposable
{
    Task<OrderBookMarket> LoadMarketAsync(string symbol, CancellationToken ct);
    Task<OrderBookSnapshot> WatchAsync(OrderBookMarket market, CancellationToken ct);
}

internal sealed class ProOrderBookClient : IOrderBookClient
{
    private readonly ccxt.Exchange _client;
    private readonly int _subscriptionDepth;
    private readonly TradingMarket _marketType;

    public ProOrderBookClient(string id, TradingEnvironment environment = TradingEnvironment.Production, TradingMarket marketType = TradingMarket.Spot)
    {
        _marketType = marketType;
        var settings = CreateSettings();
        // Bybit spot streams accept 1/50/200/1000, so receive 200 and retain the nearest 100.
        _subscriptionDepth = id == "bybit" ? 200 : 100;
        if (marketType == TradingMarket.UsdtFutures)
        {
            var options = (Dictionary<string, object>)settings["options"];
            options["defaultType"] = id == "binance" ? "future" : "swap";
            options["defaultSubType"] = "linear";
            options["fetchMarkets"] = new Dictionary<string, object> { ["types"] = new List<object> { "linear" } };
        }

        _client = id switch
        {
            "binance" when marketType == TradingMarket.UsdtFutures => new ccxt.pro.binanceusdm(settings),
            "binance" => new ccxt.pro.binance(settings),
            "bybit" => new ccxt.pro.bybit(settings),
            "okx" => new ccxt.pro.okx(settings),
            "kraken" => new ccxt.pro.kraken(settings),
            _ => throw new NotSupportedException($"Order books for exchange {id} are not supported.")
        };
        if (environment == TradingEnvironment.Testnet) _client.setSandboxMode(true);
    }

    internal static Dictionary<string, object> CreateSettings() =>
        new()
        {
            ["enableRateLimit"] = true,
            ["timeout"] = 15000,
            ["options"] = new Dictionary<string, object>
            {
                ["defaultType"] = "spot",
                ["fetchCurrencies"] = false,
                ["maxRetriesOnFailure"] = 0,
                ["fetchMarkets"] = new Dictionary<string, object> { ["types"] = new List<object> { "spot" } }
            }
        };

    public async Task<OrderBookMarket> LoadMarketAsync(string symbol, CancellationToken ct)
    {
        if (!_client.has.TryGetValue("watchOrderBook", out var supported) || supported is not true)
            throw new NotSupportedException("The exchange does not support streaming order books.");
        var markets = await AwaitAsync(_client.LoadMarkets(), ct).ConfigureAwait(false);
        if (!markets.TryGetValue(symbol, out var market) || (_marketType == TradingMarket.Spot ? market.spot != true : market.linear != true || market.contract != true || market.settle != "USDT") || market.active == false)
            throw new NotSupportedException($"Spot pair {symbol} is unavailable on this exchange.");
        var precision = market.precision?.price;
        var tick = GetTickSize(precision, Convert.ToInt32(_client.precisionMode, CultureInfo.InvariantCulture));
        var instrument = CcxtMarketDataProvider.MapInstrument(_client.id, market, Convert.ToInt32(_client.precisionMode));
        if (_marketType == TradingMarket.UsdtFutures)
        {
            instrument = instrument with
            {
                TradingRules = instrument.TradingRules! with { QuantityUnit = QuantityUnit.Contracts, ContractSize = SpotOrderMath.Number(market.contractSize), SettlementCurrency = "USDT" }
            };
        }

        return new(symbol, market.baseCurrency!, market.quote!, tick, instrument);
    }

    internal static decimal GetTickSize(double? precision, int precisionMode)
    {
        if (precision is null || !double.IsFinite(precision.Value))
            throw new NotSupportedException("The exchange did not provide a price step.");
        // CCXT: DECIMAL_PLACES = 2, TICK_SIZE = 4. Significant digits do not define a fixed ladder tick.
        decimal tick = precisionMode switch
        {
            4 => Convert.ToDecimal(precision.Value, CultureInfo.InvariantCulture),
            2 when precision.Value == Math.Truncate(precision.Value) && precision.Value is >= -28 and <= 28 =>
                DecimalPower(-(int)precision.Value),
            _ => throw new NotSupportedException("The market precision mode does not define a constant price step.")
        };
        return tick > 0 ? tick : throw new NotSupportedException("Invalid exchange price step.");
    }

    private static decimal DecimalPower(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < Math.Abs(exponent); i++) result = exponent < 0 ? result / 10m : result * 10m;
        return result;
    }

    public async Task<OrderBookSnapshot> WatchAsync(OrderBookMarket market, CancellationToken ct)
    {
        var book = await AwaitAsync(_client.WatchOrderBook(market.Symbol, _subscriptionDepth), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // Copy both sides before awaiting again: CCXT owns and updates the live cache.
        var bids = CopyLevels(book.bids.Copy(), descending: true);
        var asks = CopyLevels(book.asks.Copy(), descending: false);
        return new(market, bids, asks, DateTimeOffset.UtcNow);
    }

    internal static IReadOnlyList<OrderBookLevel> CopyLevels(IEnumerable levels, bool descending)
    {
        var copy = new List<OrderBookLevel>();
        foreach (var entry in levels)
        {
            if (entry is not IList row || row.Count < 2) continue;
            var price = Convert.ToDouble(row[0], CultureInfo.InvariantCulture);
            var amount = Convert.ToDouble(row[1], CultureInfo.InvariantCulture);
            if (!double.IsFinite(price) || !double.IsFinite(amount) || price <= 0 || amount <= 0) continue;
            copy.Add(new(Convert.ToDecimal(price, CultureInfo.InvariantCulture), Convert.ToDecimal(amount, CultureInfo.InvariantCulture)));
        }
        var ordered = descending ? copy.OrderByDescending(l => l.Price) : copy.OrderBy(l => l.Price);
        return Array.AsReadOnly(ordered.Take(100).ToArray());
    }

    private static async Task<T> AwaitAsync<T>(Task<T> task, CancellationToken ct)
    {
        try { return await task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    public async ValueTask DisposeAsync() => await _client.Close().ConfigureAwait(false);
}
```

<a id="file-11"></a>

## 11. BotView/Services/Trading/ITradingService.cs

Полная версия из `a3d8986`. SHA-256 исходного Git blob: `aa375798797bdef9d2b555a216e1068315a17efffd034adffde58671b5682678`.

```csharp
using BotView.Models;

namespace BotView.Services.Trading;

/// <summary>Account-bound contract for CCXT and broker/QUIK adapters, using application models only.
/// Dispose releases the connection without canceling live orders. Read failures throw TradingServiceException; only confirmed
/// absence returns null. Unsupported capabilities throw NotSupportedException. Mutation timeouts after
/// dispatch return Unknown and require reconciliation, not automatic resubmission.</summary>
public interface ITradingService : IAsyncDisposable
{
    TradingAccountContext Account { get; }

    /// <summary>Loads trading metadata including units, steps, limits and monetary valuation parameters.</summary>
    Task<Instrument> GetTradingInstrumentAsync(InstrumentKey instrument, CancellationToken ct = default);

    /// <summary>Returns options actually supported for this listing and the bound account.</summary>
    Task<TradingCapabilities> GetCapabilitiesAsync(InstrumentKey instrument, CancellationToken ct = default);

    /// <summary>Checks instrument/provider/account constraints without sending an order.
    /// PlaceOrderAsync must check again because balances and limits can change.</summary>
    Task<OrderValidationResult> ValidateOrderAsync(PlaceOrderRequest request, CancellationToken ct = default);

    /// <summary>Sends with an ID recorded before dispatch. Accepted does not mean filled.
    /// Cancellation before dispatch may throw; after dispatch preserve/reconcile the unknown outcome.</summary>
    Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default);

    /// <summary>Cancels by listing and exchange ID, preserving partial fills and uncertain outcomes.</summary>
    Task<CancelOrderResult> CancelOrderAsync(OrderReference order, CancellationToken ct = default);

    /// <summary>Returns latest state, filled quantity and remaining quantity; null only confirms absence.</summary>
    Task<OrderSnapshot?> GetOrderAsync(OrderReference order, CancellationToken ct = default);

    /// <summary>Reconciles a lost placement response where client-ID lookup is supported.
    /// Unsupported lookup or a failed request must not be represented as an absent order.</summary>
    Task<OrderSnapshot?> FindOrderByClientIdAsync(InstrumentKey instrument, string clientOrderId, CancellationToken ct = default);

    /// <summary>Returns a bounded page of open orders with continuation instead of silent truncation.</summary>
    Task<TradingPage<OrderSnapshot>> GetOpenOrdersAsync(TradingQuery query, CancellationToken ct = default);

    /// <summary>Returns history with filters and provider-owned continuation where supported.</summary>
    Task<TradingPage<OrderSnapshot>> GetOrderHistoryAsync(TradingQuery query, CancellationToken ct = default);

    /// <summary>Returns actual fills and fees; deduplicate by account, listing and execution identity.</summary>
    Task<TradingPage<ExecutionSnapshot>> GetExecutionsAsync(TradingQuery query, CancellationToken ct = default);

    /// <summary>Returns funds, reservations, positions and margin. Unknown values remain null.</summary>
    Task<AccountSnapshot> GetAccountStateAsync(CancellationToken ct = default);

    /// <summary>Returns cash holdings or futures positions, including direction and quantity unit.</summary>
    Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(InstrumentKey? instrument = null, CancellationToken ct = default);

    /// <summary>Streams order, execution, account and connection updates to each subscriber.
    /// Refresh snapshots/reconcile history after reconnect; the stream is not durable history.</summary>
    IAsyncEnumerable<TradingUpdate> WatchUpdatesAsync(CancellationToken ct = default);
}
```

<a id="file-12"></a>

## 12. BotView/Services/Trading/SpotTradingClient.cs

Полная версия из `a3d8986`. SHA-256 исходного Git blob: `969230644b1aede354f3fefa352047c700c9aa7678126dc5d17bd2721001a0a9`.

```csharp
using System.Globalization;
using BotView.Models;

namespace BotView.Services.Trading;

/// <summary>Small exchange boundary used by the account service and deterministic tests.</summary>
internal interface ISpotTradingClient : IAsyncDisposable
{
    Task<(string Identity, bool? CanTrade)> ConnectAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> LoadTradingPairsAsync(string spotSymbol, CancellationToken ct = default);
    Task<SpotMarket> MarketAsync(InstrumentKey key, CancellationToken ct);
    decimal NormalizeAmount(string symbol, decimal amount);
    decimal NormalizePrice(string symbol, decimal price);
    Task<OrderSnapshot> PlaceAsync(PlaceOrderRequest request, CancellationToken ct);
    Task<OrderSnapshot?> CancelAsync(OrderReference order, CancellationToken ct);
    Task<OrderSnapshot?> OrderAsync(OrderReference order, CancellationToken ct);
    Task<OrderSnapshot?> FindAsync(InstrumentKey key, string clientId, CancellationToken ct);
    Task<TradingPage<OrderSnapshot>> OrdersAsync(TradingQuery query, bool open, CancellationToken ct);
    Task<TradingPage<ExecutionSnapshot>> ExecutionsAsync(TradingQuery query, CancellationToken ct);
    Task<AccountSnapshot> BalanceAsync(TradingAccountContext account, CancellationToken ct);
    Task<IReadOnlyList<PositionSnapshot>> PositionsAsync(InstrumentKey? key, CancellationToken ct) => throw new NotSupportedException();
    bool IsFutures => false;
    Task<IReadOnlyList<TradingError>> ValidateFuturesAsync(PlaceOrderRequest request, SpotMarket market, AccountSnapshot balance, CancellationToken ct) => throw new NotSupportedException();
    Task<IReadOnlyList<TradingError>> ValidateMarketAsync(PlaceOrderRequest request, CancellationToken ct) => Task.FromResult<IReadOnlyList<TradingError>>([]);
    bool SupportsStream(string kind);
    Task<IReadOnlyList<TradingUpdate>> WatchAsync(string kind, InstrumentKey key, TradingAccountContext account, CancellationToken ct);
}

internal sealed record SpotMarket(Instrument Instrument, string Base, string Quote, decimal? FeeRate, string? FeeSide = null);

/// <summary>Decimal arithmetic and order-entry conversion shared by the UI and validation.</summary>
internal static class SpotOrderMath
{
    /// <summary>Converts a quote budget into base units, rounding down through the exchange precision function.</summary>
    internal static decimal Quantity(decimal input, decimal price, OrderQuantityCurrency unit, Func<decimal, decimal> normalize)
    {
        if (input <= 0 || price <= 0) throw new ArgumentOutOfRangeException(nameof(input));
        return unit == OrderQuantityCurrency.Base ? input : normalize(input / price);
    }

    /// <summary>Parses detached exchange numbers without treating missing values as zero.</summary>
    internal static decimal? Number(double? number) => number.HasValue && double.IsFinite(number.Value)
        ? decimal.Parse(number.Value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : null;
}
```

<a id="file-13"></a>

## 13. BotView/Services/Trading/TradingJournal.cs

Полная версия из `a3d8986`. SHA-256 исходного Git blob: `7c1ed0f4bc51509e6b913abe6d92075b2c2189d63ee1dfce07d49cdd93601a2d`.

```csharp
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using BotView.Models;

namespace BotView.Services.Trading;

/// <summary>Crash recovery journal. Only application models are serialized; keys and raw API data never enter this database.</summary>
public sealed class TradingJournal
{
    private readonly string _path;
    private readonly object _sync = new();

    /// <summary>Creates a private journal independent of market-data databases.</summary>
    public TradingJournal(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotView", "trading-journal.db");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS journal (account TEXT NOT NULL, kind TEXT NOT NULL, id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(account,kind,id));";
        command.ExecuteNonQuery();
    }

    /// <summary>Commits an operation before the network boundary; every write is its own durable transaction.</summary>
    public void Save(TradingOperation operation) => Write(operation.AccountKey, "intent", operation.Id, operation);

    /// <summary>Persists authoritative order state, including partially filled terminal orders.</summary>
    internal void Save(string account, OrderSnapshot order) => Write(account, "order", Identity(order.Reference), order);

    /// <summary>Upserts executions by exchange identity so repeated stream/history events cannot duplicate a fill.</summary>
    internal void Save(string account, ExecutionSnapshot execution) => Write(account, "fill", Identity(execution.Order) + ":" + execution.ExecutionId, execution);

    /// <summary>Loads account-scoped durable intents after account identity verification.</summary>
    public IReadOnlyList<TradingOperation> Operations(string account) => Read<TradingOperation>(account, "intent");

    /// <summary>Restores order state without claiming that a saved snapshot is current.</summary>
    internal IReadOnlyList<OrderSnapshot> Orders(string account) => Read<OrderSnapshot>(account, "order");

    /// <summary>Restores execution identities for deduplication and history watermarks.</summary>
    internal IReadOnlyList<ExecutionSnapshot> Executions(string account) => Read<ExecutionSnapshot>(account, "fill");

    /// <summary>Scopes order identities by the complete instrument listing.</summary>
    private static string Identity(OrderReference reference) => JsonSerializer.Serialize(reference);

    /// <summary>Uses parameterized writes and never interpolates application data into SQL.</summary>
    private void Write<T>(string account, string kind, string id, T value)
    {
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO journal(account,kind,id,payload) VALUES($account,$kind,$id,$payload) ON CONFLICT(account,kind,id) DO UPDATE SET payload=excluded.payload";
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Reads only the selected verified account and record category.</summary>
    private IReadOnlyList<T> Read<T>(string account, string kind)
    {
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT payload FROM journal WHERE account=$account AND kind=$kind";
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$kind", kind);
            using var reader = command.ExecuteReader();
            var result = new List<T>();
            while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))!);
            return result;
        }
    }

    /// <summary>Opens a short-lived connection with durable synchronous commits.</summary>
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return db;
    }
}
```

<a id="file-14"></a>

## 14. BotView/Services/Trading/TradingOrderValidator.cs

Полная версия из `a3d8986`. SHA-256 исходного Git blob: `4572cce54fd634156aa3f56c74d7ad786edb4fe9bea00e78f1273c3dde84502a`.

```csharp
using BotView.Models;

namespace BotView.Services.Trading;

/// <summary>Common pure validation. Adapters also check provider precision, rights, sessions,
/// balances/margin and provider-specific rules immediately before dispatch.</summary>
public static class TradingOrderValidator
{
    /// <summary>Checks a request in the instrument's native order units without silently rounding it.</summary>
    /// <param name="request">Order to validate. Quantity uses the instrument's declared unit; limit and trigger prices stay separate.</param>
    /// <param name="instrument">Listing whose venue, lot size, price step, and trading rules are checked.</param>
    /// <param name="capabilities">Order types, durations, and flags this listing and account actually support.</param>
    /// <param name="account">Bound connection. Its venue must match the instrument.</param>
    /// <param name="now">Current time. A GTD order must expire after this instant.</param>
    public static OrderValidationResult Validate(PlaceOrderRequest request, Instrument instrument,
        TradingCapabilities capabilities, TradingAccountContext account, DateTimeOffset now)
    {
        var errors = new List<TradingError>();
        // Adds safe application errors, without provider request bodies or credentials.
        void Error(string code, string message) => errors.Add(new(code, message));

        if (request.Instrument != instrument.Key || !string.Equals(instrument.Key.Venue, account.Venue, StringComparison.OrdinalIgnoreCase))
            Error("InstrumentMismatch", "The instrument does not belong to this request/account venue.");
        if (string.IsNullOrWhiteSpace(request.ClientOrderId)) Error("ClientOrderIdRequired", "A client order ID is required before dispatch.");
        if (!Enum.IsDefined(request.Side)) Error("InvalidSide", "Invalid order side.");
        if (!Enum.IsDefined(request.Type) || !capabilities.OrderTypes.Contains(request.Type)) Error("UnsupportedOrderType", "The order type is not supported.");
        if (!Enum.IsDefined(request.TimeInForce) || !capabilities.TimeInForces.Contains(request.TimeInForce)) Error("UnsupportedTimeInForce", "The order duration is not supported.");
        if (request.PostOnly && (!capabilities.SupportsPostOnly || request.Type is not (OrderType.Limit or OrderType.StopLimit) ||
            request.TimeInForce is OrderTimeInForce.Ioc or OrderTimeInForce.Fok))
            Error("InvalidPostOnly", "Post-only requires a supported limit order and compatible duration.");
        if (request.ReduceOnly && !capabilities.SupportsReduceOnly) Error("UnsupportedReduceOnly", "Reduce-only is not supported.");
        if (request.PositionSide.HasValue && (!Enum.IsDefined(request.PositionSide.Value) || !capabilities.SupportsPositionSide))
            Error("UnsupportedPositionSide", "Explicit position side is not supported.");
        if (request.TimeInForce == OrderTimeInForce.Gtd ? request.ExpiresAt is null || request.ExpiresAt <= now : request.ExpiresAt is not null)
            Error("InvalidExpiration", "An expiration in the future is required only for GTD orders.");

        var rules = instrument.TradingRules;
        if (rules is null) { Error("TradingMetadataRequired", "Trading rules must be loaded before placing an order."); return new(errors); }
        if (!Enum.IsDefined(rules.QuantityUnit) || !Enum.IsDefined(rules.PriceQuotation) ||
            rules.QuantityUnit == QuantityUnit.Lots && instrument.LotSize <= 0)
            Error("InvalidTradingMetadata", "Instrument units and lot size must be valid.");
        if (request.Quantity <= 0) Error("InvalidQuantity", "Quantity must be positive.");
        // MOEX lots/contracts are positive integers; their fixed minimum and step need no per-instrument fields.
        var moexLotsOrContracts = string.Equals(instrument.Key.Venue, "MOEX", StringComparison.OrdinalIgnoreCase)
            && rules.QuantityUnit is QuantityUnit.Lots or QuantityUnit.Contracts;
        if (moexLotsOrContracts)
        {
            if (request.Quantity != decimal.Truncate(request.Quantity))
                Error("WholeQuantityRequired", "MOEX lot and contract quantities must be whole positive numbers.");
        }
        else
        {
            if (rules.QuantityStep is > 0 && request.Quantity % rules.QuantityStep.Value != 0)
                Error("QuantityStep", "Quantity does not match the instrument step.");
            if (rules.MinQuantity.HasValue && request.Quantity < rules.MinQuantity)
                Error("MinQuantity", "Quantity is below the instrument minimum.");
        }
        if (rules.MaxQuantity.HasValue && request.Quantity > rules.MaxQuantity) Error("MaxQuantity", "Quantity exceeds the instrument maximum.");

        var hasLimit = request.Type is OrderType.Limit or OrderType.StopLimit;
        var hasTrigger = request.Type is OrderType.Stop or OrderType.StopLimit;
        if (hasLimit != request.Price.HasValue) Error("LimitPrice", "A limit price is required only for limit and stop-limit orders.");
        if (hasTrigger != request.TriggerPrice.HasValue || hasTrigger != request.TriggerCondition.HasValue ||
            request.TriggerCondition.HasValue && !Enum.IsDefined(request.TriggerCondition.Value))
            Error("TriggerPrice", "Conditional orders require a trigger price and direction.");
        // Checks both prices in quote units, independently of monetary valuation.
        void ValidatePrice(decimal? price)
        {
            if (!price.HasValue) return;
            if (!rules.AllowNonPositivePrices && price <= 0) Error("InvalidPrice", "Price must be positive for this instrument.");
            if (instrument.PriceStep is > 0 && price.Value % instrument.PriceStep.Value != 0)
                Error("PriceStep", "Price does not match the instrument step.");
            if (rules.MinPrice.HasValue && price < rules.MinPrice) Error("MinPrice", "Price is below the permitted minimum.");
            if (rules.MaxPrice.HasValue && price > rules.MaxPrice) Error("MaxPrice", "Price exceeds the permitted maximum.");
        }
        ValidatePrice(request.Price);
        ValidatePrice(request.TriggerPrice);
        if (hasLimit && request.Price.HasValue && request.Quantity > 0 && (rules.MinNotional.HasValue || rules.MaxNotional.HasValue))
        {
            var value = TradingValueCalculator.Calculate(instrument, request.Quantity, request.Price.Value);
            if (value is null) Error("ValuationUnavailable", "Monetary limits cannot be checked without valuation metadata.");
            else
            {
                if (rules.MinNotional.HasValue && Math.Abs(value.Amount) < rules.MinNotional) Error("MinNotional", "Order value is below the minimum.");
                if (rules.MaxNotional.HasValue && Math.Abs(value.Amount) > rules.MaxNotional) Error("MaxNotional", "Order value exceeds the maximum.");
            }
        }
        return new(errors);
    }
}
```

<a id="file-15"></a>

## 15. BotView/Services/Trading/TradingSessionManager.cs

Полная версия из `a3d8986`. SHA-256 исходного Git blob: `b9fe6bd277713c753d86d37934bdcf558d16017332697436b05af09fb8a4e1c1`.

```csharp
using BotView.Models;
using BotView.Services.Integrations.Ccxt;

namespace BotView.Services.Trading;

/// <summary>Application-owned sessions survive window closure. Only explicit account changes retire them.</summary>
public sealed class TradingSessionManager : IAsyncDisposable
{
    private readonly ITradingAccountStore _profiles;
    private readonly ICredentialStore _credentials;
    private readonly Func<TradingAccountProfile, AccountCredentials, ISpotTradingClient> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, CcxtTradingService> _sessions = new();
    private readonly HashSet<Guid> _invalidated = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lazy<TradingJournal> _journal;
    private int _revision;
    private bool _disposed;
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>Builds the application manager without reading credentials or connecting until requested.</summary>
    public TradingSessionManager(ITradingAccountStore profiles, ICredentialStore credentials)
        : this(profiles, credentials, (p, c) => new CcxtSpotTradingClient(p, c), new(() => new TradingJournal())) { }

    /// <summary>Injects a deterministic exchange boundary and journal for tests.</summary>
    internal TradingSessionManager(ITradingAccountStore profiles, ICredentialStore credentials,
        Func<TradingAccountProfile, AccountCredentials, ISpotTradingClient> factory, Lazy<TradingJournal> journal)
    {
        _profiles = profiles; _credentials = credentials; _factory = factory; _journal = journal;
        if (profiles is ITradingProfileNotifications changes) changes.Changed += ProfilesChanged;
        if (credentials is ICredentialNotifications keys) keys.CredentialsChanged += CredentialsChanged;
    }

    /// <summary>Returns profile metadata only; missing keys remain an explicit UI state.</summary>
    public TradingAccountsSettings Settings() => _profiles.Load();

    /// <summary>Authenticates on a background task, then reuses one session per verified owner/network.</summary>
    public Task<CcxtTradingService> GetAsync(Guid profileId, CancellationToken ct = default) => Task.Run(async () =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await RetireCoreAsync().ConfigureAwait(false);
            lock (_sync) if (_sessions.TryGetValue(profileId, out var existing)) return existing;
            var revision = Revision;
            var profile = _profiles.Load().Profiles.SingleOrDefault(p => p.Id == profileId)
                ?? throw new TradingServiceException(new("ProfileMissing", "The selected account no longer exists."));
            using var credentials = _credentials.Read(profileId)
                ?? throw new TradingServiceException(new("KeysRequired", "Enter and save this account's API keys first."));
            ISpotTradingClient? client = null;
            try
            {
                client = _factory(profile, credentials);
                var connected = await client.ConnectAsync(token).ConfigureAwait(false);
                if (revision != Revision) throw new TradingServiceException(new("AccountChanged", "Account settings changed; select the account again."));
                // A read-only profile must never acquire a session created using another profile's trading key.
                CcxtTradingService? shared;
                lock (_sync) shared = connected.CanTrade == false ? null : _sessions.Values.FirstOrDefault(s =>
                    s.Account.Venue == profile.Exchange && s.Account.Environment == profile.Environment && s.Account.Market == profile.Market && s.Account.AccountId == connected.Identity && s.KeyCanTrade == connected.CanTrade);
                if (shared is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    client = null;
                    lock (_sync) _sessions[profileId] = shared;
                    return shared;
                }
                var service = new CcxtTradingService(profile, client, _journal.Value, connected.Identity, connected.CanTrade);
                client = null;
                lock (_sync) _sessions[profileId] = service;
                return service;
            }
            catch (OperationCanceledException) { throw; }
            catch (TradingServiceException) { throw; }
            catch (Exception ex) { throw new TradingServiceException(new("ConnectionFailed", AccountConnectionVerifier.SafeError(ex))); }
            finally { if (client is not null) try { await client.DisposeAsync().ConfigureAwait(false); } catch (Exception) { } }
        }
        finally { _gate.Release(); }
    }, ct);

    /// <summary>Invalidates credentials immediately, before a window can send through a stale session.</summary>
    private void CredentialsChanged(Guid id)
    {
        lock (_sync)
        {
            _invalidated.Add(id);
            if (_sessions.TryGetValue(id, out var service)) service.Suspend();
        }
        Interlocked.Increment(ref _revision);
        _ = RetireAsync();
    }

    /// <summary>Profile removal retires its sessions; selecting an active profile leaves other sessions supervised.</summary>
    private void ProfilesChanged()
    {
        try
        {
            var ids = _profiles.Load().Profiles.Select(p => p.Id).ToHashSet();
            lock (_sync) foreach (var pair in _sessions.Where(p => !ids.Contains(p.Key))) { _invalidated.Add(pair.Key); pair.Value.Suspend(); }
        }
        catch (Exception) { }
        Interlocked.Increment(ref _revision);
        _ = RetireAsync();
    }

    /// <summary>Serializes asynchronous retirement after store notifications.</summary>
    private async Task RetireAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await RetireCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Disposes invalidated clients and removes all aliases to a shared retired session.</summary>
    private async Task RetireCoreAsync()
    {
        CcxtTradingService[] retired;
        lock (_sync)
        {
            retired = _sessions.Where(p => _invalidated.Contains(p.Key)).Select(p => p.Value).Distinct().ToArray();
            foreach (var id in _sessions.Where(p => retired.Contains(p.Value)).Select(p => p.Key).ToArray()) _sessions.Remove(id);
            _invalidated.Clear();
        }
        foreach (var session in retired) try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
    }

    /// <summary>Ends local supervision without canceling any exchange order.</summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_profiles is ITradingProfileNotifications changes) changes.Changed -= ProfilesChanged;
        if (_credentials is ICredentialNotifications keys) keys.CredentialsChanged -= CredentialsChanged;
        await _stop.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CcxtTradingService[] sessions;
            lock (_sync) { sessions = _sessions.Values.Distinct().ToArray(); _sessions.Clear(); }
            foreach (var session in sessions) try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
        }
        finally { _gate.Release(); }
    }
}
```

<a id="file-16"></a>

## 16. BotView/ViewModels/OrderBookTradingViewModel.cs

Полная версия из `a3d8986`. SHA-256 исходного Git blob: `0a65ed1a723018fbac8b4280a8aab2baa4eb12626110641d704c027450918aec`.

```csharp
﻿using System.Collections.ObjectModel;
using BotView.Models;
using BotView.Services;
using BotView.Services.Integrations.Ccxt;

namespace BotView.ViewModels;

public sealed partial class OrderBookViewModel
{
    private TradingSessionManager? _tradingManager;
    private CcxtTradingService? _trading;
    private TradingAccountProfile? _selectedAccount;
    private bool _tradeBusy;
    private bool _bindingAccount;
    private bool _postOnly;
    private int _accountGeneration;
    private int _managerRevision = -1;
    private OrderQuantityCurrency _quantityCurrency;
    private OrderSnapshot? _selectedOrder;
    private SpotTradingState? _lastTradingState;
    private Task _bindTask = Task.CompletedTask;

    public TradingEnvironment Environment { get; private set; }
    public string EnvironmentText => Environment == TradingEnvironment.Testnet ? "Testnet" : "MAINNET · live trading";
    public bool IsFutures => Market == TradingMarket.UsdtFutures;
    public InstrumentKey TradingInstrument => new(Exchange, IsFutures ? "futures" : "spot", "", Symbol);
    private bool _reduceOnly;
    private string _positionTarget = "Auto";
    public string[] PositionTargets { get; } = ["Auto", "Long", "Short"];
    public bool ReduceOnly { get => _reduceOnly; set { if (IsTradeEditable) { _reduceOnly = value; Changed(); } } }
    public string PositionTarget { get => _positionTarget; set { if (IsTradeEditable && PositionTargets.Contains(value)) { _positionTarget = value; Changed(); } } }
    private PositionSide? SelectedPositionSide => PositionTarget == "Long" ? PositionSide.Long : PositionTarget == "Short" ? PositionSide.Short : null;
    public string PositionsText => !IsFutures ? "" : string.Join(" · ", (_trading?.Current.Balance?.Positions ?? [])
        .Where(p => p.Instrument == TradingInstrument)
        .Select(p => $"{p.Side}: {p.Quantity} contracts · {p.Leverage}x · {p.MarginMode} · PnL {p.UnrealizedPnl?.Amount} USDT"));
    public ObservableCollection<TradingAccountProfile> Accounts { get; } = new();
    public IReadOnlyList<OrderSnapshot> Orders { get; private set; } = [];
    public IReadOnlyList<ExecutionSnapshot> Executions { get; private set; } = [];
    public OrderQuantityCurrency[] QuantityCurrencies { get; } = [OrderQuantityCurrency.Base, OrderQuantityCurrency.Quote];
    public RelayCommand CancelSelectedCommand { get; private set; } = null!;
    public RelayCommand CancelAllCommand { get; private set; } = null!;
    public RelayCommand ReconnectCommand { get; private set; } = null!;
    public Func<CancelBatch, Task<bool>>? ConfirmCancelAllAsync { get; set; }
    public bool IsTradeEditable => !_tradeBusy && !_bindingAccount;
    public string AccountStatus => _bindingAccount ? "Connecting account…" : _trading?.Current.Message ?? "Select an account with keys stored in memory.";
    public string AccountIdentity => _trading is null ? "" : _trading.Account.AccountId.StartsWith("key:", StringComparison.Ordinal)
        ? "Account verified by this API key" : _trading.Account.AccountId;
    public string PendingStatus => _lastTradingState is null ? "" : string.Join("\n", _lastTradingState.Pending
        .Where(o => o.Request.Instrument == TradingInstrument)
        .Select(o => $"Unknown {(o.CancelTarget is null ? "placement" : "cancellation")}: {o.Id}. Reconciling; no automatic resend."));
    public string BalanceText => IsFutures ? $"Available margin: {_trading?.Current.Balance?.Margins?.FirstOrDefault(m => m.Currency == "USDT")?.AvailableFunds?.ToString() ?? "?"} USDT" : _trading?.Current.Balance?.Balances is { } balances
        ? string.Join(" · ", balances.Where(b => b.Currency == Symbol.Split('/')[0] || b.Currency == Symbol.Split('/').Last().Split(':')[0])
            .Select(b => $"{b.Currency}: {b.Available?.ToString() ?? "?"} free")) : "Balance unavailable";

    public TradingAccountProfile? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (_tradeBusy || value?.Id == _selectedAccount?.Id) return;
            _selectedAccount = value;
            _bindTask = BindAccountAsync();
        }
    }

    public bool PostOnly { get => _postOnly; set { if (!IsTradeEditable) return; _postOnly = value; Changed(); } }
    public OrderQuantityCurrency QuantityCurrency
    {
        get => _quantityCurrency;
        set
        {
            if (!IsTradeEditable || value == _quantityCurrency || !Enum.IsDefined(value)) return;
            try
            {
                if (_trading is not null && TryPositive(PriceText, out var price) && TryPositive(AmountText, out var input))
                {
                    var quantity = _trading.ConvertQuantity(TradingInstrument, input, price, _quantityCurrency);
                    _amountText = Format(value == OrderQuantityCurrency.Base ? quantity : _trading.OrderNotional(TradingInstrument, quantity, price));
                }
                else _amountText = "";
            }
            catch (Exception) { _amountText = ""; }
            _quantityCurrency = value;
            Changed();
        }
    }

    public string QuantityHeader => IsFutures && QuantityCurrency == OrderQuantityCurrency.Base ? "Quantity (contracts)" : $"Quantity ({(QuantityCurrency == OrderQuantityCurrency.Base ? Symbol.Split('/')[0] : Symbol.Split('/').Last().Split(':')[0])})";
    public OrderSnapshot? SelectedOrder { get => _selectedOrder; set { _selectedOrder = value; Changed(); } }
    private bool TradingInputValid
    {
        get
        {
            try
            {
                if (_trading is null || !TryPositive(PriceText, out var price) || !TryPositive(AmountText, out var amount)) return false;
                var quantity = _trading.ConvertQuantity(TradingInstrument, amount, price, QuantityCurrency);
                return _trading.ValidateInput(new(TradingInstrument, OrderSide.Buy, OrderType.Limit, quantity, "bvpreview", price, PostOnly: PostOnly, ReduceOnly: ReduceOnly, PositionSide: SelectedPositionSide)).IsValid;
            }
            catch (Exception) { return false; }
        }
    }

    public string OrderPreview
    {
        get
        {
            if (_trading is null || !TryPositive(PriceText, out var price) || !TryPositive(AmountText, out var input)) return "";
            try
            {
                var amount = _trading.ConvertQuantity(TradingInstrument, input, price, QuantityCurrency);
                return $"{Format(amount)} {(IsFutures ? "contracts" : Symbol.Split('/')[0])} · {Format(_trading.OrderNotional(TradingInstrument, amount, price))} {Symbol.Split('/').Last().Split(':')[0]} (excluding commission)";
            }
            catch (Exception) { return "Invalid quantity or precision."; }
        }
    }

    /// <summary>Initializes trade controls without contacting any exchange or starting an order.</summary>
    private void InitializeTrading(TradingSessionManager? manager, TradingEnvironment environment)
    {
        _tradingManager = manager;
        Environment = environment;
        CancelSelectedCommand = new RelayCommand(CancelSelectedAsync, _ => !_tradeBusy && _trading is not null && SelectedOrder is { } order && !CcxtTradingService.Terminal(order.State));
        CancelAllCommand = new RelayCommand(CancelAllAsync, _ => !_tradeBusy && !_bindingAccount && _trading is not null);
        ReconnectCommand = new RelayCommand(_ => BindAccountAsync(), _ => !_tradeBusy && !_bindingAccount && SelectedAccount is not null);
        ReloadAccounts();
    }

    /// <summary>Updates available profiles on the UI timer and rebinds after keys/profile changes.</summary>
    private void ReloadAccounts()
    {
        if (_tradingManager is null) return;
        _managerRevision = _tradingManager.Revision;
        try
        {
            var settings = _tradingManager.Settings();
            var oldId = _selectedAccount?.Id;
            Accounts.Clear();
            foreach (var profile in settings.Profiles.Where(p => p.Exchange == Exchange && p.Environment == Environment && p.Market == Market)) Accounts.Add(profile);
            _selectedAccount = Accounts.FirstOrDefault(p => p.Id == oldId)
                ?? Accounts.FirstOrDefault(p => p.Id == settings.ActiveProfiles.GetValueOrDefault(Exchange));
            _bindTask = BindAccountAsync();
        }
        catch (Exception) { _tradeStatus = "Could not load trading account profiles."; }
    }

    /// <summary>Guards late authentication results and clears order input whenever account identity changes.</summary>
    private async Task BindAccountAsync()
    {
        var generation = ++_accountGeneration;
        var selected = _selectedAccount;
        _trading = null;
        _lastTradingState = null;
        Orders = []; Executions = []; SelectedOrder = null;
        _priceText = ""; _amountText = ""; _tradeStatus = "";
        _bindingAccount = selected is not null;
        Changed();
        if (selected is null || _tradingManager is null) { _bindingAccount = false; return; }
        try
        {
            var service = await _tradingManager.GetAsync(selected.Id, _stop.Token);
            if (_disposed || generation != _accountGeneration) return;
            // Keep the session attached even if the first snapshot fails; its worker continues recovery.
            _trading = service;
            await Task.Run(() => service.TrackAsync(TradingInstrument, _stop.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && generation == _accountGeneration) _tradeStatus = ex is TradingServiceException safe ? safe.Message : "Could not connect the trading account."; }
        finally
        {
            if (!_disposed && generation == _accountGeneration) { _bindingAccount = false; Changed(); }
        }
    }

    /// <summary>Applies the latest detached account state without accumulating Dispatcher callbacks.</summary>
    private void RefreshTrading()
    {
        if (!_tradeBusy && _tradingManager is not null && _managerRevision != _tradingManager.Revision) ReloadAccounts();
        if (_trading is null) return;
        _lastTradingState = _trading.Current;
        var selectedId = SelectedOrder?.Reference;
        var orders = _lastTradingState.Orders.Where(o => o.Reference.Instrument == TradingInstrument)
            .OrderBy(o => CcxtTradingService.Terminal(o.State)).ThenByDescending(o => o.UpdatedAt).ToArray();
        if (!Orders.SequenceEqual(orders)) Orders = orders;
        var fills = _lastTradingState.Executions.Where(f => f.Order.Instrument == TradingInstrument).Take(100).ToArray();
        if (!Executions.SequenceEqual(fills)) Executions = fills;
        _selectedOrder = Orders.FirstOrDefault(o => o.Reference == selectedId);
    }

    /// <summary>Freezes a request before awaiting, preventing edits or another Buy/Sell from changing the dispatched intent.</summary>
    internal async Task TradeAsync(OrderSide side)
    {
        if (!CanTrade || _trading is null) return;

        var service = _trading;
        var generation = _accountGeneration;
        _tradeBusy = true;

        Changed();

        try
        {
            if (!TryPositive(PriceText, out var price) || !TryPositive(AmountText, out var input)) return;

            var quantity = service.ConvertQuantity(TradingInstrument, input, price, QuantityCurrency);
            var request = new PlaceOrderRequest(TradingInstrument, side, OrderType.Limit, quantity,
                "bv" + Guid.NewGuid().ToString("N"), price, PostOnly: PostOnly, ReduceOnly: ReduceOnly, PositionSide: SelectedPositionSide);
            var result = await Task.Run(() => service.PlaceOrderAsync(request, _stop.Token));

            if (!_disposed && generation == _accountGeneration) {
                _tradeStatus = $@"{side}: {result.Status} {result.Order?.State} ·
                    {result.ExchangeOrderId ?? result.ClientOrderId} {result.Order?.StatusReason}" +
                    (result.Error is null ? "" : $" · {result.Error.Message}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) {
            if (!_disposed) {
                _tradeStatus = ex is TradingServiceException safe ?
                    safe.Message :
                    "Order could not be prepared. Check values and precision.";
            }
        }
        finally {
            _tradeBusy = false;
            if (!_disposed) {
                Changed();
            }
        }
    }

    /// <summary>Cancels exactly the selected account order, preserving a possible partial fill.</summary>
    private async Task CancelSelectedAsync(object? parameter)
    {
        if (_tradeBusy || _trading is null || SelectedOrder is not { } order) return;
        var service = _trading;
        _tradeBusy = true; Changed();
        try
        {
            var result = await Task.Run(() => service.CancelOrderAsync(order.Reference, _stop.Token));
            if (!_disposed) _tradeStatus = $"Cancel {order.Reference.ExchangeOrderId}: {result.Status} {result.Error?.Message}";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed) _tradeStatus = "Cancellation failed; check the account status."; }
        finally { _tradeBusy = false; if (!_disposed) Changed(); }
    }

    /// <summary>Confirms a frozen account/pair snapshot, including external orders, before starting batch cancellation.</summary>
    private async Task CancelAllAsync(object? parameter)
    {
        if (_tradeBusy || _trading is null || ConfirmCancelAllAsync is null) return;
        var service = _trading;
        _tradeBusy = true; Changed();
        try
        {
            var batch = await Task.Run(() => service.PrepareCancelAllAsync(TradingInstrument, _stop.Token));
            if (_disposed || !await ConfirmCancelAllAsync(batch)) return;
            var results = await Task.Run(() => service.CancelBatchAsync(batch, _stop.Token));
            if (!_disposed) _tradeStatus = string.Join("; ", results.Select(r => $"{r.Reference.ExchangeOrderId}: {r.Status} {r.Error?.Message}"));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed) _tradeStatus = "Could not complete batch cancellation. Check individual order states."; }
        finally { _tradeBusy = false; if (!_disposed) Changed(); }
    }
}
```

<a id="file-17"></a>

## 17. BotView/ViewModels/OrderBookViewModel.cs

Полная версия из `a3d8986`. SHA-256 исходного Git blob: `b0081e4fc61d894e756aea7971cc287934053ba93bb8f70fb873c580514153b0`.

```csharp
using System.ComponentModel;
using System.Globalization;
using BotView.Models;
using BotView.Services;
using BotView.Services.Integrations.Ccxt;

namespace BotView.ViewModels;

public sealed record OrderBookVolumeOption(OrderBookVolumeCurrency Currency, string Label);

public sealed partial class OrderBookViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IOrderBookService _service;
    private readonly CancellationTokenSource _stop = new();
    private IOrderBookSubscription? _subscription;
    private Task? _startTask;
    private Task? _disposeTask;
    private readonly object _disposeSync = new();
    private bool _disposed;
    private OrderBookUpdate? _update;
    private string _priceText = "";
    private string _amountText = "";
    private string _tradeStatus = "";
    private readonly string[] _maxVolumeTexts = ["", ""];
    private readonly decimal?[] _maxVolumes = new decimal?[2];
    private OrderBookVolumeCurrency _volumeCurrency;
    private int _multiplier = 1;
    private decimal? _center;
    private bool _followMarket = true;

    /// <summary>Creates a book fixed to one network; trading sessions are owned by the application manager.</summary>
    public OrderBookViewModel(string exchange, string symbol, IOrderBookService service,
        TradingSessionManager? trading = null, TradingEnvironment environment = TradingEnvironment.Production, TradingMarket market = TradingMarket.Spot)
    {
        Market = market;
        Exchange = exchange;
        Symbol = symbol;
        VolumeCurrencyOptions =
        [
            new(OrderBookVolumeCurrency.Base, IsFutures ? "Contracts" : $"Base ({symbol.Split('/')[0]})"),
            new(OrderBookVolumeCurrency.Quote, $"Quote ({symbol.Split('/').Last().Split(':')[0]})")
        ];
        _service = service;
        BuyCommand = new RelayCommand(_ => TradeAsync(OrderSide.Buy), _ => CanTrade);
        SellCommand = new RelayCommand(_ => TradeAsync(OrderSide.Sell), _ => CanTrade);
        CenterCommand = new RelayCommand(() => { _followMarket = true; _center = null; Changed(); });
        InitializeTrading(trading, environment);
    }

    public TradingMarket Market { get; }
    public string Exchange { get; }
    public string Symbol { get; }
    public string Title => $"Order book · {Symbol} · {Exchange} · {Market} · {EnvironmentText}";
    public string BaseHeader => IsFutures ? "Volume (contracts)" : $"Volume ({_update?.Snapshot?.Market.BaseCurrency ?? Symbol.Split('/')[0]})";
    public string QuoteHeader => $"Total ({_update?.Snapshot?.Market.QuoteCurrency ?? Symbol.Split('/').Last().Split(':')[0]})";
    public OrderBookVolumeOption[] VolumeCurrencyOptions { get; }
    public OrderBookVolumeCurrency VolumeCurrency
    {
        get => _volumeCurrency;
        set
        {
            if (value is not (OrderBookVolumeCurrency.Base or OrderBookVolumeCurrency.Quote) || value == _volumeCurrency) return;
            _volumeCurrency = value;
            Changed();
        }
    }
    public string MaxVolumeHeader => $"Volume for 100% ({(VolumeCurrency == OrderBookVolumeCurrency.Base
        ? IsFutures ? "contracts" : _update?.Snapshot?.Market.BaseCurrency ?? Symbol.Split('/')[0]
        : _update?.Snapshot?.Market.QuoteCurrency ?? Symbol.Split('/').Last().Split(':')[0])})";
    public string MaxVolumeText
    {
        get => _maxVolumeTexts[(int)VolumeCurrency];
        set
        {
            _maxVolumeTexts[(int)VolumeCurrency] = value;
            if (string.IsNullOrWhiteSpace(value)) _maxVolumes[(int)VolumeCurrency] = null;
            else if (TryPositive(value, out var volume)) _maxVolumes[(int)VolumeCurrency] = volume;
            Changed();
        }
    }
    public bool HasVolumeScaleError => !string.IsNullOrWhiteSpace(MaxVolumeText) && !TryPositive(MaxVolumeText, out _);
    public string VolumeScaleHint => HasVolumeScaleError
        ? "Enter a number greater than zero; the previous scale is retained."
        : string.IsNullOrWhiteSpace(MaxVolumeText) ? "Blank uses the maximum visible volume for automatic scaling."
        : "Volumes above the threshold fill the bar to 100%.";
    public int[] Multipliers { get; } = [1, 5, 10];
    public int Multiplier
    {
        get => _multiplier;
        set { if (Multipliers.Contains(value)) { _multiplier = value; Changed(); } }
    }
    public bool IsFollowing => _followMarket;
    public string FollowText => _followMarket ? "Following market" : "Manual scrolling";
    public string StatusText
    {
        get
        {
            if (_update is null) return "Connecting…";
            var snapshot = _update.Snapshot;
            var age = snapshot is null ? "" : $" · {snapshot.ReceivedAt.ToLocalTime():HH:mm:ss.fff}";
            bool stale = snapshot is not null && DateTimeOffset.UtcNow - snapshot.ReceivedAt > TimeSpan.FromSeconds(10);
            return _update.Message + (stale && _update.Connection == OrderBookConnection.Live ? " · No recent data" : "") + age;
        }
    }

    public string StepText => _update?.Snapshot is { } b ? $"Step: {Format(b.Market.TickSize * Multiplier)}" : "Step: —";
    public string PriceText { get => _priceText; set { _priceText = value; Changed(); } }
    public string AmountText { get => _amountText; set { _amountText = value; Changed(); } }
    public string TradeStatus => _tradeStatus;
    public bool CanTrade => !_disposed && !_tradeBusy && !_bindingAccount && _trading?.IsReady(TradingInstrument) == true
        && TradingInputValid && !_trading.Current.Pending.Any(o => o.Request.Instrument == TradingInstrument);
    public RelayCommand BuyCommand { get; }
    public RelayCommand SellCommand { get; }
    public RelayCommand CenterCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public Task StartAsync() => _disposed ? Task.CompletedTask : _startTask ??= StartCoreAsync();

    private async Task StartCoreAsync()
    {
        try
        {
            var subscription = IsFutures
                ? await _service.SubscribeAsync(Exchange, Symbol, Environment, Market, _stop.Token).ConfigureAwait(false)
                : Environment == TradingEnvironment.Production
                ? await _service.SubscribeAsync(Exchange, Symbol, _stop.Token).ConfigureAwait(false)
                : await _service.SubscribeAsync(Exchange, Symbol, Environment, _stop.Token).ConfigureAwait(false);
            _subscription = subscription;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { _update = new(null, OrderBookConnection.Error, ex.Message); }
    }

    /// <summary>Called only by the UI's 50 ms timer; no dispatcher queue is accumulated by the feed.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        if (_subscription is not null) _update = _subscription.Current;
        RefreshTrading();
        Changed();
    }

    public IReadOnlyList<OrderBookRow> GetRows(int count)
    {
        var book = _update?.Snapshot;
        if (book is null) return [];
        var marketCenter = book.BestBid is { } bid && book.BestAsk is { } ask ? (bid + ask) / 2
            : book.BestBid ?? book.BestAsk;
        if (marketCenter is null) return [];
        if (_followMarket || _center is null) _center = marketCenter;
        var rows = OrderBookLadder.Build(book, Multiplier, _center.Value, count, _maxVolumes[(int)VolumeCurrency], VolumeCurrency);
        var step = book.Market.TickSize * Multiplier;
        return rows.Select(row => row with { OwnOrderCount = Orders.Count(o => !CcxtTradingService.Terminal(o.State) && o.Price is { } price &&
            (o.Side == OrderSide.Buy ? decimal.Floor(price / step) : decimal.Ceiling(price / step)) * step == row.Price) }).ToArray();
    }

    public void Scroll(int levels)
    {
        if (_update?.Snapshot is not { } book) return;
        if (_center is null) GetRows(1);
        if (_center is null) return;
        _followMarket = false;
        _center = Math.Max(book.Market.TickSize, _center.Value + levels * book.Market.TickSize * Multiplier);
        Changed();
    }

    /// <summary>Copies the exact clicked price only while the order draft is editable.</summary>
    public void SelectPrice(decimal price) { if (IsTradeEditable) PriceText = Format(price); }

    internal static bool TryPositive(string text, out decimal value) =>
        decimal.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out value) && value > 0;

    internal static string Format(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    private void Changed()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        BuyCommand.RaiseCanExecuteChanged();
        SellCommand.RaiseCanExecuteChanged();
        CancelSelectedCommand?.RaiseCanExecuteChanged();
        CancelAllCommand?.RaiseCanExecuteChanged();
        ReconnectCommand?.RaiseCanExecuteChanged();
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        ++_accountGeneration;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_startTask is not null) await _startTask.ConfigureAwait(false);
        if (_subscription is not null) await _subscription.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
```

<a id="file-18"></a>

## 18. BotView/Windows/OrderBookWindow.xaml

Полная версия из `a3d8986`. SHA-256 исходного Git blob: `8fb04060a61b3b1bd9b2dd8b17105ccd0ca1b351ed805a54b6c38b52aa9857ba`.

```xml
﻿<Window x:Class="BotView.Windows.OrderBookWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:views="clr-namespace:BotView.Views"
        Title="{Binding Title}" Width="420" Height="820" MinWidth="340" MinHeight="480"
        Background="#0B1120" Foreground="#E5E7EB" FontFamily="Segoe UI"
        WindowStartupLocation="CenterOwner">
    <Window.Resources>
        <Style TargetType="Button">
            <Setter Property="Padding" Value="10,6"/>
            <Setter Property="Background" Value="#263449"/>
            <Setter Property="Foreground" Value="#F3F4F6"/>
            <Setter Property="BorderBrush" Value="#475569"/>
            <Setter Property="Cursor" Value="Hand"/>
        </Style>
        <Style TargetType="TextBox">
            <Setter Property="Padding" Value="6,5"/>
            <Setter Property="Background" Value="#182235"/>
            <Setter Property="Foreground" Value="#F3F4F6"/>
            <Setter Property="BorderBrush" Value="#475569"/>
        </Style>
    </Window.Resources>
    <Grid Margin="10">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*" MinHeight="80"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="1.1*" MinHeight="190"/>
        </Grid.RowDefinitions>
        <Grid Margin="0,0,0,8">
            <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
            <StackPanel>
                <TextBlock Text="{Binding Symbol}" FontSize="19" FontWeight="SemiBold"/>
                <StackPanel Orientation="Horizontal">
                    <TextBlock Text="{Binding Exchange}" Foreground="#94A3B8" FontSize="11"/>
                    <TextBlock Text="{Binding EnvironmentText}" Foreground="#FBBF24" FontWeight="Bold" FontSize="11" Margin="5,0,0,0"/>
                </StackPanel>
                <TextBlock Text="{Binding SelectedAccount.Name}" Foreground="#94A3B8" FontSize="11" TextTrimming="CharacterEllipsis">
                    <TextBlock.Style><Style TargetType="TextBlock"><Style.Triggers>
                        <DataTrigger Binding="{Binding SelectedAccount}" Value="{x:Null}"><Setter Property="Visibility" Value="Collapsed"/></DataTrigger>
                    </Style.Triggers></Style></TextBlock.Style>
                </TextBlock>
            </StackPanel>
            <Button Grid.Column="1" Content="Center on market" Command="{Binding CenterCommand}" VerticalAlignment="Center"/>
        </Grid>
        <Expander Grid.Row="1" Header="Depth display settings" Foreground="#94A3B8" Margin="0,0,0,5">
        <StackPanel>
        <Grid Margin="0,0,0,8">
            <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="65"/></Grid.ColumnDefinitions>
            
            <TextBlock Grid.Column="1" Text="{Binding StepText}" VerticalAlignment="Center" Margin="4,0,8,0" FontSize="11"/>
            <ComboBox Grid.Column="2" ItemsSource="{Binding Multipliers}" SelectedItem="{Binding Multiplier}" VerticalContentAlignment="Center"
                      ToolTip="Exchange price step multiplier">
                <ComboBox.ItemTemplate><DataTemplate><TextBlock Text="{Binding StringFormat=×{0}}"/></DataTemplate></ComboBox.ItemTemplate>
            </ComboBox>
        </Grid>
        <Grid Margin="0,0,0,8">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>
            <Grid.ColumnDefinitions>
                <ColumnDefinition />
                <ColumnDefinition Width="105"/>
            </Grid.ColumnDefinitions>
            <StackPanel Margin="0,0,8,0">
                <TextBlock Text="{Binding MaxVolumeHeader}" FontSize="10" Margin="0,0,0,2"/>
                <ComboBox x:Name="VolumeCurrencySelector" ItemsSource="{Binding VolumeCurrencyOptions}"
                          SelectedValuePath="Currency" DisplayMemberPath="Label" SelectedValue="{Binding VolumeCurrency}"
                          MinHeight="24" VerticalContentAlignment="Center" ToolTip="Currency used for volume bars and the fill threshold"/>
            </StackPanel>
            <TextBox x:Name="MaxVolumeInput" Grid.Column="1" Text="{Binding MaxVolumeText, UpdateSourceTrigger=PropertyChanged}"
                     VerticalAlignment="Center" ToolTip="Volume in the selected currency for a full-width bar. Leave blank for automatic scaling.">
                <TextBox.Style>
                    <Style TargetType="TextBox" BasedOn="{StaticResource {x:Type TextBox}}">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding HasVolumeScaleError}" Value="True">
                                <Setter Property="BorderBrush" Value="#F87171"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBox.Style>
            </TextBox>
            <TextBlock Grid.Row="1" Grid.ColumnSpan="2" Text="{Binding VolumeScaleHint}" FontSize="10" Foreground="#94A3B8"
                       TextWrapping="Wrap" Margin="0,3,0,0"/>
        </Grid>
        </StackPanel>
        </Expander>
        <Grid Grid.Row="2" Background="#182235" Height="26">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="34*"/>
                <ColumnDefinition Width="34*"/>
                <ColumnDefinition Width="32*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <TextBlock Text="{Binding BaseHeader}" FontSize="10" VerticalAlignment="Center" HorizontalAlignment="Right" Margin="4,0,6,0"/>
            <TextBlock Grid.Column="1" Text="Price" FontSize="10" VerticalAlignment="Center" HorizontalAlignment="Right" Margin="4,0,6,0"/>
            <TextBlock Grid.Column="2" Text="{Binding QuoteHeader}" FontSize="10" VerticalAlignment="Center" HorizontalAlignment="Right" Margin="4,0,6,0"/>
        </Grid>
        <views:OrderBookLadderControl x:Name="Ladder" Grid.Row="3" ToolTip="Scroll to browse prices. Click to select an order price. An em dash means depth or monetary valuation data is unavailable."/>
        <TextBlock Grid.Row="4" Text="{Binding StatusText}" Foreground="#94A3B8" FontSize="10" TextWrapping="Wrap" MaxHeight="44" Margin="0,6"/>
        <Grid Grid.Row="5">
        <Grid.RowDefinitions><RowDefinition/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
        <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
        <StackPanel>
        <ComboBox ItemsSource="{Binding Accounts}" SelectedItem="{Binding SelectedAccount}" DisplayMemberPath="DisplayName"
                  IsEnabled="{Binding IsTradeEditable}" ToolTip="Accounts for this exchange and network" Margin="0,0,0,4"/>
        <TextBlock Text="{Binding AccountStatus}" FontSize="10" TextWrapping="Wrap" Foreground="#94A3B8"/>
        <Button Content="Reconnect account" Command="{Binding ReconnectCommand}" HorizontalAlignment="Left" Padding="5,2" Margin="0,3"/>
        <TextBlock Text="{Binding AccountIdentity}" FontSize="10" Foreground="#94A3B8"/>
        <TextBlock Text="{Binding BalanceText}" FontSize="10" TextWrapping="Wrap" Margin="0,2,0,5"/>
        <Grid IsEnabled="{Binding IsTradeEditable}">
            <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
            <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="10"/><ColumnDefinition/></Grid.ColumnDefinitions>
            <TextBlock Text="Order price" Foreground="#94A3B8" FontSize="11"/>
            <TextBlock Grid.Column="2" Text="{Binding QuantityHeader}" Foreground="#94A3B8" FontSize="11"/>
            <TextBox x:Name="PriceInput" Grid.Row="1" Text="{Binding PriceText, UpdateSourceTrigger=PropertyChanged}" Margin="0,3,0,7"/>
            <TextBox Grid.Row="1" Grid.Column="2" Text="{Binding AmountText, UpdateSourceTrigger=PropertyChanged}" Margin="0,3,0,7"/>
            <CheckBox Grid.Row="2" Content="Post-only" IsChecked="{Binding PostOnly}" Foreground="#E5E7EB" ToolTip="Unchecked: ordinary GTC limit order"/>
            <ComboBox Grid.Row="2" Grid.Column="2" ItemsSource="{Binding QuantityCurrencies}" SelectedItem="{Binding QuantityCurrency}" ToolTip="Order quantity unit; independent of depth volume bars"/>
            <TextBlock Grid.Row="3" Grid.ColumnSpan="3" Text="{Binding OrderPreview}" FontSize="10" TextWrapping="Wrap" Margin="0,4"/>
        </Grid>
        <StackPanel IsEnabled="{Binding IsTradeEditable}" Margin="0,4">
            <StackPanel.Style>
                <Style TargetType="StackPanel">
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding IsFutures}" Value="False">
                            <Setter Property="Visibility" Value="Collapsed"/>
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </StackPanel.Style>
            <CheckBox Content="Reduce-only" IsChecked="{Binding ReduceOnly}" Foreground="#E5E7EB"/>
            <ComboBox ItemsSource="{Binding PositionTargets}" SelectedItem="{Binding PositionTarget}" ToolTip="Auto for one-way mode; Long or Short for hedge mode"/>
            <TextBlock Text="{Binding PositionsText}" TextWrapping="Wrap" FontSize="10"/>
        </StackPanel>
        <TextBlock Text="{Binding PendingStatus}" TextWrapping="Wrap" Foreground="#FBBF24" FontSize="11"/>
        <TextBlock Text="{Binding TradeStatus}" TextWrapping="Wrap" Foreground="#FBBF24" FontSize="11" Margin="0,4"/>
        <TextBlock Text="Account orders for this pair" FontSize="11" Margin="0,5"/>
        <DataGrid ItemsSource="{Binding Orders}" SelectedItem="{Binding SelectedOrder}" IsReadOnly="True" AutoGenerateColumns="False"
                  MaxHeight="150" MinHeight="55" Background="#182235" Foreground="#172033" SelectionMode="Single"
                  CanUserAddRows="False" HeadersVisibility="Column" HorizontalScrollBarVisibility="Auto">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Side" Binding="{Binding Side}"/>
                <DataGridTextColumn Header="Position" Binding="{Binding PositionSide}"/>
                <DataGridTextColumn Header="Reduce-only" Binding="{Binding ReduceOnly}"/>
                <DataGridTextColumn Header="Price" Binding="{Binding Price}"/>
                <DataGridTextColumn Header="Quantity" Binding="{Binding Quantity}"/>
                <DataGridTextColumn Header="Filled" Binding="{Binding FilledQuantity}"/>
                <DataGridTextColumn Header="Remaining" Binding="{Binding RemainingQuantity}"/>
                <DataGridTextColumn Header="State" Binding="{Binding State}"/>
                <DataGridTextColumn Header="Reason" Binding="{Binding StatusReason}"/>
                <DataGridTextColumn Header="Order ID" Binding="{Binding Reference.ExchangeOrderId}"/>
            </DataGrid.Columns>
        </DataGrid>
        <WrapPanel Margin="0,5">
            <Button Content="Cancel selected" Command="{Binding CancelSelectedCommand}" Margin="0,0,5,0"/>
            <Button Content="Cancel all for pair" Command="{Binding CancelAllCommand}"/>
        </WrapPanel>
        <TextBlock Text="Recent executions" FontSize="11" Margin="0,5"/>
        <ItemsControl ItemsSource="{Binding Executions}">
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <TextBlock FontSize="10" TextWrapping="Wrap">
                        <Run Text="{Binding ExecutedAt, StringFormat=HH:mm:ss}"/><Run Text=" "/>
                        <Run Text="{Binding Side}"/><Run Text=" "/><Run Text="{Binding Quantity}"/>
                        <Run Text=" @ "/><Run Text="{Binding Price}"/>
                        <Run Text=" · Fee: "/><Run Text="{Binding Fee.Amount}"/><Run Text=" "/>
                        <Run Text="{Binding Fee.Currency}"/>
                    </TextBlock>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
        </StackPanel>
        </ScrollViewer>
        <Grid Grid.Row="1" Margin="0,4,0,0">
            <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="10"/><ColumnDefinition/></Grid.ColumnDefinitions>
            <Button Content="Buy" Command="{Binding BuyCommand}" Background="#22613B"/>
            <Button Grid.Column="2" Content="Sell" Command="{Binding SellCommand}" Background="#873C43"/>
        </Grid>
        </Grid>
    </Grid>
</Window>
```

<a id="file-19"></a>

## 19. docs/FuturesTrading.md

Полная версия из `a3d8986`. SHA-256 исходного Git blob: `78effb386c7f6b0693b287782597f616f2e7a7720b35c8eca44ccdf7d6e3a22a`.

```markdown
# Торговля USDT-фьючерсами

В торговых счетах создайте профиль Binance или Bybit и выберите рынок `UsdtFutures`.
Сеть и рынок фиксируются в профиле; существующие профили без поля Market считаются
спотовыми. Ключи хранятся только в памяти и вводятся заново после перезапуска.

Выберите профиль для биржи и откройте «Стакан» через контекстное меню пары.
Приложение загрузит публичный каталог выбранной сети и предложит реальные символы
линейных контрактов с расчетом в USDT: бессрочные и срочные, если биржа их предоставляет.
Стакан и торговая сессия используют один рынок и сеть. Графики продолжают использовать
свои существующие подключения.

Поддерживаются лимитные GTC и Post-only заявки, отмена выбранной заявки и всех
заявок по контракту, собственные заявки на лестнице, исполнения и REST-сверка.
Журнал фьючерсных операций отделен от спотовых. Тайм-аут не вызывает повторную отправку.
Закрытие окна не отменяет заявки.

Количество задается в нативных контрактах либо суммой USDT без комиссии. Для
пересчета и стоимости используется contractSize из метаданных CCXT. Шаг количества
берется у биржи, дробные количества допускаются там, где это поддерживает контракт.

В one-way режиме выберите `Auto`. Для hedge-режима укажите `Long` или `Short`.
`Buy`/`Sell` определяет направление заявки; выбор позиции определяет ее hedge-сторону.
Для закрытия включите Reduce-only: количество не может превышать наблюдаемый размер
позиции, сторона должна ее уменьшать. Binance в hedge-режиме получает LONG/SHORT
без запрещенного там флага reduceOnly; Bybit получает positionIdx и reduceOnly.

Плечо и режим маржи читаются с биржи и автоматически не переключаются. Проверка
средств использует свободное обеспечение, текущее плечо, размер контракта, текущую
цену, возможный убыток открытия по лимитной цене и покрытие комиссии открытия/закрытия.
Это предварительная оценка; биржа дополнительно применяет собственные уровни риска.
Если режим позиций, плечо или обеспечение неизвестны, новые позиции блокируются.

Первая реализация ограничена стандартными счетами с обеспечением в USDT.
Binance Multi-Assets и Bybit с несколькими валютами обеспечения не поддерживаются;
portfolio margin с неизвестным плечом не используется для увеличения позиции.
Инверсные и USDC-контракты не входят в этот рынок. Для Binance без доступного UID
восстановление журнала привязано к отпечатку того же проверенного API key.

Автоматические проверки используют вымышленные ключи и подставные ответы.
Реальные сделки в Testnet требуют отдельных ключей и ручной приемки: открытие Long
и Short, частичное исполнение, Reduce-only, отмена, Post-only с пересечением цены,
разрыв соединения и восстановление после перезапуска. По умолчанию тесты не отправляют
реальные заявки и не меняют плечо или режим позиций.

Документация: [CCXT](https://docs.ccxt.com/docs/manual),
[Bybit positions](https://bybit-exchange.github.io/docs/v5/position),
[Bybit orders](https://bybit-exchange.github.io/docs/v5/order/create-order),
[Binance USD-M](https://developers.binance.com/docs/derivatives/usds-margined-futures/trade/rest-api/New-Order).
```

<a id="file-20"></a>

## 20. docs/TradingService.md

Полная версия из `a3d8986`. SHA-256 исходного Git blob: `236850326e84375a137e1b0cc14c6db00b611cd264115655623635ba34a0758e`.

```markdown
# Общий торговый контракт

`Services/ITradingService.cs` определяет общий контракт. `CcxtTradingService`
реализует торговлю спотом и USDT-фьючерсами Binance/Bybit в Testnet и Mainnet; QUIK пока предоставляет
каталог без торгового адаптера. Модели находятся в `Models/Trading.cs`, правила
инструмента — в `Models/TradingInstrumentRules.cs`.

USDT-контракты, режимы позиций и ограничения обеспечения описаны в [FuturesTrading.md](FuturesTrading.md).

## Торговля из стакана

`TradingSessionManager` создает авторизованный CCXT.Pro-клиент в фоне и закрепляет
сессию за проверенным владельцем счета и сетью. Окна одного счета используют общие
заявки и свободные средства. Профиль с явным запретом торговли не получает сессию
с торговыми правами другого ключа. Если биржа не возвращает ID владельца, журнал
привязан к SHA-256 отпечатку проверенного API key: новые ключи не получают старые
намерения автоматически. Сам ключ и секрет остаются только в ОЗУ.

Buy/Sell отправляют Limit GTC или Post-only без дополнительного подтверждения.
Количество можно ввести в базовой валюте либо суммой котировки без комиссии;
во втором случае количество округляется вниз средствами CCXT. Цена не округляется
молча. Проверяются точность, фильтры, текущие ценовые диапазоны, разрешения и баланс.
При неизвестной комиссии или свободных средствах отправка блокируется. Для известной
валюты комиссии учитывается ее сторона; при неизвестной стороне используется
консервативное покрытие комиссии в расходуемой валюте заявки.

Сессия сериализует проверку и отправку. При неопределенной операции блокируется
повторное выставление на этой паре и конкурирующее расходование той же валюты.
Так неизвестный резерв не вычитается второй раз из уже уменьшенного free balance.
Подтвержденные заявки учитываются через свежий биржевой баланс, а не второй локальный
резерв. Неподтвержденная отмена не означает освобождение средств.

Журнал `%LOCALAPPDATA%/BotView/trading-journal.db` сохраняет намерение до отправки,
фазы Prepared/Dispatching/Unknown/Resolved, заявки и исполнения. После сбоя Dispatching
восстанавливается как Unknown, Prepared никогда автоматически не отправляется.
Ключи, заголовки и исходные ответы API не записываются. Пустой результат поиска
не снимает Unknown; повторной автоматической отправки нет.

Приватные подписки дополняются REST-сверкой раз в 2 секунды с rate limiter CCXT.
После разрыва выполняются полная пагинация открытых заявок, запросы исчезнувших
заявок, сверка клиентских ID, исполнения с перекрытием watermark и баланс.
История читается окнами до семи дней; отсутствие старой истории не считается отказом
заявки. Дубликаты исполнений подавляются, завершенная заявка не открывается старым
событием. История API ограничена самой биржей: недоказанный результат остается Unknown.

Массовая отмена включает все заявки счета по паре, включая внешние. Подтверждение
показывает счет, сеть и число заранее зафиксированных идентификаторов; новые заявки
в пакет не включаются. Каждая отмена имеет самостоятельный результат.

Закрытие стакана не отменяет заявки и не завершает сессию. Смена ключей закрывает
старый клиент; после повторной авторизации восстанавливается журнал этого владельца
и сети. Завершение приложения закрывает транспорт без отмены биржевых заявок.

Проверки без торговли: собрать `BotView.Tests`, затем запустить
`BotView.Tests.exe -class BotView.Tests.SpotTradingTests`.
Реальные сделки автоматическими тестами не отправляются. Приемка Testnet требует
явного запуска приложения и ключей: Limit, Post-only с пересечением, частичное
исполнение, отмена, отключение сети и перезапуск с повторным вводом ключей.

Документация: [CCXT.Pro](https://docs.ccxt.com/docs/pro-manual),
[Bybit orders](https://bybit-exchange.github.io/docs/v5/order/create-order),
[Bybit price limits](https://bybit-exchange.github.io/docs/v5/market/order-price-limit),
[Binance filters](https://developers.binance.com/docs/binance-spot-api-docs/filters).

## Подключение и идентификаторы

- Экземпляр сервиса закреплен за `TradingAccountContext`: подключением, счетом,
  площадкой и Testnet/Production. При смене счета нужен другой экземпляр.
- Инструмент идентифицируется полным `InstrumentKey`: Venue, Market, Board, SecurityId.
  Символ для отображения не используется как глобальный ключ.
- `ClientOrderId` создается и сохраняется до отправки. Это идентификатор намерения
  приложения, а не гарантия идемпотентности API. Поддержка его передачи и поиска
  определяется адаптером.
- `ProviderRequestId` обозначает, например, номер транзакции QUIK, а `ExchangeOrderId` —
  номер биржевой заявки. Адаптер хранит их связь; эти значения не подменяют друг друга.

## Жизненный цикл

`PlaceOrderStatus.Submitted` подтверждает отправку, `Accepted` — принятие заявки,
`Rejected` — явный отказ, `Unknown` — отсутствие достоверного результата. Принятие
не означает исполнения. В QUIK результат отправки транзакции сопоставляется с
`OnTransReply` и обновлениями `OnOrder`; CCXT адаптер приводит ответы API к тем же моделям.

После тайм-аута/отмены ожидания уже отправленной операции запрещено автоматически
создавать новую заявку. Нужно сверить биржевой ID, client ID или собственную таблицу
соответствия транзакций. До отправки `CancellationToken` может отменить операцию обычным
исключением; после отправки должен сохраняться доступный для сверки результат.

Состояние самой заявки — отдельный `OrderState`. Частичное исполнение отражается
`FilledQuantity` и `RemainingQuantity`. Отмененная заявка может иметь исполнения.
Отмена возвращает свой результат: подтверждение отправки запроса не приравнивается
к окончательной отмене; заявка могла исполниться одновременно с запросом отмены.

Чтение заявки возвращает `null` только при подтвержденном отсутствии. Ошибки сети
и неподдерживаемый поиск не означают отсутствия заявки. Неподдерживаемые операции
выдают `NotSupportedException`, а не пустой успешный ответ.
Остальные ошибки чтения адаптер нормализует в `TradingServiceException` с безопасным
`TradingError`: наружу не передаются исключения CCXT/QUIK с исходными запросами.

## Количество и цена

Количество всегда выражено в `TradingInstrumentRules.QuantityUnit`:

| Тип | Единицы заявки | Котировка |
|---|---|---|
| Криптовалюта, спот | Базовая валюта | Валюта котирования за единицу |
| Акции через QUIK | Лоты | Цена за бумагу |
| Облигации через QUIK | Лоты | Проценты текущего номинала |
| Фьючерсы MOEX через QUIK | Контракты | Пункты котировки |

Для лотов и контрактов MOEX количество проверяется как целое положительное число.
QUIK-каталог не заполняет избыточные QuantityStep и MinQuantity; минимум 1 и шаг 1
следуют из этого правила. Криптоадаптер берет шаг количества,
лимиты и шаг цены из CCXT. Режим significant digits не превращается в выдуманный
постоянный шаг: точность дополнительно проверяет торговый адаптер средствами CCXT.

Рыночная заявка не требует лимитной цены. Stop требует отдельные `TriggerPrice`
и `TriggerCondition`, StopLimit — также лимитную `Price`. Возможности инструмента
определяют доступность типов, сроков действия, PostOnly, ReduceOnly и стороны позиции.
GTD требует будущую дату истечения; IOC/FOK несовместимы с PostOnly.

`TradingOrderValidator` проверяет общие правила без округления. Полная реализация
`ValidateOrderAsync`/`PlaceOrderAsync` дополнительно проверяет текущие данные провайдера,
права, торговую сессию, баланс/обеспечение и особенности брокера. Успех локальной
проверки не гарантирует принятия заявки биржей.

## Денежная оценка и счет

`TradingValueCalculator` использует метаданные инструмента:

- Спот/акции: цена × количество единиц; лоты переводятся через LotSize.
- Линейный криптоконтракт: цена × число контрактов × ContractSize.
- Облигации: число бумаг × (цена / 100 × текущий номинал + НКД на бумагу).
- Фьючерсы в пунктах: число контрактов × цена / шаг цены × стоимость шага.

Результат — стоимость в объявленной валюте, не требуемое обеспечение. ГО, вариационная
маржа и свободные средства возвращаются отдельно в состоянии счета. Inverse/quanto
контракты помечаются `ProviderSpecific` и рассчитываются адаптером.
Если валюта котировки/номинала и валюта расчетов различаются, без явной конвертации
адаптером сумма остается неизвестной; смена обозначения валюты не заменяет конвертацию.

Неизвестные номинал, НКД, стоимость шага или валюта дают `null`, а не нулевую сумму.
Каталог QUIK пока не загружает эти финансовые параметры: торговый адаптер должен
дополнить их при `GetTradingInstrumentAsync`. Денежные поля стакана принимают такое
описание инструмента и отображают неизвестную оценку как «—». Публичный криптостакан
сохраняет существующее поведение.

Состояние счета содержит денежные балансы/резервы, позиции и маржинальные показатели.
`null` в разделе снимка означает отсутствие достоверных данных, пустой список —
подтвержденное отсутствие записей.
У позиции явно заданы направление и единицы количества. Фактические исполнения
имеют отдельные ID и комиссии; ключ дедупликации — счет + инструмент + ID исполнения.

История, исполнения и открытые заявки используют `TradingPage<T>` с непрозрачным
курсором, чтобы не терять записи при ограничениях API. Адаптеры не игнорируют
неподдерживаемые фильтры. Поток `WatchUpdatesAsync` публикует изменения заявок,
исполнений, счета и соединения; после переподключения `RequiresResync` требует
сверки со снимками/историей. Закрытие сервиса освобождает соединение без отмены заявок.
```

## Diff рефакторинга без unit-тестов

Команда: `git diff --find-renames a3d8986 57f93c9`.

```diff
diff --git a/BotView/MainWindow.xaml.cs b/BotView/MainWindow.xaml.cs
index a368520..1165d05 100644
--- a/BotView/MainWindow.xaml.cs
+++ b/BotView/MainWindow.xaml.cs
@@ -371,20 +371,18 @@ namespace BotView
                 try
                 {
                     var profile = new TradingAccountProfile(Guid.NewGuid(), "Catalog", exchange, environment, TradingMarket.UsdtFutures);
-                    using var placeholder = new AccountCredentials("unused", "unused");
-                    await using var client = new CcxtSpotTradingClient(profile, placeholder);
-                    // Public catalog loading does not require a trading key.
-                    client.Transport.apiKey = "";
-                    client.Transport.secret = "";
-
-                    var contracts = await client.LoadTradingPairsAsync(symbol);
+                    await using var client = CcxtTradingClientFactory.Create(profile);
+                    var catalog = await client.LoadMarketsAsync();
+                    var contracts = catalog.Values.Where(m => m.contract == true && m.linear == true && m.settle == "USDT"
+                        && m.active != false && m.symbol!.Split(':')[0] == symbol.Split(':')[0])
+                        .OrderBy(m => m.expiry.HasValue).ThenBy(m => m.expiry).Select(m => m.symbol!).ToArray();
 
                     if (!this.IsLoaded)
                     {
                         return;
                     }
 
-                    if (contracts.Count == 0)
+                    if (contracts.Length == 0)
                     {
                         MessageBox.Show(this, "No active USDT contracts are available for this pair on the selected network.");
                         return;
diff --git a/BotView/Models/SpotTrading.cs b/BotView/Models/SpotTrading.cs
deleted file mode 100644
index 1511852..0000000
--- a/BotView/Models/SpotTrading.cs
+++ /dev/null
@@ -1,17 +0,0 @@
-namespace BotView.Models;
-
-public enum OrderQuantityCurrency { Base, Quote }
-public enum TradingOperationPhase { Prepared, Dispatching, Unknown, Resolved }
-
-/// <summary>Durable intent, scoped to the verified exchange identity and network; contains no credentials.</summary>
-public sealed record TradingOperation(string Id, string AccountKey, PlaceOrderRequest Request,
-    TradingOperationPhase Phase, DateTimeOffset CreatedAt, OrderReference? CancelTarget = null,
-    string? ExchangeOrderId = null);
-
-/// <summary>Detached account state shared by all windows. Uncertainty is separate from an order's exchange status.</summary>
-public sealed record SpotTradingState(TradingConnectionState Connection, string Message,
-    IReadOnlyList<OrderSnapshot> Orders, IReadOnlyList<ExecutionSnapshot> Executions,
-    AccountSnapshot? Balance, IReadOnlyList<TradingOperation> Pending, bool Ready);
-
-public sealed record CancelBatch(TradingAccountContext Account, InstrumentKey Instrument,
-    IReadOnlyList<OrderReference> Orders);
diff --git a/BotView/Models/Trading.cs b/BotView/Models/Trading.cs
index f2a73c0..b434352 100644
--- a/BotView/Models/Trading.cs
+++ b/BotView/Models/Trading.cs
@@ -8,22 +8,17 @@ public sealed record TradingAccountContext(
 
 public enum OrderType { Market, Limit, Stop, StopLimit }
 public enum OrderSide { Buy, Sell }
-public enum OrderTimeInForce { Gtc, Day, Ioc, Fok, Gtd }
-public enum TriggerCondition { AtOrAbove, AtOrBelow }
 public enum PositionSide { Long, Short }
-public enum PlaceOrderStatus { Submitted, Accepted, Rejected, Unknown }
-public enum CancelOrderStatus { Submitted, Canceled, AlreadyClosed, Rejected, Unknown }
 public enum OrderState { Pending, Open, Filled, Canceled, Expired, Rejected, Unknown }
 
-/// <summary>Generate and record ClientOrderId before dispatch; never reuse it for a new intent.
-/// Quantity uses the instrument's declared unit. Limit and trigger prices are separate.
-/// Client-ID lookup/idempotency depends on provider capabilities.</summary>
-public sealed record PlaceOrderRequest(
-    InstrumentKey Instrument, OrderSide Side, OrderType Type, decimal Quantity, string ClientOrderId,
-    decimal? Price = null, decimal? TriggerPrice = null, TriggerCondition? TriggerCondition = null,
-    OrderTimeInForce TimeInForce = OrderTimeInForce.Gtc, DateTimeOffset? ExpiresAt = null,
+/// <summary>Limit order in native asset or contract units, with an explicit limit price.</summary>
+public sealed record LimitOrderRequest(InstrumentKey Instrument, OrderSide Side, decimal Quantity, decimal Price,
     bool PostOnly = false, bool ReduceOnly = false, PositionSide? PositionSide = null);
 
+/// <summary>Market order in native units; a limit price and Post-only cannot be supplied.</summary>
+public sealed record MarketOrderRequest(InstrumentKey Instrument, OrderSide Side, decimal Quantity,
+    bool ReduceOnly = false, PositionSide? PositionSide = null);
+
 /// <summary>Exchange order identity is scoped by the bound account and full listing key.</summary>
 public sealed record OrderReference(InstrumentKey Instrument, string ExchangeOrderId);
 
@@ -38,19 +33,6 @@ public sealed record OrderSnapshot(
 /// <summary>Sanitized application error. Never include raw provider responses or credentials.</summary>
 public sealed record TradingError(string Code, string Message);
 
-/// <summary>Submitted confirms dispatch; Accepted confirms acceptance, not execution. ProviderRequestId
-/// identifies e.g. a QUIK transaction, independently of ExchangeOrderId. Unknown requires reconciliation.</summary>
-public sealed record PlaceOrderResult(
-    PlaceOrderStatus Status, string ClientOrderId, InstrumentKey Instrument,
-    string? ExchangeOrderId = null, string? ProviderRequestId = null,
-    OrderSnapshot? Order = null, TradingError? Error = null);
-
-/// <summary>Submitted is a cancel acknowledgement. Canceled requires confirmed final state.
-/// AlreadyClosed includes the observed terminal order when available; partial fills remain authoritative.</summary>
-public sealed record CancelOrderResult(
-    CancelOrderStatus Status, OrderReference Reference, string? ProviderRequestId = null,
-    OrderSnapshot? Order = null, TradingError? Error = null);
-
 public sealed record Money(decimal Amount, string Currency);
 public sealed record AccountBalance(string Currency, decimal? Total, decimal? Available, decimal? Reserved);
 
@@ -75,34 +57,3 @@ public sealed record AccountSnapshot(
 public sealed record ExecutionSnapshot(
     string ExecutionId, OrderReference Order, OrderSide Side, decimal Quantity, decimal Price,
     DateTimeOffset ExecutedAt, Money? Fee = null);
-
-/// <summary>UTC bounds and an opaque continuation cursor. Reject unsupported filters instead of
-/// ignoring them. A null instrument means all listings only where supported by the provider.</summary>
-public sealed record TradingQuery(
-    InstrumentKey? Instrument = null, DateTimeOffset? Since = null, DateTimeOffset? Until = null,
-    int Limit = 100, string? Cursor = null);
-public sealed record TradingPage<T>(IReadOnlyList<T> Items, string? NextCursor = null);
-
-/// <summary>Capabilities are listing/account-specific, not inferred from available enum values.
-/// Unsupported methods/options must fail explicitly rather than return an empty success.</summary>
-public sealed record TradingCapabilities(
-    IReadOnlySet<OrderType> OrderTypes, IReadOnlySet<OrderTimeInForce> TimeInForces,
-    bool SupportsPostOnly = false, bool SupportsReduceOnly = false, bool SupportsPositionSide = false,
-    bool SupportsLookupByClientOrderId = false, bool SupportsOrderHistory = false,
-    bool SupportsExecutions = false, bool SupportsPositions = false);
-
-public sealed record OrderValidationResult(IReadOnlyList<TradingError> Errors)
-{
-    public bool IsValid => Errors.Count == 0;
-}
-
-public enum TradingConnectionState { Connecting, Connected, Reconnecting, Disconnected }
-public abstract record TradingUpdate(DateTimeOffset AsOf);
-public sealed record OrderChanged(OrderSnapshot Order) : TradingUpdate(Order.UpdatedAt);
-public sealed record ExecutionReceived(ExecutionSnapshot Execution) : TradingUpdate(Execution.ExecutedAt);
-public sealed record AccountChanged(AccountSnapshot Account) : TradingUpdate(Account.AsOf);
-/// <summary>RequiresResync signals that consumers must refresh snapshots and reconcile history.
-/// A reconnected stream is not proof that no order or execution updates were missed.</summary>
-public sealed record TradingConnectionChanged(
-    TradingConnectionState State, DateTimeOffset Timestamp, bool RequiresResync = false, TradingError? Error = null)
-    : TradingUpdate(Timestamp);
diff --git a/BotView/Models/TradingAccountState.cs b/BotView/Models/TradingAccountState.cs
new file mode 100644
index 0000000..9ec0bd0
--- /dev/null
+++ b/BotView/Models/TradingAccountState.cs
@@ -0,0 +1,12 @@
+namespace BotView.Models;
+
+public enum OrderQuantityCurrency { Base, Quote }
+public enum TradingStream { Orders, MyTrades, Balance }
+
+/// <summary>Current display state; no local intentions, reservations or durable operation state.</summary>
+public sealed record TradingAccountState(string Message, IReadOnlyList<OrderSnapshot> Orders,
+    IReadOnlyList<ExecutionSnapshot> Executions, AccountSnapshot? Balance, bool Connected);
+
+/// <summary>Frozen list of open exchange IDs for a user-confirmed cancellation.</summary>
+public sealed record CancelBatch(TradingAccountContext Account, InstrumentKey Instrument,
+    IReadOnlyList<OrderReference> Orders);
diff --git a/BotView/Services/Integrations/Ccxt/CcxtSpotTradingClient.cs b/BotView/Services/Integrations/Ccxt/CcxtSpotTradingClient.cs
deleted file mode 100644
index 3a8ef61..0000000
--- a/BotView/Services/Integrations/Ccxt/CcxtSpotTradingClient.cs
+++ /dev/null
@@ -1,836 +0,0 @@
-﻿using System.Collections;
-using System.Collections.Concurrent;
-using System.Globalization;
-using System.Security.Cryptography;
-using System.Text;
-using BotView.Models;
-using BotView.Services;
-
-namespace BotView.Services.Integrations.Ccxt;
-
-/// <summary>Binance/Bybit spot and USDT contract mapping. Raw CCXT values and signed errors never escape this boundary.</summary>
-internal sealed class CcxtSpotTradingClient : ISpotTradingClient
-{
-    private readonly ccxt.Exchange _client;
-    private readonly TradingAccountProfile _profile;
-    private readonly string _keyIdentity;
-    private readonly Dictionary<string, SpotMarket> _markets = new();
-    private readonly Dictionary<string, DateTimeOffset> _marketTimes = new();
-    private string _bybitAccountType = "UNIFIED";
-    private readonly Dictionary<string, ccxt.MarketInterface> _rawMarkets = new();
-    internal ccxt.Exchange Transport => _client;
-    private readonly ConcurrentDictionary<string, string> _bybitOrderFilters = new();
-    private readonly ConcurrentDictionary<string, (PlaceOrderRequest Request, bool Hedged)> _validatedModes = new();
-    public bool IsFutures => _profile.Market == TradingMarket.UsdtFutures;
-    private string Category => IsFutures ? "linear" : "spot";
-    private string Listing => IsFutures ? "futures" : "spot";
-
-    /// <summary>Creates an isolated Pro client and selects sandbox before any request.</summary>
-    internal CcxtSpotTradingClient(TradingAccountProfile profile, AccountCredentials credentials,
-        ccxt.Exchange? transport = null)
-    {
-        _profile = profile;
-        var settings = ProOrderBookClient.CreateSettings();
-        settings["apiKey"] = credentials.ApiKey;
-        settings["secret"] = credentials.Secret;
-        settings["verbose"] = false;
-        _client = transport ?? (IsFutures ? CreateFuturesExchange(profile, settings) : CreateSpotExchange(profile, settings));
-        _keyIdentity = "key:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentials.ApiKey)));
-        if (profile.Environment == TradingEnvironment.Testnet) _client.setSandboxMode(true);
-    }
-
-    /// <summary>Loads active USDT-margined contract symbols that match the spot pair.</summary>
-    public async Task<IReadOnlyList<string>> LoadTradingPairsAsync(string spotSymbol, CancellationToken ct = default)
-    {
-        var markets = await _client.LoadMarkets().WaitAsync(ct).ConfigureAwait(false);
-        var pair = spotSymbol.Split(':')[0];
-        return markets.Values
-            .Where(m => IsLinearUsdtContract(m) && m.active != false && m.symbol!.Split(':')[0] == pair)
-            .OrderBy(m => m.expiry.HasValue)
-            .ThenBy(m => m.expiry)
-            .Select(m => m.symbol!)
-            .ToArray();
-    }
-
-    /// <summary>Authenticates and obtains a stable owner ID; a key fingerprint isolates recovery when no owner ID is returned.</summary>
-    public Task<(string Identity, bool? CanTrade)> ConnectAsync(CancellationToken ct) =>
-        IsFutures ? ConnectFuturesAsync(ct) : ConnectSpotAsync(ct);
-
-    /// <summary>Confirms futures access and rejects Binance multi-asset margin, which this adapter does not settle.</summary>
-    private async Task<(string Identity, bool? CanTrade)> ConnectFuturesAsync(CancellationToken ct)
-    {
-        await _client.FetchBalance(Parameters()).WaitAsync(ct).ConfigureAwait(false);
-        if (_client is ccxt.binance futuresBinance)
-        {
-            var mode = await futuresBinance.fapiPrivateGetMultiAssetsMargin().WaitAsync(ct).ConfigureAwait(false);
-            if (mode.TryGetValue("multiAssetsMargin", out var enabled) && Convert.ToBoolean(enabled, CultureInfo.InvariantCulture))
-            {
-                throw new NotSupportedException("USDT futures currently require single-asset margin mode.");
-            }
-        }
-
-        if (_client is ccxt.bybit futuresBybit)
-        {
-            var futuresKeyResponse = await futuresBybit.privateGetV5UserQueryApi().WaitAsync(ct).ConfigureAwait(false);
-            var keyInfo = (IDictionary<string, object>)futuresKeyResponse["result"];
-            bool? allowed = Text(keyInfo, "readOnly") == "1" ? false : null;
-            if (keyInfo.TryGetValue("permissions", out var futuresPermissions) && futuresPermissions is IDictionary<string, object> permissionMap &&
-                permissionMap.TryGetValue("ContractTrade", out var contractRights) && contractRights is IEnumerable rights)
-            {
-                allowed = allowed == false ? false : rights.Cast<object>().Any(p => p?.ToString() == "Order");
-            }
-
-            var owner = Text(keyInfo, "userID");
-            return (string.IsNullOrWhiteSpace(owner) ? _keyIdentity : "uid:" + owner, allowed);
-        }
-
-        return (_keyIdentity, null);
-    }
-
-    /// <summary>Reads the spot account owner and whether this key itself may trade.</summary>
-    private async Task<(string Identity, bool? CanTrade)> ConnectSpotAsync(CancellationToken ct)
-    {
-        if (_client is ccxt.binance binance)
-        {
-            var account = await binance.privateGetAccount().WaitAsync(ct).ConfigureAwait(false);
-            var uid = Text(account, "uid");
-            bool? canTrade = account.TryGetValue("canTrade", out var flag) && flag is bool allowed ? allowed : null;
-            bool? keyCanTrade = null;
-            if (_profile.Environment == TradingEnvironment.Production)
-            {
-                try { keyCanTrade = CcxtAccountVerificationClient.ParseBinancePermissions(await binance.sapiGetAccountApiRestrictions().WaitAsync(ct).ConfigureAwait(false)).CanTrade; }
-                catch (ccxt.ExchangeError) { }
-            }
-            // Account-wide canTrade is not proof that this particular API key has trading permission.
-            return (string.IsNullOrWhiteSpace(uid) ? _keyIdentity : "uid:" + uid, canTrade == false || keyCanTrade == false ? false : keyCanTrade);
-        }
-        var response = await ((ccxt.bybit)_client).privateGetV5UserQueryApi().WaitAsync(ct).ConfigureAwait(false);
-        var result = response["result"] as IDictionary<string, object> ?? throw new NotSupportedException("Account identity unavailable.");
-        var permissions = CcxtAccountVerificationClient.ParseBybitPermissions(result);
-        _bybitAccountType = Text(result, "uta") == "0" ? "SPOT" : "UNIFIED";
-        return (string.IsNullOrWhiteSpace(Text(result, "userID")) ? _keyIdentity : "uid:" + Text(result, "userID"), permissions.CanTrade);
-    }
-
-    /// <summary>Loads spot filters and account fees, retaining only detached application metadata.</summary>
-    public async Task<SpotMarket> MarketAsync(InstrumentKey key, CancellationToken ct)
-    {
-        if (key.Venue != _profile.Exchange || key.Market != Listing || key.Board != "") throw new NotSupportedException("Only this account's spot listings are supported.");
-        if (_markets.TryGetValue(key.SecurityId, out var cached) && DateTimeOffset.UtcNow - _marketTimes[key.SecurityId] < TimeSpan.FromMinutes(5)) return cached;
-        if (_markets.Count > 0) await _client.loadMarkets(true).WaitAsync(ct).ConfigureAwait(false);
-        var markets = await _client.LoadMarkets().WaitAsync(ct).ConfigureAwait(false);
-        if (!markets.TryGetValue(key.SecurityId, out var market) || !IsTradable(market) || market.active == false)
-            throw new NotSupportedException("The spot instrument is unavailable.");
-        decimal? fee = null;
-        if (_client.has.GetValueOrDefault("fetchTradingFee") is true && !(_profile.Exchange == "binance" && _profile.Environment == TradingEnvironment.Testnet))
-        {
-            try
-            {
-                var rates = await _client.FetchTradingFee(key.SecurityId).WaitAsync(ct).ConfigureAwait(false);
-                if (rates.percentage != false && rates.maker.HasValue && rates.taker.HasValue)
-                    fee = Math.Max(0, Math.Max(SpotOrderMath.Number(rates.maker)!.Value, SpotOrderMath.Number(rates.taker)!.Value));
-            }
-            catch (ccxt.ExchangeError) { }
-        }
-        if (fee is null && market.percentage == true && market.maker.HasValue && market.taker.HasValue)
-            fee = Math.Max(0, Math.Max(SpotOrderMath.Number(market.maker)!.Value, SpotOrderMath.Number(market.taker)!.Value));
-        var instrument = DescribeInstrument(CcxtMarketDataProvider.MapInstrument(_profile.Exchange, market, Convert.ToInt32(_client.precisionMode)), market, key);
-
-        cached = new(instrument, market.baseCurrency!, market.quote!, fee, market.feeSide);
-        _rawMarkets[key.SecurityId] = market;
-        _markets[key.SecurityId] = cached;
-        _marketTimes[key.SecurityId] = DateTimeOffset.UtcNow;
-        return cached;
-    }
-
-    /// <summary>Truncates quantities with CCXT's actual precision mode rather than assuming decimal places.</summary>
-    public decimal NormalizeAmount(string symbol, decimal amount) => decimal.Parse(_client.amountToPrecision(symbol, amount.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);
-
-    /// <summary>Normalizes prices for equality checks; callers reject any changed entered price.</summary>
-    public decimal NormalizePrice(string symbol, decimal price) => decimal.Parse(_client.priceToPrecision(symbol, price.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);
-
-    /// <summary>Verifies both precision and the typed CCXT double boundary before dispatch.</summary>
-    internal double WireNumber(string symbol, decimal value, bool amount)
-    {
-        var wire = (double)value;
-        var text = wire.ToString("R", CultureInfo.InvariantCulture);
-        var roundTrip = decimal.Parse((amount ? _client.amountToPrecision(symbol, text) : _client.priceToPrecision(symbol, text))!, CultureInfo.InvariantCulture);
-        if (roundTrip != value) throw new ArgumentException("The value cannot be represented at the exchange precision.");
-        return wire;
-    }
-
-    /// <summary>Maps Post-only explicitly, never downgrading a rejected maker request to an ordinary limit.</summary>
-    internal static Dictionary<string, object> OrderParameters(string exchange, PlaceOrderRequest request)
-    {
-        var parameters = new Dictionary<string, object> { ["clientOrderId"] = request.ClientOrderId };
-        if (exchange == "binance")
-        {
-            if (request.PostOnly) parameters["postOnly"] = true;
-            else parameters["timeInForce"] = "GTC";
-            parameters["newOrderRespType"] = "FULL";
-        }
-        else
-        {
-            parameters["category"] = "spot";
-            parameters["isLeverage"] = 0;
-            parameters["timeInForce"] = request.PostOnly ? "PostOnly" : "GTC";
-        }
-        return parameters;
-    }
-
-    /// <summary>Sends exactly once; transport uncertainty is classified by the account service.</summary>
-    public async Task<OrderSnapshot> PlaceAsync(PlaceOrderRequest request, CancellationToken ct)
-    {
-        var symbol = request.Instrument.SecurityId;
-        var amount = WireNumber(symbol, request.Quantity, true);
-        var price = WireNumber(symbol, request.Price!.Value, false);
-        var parameters = await CreateOrderParametersAsync(request, ct).ConfigureAwait(false);
-
-        var order = await _client.CreateOrder(symbol, "limit", request.Side == OrderSide.Buy ? "buy" : "sell", amount, price,
-            parameters).WaitAsync(ct).ConfigureAwait(false);
-        return MapOrder(order, request.Instrument) with { ClientOrderId = request.ClientOrderId,
-            Quantity = request.Quantity, Price = request.Price, Side = request.Side,
-            PositionSide = request.PositionSide, ReduceOnly = request.ReduceOnly };
-    }
-
-    /// <summary>An acknowledgement of cancellation is not assumed to be a final canceled state.</summary>
-    public async Task<OrderSnapshot?> CancelAsync(OrderReference order, CancellationToken ct)
-    {
-        if (_client is ccxt.bybit bybit)
-        {
-            await bybit.privatePostV5OrderCancel(BybitCancelParameters(order)).WaitAsync(ct).ConfigureAwait(false);
-            return null;
-        }
-        return MapOrder(await _client.CancelOrder(order.ExchangeOrderId, order.Instrument.SecurityId, Parameters()).WaitAsync(ct).ConfigureAwait(false), order.Instrument);
-    }
-
-    /// <summary>Returns null only for the provider's explicit order-not-found response.</summary>
-    public async Task<OrderSnapshot?> OrderAsync(OrderReference order, CancellationToken ct)
-    {
-        if (_profile.Exchange == "bybit")
-        {
-            foreach (var open in new[] { true, false })
-            {
-                var page = await BybitOrdersAsync(new(order.Instrument), open, ct, exchangeId: order.ExchangeOrderId).ConfigureAwait(false);
-                var found = page.Items.FirstOrDefault(o => o.Reference == order);
-                if (found is not null) return found;
-            }
-            return null;
-        }
-        try { return MapOrder(await _client.FetchOrder(order.ExchangeOrderId, order.Instrument.SecurityId, Parameters()).WaitAsync(ct).ConfigureAwait(false), order.Instrument); }
-        catch (ccxt.OrderNotFound) { return null; }
-    }
-
-    /// <summary>Looks up the original client ID, including Bybit history after an order leaves the realtime cache.</summary>
-    public async Task<OrderSnapshot?> FindAsync(InstrumentKey key, string clientId, CancellationToken ct)
-    {
-        if (_profile.Exchange == "binance")
-            return await (IsFutures ? FindBinanceFuturesOrderAsync(key, clientId, ct) : FindBinanceSpotOrderAsync(key, clientId, ct)).ConfigureAwait(false);
-        foreach (var open in new[] { true, false })
-        {
-            var page = await BybitOrdersAsync(new(key), open, ct, clientId).ConfigureAwait(false);
-            foreach (var order in page.Items) if (order.ClientOrderId == clientId) return order;
-        }
-        return null;
-    }
-
-    /// <summary>Uses provider continuation: Bybit cursor, Binance increasing order ID for history.</summary>
-    public async Task<TradingPage<OrderSnapshot>> OrdersAsync(TradingQuery query, bool open, CancellationToken ct)
-    {
-        var key = query.Instrument ?? throw new NotSupportedException("A spot instrument is required.");
-        if (_profile.Exchange == "bybit") return await BybitOrdersAsync(query, open, ct).ConfigureAwait(false);
-        var parameters = Parameters();
-        if (query.Until.HasValue) parameters[_profile.Exchange == "binance" ? "endTime" : "endTime"] = query.Until.Value.ToUnixTimeMilliseconds();
-        if (query.Cursor is not null) parameters[_profile.Exchange == "binance" ? "orderId" : "cursor"] = query.Cursor;
-        var limit = _profile.Exchange == "binance" && open ? (int?)null : Math.Clamp(query.Limit, 1, _profile.Exchange == "bybit" ? 50 : 1000);
-        var orders = open
-            ? await _client.FetchOpenOrders(key.SecurityId, null, limit, parameters).WaitAsync(ct).ConfigureAwait(false)
-            : await _client.FetchOrders(key.SecurityId, query.Since?.ToUnixTimeMilliseconds(), limit, parameters).WaitAsync(ct).ConfigureAwait(false);
-        string? next = null;
-        if (_profile.Exchange == "binance" && !open && orders.Count == limit && long.TryParse(orders[^1].id, out var id)) next = (id + 1).ToString(CultureInfo.InvariantCulture);
-        return new(orders.Select(o => MapOrder(o, key)).ToArray(), string.IsNullOrEmpty(next) ? null : next);
-    }
-
-    /// <summary>Fetches paged actual executions; the caller persists identities and overlaps its watermark.</summary>
-    public async Task<TradingPage<ExecutionSnapshot>> ExecutionsAsync(TradingQuery query, CancellationToken ct)
-    {
-        var key = query.Instrument ?? throw new NotSupportedException("A spot instrument is required.");
-        if (_profile.Exchange == "bybit")
-        {
-            var request = BybitQuery(query);
-            var response = await ((ccxt.bybit)_client).privateGetV5ExecutionList(request).WaitAsync(ct).ConfigureAwait(false);
-            return ParseBybitExecutions(response, key);
-        }
-        var parameters = Parameters();
-        if (query.Cursor is not null) parameters[_profile.Exchange == "binance" ? "fromId" : "cursor"] = query.Cursor;
-        if (query.Until.HasValue) parameters["endTime"] = query.Until.Value.ToUnixTimeMilliseconds();
-        var limit = Math.Clamp(query.Limit, 1, _profile.Exchange == "bybit" ? 100 : 1000);
-        var trades = await _client.FetchMyTrades(key.SecurityId, query.Cursor is null ? query.Since?.ToUnixTimeMilliseconds() : null, limit, parameters).WaitAsync(ct).ConfigureAwait(false);
-        string? next = trades.Count > 0 ? ResponseCursor(trades[^1].info) : null;
-        if (_profile.Exchange == "binance" && trades.Count == limit && long.TryParse(trades[^1].id, out var id)) next = (id + 1).ToString(CultureInfo.InvariantCulture);
-        return new(trades.Select(t => MapExecution(t, key)).ToArray(), string.IsNullOrEmpty(next) ? null : next);
-    }
-
-    /// <summary>Copies balances, preserving unknown free/used values instead of manufacturing zeroes.</summary>
-    public Task<AccountSnapshot> BalanceAsync(TradingAccountContext account, CancellationToken ct) =>
-        IsFutures ? FuturesBalanceAsync(account, ct) : SpotBalanceAsync(account, ct);
-
-    /// <summary>Copies spot wallet balances, preserving unknown free/used values instead of manufacturing zeroes.</summary>
-    private async Task<AccountSnapshot> SpotBalanceAsync(TradingAccountContext account, CancellationToken ct)
-    {
-        if (_client is ccxt.bybit bybit)
-        {
-            var raw = await bybit.privateGetV5AccountWalletBalance(new Dictionary<string, object> { ["accountType"] = _bybitAccountType }).WaitAsync(ct).ConfigureAwait(false);
-            var walletBalances = new List<AccountBalance>();
-            var result = (IDictionary<string, object>)raw["result"];
-            foreach (var wallet in ((IEnumerable)result["list"]).Cast<IDictionary<string, object>>())
-                foreach (var coin in ((IEnumerable)wallet["coin"]).Cast<IDictionary<string, object>>())
-                {
-                    var total = DecimalField(coin, "walletBalance");
-                    var locked = DecimalField(coin, "locked");
-                    var borrow = DecimalField(coin, "spotBorrow") ?? DecimalField(coin, "borrowAmount");
-                    // Wallet holdings, less locked/borrowed assets; available margin is not spot cash.
-                    var free = DecimalField(coin, "free") ?? (total.HasValue && locked.HasValue ? Math.Max(0, total.Value - locked.Value - (borrow ?? 0)) : (decimal?)null);
-                    walletBalances.Add(new(Text(coin, "coin")!, total, free, locked));
-                }
-            return new(account, walletBalances, null, null, DateTimeOffset.UtcNow);
-        }
-        var balances = await _client.FetchBalance(Parameters()).WaitAsync(ct).ConfigureAwait(false);
-        return MapBalance(balances, account);
-    }
-
-    /// <summary>Checks runtime capabilities because private-stream support differs by exchange and version.</summary>
-    public bool SupportsStream(string kind) => _client.has.GetValueOrDefault(kind) is true;
-
-    /// <summary>Copies a private stream batch before calling watch again.</summary>
-    public async Task<IReadOnlyList<TradingUpdate>> WatchAsync(string kind, InstrumentKey key, TradingAccountContext account, CancellationToken ct)
-    {
-        if (kind == "watchOrders") return (await _client.WatchOrders(key.SecurityId, null, null, Parameters()).WaitAsync(ct).ConfigureAwait(false))
-            .Where(o => o.symbol == key.SecurityId).Select(o => (TradingUpdate)new OrderChanged(MapAccountOrder(o, key))).ToArray();
-        if (kind == "watchMyTrades") return (await _client.WatchMyTrades(key.SecurityId, null, null, Parameters()).WaitAsync(ct).ConfigureAwait(false))
-            .Where(t => t.symbol == key.SecurityId).Select(t => (TradingUpdate)new ExecutionReceived(MapExecution(t, key))).ToArray();
-        return [new AccountChanged(MapBalance(await _client.WatchBalance(Parameters()).WaitAsync(ct).ConfigureAwait(false), account))];
-    }
-
-    /// <summary>Routes unified calls without leaking derivative-only options into spot HTTP requests.
-    /// The linear subtype is configured on the futures client, not forwarded to individual endpoints.</summary>
-    private Dictionary<string, object> Parameters() => IsFutures ? FuturesParameters() : SpotParameters();
-
-    /// <summary>Spot requests stay free of derivative routing options.</summary>
-    private static Dictionary<string, object> SpotParameters() => new();
-
-    /// <summary>Selects the USDT-M route. Linearity stays on the futures client, not on each request.</summary>
-    private Dictionary<string, object> FuturesParameters() => new()
-    {
-        ["type"] = _profile.Exchange == "binance" ? "future" : "swap"
-    };
-
-    /// <summary>Preserves provider status rather than treating an asynchronous acknowledgement as an open order.</summary>
-    internal static OrderSnapshot MapOrder(ccxt.Order order, InstrumentKey key) => new(
-        new(key, order.id ?? ""), order.clientOrderId, order.side == "sell" ? OrderSide.Sell : OrderSide.Buy,
-        order.type switch { "market" => OrderType.Market, "stop" or "stop_market" => OrderType.Stop,
-            "stop_limit" => OrderType.StopLimit, _ => OrderType.Limit }, order.status switch { "open" => OrderState.Open, "closed" => OrderState.Filled,
-            "canceled" or "cancelled" => OrderState.Canceled, "expired" => OrderState.Expired, "rejected" => OrderState.Rejected, _ => OrderState.Pending },
-        SpotOrderMath.Number(order.amount) ?? 0, SpotOrderMath.Number(order.filled), SpotOrderMath.Number(order.remaining),
-        SpotOrderMath.Number(order.price), SpotOrderMath.Number(order.average), At(order.timestamp),
-        At(order.lastUpdateTimestamp ?? order.lastTradeTimestamp ?? order.timestamp) ?? DateTimeOffset.MinValue, StatusReason(order),
-        GetPositionSide(order), order.reduceOnly);
-
-    /// <summary>Retains the hedge leg for exchange orders created by this or another application.</summary>
-    private static PositionSide? GetPositionSide(ccxt.Order order)
-    {
-        if (order.info is null)
-        {
-            return null;
-        }
-
-        return Text(order.info, "positionSide") switch
-        {
-            "LONG" => PositionSide.Long,
-            "SHORT" => PositionSide.Short,
-            _ => Text(order.info, "positionIdx") switch
-            {
-                "1" => PositionSide.Long,
-                "2" => PositionSide.Short,
-                _ => null
-            }
-        };
-    }
-
-    /// <summary>Maps an allowlist of provider reason codes; raw rejection text is never shown or persisted.</summary>
-    private static string? StatusReason(ccxt.Order order)
-    {
-        var reason = order.info is null ? null : Text(order.info, "rejectReason") ?? Text(order.info, "cancelType") ?? Text(order.info, "expiryReason");
-        if (reason is "EC_PostOnlyWillTakeLiquidity" or "CancelByPostOnly") return "Post-only would execute immediately.";
-        if (reason is "EC_NoEnoughQtyToFill" or "INSUFFICIENT_LIQUIDITY") return "Insufficient exchange liquidity.";
-        return order.status switch { "canceled" or "cancelled" => "Canceled by the exchange.", "rejected" => "Rejected by the exchange.",
-            "expired" => "Expired at the exchange.", _ => null };
-    }
-
-    /// <summary>Maps a fill independently of its order's current state, retaining the actual commission currency.</summary>
-    private static ExecutionSnapshot MapExecution(ccxt.Trade trade, InstrumentKey key) => new(trade.id ?? "",
-        new(key, trade.order ?? trade.orderId ?? ""), trade.side == "sell" ? OrderSide.Sell : OrderSide.Buy,
-        SpotOrderMath.Number(trade.amount) ?? 0, SpotOrderMath.Number(trade.price) ?? 0,
-        At(trade.timestamp) ?? DateTimeOffset.MinValue,
-        trade.fee is { } fee && fee.currency is not null && SpotOrderMath.Number(fee.cost) is { } cost ? new(cost, fee.currency) : null);
-
-    /// <summary>Copies all currency balances out of the mutable CCXT cache.</summary>
-    private static AccountSnapshot MapBalance(ccxt.Balances balance, TradingAccountContext account) => new(account,
-        balance.balances.Select(p => new AccountBalance(p.Key, SpotOrderMath.Number(p.Value.total),
-            SpotOrderMath.Number(p.Value.free), SpotOrderMath.Number(p.Value.used))).ToArray(), null, null, DateTimeOffset.UtcNow);
-
-    /// <summary>Reads optional detached provider metadata.</summary>
-    private static string? Text(IDictionary<string, object> data, string key) => data.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
-
-    /// <summary>Extracts a cursor if a provider preserves it in unified item metadata.</summary>
-    private static string? ResponseCursor(IDictionary<string, object>? data) => data is null ? null : Text(data, "nextPageCursor");
-
-    /// <summary>Retains Bybit's envelope cursor, which unified CCXT order lists do not expose.</summary>
-    private async Task<TradingPage<OrderSnapshot>> BybitOrdersAsync(TradingQuery query, bool open, CancellationToken ct, string? clientId = null, string? exchangeId = null)
-    {
-        var request = BybitQuery(query);
-        if (clientId is not null) request["orderLinkId"] = clientId;
-        if (exchangeId is not null) request["orderId"] = exchangeId;
-        var bybit = (ccxt.bybit)_client;
-        var response = open ? await bybit.privateGetV5OrderRealtime(request).WaitAsync(ct).ConfigureAwait(false)
-            : await bybit.privateGetV5OrderHistory(request).WaitAsync(ct).ConfigureAwait(false);
-        return ParseBybitOrders(response, query.Instrument!);
-    }
-
-    /// <summary>Retains the cursor from the response envelope while parsing detached spot orders.</summary>
-    internal TradingPage<OrderSnapshot> ParseBybitOrders(IDictionary<string, object> response, InstrumentKey key)
-    {
-        var result = (IDictionary<string, object>)response["result"];
-        var items = ((IEnumerable)result["list"]).Cast<object>()
-            .Select(raw => MapAccountOrder(new ccxt.Order(_client.parseOrder(raw, _client.market(key.SecurityId))), key)).ToArray();
-        return new(items, EmptyCursor(Text(result, "nextPageCursor")));
-    }
-
-    /// <summary>Parses actual fill IDs, commissions and the execution endpoint's independent cursor.</summary>
-    internal TradingPage<ExecutionSnapshot> ParseBybitExecutions(IDictionary<string, object> response, InstrumentKey key)
-    {
-        var result = (IDictionary<string, object>)response["result"];
-        var items = ((IEnumerable)result["list"]).Cast<object>().Select(raw => MapExecution(new ccxt.Trade(_client.parseTrade(raw, _client.market(key.SecurityId))), key)).ToArray();
-        return new(items, EmptyCursor(Text(result, "nextPageCursor")));
-    }
-
-    /// <summary>Routes an external conditional order's cancellation using its original spot order filter.</summary>
-    internal Dictionary<string, object> BybitCancelParameters(OrderReference order) =>
-        IsFutures ? FuturesBybitCancelParameters(order) : SpotBybitCancelParameters(order);
-
-    /// <summary>Keeps provider routing for externally created conditional spot orders inside the adapter.</summary>
-    private OrderSnapshot MapAccountOrder(ccxt.Order order, InstrumentKey key) =>
-        IsFutures ? MapOrder(order, key) : MapSpotAccountOrder(order, key);
-
-    /// <summary>Builds an explicitly spot-only query and preserves caller bounds and cursor.</summary>
-    private Dictionary<string, object> BybitQuery(TradingQuery query)
-    {
-        if (query.Since.HasValue && query.Until.HasValue && query.Until - query.Since > TimeSpan.FromDays(7))
-            throw new NotSupportedException("Bybit history requests must use windows of at most seven days.");
-        var request = new Dictionary<string, object> { ["category"] = Category, ["symbol"] = _client.Market(query.Instrument!.SecurityId).id!, ["limit"] = Math.Clamp(query.Limit, 1, 50) };
-        if (query.Cursor is not null) request["cursor"] = query.Cursor;
-        if (query.Since.HasValue) request["startTime"] = query.Since.Value.ToUnixTimeMilliseconds();
-        if (query.Until.HasValue) request["endTime"] = query.Until.Value.ToUnixTimeMilliseconds();
-        return request;
-    }
-
-    /// <summary>Checks Binance's moving price bands with a fresh reference instead of stale depth.</summary>
-    public Task<IReadOnlyList<TradingError>> ValidateMarketAsync(PlaceOrderRequest request, CancellationToken ct) =>
-        IsFutures ? Task.FromResult<IReadOnlyList<TradingError>>([]) : ValidateSpotMarketAsync(request, ct);
-
-    /// <summary>Rejects a spot limit that sits outside the exchange's current price band.</summary>
-    private async Task<IReadOnlyList<TradingError>> ValidateSpotMarketAsync(PlaceOrderRequest request, CancellationToken ct)
-    {
-        WireNumber(request.Instrument.SecurityId, request.Quantity, true);
-        WireNumber(request.Instrument.SecurityId, request.Price!.Value, false);
-        if (_client is ccxt.bybit bybit)
-        {
-            var response = await bybit.publicGetV5MarketPriceLimit(new Dictionary<string, object>
-                { ["category"] = Category, ["symbol"] = _client.Market(request.Instrument.SecurityId).id! }).WaitAsync(ct).ConfigureAwait(false);
-            var limits = (IDictionary<string, object>)response["result"];
-            var maximumBuy = DecimalField(limits, "buyLmt");
-            var minimumSell = DecimalField(limits, "sellLmt");
-            if (maximumBuy is null || minimumSell is null) return [new("PriceBandUnavailable", "The current spot price band is unavailable.")];
-            if (request.Side == OrderSide.Buy && request.Price > maximumBuy || request.Side == OrderSide.Sell && request.Price < minimumSell)
-                return [new("PriceBand", "Price is outside the exchange's current allowed band.")];
-            return [];
-        }
-        if (_client is not ccxt.binance binance || !_rawMarkets.TryGetValue(request.Instrument.SecurityId, out var market) ||
-            market.info is null || !market.info.TryGetValue("filters", out var raw) || raw is not IEnumerable filters) return [];
-        decimal? referencePrice = null;
-        try
-        {
-            var reference = await binance.publicGetReferencePrice(new Dictionary<string, object> { ["symbol"] = market.id! }).WaitAsync(ct).ConfigureAwait(false);
-            referencePrice = DecimalField(reference, "referencePrice");
-        }
-        catch (ccxt.NotSupported) { }
-        catch (ccxt.BadRequest) { }
-        foreach (var filter in filters.Cast<IDictionary<string, object>>().Where(f => Text(f, "filterType") is "PERCENT_PRICE" or "PERCENT_PRICE_BY_SIDE"))
-        {
-            var parameters = new Dictionary<string, object> { ["symbol"] = market.id! };
-            var zeroMinutes = Text(filter, "avgPriceMins") == "0";
-            var average = referencePrice;
-            if (average is null)
-            {
-                var reference = zeroMinutes ? await binance.publicGetTickerPrice(parameters).WaitAsync(ct).ConfigureAwait(false)
-                    : await binance.publicGetAvgPrice(parameters).WaitAsync(ct).ConfigureAwait(false);
-                average = DecimalField((IDictionary<string, object>)reference, "price");
-            }
-            var prefix = Text(filter, "filterType") == "PERCENT_PRICE_BY_SIDE" ? request.Side == OrderSide.Buy ? "bidMultiplier" : "askMultiplier" : "multiplier";
-            var lower = DecimalField(filter, prefix + "Down");
-            var upper = DecimalField(filter, prefix + "Up");
-            if (average is null || lower is null || upper is null) return [new("PriceBandUnavailable", "The exchange price band could not be checked.")];
-            if (request.Price < average * lower || request.Price > average * upper) return [new("PriceBand", "Price is outside the exchange's current allowed band.")];
-        }
-        return [];
-    }
-
-    /// <summary>Parses optional provider decimal strings without passing through binary floating point.</summary>
-    private static decimal? DecimalField(IDictionary<string, object> value, string key) => decimal.TryParse(Text(value, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
-
-    /// <summary>Normalizes an empty provider cursor to the end of pagination.</summary>
-    private static string? EmptyCursor(string? cursor) => string.IsNullOrWhiteSpace(cursor) ? null : cursor;
-
-    /// <summary>Converts only actual exchange timestamps; unknown timestamps do not get invented.</summary>
-    private static DateTimeOffset? At(long? timestamp) => timestamp.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value) : null;
-
-    /// <summary>Closes transport and removes client-owned credential references without canceling any order.</summary>
-    public async ValueTask DisposeAsync()
-    {
-        try { await _client.Close().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
-        finally { _client.apiKey = ""; _client.secret = ""; }
-    }
-
-    /// <summary>Opens a public spot client. Futures options are never applied here.</summary>
-    private static ccxt.Exchange CreateSpotExchange(TradingAccountProfile profile, Dictionary<string, object> settings) => profile.Exchange switch
-    {
-        "binance" => new ccxt.pro.binance(settings),
-        "bybit" => new ccxt.pro.bybit(settings),
-        _ => throw new NotSupportedException("Spot trading supports Binance and Bybit only.")
-    };
-
-    /// <summary>Opens a USDT-M client and limits market loading to linear contracts.</summary>
-    private static ccxt.Exchange CreateFuturesExchange(TradingAccountProfile profile, Dictionary<string, object> settings)
-    {
-        var options = (Dictionary<string, object>)settings["options"];
-        options["defaultType"] = profile.Exchange == "binance" ? "future" : "swap";
-        options["defaultSubType"] = "linear";
-        options["fetchMarkets"] = new Dictionary<string, object> { ["types"] = new List<object> { "linear" } };
-        return profile.Exchange switch
-        {
-            "binance" => new ccxt.pro.binanceusdm(settings),
-            "bybit" => new ccxt.pro.bybit(settings),
-            _ => throw new NotSupportedException("Spot trading supports Binance and Bybit only.")
-        };
-    }
-
-    private bool IsTradable(ccxt.MarketInterface market) => IsFutures ? IsLinearUsdtContract(market) : market.spot == true;
-
-    private static bool IsLinearUsdtContract(ccxt.MarketInterface market) =>
-        market.contract == true && market.linear == true && market.settle == "USDT";
-
-    private Instrument DescribeInstrument(Instrument instrument, ccxt.MarketInterface market, InstrumentKey key) =>
-        IsFutures ? DescribeFuturesInstrument(instrument, market, key) : instrument;
-
-    /// <summary>Records contract size and USDT settlement on top of the shared market metadata.</summary>
-    private static Instrument DescribeFuturesInstrument(Instrument instrument, ccxt.MarketInterface market, InstrumentKey key) => instrument with
-    {
-        Key = key,
-        Expiry = market.expiry.HasValue ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(market.expiry.Value).UtcDateTime) : null,
-        TradingRules = instrument.TradingRules! with
-        {
-            QuantityUnit = QuantityUnit.Contracts,
-            ContractSize = SpotOrderMath.Number(market.contractSize),
-            SettlementCurrency = "USDT"
-        }
-    };
-
-    private Task<Dictionary<string, object>> CreateOrderParametersAsync(PlaceOrderRequest request, CancellationToken ct) =>
-        IsFutures ? FuturesParametersAsync(request, ct) : Task.FromResult(OrderParameters(_profile.Exchange, request));
-
-    /// <summary>Resolves a spot order by the original client id. Implicit Binance endpoints ignore CCXT routing options.</summary>
-    private async Task<OrderSnapshot?> FindBinanceSpotOrderAsync(InstrumentKey key, string clientId, CancellationToken ct)
-    {
-        try
-        {
-            var raw = await ((ccxt.binance)_client).privateGetOrder(BinanceClientOrderQuery(key, clientId)).WaitAsync(ct).ConfigureAwait(false);
-            return MapOrder(new ccxt.Order(_client.parseOrder(raw, _client.market(key.SecurityId))), key);
-        }
-        catch (ccxt.OrderNotFound) { return null; }
-    }
-
-    /// <summary>Resolves a USDT-M order by the original client id through the futures endpoint.</summary>
-    private async Task<OrderSnapshot?> FindBinanceFuturesOrderAsync(InstrumentKey key, string clientId, CancellationToken ct)
-    {
-        try
-        {
-            var raw = await ((ccxt.binance)_client).fapiPrivateGetOrder(BinanceClientOrderQuery(key, clientId)).WaitAsync(ct).ConfigureAwait(false);
-            return MapOrder(new ccxt.Order(_client.parseOrder(raw, _client.market(key.SecurityId))), key);
-        }
-        catch (ccxt.OrderNotFound) { return null; }
-    }
-
-    private Dictionary<string, object> BinanceClientOrderQuery(InstrumentKey key, string clientId) => new()
-    {
-        ["origClientOrderId"] = clientId,
-        ["symbol"] = _client.Market(key.SecurityId).id!
-    };
-
-    private Dictionary<string, object> SpotBybitCancelParameters(OrderReference order)
-    {
-        var parameters = BybitCancelRequest(order);
-        parameters["orderFilter"] = _bybitOrderFilters.GetValueOrDefault(order.ExchangeOrderId, "Order");
-        return parameters;
-    }
-
-    private Dictionary<string, object> FuturesBybitCancelParameters(OrderReference order) => BybitCancelRequest(order);
-
-    private Dictionary<string, object> BybitCancelRequest(OrderReference order) => new()
-    {
-        ["category"] = Category,
-        ["symbol"] = _client.Market(order.Instrument.SecurityId).id!,
-        ["orderId"] = order.ExchangeOrderId
-    };
-
-    /// <summary>Remembers Bybit's order filter so a later spot cancel reaches the same conditional order.</summary>
-    private OrderSnapshot MapSpotAccountOrder(ccxt.Order order, InstrumentKey key)
-    {
-        if (_profile.Exchange == "bybit" && order.id is not null && order.info is { } info)
-        {
-            var filter = Text(info, "orderFilter");
-            if (filter is not ("Order" or "StopOrder" or "tpslOrder" or "OcoOrder" or "BidirectionalTpslOrder"))
-                filter = Text(info, "stopOrderType") switch { "Stop" => "StopOrder", "TakeProfit" or "StopLoss" => "tpslOrder",
-                    "OcoOrder" => "OcoOrder", "BidirectionalTpslOrder" => "BidirectionalTpslOrder", _ => "Order" };
-            _bybitOrderFilters[order.id] = filter;
-        }
-        return MapOrder(order, key);
-    }
-
-    /// <summary>Spot accounts use balances instead of contract positions.</summary>
-    public Task<IReadOnlyList<PositionSnapshot>> PositionsAsync(InstrumentKey? key, CancellationToken ct) =>
-        IsFutures ? LoadFuturesPositionsAsync(key, ct) : throw new NotSupportedException("Spot accounts use balances instead of contract positions.");
-
-    /// <summary>Reads position size, actual leverage and position mode without changing exchange settings.</summary>
-    private async Task<IReadOnlyList<PositionSnapshot>> LoadFuturesPositionsAsync(InstrumentKey? key, CancellationToken ct)
-    {
-        var parameters = Parameters();
-        parameters["settleCoin"] = "USDT";
-        var positions = await _client.FetchPositions(key is null ? null! : new List<object> { key.SecurityId }, parameters)
-            .WaitAsync(ct).ConfigureAwait(false);
-        var result = new List<PositionSnapshot>();
-
-        foreach (var position in positions)
-        {
-            var market = _client.Market(position.symbol!);
-            if (market.linear != true || market.settle != "USDT")
-            {
-                continue;
-            }
-
-            var index = position.info is null ? null : Text(position.info, "positionIdx");
-            var side = index == "2" || position.side == "short" ? PositionSide.Short : PositionSide.Long;
-            var hedged = index is "1" or "2" ? true : index == "0" ? false : position.hedged;
-            result.Add(new PositionSnapshot(
-                new(_profile.Exchange, "futures", "", position.symbol!), side,
-                Math.Abs(SpotOrderMath.Number(position.contracts) ?? 0), QuantityUnit.Contracts,
-                SpotOrderMath.Number(position.entryPrice),
-                SpotOrderMath.Number(position.unrealizedPnl) is { } pnl ? new(pnl, "USDT") : null,
-                SpotOrderMath.Number(position.initialMargin) is { } margin ? new(margin, "USDT") : null,
-                SpotOrderMath.Number(position.leverage), hedged, position.marginMode));
-        }
-
-        if (_client is ccxt.binance binance && key is not null)
-        {
-            var mode = await binance.fapiPrivateGetPositionSideDual().WaitAsync(ct).ConfigureAwait(false);
-            var hedged = mode.TryGetValue("dualSidePosition", out var flag) && Convert.ToBoolean(flag, CultureInfo.InvariantCulture);
-            var configuration = await binance.fapiPrivateGetSymbolConfig(new Dictionary<string, object>
-            {
-                ["symbol"] = _client.Market(key.SecurityId).id!
-            }).WaitAsync(ct).ConfigureAwait(false);
-            var config = configuration.OfType<IDictionary<string, object>>().FirstOrDefault();
-            var leverage = config is null ? null : DecimalField(config, "leverage");
-
-            for (var index = 0; index < result.Count; index++)
-            {
-                result[index] = result[index] with { Leverage = leverage, Hedged = hedged };
-            }
-
-            foreach (var side in hedged ? new[] { PositionSide.Long, PositionSide.Short } : new[] { PositionSide.Long })
-            {
-                if (!result.Any(p => p.Side == side))
-                {
-                    result.Add(new(key, side, 0, QuantityUnit.Contracts, Leverage: leverage, Hedged: hedged,
-                        MarginMode: config is null ? null : Text(config, "marginType")));
-                }
-            }
-        }
-
-        return result;
-    }
-
-    /// <summary>Maps hedge and one-way semantics, including Binance's prohibition on the reduceOnly flag in hedge mode.</summary>
-    internal static Dictionary<string, object> FuturesOrderParameters(string exchange, PlaceOrderRequest request, bool hedged)
-    {
-        var parameters = new Dictionary<string, object>
-        {
-            ["clientOrderId"] = request.ClientOrderId,
-            ["timeInForce"] = request.PostOnly ? exchange == "binance" ? "GTX" : "PostOnly" : "GTC"
-        };
-
-        if (exchange == "binance")
-        {
-            parameters["positionSide"] = hedged
-                ? request.PositionSide == PositionSide.Long ? "LONG" : "SHORT"
-                : "BOTH";
-
-            if (!hedged)
-            {
-                parameters["reduceOnly"] = request.ReduceOnly;
-            }
-        }
-        else
-        {
-            parameters["category"] = "linear";
-            parameters["positionIdx"] = hedged ? request.PositionSide == PositionSide.Long ? 1 : 2 : 0;
-            parameters["reduceOnly"] = request.ReduceOnly;
-        }
-
-        return parameters;
-    }
-
-    /// <summary>Obtains the current mode immediately before building the exchange request.</summary>
-    private Task<Dictionary<string, object>> FuturesParametersAsync(PlaceOrderRequest request, CancellationToken ct)
-    {
-        ct.ThrowIfCancellationRequested();
-        if (!_validatedModes.TryRemove(request.ClientOrderId, out var validated) || validated.Request != request)
-        {
-            throw new TradingServiceException(new("ValidationRequired", "The exact futures request must be validated before dispatch."));
-        }
-
-        return Task.FromResult(FuturesOrderParameters(_profile.Exchange, request, validated.Hedged));
-    }
-
-    /// <summary>Rejects ambiguous mode/side combinations and closing requests that could increase exposure.</summary>
-    internal static IReadOnlyList<TradingError> ValidatePositionIntent(PlaceOrderRequest request, IReadOnlyList<PositionSnapshot> positions)
-    {
-        if (positions.Count == 0 || positions.Any(p => p.Hedged is null))
-        {
-            return [new("PositionModeUnknown", "The exchange position mode could not be established.")];
-        }
-
-        var hedged = positions.Any(p => p.Hedged == true);
-        if (hedged != request.PositionSide.HasValue)
-        {
-            return [new("PositionSide", hedged ? "Select Long or Short for hedge mode." : "Use Auto for one-way mode.")];
-        }
-
-        if (request.ReduceOnly)
-        {
-            var target = hedged ? request.PositionSide!.Value
-                : request.Side == OrderSide.Sell ? PositionSide.Long : PositionSide.Short;
-            var available = positions.Where(p => p.Side == target).Sum(p => p.Quantity);
-            var closingSide = target == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
-
-            if (request.Side != closingSide || request.Quantity > available)
-            {
-                return [new("ReduceOnly", "Reduce-only must close an existing position without exceeding its size.")];
-            }
-        }
-
-        return [];
-    }
-
-    /// <summary>Checks real available collateral and current leverage; spot cash checks are not reused for contracts.</summary>
-    public async Task<IReadOnlyList<TradingError>> ValidateFuturesAsync(
-        PlaceOrderRequest request, SpotMarket market, AccountSnapshot balance, CancellationToken ct)
-    {
-        var positions = await PositionsAsync(request.Instrument, ct).ConfigureAwait(false);
-        var errors = ValidatePositionIntent(request, positions);
-        if (errors.Count > 0)
-        {
-            return errors;
-        }
-
-        if (request.ReduceOnly)
-        {
-            _validatedModes[request.ClientOrderId] = (request, positions.Any(p => p.Hedged == true));
-            return [];
-        }
-
-        var leverage = positions.Where(p => !request.PositionSide.HasValue || p.Side == request.PositionSide)
-            .Select(p => p.Leverage).FirstOrDefault();
-        var collateral = balance.Margins?.FirstOrDefault(m => m.Currency == "USDT")?.AvailableFunds;
-        var contractSize = market.Instrument.TradingRules?.ContractSize;
-
-        if (leverage is not > 0 || collateral is null || contractSize is not > 0 || market.FeeRate is null)
-        {
-            return [new("MarginUnavailable", "Available USDT margin, leverage or contract metadata is unknown.")];
-        }
-
-        var ticker = await _client.FetchTicker(request.Instrument.SecurityId, Parameters()).WaitAsync(ct).ConfigureAwait(false);
-        var reference = SpotOrderMath.Number(ticker.last);
-        if (reference is not > 0)
-        {
-            return [new("PriceUnavailable", "A fresh contract price is required for the margin check.")];
-        }
-
-        var required = RequiredMargin(request.Quantity, contractSize.Value, request.Price!.Value,
-            reference.Value, leverage.Value, market.FeeRate.Value, request.Side);
-        if (collateral < required)
-        {
-            return [new("InsufficientMargin", "Insufficient available USDT margin, including commission coverage.")];
-        }
-
-        _validatedModes[request.ClientOrderId] = (request, positions.Any(p => p.Hedged == true));
-        return [];
-    }
-
-    /// <summary>Reserves initial margin plus opening/closing commission at the higher current or limit price.</summary>
-    internal static decimal RequiredMargin(decimal quantity, decimal contractSize, decimal price,
-        decimal reference, decimal leverage, decimal fee, OrderSide side = OrderSide.Buy)
-    {
-        if (quantity <= 0 || contractSize <= 0 || price <= 0 || reference <= 0 || leverage <= 0 || fee < 0)
-        {
-            throw new ArgumentOutOfRangeException(nameof(quantity));
-        }
-
-        var notional = quantity * contractSize * Math.Max(price, reference);
-        var openingLoss = quantity * contractSize * Math.Max(0, side == OrderSide.Buy ? price - reference : reference - price);
-        return notional / leverage + notional * fee * 2 + openingLoss;
-    }
-
-    /// <summary>Reads derivative collateral; Bybit multi-coin and portfolio margin remain explicitly unavailable.</summary>
-    private async Task<AccountSnapshot> FuturesBalanceAsync(TradingAccountContext account, CancellationToken ct)
-    {
-        var balances = await _client.FetchBalance(Parameters()).WaitAsync(ct).ConfigureAwait(false);
-        decimal? available = null;
-
-        if (_profile.Exchange == "binance")
-        {
-            available = SpotOrderMath.Number(balances.free.GetValueOrDefault("USDT"));
-        }
-        else if (balances.info?.GetValueOrDefault("result") is IDictionary<string, object> result &&
-                 result.TryGetValue("list", out var walletList) && walletList is IEnumerable wallets)
-        {
-            foreach (var wallet in wallets.Cast<IDictionary<string, object>>())
-            {
-                var coins = ((IEnumerable)wallet["coin"]).Cast<IDictionary<string, object>>().ToArray();
-                var usdt = coins.FirstOrDefault(c => Text(c, "coin") == "USDT");
-                var otherCollateral = coins.Any(c => Text(c, "coin") != "USDT" && DecimalField(c, "equity") is not (null or 0) &&
-                    (!bool.TryParse(Text(c, "collateralSwitch"), out var enabled) || enabled));
-
-                if (usdt is not null && !otherCollateral && DecimalField(usdt, "equity") is > 0 &&
-                    DecimalField(usdt, "usdValue") is > 0 && DecimalField(wallet, "totalAvailableBalance") is { } dollars)
-                {
-                    available = dollars * DecimalField(usdt, "equity") / DecimalField(usdt, "usdValue");
-                }
-            }
-        }
-
-        var snapshot = MapBalance(balances, account);
-        return snapshot with
-        {
-            Margins = [new MarginSnapshot("USDT", AvailableFunds: available)],
-            Positions = null
-        };
-    }
-}
diff --git a/BotView/Services/Integrations/Ccxt/CcxtTradingClient.cs b/BotView/Services/Integrations/Ccxt/CcxtTradingClient.cs
new file mode 100644
index 0000000..08b30ac
--- /dev/null
+++ b/BotView/Services/Integrations/Ccxt/CcxtTradingClient.cs
@@ -0,0 +1,185 @@
+using System.Globalization;
+using BotView.Models;
+
+namespace BotView.Services.Integrations.Ccxt;
+
+/// <summary>Thin, stateless boundary around a configured CCXT exchange. Responses remain native CCXT types.</summary>
+public sealed class CcxtTradingClient : IAsyncDisposable
+{
+    private readonly ccxt.Exchange _exchange;
+    private readonly string _venue;
+    internal ccxt.Exchange Transport => _exchange;
+
+    /// <summary>Uses an already configured transport; construction never makes a network request.</summary>
+    public CcxtTradingClient(ccxt.Exchange exchange, string venue)
+    {
+        _exchange = exchange;
+        _venue = venue;
+    }
+
+    /// <summary>Loads the public instrument catalog using CCXT caching.</summary>
+    public Task<Dictionary<string, ccxt.MarketInterface>> LoadMarketsAsync(CancellationToken ct = default) =>
+        CallAsync(() => _exchange.LoadMarkets(), ct);
+
+    /// <summary>Returns whether the configured transport supports a native operation.</summary>
+    public bool Supports(string operation) => _exchange.has.GetValueOrDefault(operation) is true;
+
+    /// <summary>Formats amount precision using the loaded market metadata.</summary>
+    public decimal AmountToPrecision(string symbol, decimal amount) => decimal.Parse(
+        _exchange.amountToPrecision(symbol, amount.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);
+
+    /// <summary>Formats price precision using the loaded market metadata.</summary>
+    public decimal PriceToPrecision(string symbol, decimal price) => decimal.Parse(
+        _exchange.priceToPrecision(symbol, price.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);
+
+    /// <summary>Returns the precision convention required to map market metadata.</summary>
+    public int PrecisionMode => Convert.ToInt32(_exchange.precisionMode);
+
+    /// <summary>Creates one spot limit order, optionally maker-only.</summary>
+    public Task<ccxt.Order> CreateSpotLimitOrderAsync(string symbol, string side, decimal amount, decimal price,
+        bool postOnly = false, CancellationToken ct = default) =>
+        CallAsync(() => _exchange.CreateOrder(symbol, "limit", side, (double)amount, (double)price, SpotLimitParameters(postOnly)), ct);
+
+    /// <summary>Creates one spot market order with a base-asset quantity and no limit price.</summary>
+    public Task<ccxt.Order> CreateSpotMarketOrderAsync(string symbol, string side, decimal amount, CancellationToken ct = default) =>
+        CallAsync(() => _exchange.CreateOrder(symbol, "market", side, (double)amount, null, SpotParameters()), ct);
+
+    /// <summary>Creates one USDT contract limit order with explicit position parameters.</summary>
+    public Task<ccxt.Order> CreateUsdtFuturesLimitOrderAsync(string symbol, string side, decimal contracts, decimal price,
+        bool postOnly = false, bool reduceOnly = false, PositionSide? positionSide = null, CancellationToken ct = default)
+    {
+        var parameters = FuturesOrderParameters(reduceOnly, positionSide);
+        parameters["timeInForce"] = postOnly ? _venue == "binance" ? "GTX" : "PostOnly" : "GTC";
+        return CallAsync(() => _exchange.CreateOrder(symbol, "limit", side, (double)contracts, (double)price, parameters), ct);
+    }
+
+    /// <summary>Creates one USDT contract market order without a price or maker-only flag.</summary>
+    public Task<ccxt.Order> CreateUsdtFuturesMarketOrderAsync(string symbol, string side, decimal contracts,
+        bool reduceOnly = false, PositionSide? positionSide = null, CancellationToken ct = default) =>
+        CallAsync(() => _exchange.CreateOrder(symbol, "market", side, (double)contracts, null,
+            FuturesOrderParameters(reduceOnly, positionSide)), ct);
+
+    /// <summary>Builds spot routing without derivative-only options.</summary>
+    private Dictionary<string, object> SpotParameters() => _venue == "bybit"
+        ? new() { ["category"] = "spot" } : new();
+
+    /// <summary>Builds maker parameters without changing a rejected maker request into a taker order.</summary>
+    private Dictionary<string, object> SpotLimitParameters(bool postOnly)
+    {
+        var parameters = SpotParameters();
+        if (postOnly) parameters["postOnly"] = true;
+        else parameters["timeInForce"] = "GTC";
+        return parameters;
+    }
+
+    /// <summary>Selects linear USDT routing; exchange-specific options stay at this boundary.</summary>
+    private Dictionary<string, object> UsdtFuturesParameters() => _venue == "binance"
+        ? new() { ["type"] = "future" } : new() { ["category"] = "linear", ["type"] = "swap" };
+
+    /// <summary>Maps the user's position selection directly; the exchange checks the account's position mode.</summary>
+    private Dictionary<string, object> FuturesOrderParameters(bool reduceOnly, PositionSide? side)
+    {
+        var parameters = UsdtFuturesParameters();
+        if (_venue == "binance")
+        {
+            parameters["positionSide"] = side == PositionSide.Long ? "LONG" : side == PositionSide.Short ? "SHORT" : "BOTH";
+            if (reduceOnly) parameters["reduceOnly"] = true;
+        }
+        else
+        {
+            parameters["positionIdx"] = side == PositionSide.Long ? 1 : side == PositionSide.Short ? 2 : 0;
+            if (reduceOnly) parameters["reduceOnly"] = true;
+        }
+        return parameters;
+    }
+
+    /// <summary>Cancels a spot order by exchange ID without assuming it was fully unfilled.</summary>
+    public Task<ccxt.Order> CancelSpotOrderAsync(string id, string symbol, CancellationToken ct = default) =>
+        CallAsync(() => _exchange.CancelOrder(id, symbol, SpotParameters()), ct);
+
+    /// <summary>Cancels a contract order using its linear account route.</summary>
+    public Task<ccxt.Order> CancelUsdtFuturesOrderAsync(string id, string symbol, CancellationToken ct = default) =>
+        CallAsync(() => _exchange.CancelOrder(id, symbol, UsdtFuturesParameters()), ct);
+
+    /// <summary>Fetches one spot order by its exchange ID.</summary>
+    public Task<ccxt.Order> FetchSpotOrderAsync(string id, string symbol, CancellationToken ct = default) =>
+        CallAsync(() => _exchange.FetchOrder(id, symbol, SpotParameters()), ct);
+
+    /// <summary>Fetches one contract order by its exchange ID.</summary>
+    public Task<ccxt.Order> FetchUsdtFuturesOrderAsync(string id, string symbol, CancellationToken ct = default) =>
+        CallAsync(() => _exchange.FetchOrder(id, symbol, UsdtFuturesParameters()), ct);
+
+    /// <summary>Fetches the current spot open-order snapshot using CCXT pagination.</summary>
+    public Task<List<ccxt.Order>> FetchSpotOpenOrdersAsync(string symbol, CancellationToken ct = default)
+    {
+        var parameters = SpotParameters();
+        parameters["paginate"] = true;
+        return CallAsync(() => _exchange.FetchOpenOrders(symbol, null, null, parameters), ct);
+    }
+
+    /// <summary>Fetches the current contract open-order snapshot using CCXT pagination.</summary>
+    public Task<List<ccxt.Order>> FetchUsdtFuturesOpenOrdersAsync(string symbol, CancellationToken ct = default)
+    {
+        var parameters = UsdtFuturesParameters();
+        parameters["paginate"] = true;
+        return CallAsync(() => _exchange.FetchOpenOrders(symbol, null, null, parameters), ct);
+    }
+
+    /// <summary>Fetches recent spot executions; this is a display window, not a durable history.</summary>
+    public Task<List<ccxt.Trade>> FetchSpotMyTradesAsync(string symbol, CancellationToken ct = default) =>
+        CallAsync(() => _exchange.FetchMyTrades(symbol, null, 100, SpotParameters()), ct);
+
+    /// <summary>Fetches recent executions for a USDT contract.</summary>
+    public Task<List<ccxt.Trade>> FetchUsdtFuturesMyTradesAsync(string symbol, CancellationToken ct = default) =>
+        CallAsync(() => _exchange.FetchMyTrades(symbol, null, 100, UsdtFuturesParameters()), ct);
+
+    /// <summary>Fetches native spot balances.</summary>
+    public Task<ccxt.Balances> FetchSpotBalanceAsync(CancellationToken ct = default) =>
+        CallAsync(() => _exchange.FetchBalance(SpotParameters()), ct);
+
+    /// <summary>Fetches native futures collateral balances.</summary>
+    public Task<ccxt.Balances> FetchUsdtFuturesBalanceAsync(CancellationToken ct = default) =>
+        CallAsync(() => _exchange.FetchBalance(UsdtFuturesParameters()), ct);
+
+    /// <summary>Fetches positions for one USDT contract.</summary>
+    public Task<List<ccxt.Position>> FetchUsdtFuturesPositionsAsync(string symbol, CancellationToken ct = default) =>
+        CallAsync(() => _exchange.FetchPositions(new List<string> { symbol }, UsdtFuturesParameters()), ct);
+
+    /// <summary>Reads one private spot order batch.</summary>
+    public Task<List<ccxt.Order>> WatchSpotOrdersAsync(string symbol, CancellationToken ct = default) =>
+        CallAsync(() => _exchange.WatchOrders(symbol, null, null, SpotParameters()), ct);
+
+    /// <summary>Reads one private contract order batch.</summary>
+    public Task<List<ccxt.Order>> WatchUsdtFuturesOrdersAsync(string symbol, CancellationToken ct = default) =>
+        CallAsync(() => _exchange.WatchOrders(symbol, null, null, UsdtFuturesParameters()), ct);
+
+    /// <summary>Reads one private spot execution batch.</summary>
+    public Task<List<ccxt.Trade>> WatchSpotMyTradesAsync(string symbol, CancellationToken ct = default) =>
+        CallAsync(() => _exchange.WatchMyTrades(symbol, null, null, SpotParameters()), ct);
+
+    /// <summary>Reads one private contract execution batch.</summary>
+    public Task<List<ccxt.Trade>> WatchUsdtFuturesMyTradesAsync(string symbol, CancellationToken ct = default) =>
+        CallAsync(() => _exchange.WatchMyTrades(symbol, null, null, UsdtFuturesParameters()), ct);
+
+    /// <summary>Reads one private spot balance update.</summary>
+    public Task<ccxt.Balances> WatchSpotBalanceAsync(CancellationToken ct = default) =>
+        CallAsync(() => _exchange.WatchBalance(SpotParameters()), ct);
+
+    /// <summary>Reads one private futures balance update.</summary>
+    public Task<ccxt.Balances> WatchUsdtFuturesBalanceAsync(CancellationToken ct = default) =>
+        CallAsync(() => _exchange.WatchBalance(UsdtFuturesParameters()), ct);
+
+    /// <summary>Checks cancellation before dispatch and waits without repeating the native call.</summary>
+    private static Task<T> CallAsync<T>(Func<Task<T>> operation, CancellationToken ct)
+    {
+        ct.ThrowIfCancellationRequested();
+        return operation().WaitAsync(ct);
+    }
+
+    /// <summary>Closes local transports without canceling exchange orders.</summary>
+    public async ValueTask DisposeAsync()
+    {
+        try { await _exchange.Close().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
+        finally { _exchange.apiKey = ""; _exchange.secret = ""; }
+    }
+}
diff --git a/BotView/Services/Integrations/Ccxt/CcxtTradingClientFactory.cs b/BotView/Services/Integrations/Ccxt/CcxtTradingClientFactory.cs
new file mode 100644
index 0000000..b38f921
--- /dev/null
+++ b/BotView/Services/Integrations/Ccxt/CcxtTradingClientFactory.cs
@@ -0,0 +1,37 @@
+using BotView.Models;
+
+namespace BotView.Services.Integrations.Ccxt;
+
+/// <summary>Creates isolated transports and selects the network before any request.</summary>
+public static class CcxtTradingClientFactory
+{
+    /// <summary>Creates a configured spot or linear USDT CCXT.Pro client without connecting.</summary>
+    public static CcxtTradingClient Create(TradingAccountProfile profile, AccountCredentials? credentials = null)
+    {
+        if (!Enum.IsDefined(profile.Market) || !Enum.IsDefined(profile.Environment))
+            throw new NotSupportedException("Unsupported trading market or network.");
+
+        var settings = ProOrderBookClient.CreateSettings();
+        settings["apiKey"] = credentials?.ApiKey ?? "";
+        settings["secret"] = credentials?.Secret ?? "";
+        settings["verbose"] = false;
+        var options = (Dictionary<string, object>)settings["options"];
+        if (profile.Market == TradingMarket.UsdtFutures)
+        {
+            options["defaultType"] = profile.Exchange == "binance" ? "future" : "swap";
+            options["defaultSubType"] = "linear";
+            options["fetchMarkets"] = new Dictionary<string, object> { ["types"] = new List<object> { "linear" } };
+        }
+        else options["defaultType"] = "spot";
+
+        ccxt.Exchange exchange = profile.Exchange switch
+        {
+            "binance" when profile.Market == TradingMarket.UsdtFutures => new ccxt.pro.binanceusdm(settings),
+            "binance" => new ccxt.pro.binance(settings),
+            "bybit" => new ccxt.pro.bybit(settings),
+            _ => throw new NotSupportedException("Trading supports Binance and Bybit only.")
+        };
+        if (profile.Environment == TradingEnvironment.Testnet) exchange.setSandboxMode(true);
+        return new(exchange, profile.Exchange);
+    }
+}
diff --git a/BotView/Services/Integrations/Ccxt/CcxtTradingMapper.cs b/BotView/Services/Integrations/Ccxt/CcxtTradingMapper.cs
new file mode 100644
index 0000000..184dcc0
--- /dev/null
+++ b/BotView/Services/Integrations/Ccxt/CcxtTradingMapper.cs
@@ -0,0 +1,95 @@
+using System.Globalization;
+using BotView.Models;
+
+namespace BotView.Services.Integrations.Ccxt;
+
+/// <summary>Copies native CCXT responses into detached order-book models.</summary>
+internal static class CcxtTradingMapper
+{
+    /// <summary>Preserves provider status rather than treating an asynchronous acknowledgement as an open order.</summary>
+    internal static OrderSnapshot MapOrder(ccxt.Order order, InstrumentKey key) => new(
+        new(key, order.id ?? ""), order.clientOrderId, order.side == "sell" ? OrderSide.Sell : OrderSide.Buy,
+        order.type switch { "market" => OrderType.Market, "stop" or "stop_market" => OrderType.Stop,
+            "stop_limit" => OrderType.StopLimit, _ => OrderType.Limit }, order.status switch { "open" => OrderState.Open, "closed" => OrderState.Filled,
+            "canceled" or "cancelled" => OrderState.Canceled, "expired" => OrderState.Expired, "rejected" => OrderState.Rejected, _ => OrderState.Pending },
+        Number(order.amount) ?? 0, Number(order.filled), Number(order.remaining),
+        Number(order.price), Number(order.average), At(order.timestamp),
+        At(order.lastUpdateTimestamp ?? order.lastTradeTimestamp ?? order.timestamp) ?? DateTimeOffset.MinValue, StatusReason(order),
+        GetPositionSide(order), order.reduceOnly);
+
+    /// <summary>Retains the hedge leg for exchange orders created by this or another application.</summary>
+    private static PositionSide? GetPositionSide(ccxt.Order order)
+    {
+        if (order.info is null)
+        {
+            return null;
+        }
+
+        return Text(order.info, "positionSide") switch
+        {
+            "LONG" => PositionSide.Long,
+            "SHORT" => PositionSide.Short,
+            _ => Text(order.info, "positionIdx") switch
+            {
+                "1" => PositionSide.Long,
+                "2" => PositionSide.Short,
+                _ => null
+            }
+        };
+    }
+
+    /// <summary>Maps an allowlist of provider reason codes; raw rejection text is never shown or persisted.</summary>
+    private static string? StatusReason(ccxt.Order order)
+    {
+        var reason = order.info is null ? null : Text(order.info, "rejectReason") ?? Text(order.info, "cancelType") ?? Text(order.info, "expiryReason");
+        if (reason is "EC_PostOnlyWillTakeLiquidity" or "CancelByPostOnly") return "Post-only would execute immediately.";
+        if (reason is "EC_NoEnoughQtyToFill" or "INSUFFICIENT_LIQUIDITY") return "Insufficient exchange liquidity.";
+        return order.status switch { "canceled" or "cancelled" => "Canceled by the exchange.", "rejected" => "Rejected by the exchange.",
+            "expired" => "Expired at the exchange.", _ => null };
+    }
+
+    /// <summary>Maps a fill independently of its order's current state, retaining the actual commission currency.</summary>
+    private static ExecutionSnapshot MapExecution(ccxt.Trade trade, InstrumentKey key) => new(trade.id ?? "",
+        new(key, trade.order ?? trade.orderId ?? ""), trade.side == "sell" ? OrderSide.Sell : OrderSide.Buy,
+        Number(trade.amount) ?? 0, Number(trade.price) ?? 0,
+        At(trade.timestamp) ?? DateTimeOffset.MinValue,
+        trade.fee is { } fee && fee.currency is not null && Number(fee.cost) is { } cost ? new(cost, fee.currency) : null);
+
+    /// <summary>Copies all currency balances out of the mutable CCXT cache.</summary>
+    private static AccountSnapshot MapBalance(ccxt.Balances balance, TradingAccountContext account) => new(account,
+        balance.balances.Select(p => new AccountBalance(p.Key, Number(p.Value.total),
+            Number(p.Value.free), Number(p.Value.used))).ToArray(), null, null, DateTimeOffset.UtcNow);
+
+    /// <summary>Reads optional detached provider metadata.</summary>
+    private static string? Text(IDictionary<string, object> data, string key) => data.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
+
+
+    /// <summary>Converts an optional exchange timestamp to UTC.</summary>
+    internal static DateTimeOffset? At(long? timestamp) => timestamp.HasValue
+        ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value) : null;
+
+    /// <summary>Converts exchange floating-point values without inventing missing amounts.</summary>
+    internal static decimal? Number(double? number) => number is double value && double.IsFinite(value)
+        ? decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : null;
+
+    /// <summary>Copies a fill, preserving its commission and exchange identity.</summary>
+    internal static ExecutionSnapshot Execution(ccxt.Trade trade, InstrumentKey key) => MapExecution(trade, key);
+
+    /// <summary>Copies balances and USDT collateral reported by the exchange.</summary>
+    internal static AccountSnapshot Balance(ccxt.Balances balance, TradingAccountContext account)
+    {
+        var snapshot = MapBalance(balance, account);
+        return account.Market == TradingMarket.Spot ? snapshot : snapshot with
+        {
+            Margins = [new("USDT", AvailableFunds: Number(balance.free.GetValueOrDefault("USDT")))]
+        };
+    }
+
+    /// <summary>Copies contract quantities, margin metadata and the hedge leg.</summary>
+    internal static PositionSnapshot Position(ccxt.Position position, InstrumentKey key) => new(
+        key, position.side == "short" ? PositionSide.Short : PositionSide.Long,
+        Math.Abs(Number(position.contracts) ?? 0), QuantityUnit.Contracts,
+        Number(position.entryPrice), Number(position.unrealizedPnl) is decimal pnl ? new(pnl, "USDT") : null,
+        Number(position.initialMargin) is decimal margin ? new(margin, "USDT") : null,
+        Number(position.leverage), position.hedged, position.marginMode);
+}
diff --git a/BotView/Services/Integrations/Ccxt/CcxtTradingService.cs b/BotView/Services/Integrations/Ccxt/CcxtTradingService.cs
index d434543..56b2c71 100644
--- a/BotView/Services/Integrations/Ccxt/CcxtTradingService.cs
+++ b/BotView/Services/Integrations/Ccxt/CcxtTradingService.cs
@@ -1,708 +1,150 @@
-﻿using System.Collections.Concurrent;
-using System.Runtime.CompilerServices;
-using System.Threading.Channels;
 using BotView.Models;
-using BotView.Services;
+using static BotView.Services.Integrations.Ccxt.CcxtTradingMapper;
 
 namespace BotView.Services.Integrations.Ccxt;
 
-/// <summary>One account and market's session. Owns recovery, validation and serialized mutations.</summary>
+/// <summary>Converts order-book calls and native CCXT results. Owns no background work or order state.</summary>
 public sealed class CcxtTradingService : ITradingService
 {
-    private static readonly TradingCapabilities SpotCapabilities = new(new HashSet<OrderType> { OrderType.Limit },
-        new HashSet<OrderTimeInForce> { OrderTimeInForce.Gtc }, SupportsPostOnly: true,
-        SupportsLookupByClientOrderId: true, SupportsOrderHistory: true, SupportsExecutions: true);
-    private TradingCapabilities Capabilities => _client.IsFutures
-        ? SpotCapabilities with { SupportsReduceOnly = true, SupportsPositionSide = true, SupportsPositions = true }
-        : SpotCapabilities;
-
-    private readonly ISpotTradingClient _client;
-    private readonly TradingJournal _journal;
-    private readonly string _accountKey;
-    private readonly bool? _canTrade;
-    private readonly SemaphoreSlim _gate = new(1, 1);
-    private readonly object _sync = new();
-    private readonly CancellationTokenSource _stop = new();
-    private readonly ConcurrentDictionary<InstrumentKey, byte> _tracked = new();
-    private readonly ConcurrentDictionary<InstrumentKey, bool> _ready = new();
-    private readonly ConcurrentDictionary<InstrumentKey, SpotMarket> _markets = new();
-    private readonly ConcurrentDictionary<InstrumentKey, DateTimeOffset> _marketTimes = new();
-    private readonly Dictionary<OrderReference, OrderSnapshot> _orders = new();
-    private readonly Dictionary<(InstrumentKey, string), ExecutionSnapshot> _fills = new();
-    private readonly Dictionary<string, TradingOperation> _operations = new();
-    private readonly ConcurrentDictionary<Guid, Channel<TradingUpdate>> _subscribers = new();
-    private readonly List<Task> _streams = new();
-    private readonly Task _worker;
-    private AccountSnapshot? _balance;
-    private string _message = "Synchronizing account…";
-    private TradingConnectionState _connection = TradingConnectionState.Connecting;
-    private bool _balanceDirty = true;
-    private bool _disposed;
-    private Task? _disposeTask;
-    private int _resyncGeneration;
-
+    private readonly CcxtTradingClient _client;
+    private bool IsFutures => Account.Market == TradingMarket.UsdtFutures;
     public TradingAccountContext Account { get; }
-    internal bool? KeyCanTrade => _canTrade;
-
-    /// <summary>Immediately prevents further mutations when account credentials change.</summary>
-    internal void Suspend() { _disposed = true; _stop.Cancel(); }
-
-    /// <summary>Checks pair readiness independently of other windows' tracked markets.</summary>
-    public bool IsReady(InstrumentKey key) => !_disposed && _ready.GetValueOrDefault(key) && Current.Ready;
-
-    /// <summary>Validates cached limits and precision for immediate input feedback without network requests.</summary>
-    public OrderValidationResult ValidateInput(PlaceOrderRequest request)
-    {
-        if (!_markets.TryGetValue(request.Instrument, out var market)) return new([new("MetadataRequired", "Wait for trading metadata.")]);
-        var errors = TradingOrderValidator.Validate(request, market.Instrument, Capabilities, Account, DateTimeOffset.UtcNow).Errors.ToList();
-        try
-        {
-            if (request.Quantity > 0 && _client.NormalizeAmount(market.Instrument.Symbol, request.Quantity) != request.Quantity) errors.Add(new("QuantityPrecision", "Quantity does not match exchange precision."));
-            if (request.Price is > 0 && _client.NormalizePrice(market.Instrument.Symbol, request.Price.Value) != request.Price) errors.Add(new("PricePrecision", "Price does not match exchange precision."));
-        }
-        catch (Exception) { errors.Add(new("Precision", "The entered values cannot be represented at the exchange precision.")); }
-        if (_canTrade == false) errors.Add(new("TradingDenied", "Trading is denied for this API key."));
-        return new(errors);
-    }
 
-    /// <summary>Restores durable state only after the provider has authenticated this exact account/network.</summary>
-    internal CcxtTradingService(TradingAccountProfile profile, ISpotTradingClient client, TradingJournal journal, string identity, bool? canTrade)
+    /// <summary>Binds a configured CCXT boundary to a single profile and network.</summary>
+    public CcxtTradingService(TradingAccountProfile profile, CcxtTradingClient client)
     {
         _client = client;
-        _journal = journal;
-        _accountKey = profile.Market == TradingMarket.Spot
-            ? $"{profile.Exchange}:{profile.Environment}:{identity}"
-            : $"{profile.Exchange}:{profile.Environment}:{profile.Market}:{identity}";
-        Account = new(profile.Id.ToString(), identity, profile.Exchange, profile.Environment, profile.Market);
-        _canTrade = canTrade;
-        foreach (var order in journal.Orders(_accountKey)) _orders[order.Reference] = order;
-        foreach (var fill in journal.Executions(_accountKey)) _fills[(fill.Order.Instrument, fill.ExecutionId)] = fill;
-        foreach (var operation in journal.Operations(_accountKey))
-        {
-            var restored = operation.Phase == TradingOperationPhase.Dispatching ? operation with { Phase = TradingOperationPhase.Unknown } : operation;
-            _operations[operation.Id] = restored;
-            if (restored.Phase == TradingOperationPhase.Unknown) _tracked.TryAdd(restored.Request.Instrument, 0);
-        }
-        foreach (var order in _orders.Values.Where(o => !Terminal(o.State))) _tracked.TryAdd(order.Reference.Instrument, 0);
-        _worker = Task.Run(RunAsync);
-    }
-
-    /// <summary>Returns a detached state; views can coalesce updates on their existing 50 ms timer.</summary>
-    public SpotTradingState Current
-    {
-        get
-        {
-            lock (_sync) return new(_connection, _message, _orders.Values.ToArray(), _fills.Values.OrderByDescending(f => f.ExecutedAt).ToArray(),
-                _balance, _operations.Values.Where(Pending).ToArray(), !_disposed && !_balanceDirty && _connection == TradingConnectionState.Connected);
-        }
-    }
-
-    /// <summary>Adds a pair for ongoing supervision; window closure deliberately does not untrack live orders.</summary>
-    public async Task TrackAsync(InstrumentKey key, CancellationToken ct = default)
-    {
-        _tracked.TryAdd(key, 0);
-        await _gate.WaitAsync(ct).ConfigureAwait(false);
-        try
-        {
-            await MarketCoreAsync(key, ct).ConfigureAwait(false);
-            StartStreams(key);
-            await ReconcileCoreAsync(key, ct).ConfigureAwait(false);
-        }
-        catch (Exception ex) { MarkDisconnected(ex); throw Safe(ex); }
-        finally { _gate.Release(); }
-    }
-
-    /// <summary>Converts quote input through the exchange's quantity precision without rounding the price.</summary>
-    public decimal ConvertQuantity(InstrumentKey key, decimal input, decimal price, OrderQuantityCurrency currency) =>
-        SpotOrderMath.Quantity(input, price * (_markets.GetValueOrDefault(key)?.Instrument.TradingRules?.ContractSize ?? 1m), currency, value => _client.NormalizeAmount(key.SecurityId, value));
-
-    /// <summary>Values native contract quantities using the exchange contract multiplier.</summary>
-    public decimal OrderNotional(InstrumentKey key, decimal quantity, decimal price) =>
-        quantity * price * (_markets.GetValueOrDefault(key)?.Instrument.TradingRules?.ContractSize ?? 1m);
-
-    /// <summary>Loads market metadata within the account request gate.</summary>
-    public async Task<Instrument> GetTradingInstrumentAsync(InstrumentKey instrument, CancellationToken ct = default)
-    {
-        await _gate.WaitAsync(ct).ConfigureAwait(false);
-        try { return (await MarketCoreAsync(instrument, ct).ConfigureAwait(false)).Instrument; }
-        catch (Exception ex) { throw Safe(ex); }
-        finally { _gate.Release(); }
-    }
-
-    /// <summary>Advertises only the spot limit functionality implemented by this adapter.</summary>
-    public async Task<TradingCapabilities> GetCapabilitiesAsync(InstrumentKey instrument, CancellationToken ct = default)
-    {
-        await GetTradingInstrumentAsync(instrument, ct).ConfigureAwait(false);
-        return Capabilities;
-    }
-
-    /// <summary>Runs a preview check; placement repeats it under the same account mutation gate.</summary>
-    public async Task<OrderValidationResult> ValidateOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)
-    {
-        await _gate.WaitAsync(ct).ConfigureAwait(false);
-        try { return await ValidateCoreAsync(request, ct).ConfigureAwait(false); }
-        catch (Exception ex) { throw Safe(ex); }
-        finally { _gate.Release(); }
-    }
-
-    /// <summary>Records a unique intent durably, then dispatches once; uncertainty never causes automatic resubmission.</summary>
-    public async Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)
-    {
-        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
-        ct = lifetime.Token;
-        await _gate.WaitAsync(ct).ConfigureAwait(false);
-        TradingOperation? operation = null;
-        var dispatched = false;
-        var responded = false;
-        try
-        {
-            ObjectDisposedException.ThrowIf(_disposed, this);
-            lock (_sync) if (_operations.Values.Any(o => o.Request.ClientOrderId == request.ClientOrderId))
-                return new(PlaceOrderStatus.Rejected, request.ClientOrderId, request.Instrument, Error: new("DuplicateIntent", "This client order ID has already been used."));
-            var validation = await ValidateCoreAsync(request, ct).ConfigureAwait(false);
-            if (!validation.IsValid) return new(PlaceOrderStatus.Rejected, request.ClientOrderId, request.Instrument, Error: validation.Errors[0]);
-            operation = new(request.ClientOrderId, _accountKey, request, TradingOperationPhase.Prepared, DateTimeOffset.UtcNow);
-            Save(operation);
-            ct.ThrowIfCancellationRequested();
-            operation = operation with { Phase = TradingOperationPhase.Dispatching };
-            Save(operation);
-            lock (_sync) _balanceDirty = true;
-            dispatched = true;
-            var order = await _client.PlaceAsync(request, ct).ConfigureAwait(false);
-            responded = true;
-            if (!string.IsNullOrWhiteSpace(order.Reference.ExchangeOrderId)) Merge(order);
-            operation = operation with { ExchangeOrderId = order.Reference.ExchangeOrderId,
-                Phase = order.State is OrderState.Open || Terminal(order.State) ? TradingOperationPhase.Resolved : TradingOperationPhase.Unknown };
-            Save(operation);
-            // Only an authoritative balance fetched after the order observation can release the local spend barrier.
-            try { await RefreshBalanceCoreAsync(_stop.Token).ConfigureAwait(false); }
-            catch (Exception) { MarkDisconnected(null); }
-            return new(order.State == OrderState.Rejected ? PlaceOrderStatus.Rejected : operation.Phase == TradingOperationPhase.Resolved ? PlaceOrderStatus.Accepted : PlaceOrderStatus.Submitted,
-                request.ClientOrderId, request.Instrument, order.Reference.ExchangeOrderId, Order: order);
-        }
-        catch (Exception ex)
-        {
-            if (dispatched && operation is not null)
-            {
-                var rejected = !responded && DefiniteRejection(ex);
-                RecordFailure(operation, rejected);
-                return new(rejected ? PlaceOrderStatus.Rejected : PlaceOrderStatus.Unknown, request.ClientOrderId, request.Instrument,
-                    Error: rejected ? Error(ex) : new("ResultUnknown", "No definitive order outcome. Reconciling the original client ID; no automatic resend."));
-            }
-            if (ex is OperationCanceledException) throw;
-            return new(PlaceOrderStatus.Rejected, request.ClientOrderId, request.Instrument, Error: Error(ex));
-        }
-        finally { _gate.Release(); }
-    }
-
-    /// <summary>Persists cancellation uncertainty independently of the order's partial executions.</summary>
-    public async Task<CancelOrderResult> CancelOrderAsync(OrderReference order, CancellationToken ct = default)
-    {
-        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
-        ct = lifetime.Token;
-        await _gate.WaitAsync(ct).ConfigureAwait(false);
-        TradingOperation? operation = null;
-        try
-        {
-            ObjectDisposedException.ThrowIf(_disposed, this);
-            await MarketCoreAsync(order.Instrument, ct).ConfigureAwait(false);
-            if (_canTrade == false) return new(CancelOrderStatus.Rejected, order, Error: new("TradingDenied", "This API key cannot cancel orders."));
-            var known = await _client.OrderAsync(order, ct).ConfigureAwait(false);
-            if (known is not null)
-            {
-                Merge(known);
-                if (Terminal(known.State)) return new(CancelOrderStatus.AlreadyClosed, order, Order: known);
-            }
-            lock (_sync) if (_operations.Values.Any(o => Pending(o) && o.CancelTarget == order))
-                return new(CancelOrderStatus.Unknown, order, Error: new("CancelPending", "An earlier cancellation is still being reconciled."));
-            var request = new PlaceOrderRequest(order.Instrument, known?.Side ?? OrderSide.Buy, OrderType.Limit,
-                known?.Quantity ?? 0, known?.ClientOrderId ?? "", known?.Price);
-            operation = new("cancel:" + Guid.NewGuid().ToString("N"), _accountKey, request, TradingOperationPhase.Prepared, DateTimeOffset.UtcNow, order);
-            Save(operation);
-            ct.ThrowIfCancellationRequested();
-            operation = operation with { Phase = TradingOperationPhase.Dispatching };
-            Save(operation);
-            var result = await _client.CancelAsync(order, ct).ConfigureAwait(false);
-            if (result is not null && result.Quantity > 0) Merge(result);
-            var final = result is not null && Terminal(result.State);
-            Save(operation with { Phase = final ? TradingOperationPhase.Resolved : TradingOperationPhase.Unknown });
-            lock (_sync) _balanceDirty = true;
-            return new(final ? result!.State == OrderState.Filled ? CancelOrderStatus.AlreadyClosed : CancelOrderStatus.Canceled : CancelOrderStatus.Submitted, order, Order: result);
-        }
-        catch (Exception ex)
-        {
-            if (operation?.Phase == TradingOperationPhase.Dispatching)
-            {
-                if (ex is ccxt.PermissionDenied or ccxt.AuthenticationError)
-                {
-                    RecordFailure(operation, true);
-                    return new(CancelOrderStatus.Rejected, order, Error: Error(ex));
-                }
-                RecordFailure(operation, false);
-                return new(CancelOrderStatus.Unknown, order, Error: new("CancelUnknown", "Cancellation outcome is unknown; orders and executions will be reconciled."));
-            }
-            if (ex is OperationCanceledException) throw;
-            return new(CancelOrderStatus.Rejected, order, Error: Error(ex));
-        }
-        finally { _gate.Release(); }
-    }
-
-    /// <summary>Captures every current order ID before the view requests confirmation.</summary>
-    public async Task<CancelBatch> PrepareCancelAllAsync(InstrumentKey key, CancellationToken ct = default)
-    {
-        var orders = new List<OrderSnapshot>();
-        string? cursor = null;
-        var cursors = new HashSet<string>();
-        do
-        {
-            var page = await GetOpenOrdersAsync(new(key, Cursor: cursor), ct).ConfigureAwait(false);
-            orders.AddRange(page.Items);
-            cursor = page.NextCursor;
-            if (cursor is not null && !cursors.Add(cursor)) throw new TradingServiceException(new("Pagination", "Could not capture a complete cancellation snapshot."));
-        } while (cursor is not null);
-        if (orders.Any(o => string.IsNullOrWhiteSpace(o.Reference.ExchangeOrderId)))
-            throw new TradingServiceException(new("OrderIdentityMissing", "An exchange order has no usable identity; cancellation snapshot is incomplete."));
-        return new(Account, key, orders.Where(o => !Terminal(o.State)).Select(o => o.Reference).Distinct().ToArray());
-    }
-
-    /// <summary>Cancels only confirmed snapshot IDs and returns every individual outcome, including partial failure.</summary>
-    public async Task<IReadOnlyList<CancelOrderResult>> CancelBatchAsync(CancelBatch batch, CancellationToken ct = default)
-    {
-        if (batch.Account != Account || batch.Orders.Any(o => o.Instrument != batch.Instrument)) throw new ArgumentException("Cancel batch account mismatch.");
-        var results = new List<CancelOrderResult>();
-        foreach (var order in batch.Orders) results.Add(await CancelOrderAsync(order, ct).ConfigureAwait(false));
-        return results;
-    }
-
-    /// <summary>Reads an authoritative order without leaking provider exceptions.</summary>
-    public Task<OrderSnapshot?> GetOrderAsync(OrderReference order, CancellationToken ct = default) => ReadInstrumentAsync(order.Instrument, () => _client.OrderAsync(order, ct), ct);
-
-    /// <summary>Missing history is not used as proof that an uncertain placement failed.</summary>
-    public Task<OrderSnapshot?> FindOrderByClientIdAsync(InstrumentKey instrument, string clientOrderId, CancellationToken ct = default) => ReadInstrumentAsync(instrument, () => _client.FindAsync(instrument, clientOrderId, ct), ct);
-
-    /// <summary>Exposes provider continuation rather than silently truncating open orders.</summary>
-    public Task<TradingPage<OrderSnapshot>> GetOpenOrdersAsync(TradingQuery query, CancellationToken ct = default) => ReadInstrumentAsync(query.Instrument, () => _client.OrdersAsync(query, true, ct), ct);
-
-    /// <summary>Exposes paged order history in application models.</summary>
-    public Task<TradingPage<OrderSnapshot>> GetOrderHistoryAsync(TradingQuery query, CancellationToken ct = default) => ReadInstrumentAsync(query.Instrument, () => _client.OrdersAsync(query, false, ct), ct);
-
-    /// <summary>Exposes paged executions with stable fill identities.</summary>
-    public Task<TradingPage<ExecutionSnapshot>> GetExecutionsAsync(TradingQuery query, CancellationToken ct = default) => ReadInstrumentAsync(query.Instrument, () => _client.ExecutionsAsync(query, ct), ct);
-
-    /// <summary>Returns a fresh account snapshot rather than substituting an unavailable section with zeroes.</summary>
-    public Task<AccountSnapshot> GetAccountStateAsync(CancellationToken ct = default) => ReadAsync(() => _client.BalanceAsync(Account, ct), ct);
-
-    /// <summary>Spot holdings are balances; derivative position operations are not supported.</summary>
-    public Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(InstrumentKey? instrument = null, CancellationToken ct = default) => ReadAsync(() => _client.PositionsAsync(instrument, ct), ct);
-
-    /// <summary>Broadcasts independent bounded streams; a slow reader is instructed to resynchronize after overflow.</summary>
-    public async IAsyncEnumerable<TradingUpdate> WatchUpdatesAsync([EnumeratorCancellation] CancellationToken ct = default)
-    {
-        var id = Guid.NewGuid();
-        var channel = Channel.CreateBounded<TradingUpdate>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.Wait });
-        _subscribers[id] = channel;
-        channel.Writer.TryWrite(new TradingConnectionChanged(Current.Connection, DateTimeOffset.UtcNow, true));
-        try { await foreach (var update in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return update; }
-        finally { _subscribers.TryRemove(id, out _); }
-    }
-
-    /// <summary>Serializes REST operations while allowing independent private-stream waits.</summary>
-    private async Task<T> ReadAsync<T>(Func<Task<T>> read, CancellationToken ct)
-    {
-        await _gate.WaitAsync(ct).ConfigureAwait(false);
-        try { ObjectDisposedException.ThrowIf(_disposed, this); return await read().ConfigureAwait(false); }
-        catch (Exception ex) { throw Safe(ex); }
-        finally { _gate.Release(); }
-    }
-
-    /// <summary>Ensures listing metadata exists even when a contract method is used before a book is opened.</summary>
-    private Task<T> ReadInstrumentAsync<T>(InstrumentKey? key, Func<Task<T>> read, CancellationToken ct) => ReadAsync(async () =>
-    {
-        if (key is null) throw new NotSupportedException("A spot instrument is required.");
-        await MarketCoreAsync(key, ct).ConfigureAwait(false);
-        return await read().ConfigureAwait(false);
-    }, ct);
-
-    /// <summary>Caches spot metadata after successful loading under the account gate.</summary>
-    private async Task<SpotMarket> MarketCoreAsync(InstrumentKey key, CancellationToken ct)
-    {
-        if (_markets.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - _marketTimes[key] < TimeSpan.FromMinutes(5)) return cached;
-        var market = await _client.MarketAsync(key, ct).ConfigureAwait(false);
-        _markets[key] = market;
-        _marketTimes[key] = DateTimeOffset.UtcNow;
-        return market;
-    }
-
-    /// <summary>Checks limits, precision, permission and fresh funds immediately before dispatch.</summary>
-    private async Task<OrderValidationResult> ValidateCoreAsync(PlaceOrderRequest request, CancellationToken ct)
-    {
-        var market = await MarketCoreAsync(request.Instrument, ct).ConfigureAwait(false);
-        var errors = ValidateInput(request).Errors.ToList();
-        if (request.ClientOrderId.Length > 36 || request.ClientOrderId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) errors.Add(new("ClientOrderId", "Use a unique client ID of at most 36 ASCII characters."));
-        if (!_ready.GetValueOrDefault(request.Instrument) || _disposed) errors.Add(new("Synchronizing", "Wait for the account and this pair to synchronize."));
-        TradingOperation[] pending;
-        lock (_sync) pending = _operations.Values.Where(Pending).ToArray();
-        if (pending.Any(o => o.Request.Instrument == request.Instrument)) errors.Add(new("UnknownOperation", "An operation on this pair is still being reconciled; it will not be resent."));
-        var spendCurrency = _client.IsFutures ? "USDT" : request.Side == OrderSide.Buy ? market.Quote : market.Base;
-        // When an unknown order may already be reflected in free balance, subtraction could count it twice.
-        // Block competing spending in its currency until order+balance reconciliation establishes the truth.
-        if (pending.Any(o => _client.IsFutures ||
-            _markets.TryGetValue(o.Request.Instrument, out var m) &&
-            (o.Request.Side == OrderSide.Buy ? m.Quote : m.Base) == spendCurrency))
-            errors.Add(new("FundsUncertain", "Funds in this currency are awaiting reconciliation of an earlier operation."));
-        if (errors.Count > 0) return new(errors);
-        errors.AddRange(await _client.ValidateMarketAsync(request, ct).ConfigureAwait(false));
-        if (errors.Count > 0) return new(errors);
-        if (_balanceDirty || _balance is null || DateTimeOffset.UtcNow - _balance.AsOf > TimeSpan.FromSeconds(5))
-            await RefreshBalanceCoreAsync(ct).ConfigureAwait(false);
-        if (_client.IsFutures)
-        {
-            errors.AddRange(await _client.ValidateFuturesAsync(request, market, _balance!, ct).ConfigureAwait(false));
-            return new(errors);
-        }
-
-        if (market.FeeRate is null) errors.Add(new("FeeUnavailable", "Trading fees could not be established."));
-        else
-        {
-            // Conservative coverage permits commissions in either input asset without assuming discounts or borrowing.
-            var feeInSpend = market.FeeSide switch { "get" => false, "base" => request.Side == OrderSide.Sell,
-                "quote" => request.Side == OrderSide.Buy, _ => true };
-            var required = (request.Side == OrderSide.Buy ? request.Quantity * request.Price!.Value : request.Quantity) * (1 + (feeInSpend ? market.FeeRate.Value : 0));
-            var available = _balance?.Balances?.FirstOrDefault(b => b.Currency == spendCurrency)?.Available;
-            if (available is null) errors.Add(new("BalanceUnavailable", $"Available {spendCurrency} balance is unknown."));
-            else if (available < required) errors.Add(new("InsufficientFunds", $"Insufficient available {spendCurrency}, including commission coverage."));
-        }
-        return new(errors);
-    }
-
-    /// <summary>Refreshes all tracked pairs after stream disruption, with capped exponential reconnect delay.</summary>
-    private async Task RunAsync()
-    {
-        var retry = 1;
-        while (!_stop.IsCancellationRequested)
-        {
-            try
-            {
-                foreach (var key in _tracked.Keys)
-                {
-                    await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
-                    try
-                    {
-                        await MarketCoreAsync(key, _stop.Token).ConfigureAwait(false);
-                        StartStreams(key);
-                        await ReconcileCoreAsync(key, _stop.Token).ConfigureAwait(false);
-                    }
-                    finally { _gate.Release(); }
-                }
-                retry = 1;
-                await Task.Delay(2000, _stop.Token).ConfigureAwait(false);
-            }
-            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
-            catch (Exception ex)
-            {
-                MarkDisconnected(ex);
-                try { await Task.Delay(TimeSpan.FromSeconds(retry), _stop.Token).ConfigureAwait(false); }
-                catch (OperationCanceledException) { break; }
-                retry = Math.Min(30, retry * 2);
-            }
-        }
-    }
-
-    private readonly HashSet<(string, InstrumentKey?)> _started = new();
-
-    /// <summary>Starts private watchers before REST snapshots; reducers safely merge buffered and later events.</summary>
-    private void StartStreams(InstrumentKey key)
-    {
-        foreach (var kind in new[] { "watchOrders", "watchMyTrades", "watchBalance" })
-        {
-            var identity = (kind, kind == "watchBalance" ? null : key);
-            if (!_client.SupportsStream(kind) || !_started.Add(identity)) continue;
-            _streams.Add(Task.Run(() => WatchCoreAsync(kind, key)));
-        }
-    }
-
-    /// <summary>Retries failed streams while REST reconciliation remains the authority for readiness.</summary>
-    private async Task WatchCoreAsync(string kind, InstrumentKey key)
-    {
-        var retry = 1;
-        while (!_stop.IsCancellationRequested)
-        {
-            try
-            {
-                foreach (var update in await _client.WatchAsync(kind, key, Account, _stop.Token).ConfigureAwait(false))
-                {
-                    if (update is OrderChanged order) Merge(order.Order);
-                    else if (update is ExecutionReceived fill) Merge(fill.Execution);
-                    else if (update is AccountChanged balance)
-                    {
-                        lock (_sync)
-                        {
-                            if (_balance is null || balance.AsOf >= _balance.AsOf) _balance = balance.Account;
-                            _balanceDirty = true;
-                        }
-                        Publish(update);
-                    }
-                }
-                retry = 1;
-            }
-            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
-            catch (ccxt.NotSupported) { break; }
-            catch (Exception ex)
-            {
-                MarkDisconnected(ex);
-                try { await Task.Delay(TimeSpan.FromSeconds(retry), _stop.Token).ConfigureAwait(false); }
-                catch (OperationCanceledException) { break; }
-                retry = Math.Min(30, retry * 2);
-            }
-        }
-    }
-
-    /// <summary>Combines complete open-order pages, point lookups, unresolved intents, executions and a final balance.</summary>
-    private async Task ReconcileCoreAsync(InstrumentKey key, CancellationToken ct)
-    {
-        var generation = Volatile.Read(ref _resyncGeneration);
-        string? cursor = null;
-        var seen = new HashSet<string>();
-        var observedOpen = new HashSet<OrderReference>();
-        do
-        {
-            var page = await _client.OrdersAsync(new(key, Limit: 1000, Cursor: cursor), true, ct).ConfigureAwait(false);
-            foreach (var order in page.Items) { observedOpen.Add(order.Reference); Merge(order); }
-            cursor = page.NextCursor;
-            if (cursor is not null && !seen.Add(cursor)) throw new InvalidOperationException("Repeated history cursor.");
-        } while (cursor is not null);
-        OrderSnapshot[] known;
-        TradingOperation[] pending;
-        lock (_sync)
-        {
-            known = _orders.Values.Where(o => o.Reference.Instrument == key && !Terminal(o.State)).ToArray();
-            pending = _operations.Values.Where(o => Pending(o) && o.Request.Instrument == key).ToArray();
-        }
-        foreach (var order in known.Where(o => !observedOpen.Contains(o.Reference)))
-        {
-            var current = await _client.OrderAsync(order.Reference, ct).ConfigureAwait(false);
-            if (current is not null) Merge(current);
-        }
-        foreach (var operation in pending)
-        {
-            var order = operation.CancelTarget is { } target ? await _client.OrderAsync(target, ct).ConfigureAwait(false)
-                : await _client.FindAsync(key, operation.Request.ClientOrderId, ct).ConfigureAwait(false);
-            if (order is null && operation.CancelTarget is null && operation.CreatedAt < DateTimeOffset.UtcNow.AddDays(-6))
-                order = await FindInHistoryCoreAsync(operation, ct).ConfigureAwait(false);
-            if (order is not null)
-            {
-                Merge(order);
-                if (operation.CancelTarget is null && order.State is not (OrderState.Pending or OrderState.Unknown) || operation.CancelTarget is not null && Terminal(order.State))
-                    Save(operation with { Phase = TradingOperationPhase.Resolved, ExchangeOrderId = order.Reference.ExchangeOrderId });
-            }
-        }
-        DateTimeOffset since;
-        var recoveryStart = known.Where(o => o.CreatedAt.HasValue).Select(o => o.CreatedAt!.Value)
-            .Concat(pending.Select(o => o.CreatedAt)).DefaultIfEmpty(DateTimeOffset.UtcNow.AddDays(-1)).Min();
-        lock (_sync) since = _fills.Values.Where(f => f.Order.Instrument == key).Select(f => f.ExecutedAt).DefaultIfEmpty(recoveryStart).Max().AddMinutes(-5);
-        var until = DateTimeOffset.UtcNow;
-        while (since < until)
-        {
-            var end = HistoryEnd(since, until);
-            cursor = null; seen.Clear();
-            do
-            {
-                var page = await _client.ExecutionsAsync(new(key, Since: since, Until: end, Limit: 1000, Cursor: cursor), ct).ConfigureAwait(false);
-                foreach (var fill in page.Items) Merge(fill);
-                cursor = page.NextCursor;
-                if (cursor is not null && !seen.Add(cursor)) throw new InvalidOperationException("Repeated execution cursor.");
-            } while (cursor is not null);
-            since = end;
-        }
-        await RefreshBalanceCoreAsync(ct).ConfigureAwait(false);
-        if (generation != Volatile.Read(ref _resyncGeneration)) return;
-        _ready[key] = true;
-        lock (_sync)
-        {
-            _connection = TradingConnectionState.Connected;
-            _message = _canTrade is null ? "Connected · trading permissions unknown" : _canTrade == false ? "Connected · trading denied" : "Connected";
-        }
-        Publish(new TradingConnectionChanged(TradingConnectionState.Connected, DateTimeOffset.UtcNow));
-    }
-
-    /// <summary>Keeps one history request inside the venue limit. Binance rejects startTime and endTime more than 24 hours apart; Bybit allows seven days.</summary>
-    private DateTimeOffset HistoryEnd(DateTimeOffset since, DateTimeOffset until)
-    {
-        var window = Account.Venue == "bybit" ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24) - TimeSpan.FromMilliseconds(1);
-        var end = since + window;
-        return end < until ? end : until;
-    }
-
-    /// <summary>Searches older unresolved client IDs in bounded history windows without guessing or resending.</summary>
-    private async Task<OrderSnapshot?> FindInHistoryCoreAsync(TradingOperation operation, CancellationToken ct)
-    {
-        var since = operation.CreatedAt.AddMinutes(-5);
-        var until = DateTimeOffset.UtcNow;
-        while (since < until)
-        {
-            var end = HistoryEnd(since, until);
-            string? cursor = null;
-            var seen = new HashSet<string>();
-            do
-            {
-                var page = await _client.OrdersAsync(new(operation.Request.Instrument, since, end, 1000, cursor), false, ct).ConfigureAwait(false);
-                var found = page.Items.FirstOrDefault(o => o.ClientOrderId == operation.Request.ClientOrderId);
-                if (found is not null) return found;
-                cursor = page.NextCursor;
-                if (cursor is not null && !seen.Add(cursor)) throw new InvalidOperationException("Repeated order history cursor.");
-            } while (cursor is not null);
-            since = end;
-        }
-        return null;
-    }
-
-    /// <summary>Fetches authoritative free balances after order state, avoiding double local reservations.</summary>
-    private async Task RefreshBalanceCoreAsync(CancellationToken ct)
-    {
-        var balance = await _client.BalanceAsync(Account, ct).ConfigureAwait(false);
-        if (_client.IsFutures)
-        {
-            balance = balance with { Positions = await _client.PositionsAsync(null, ct).ConfigureAwait(false) };
-        }
-        lock (_sync) { _balance = balance; _balanceDirty = false; }
-        Publish(new AccountChanged(balance));
-    }
-
-    /// <summary>Reduces duplicate/out-of-order snapshots without losing fills or reopening terminal orders.</summary>
-    internal void Merge(OrderSnapshot order)
-    {
-        if (string.IsNullOrWhiteSpace(order.Reference.ExchangeOrderId)) return;
-        lock (_sync)
-        {
-            if (_orders.TryGetValue(order.Reference, out var old)) order = Reduce(old, order);
-            _orders[order.Reference] = order;
-            _journal.Save(_accountKey, order);
-            foreach (var operation in _operations.Values.Where(o => Pending(o) &&
-                (o.CancelTarget == order.Reference || o.CancelTarget is null && o.Request.Instrument == order.Reference.Instrument && o.Request.ClientOrderId == order.ClientOrderId)).ToArray())
-                if (operation.CancelTarget is null && order.State is not (OrderState.Pending or OrderState.Unknown) || operation.CancelTarget is not null && Terminal(order.State))
-                    Save(operation with { Phase = TradingOperationPhase.Resolved, ExchangeOrderId = order.Reference.ExchangeOrderId });
-        }
-        Publish(new OrderChanged(order));
-    }
-
-    /// <summary>Preserves monotonic filled quantity and terminal state under delayed stream messages.</summary>
-    internal static OrderSnapshot Reduce(OrderSnapshot old, OrderSnapshot next)
-    {
-        var filled = old.FilledQuantity.HasValue && next.FilledQuantity.HasValue ? Math.Max(old.FilledQuantity.Value, next.FilledQuantity.Value) : next.FilledQuantity ?? old.FilledQuantity;
-        var newer = next.UpdatedAt >= old.UpdatedAt || (next.FilledQuantity ?? 0) > (old.FilledQuantity ?? 0);
-        var result = newer ? next : old;
-        if (Terminal(old.State) && !Terminal(result.State)) result = result with { State = old.State };
-        if (old.State == OrderState.Filled) result = result with { State = OrderState.Filled };
-        var quantity = result.Quantity > 0 ? result.Quantity : old.Quantity;
-        return result with { Quantity = quantity, FilledQuantity = filled,
-            RemainingQuantity = filled.HasValue ? Math.Max(0, quantity - filled.Value) : result.RemainingQuantity ?? old.RemainingQuantity,
-            Price = result.Price ?? old.Price, ClientOrderId = result.ClientOrderId ?? old.ClientOrderId,
-            StatusReason = result.StatusReason ?? old.StatusReason,
-            PositionSide = result.PositionSide ?? old.PositionSide,
-            ReduceOnly = result.ReduceOnly ?? old.ReduceOnly,
-            AverageFillPrice = result.AverageFillPrice ?? old.AverageFillPrice, CreatedAt = result.CreatedAt ?? old.CreatedAt,
-            UpdatedAt = next.UpdatedAt > old.UpdatedAt ? next.UpdatedAt : old.UpdatedAt };
+        Account = new(profile.Id.ToString(), profile.Name, profile.Exchange, profile.Environment, profile.Market);
     }
 
-    /// <summary>Persists a fill once, allowing a later event to enrich a previously unknown commission.</summary>
-    private void Merge(ExecutionSnapshot fill)
+    /// <summary>Loads and maps units without querying fees or pre-validating account funds.</summary>
+    public Task<Instrument> FetchInstrumentAsync(InstrumentKey key, CancellationToken ct = default) => ReadAsync(async () =>
     {
-        if (string.IsNullOrEmpty(fill.ExecutionId)) return;
-        lock (_sync)
+        if (key.Venue != Account.Venue || key.Market != (IsFutures ? "futures" : "spot"))
+            throw new NotSupportedException("The instrument belongs to another account market.");
+        var market = (await _client.LoadMarketsAsync(ct))[key.SecurityId];
+        var instrument = CcxtMarketDataProvider.MapInstrument(Account.Venue, market, _client.PrecisionMode) with { Key = key };
+        return IsFutures ? instrument with
         {
-            var key = (fill.Order.Instrument, fill.ExecutionId);
-            if (_fills.TryGetValue(key, out var old) && (old.Fee is not null || fill.Fee is null)) return;
-            _fills[key] = fill;
-            _journal.Save(_accountKey, fill);
-        }
-        Publish(new ExecutionReceived(fill));
-    }
-
-    /// <summary>Updates durable and in-memory intent state together before returning to a caller.</summary>
-    private void Save(TradingOperation operation)
-    {
-        lock (_sync)
-        {
-            if (_operations.TryGetValue(operation.Id, out var old) && old.Phase == TradingOperationPhase.Resolved && operation.Phase != TradingOperationPhase.Resolved) return;
-            _journal.Save(operation); _operations[operation.Id] = operation;
-        }
-    }
-
-    /// <summary>Retains uncertainty in RAM even if storage fails after dispatch; the earlier durable Dispatching remains recoverable.</summary>
-    private void RecordFailure(TradingOperation operation, bool rejected)
-    {
-        var updated = operation with { Phase = rejected ? TradingOperationPhase.Resolved : TradingOperationPhase.Unknown };
-        try { Save(updated); }
-        catch (Exception)
-        {
-            lock (_sync)
+            TradingRules = instrument.TradingRules! with
             {
-                if (!_operations.TryGetValue(updated.Id, out var old) || old.Phase != TradingOperationPhase.Resolved) _operations[updated.Id] = updated;
-                _balanceDirty = true;
+                QuantityUnit = QuantityUnit.Contracts, ContractSize = Number(market.contractSize), SettlementCurrency = "USDT"
             }
-        }
-    }
-
-    /// <summary>Invalidates placement readiness on any stream/REST gap until reconciliation succeeds.</summary>
-    private void MarkDisconnected(Exception? ex)
-    {
-        Interlocked.Increment(ref _resyncGeneration);
-        foreach (var key in _tracked.Keys) _ready[key] = false;
-        lock (_sync) { _connection = TradingConnectionState.Reconnecting; _balanceDirty = true; _message = ex is null ? "Balance synchronization required" : Error(ex).Message; }
-        Publish(new TradingConnectionChanged(TradingConnectionState.Reconnecting, DateTimeOffset.UtcNow, true, ex is null ? null : Error(ex)));
-    }
-
-    /// <summary>Broadcasts detached events and marks an overflowed subscriber as requiring a full resync.</summary>
-    private void Publish(TradingUpdate update)
-    {
-        foreach (var channel in _subscribers.Values)
-            if (!channel.Writer.TryWrite(update))
-            {
-                while (channel.Reader.TryRead(out _)) { }
-                channel.Writer.TryWrite(new TradingConnectionChanged(TradingConnectionState.Reconnecting, DateTimeOffset.UtcNow, true));
-            }
-    }
-
-    /// <summary>Identifies only states whose exchange lifecycle has ended.</summary>
-    internal static bool Terminal(OrderState state) => state is OrderState.Filled or OrderState.Canceled or OrderState.Expired or OrderState.Rejected;
-
-    /// <summary>Prepared intents have not crossed the network boundary and must never be replayed.</summary>
-    private static bool Pending(TradingOperation operation) => operation.Phase is TradingOperationPhase.Dispatching or TradingOperationPhase.Unknown;
-
-    /// <summary>Only explicit business rejections prove that placement failed; timeouts and duplicates remain uncertain.</summary>
-    private static bool DefiniteRejection(Exception ex) => ex is ccxt.InsufficientFunds or ccxt.PermissionDenied or ccxt.AuthenticationError ||
-        ex is ccxt.InvalidOrder && ex is not ccxt.OrderNotFound && !ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
-
-    /// <summary>Produces stable user-facing errors without provider payloads or signed URLs.</summary>
-    private static TradingError Error(Exception ex) => ex is TradingServiceException service ? service.Error : ex switch
-    {
-        ccxt.InsufficientFunds => new("InsufficientFunds", "The exchange rejected the order: insufficient funds."),
-        ccxt.InvalidOrder => new("InvalidOrder", "The exchange rejected the order parameters or Post-only condition."),
-        OperationCanceledException or ccxt.NetworkError => new("ConnectionLost", "Connection interrupted. Any dispatched operation will be reconciled, not resent."),
-        NotSupportedException => new("Unsupported", "This operation or spot instrument is unavailable."),
-        _ => new("ExchangeFailure", AccountConnectionVerifier.SafeError(ex))
-    };
-
-    /// <summary>Normalizes provider read failures while preserving caller cancellation.</summary>
-    private static Exception Safe(Exception ex) => ex is OperationCanceledException or NotSupportedException ? ex : new TradingServiceException(Error(ex));
-
-    /// <summary>Closes transports and workers without issuing exchange cancellation requests.</summary>
-    public ValueTask DisposeAsync()
-    {
-        lock (_sync) return new(_disposeTask ??= DisposeCoreAsync());
-    }
-
-    /// <summary>Stops stream waits, drains the current bounded request and retains uncertain journal entries.</summary>
-    private async Task DisposeCoreAsync()
-    {
-        _disposed = true;
-        await _stop.CancelAsync().ConfigureAwait(false);
-        await _gate.WaitAsync().ConfigureAwait(false);
-        try { await _client.DisposeAsync().ConfigureAwait(false); }
-        finally { _gate.Release(); }
-        await _worker.ConfigureAwait(false);
-        await Task.WhenAll(_streams).ConfigureAwait(false);
-        foreach (var channel in _subscribers.Values) channel.Writer.TryComplete();
-        lock (_sync) { _connection = TradingConnectionState.Disconnected; _message = "Disconnected"; }
-    }
+        } : instrument;
+    });
+
+    /// <summary>Uses the exchange's native amount formatter.</summary>
+    public decimal AmountToPrecision(InstrumentKey key, decimal quantity) => _client.AmountToPrecision(key.SecurityId, quantity);
+
+    /// <summary>Uses the exchange's native price formatter.</summary>
+    public decimal PriceToPrecision(InstrumentKey key, decimal price) => _client.PriceToPrecision(key.SecurityId, price);
+
+    /// <summary>Routes one limit order to a named market operation.</summary>
+    public Task<OrderSnapshot> CreateLimitOrderAsync(LimitOrderRequest request, CancellationToken ct = default) => ReadAsync(async () =>
+    {
+        ValidateRequest(request.Instrument, request.Side, request.Quantity);
+        var amount = AmountToPrecision(request.Instrument, request.Quantity);
+        if (amount <= 0) throw new ArgumentException("Quantity rounds to zero.");
+        if (request.Price <= 0) throw new ArgumentException("A positive limit price is required.");
+        var price = PriceToPrecision(request.Instrument, request.Price);
+        if (price <= 0) throw new ArgumentException("Price rounds to zero.");
+        var side = request.Side == OrderSide.Buy ? "buy" : "sell";
+        var order = IsFutures
+            ? await _client.CreateUsdtFuturesLimitOrderAsync(request.Instrument.SecurityId, side, amount, price,
+                request.PostOnly, request.ReduceOnly, request.PositionSide, ct)
+            : await _client.CreateSpotLimitOrderAsync(request.Instrument.SecurityId, side, amount, price, request.PostOnly, ct);
+        return MapOrder(order, request.Instrument);
+    });
+
+    /// <summary>Routes one market order without manufacturing a limit price.</summary>
+    public Task<OrderSnapshot> CreateMarketOrderAsync(MarketOrderRequest request, CancellationToken ct = default) => ReadAsync(async () =>
+    {
+        ValidateRequest(request.Instrument, request.Side, request.Quantity);
+        var amount = AmountToPrecision(request.Instrument, request.Quantity);
+        if (amount <= 0) throw new ArgumentException("Quantity rounds to zero.");
+        var side = request.Side == OrderSide.Buy ? "buy" : "sell";
+        var order = IsFutures
+            ? await _client.CreateUsdtFuturesMarketOrderAsync(request.Instrument.SecurityId, side, amount,
+                request.ReduceOnly, request.PositionSide, ct)
+            : await _client.CreateSpotMarketOrderAsync(request.Instrument.SecurityId, side, amount, ct);
+        return MapOrder(order, request.Instrument);
+    });
+
+    /// <summary>Requests cancellation through the profile's market route.</summary>
+    public Task<OrderSnapshot> CancelOrderAsync(OrderReference order, CancellationToken ct = default) => ReadAsync(async () =>
+        MapOrder(IsFutures
+            ? await _client.CancelUsdtFuturesOrderAsync(order.ExchangeOrderId, order.Instrument.SecurityId, ct)
+            : await _client.CancelSpotOrderAsync(order.ExchangeOrderId, order.Instrument.SecurityId, ct), order.Instrument));
+
+    /// <summary>Fetches the provider's current view of one order.</summary>
+    public Task<OrderSnapshot> FetchOrderAsync(OrderReference order, CancellationToken ct = default) => ReadAsync(async () =>
+        MapOrder(IsFutures
+            ? await _client.FetchUsdtFuturesOrderAsync(order.ExchangeOrderId, order.Instrument.SecurityId, ct)
+            : await _client.FetchSpotOrderAsync(order.ExchangeOrderId, order.Instrument.SecurityId, ct), order.Instrument));
+
+    /// <summary>Maps a fresh open-order snapshot.</summary>
+    public Task<IReadOnlyList<OrderSnapshot>> FetchOpenOrdersAsync(InstrumentKey key, CancellationToken ct = default) =>
+        ReadAsync<IReadOnlyList<OrderSnapshot>>(async () => (IsFutures
+            ? await _client.FetchUsdtFuturesOpenOrdersAsync(key.SecurityId, ct)
+            : await _client.FetchSpotOpenOrdersAsync(key.SecurityId, ct)).Select(o => MapOrder(o, key)).ToArray());
+
+    /// <summary>Maps recent exchange executions for the selected listing.</summary>
+    public Task<IReadOnlyList<ExecutionSnapshot>> FetchMyTradesAsync(InstrumentKey key, CancellationToken ct = default) =>
+        ReadAsync<IReadOnlyList<ExecutionSnapshot>>(async () => (IsFutures
+            ? await _client.FetchUsdtFuturesMyTradesAsync(key.SecurityId, ct)
+            : await _client.FetchSpotMyTradesAsync(key.SecurityId, ct)).Select(t => Execution(t, key)).ToArray());
+
+    /// <summary>Maps current balances and reported collateral.</summary>
+    public Task<AccountSnapshot> FetchBalanceAsync(CancellationToken ct = default) => ReadAsync(async () => Balance(IsFutures
+        ? await _client.FetchUsdtFuturesBalanceAsync(ct) : await _client.FetchSpotBalanceAsync(ct), Account));
+
+    /// <summary>Maps native contract positions; spot has no separate contract position table.</summary>
+    public Task<IReadOnlyList<PositionSnapshot>> FetchPositionsAsync(InstrumentKey key, CancellationToken ct = default) =>
+        ReadAsync<IReadOnlyList<PositionSnapshot>>(async () => IsFutures
+            ? (await _client.FetchUsdtFuturesPositionsAsync(key.SecurityId, ct)).Select(p => Position(p, key)).ToArray() : []);
+
+    /// <summary>Checks typed stream support without exposing CCXT strings to the order book.</summary>
+    public bool SupportsStream(TradingStream stream) => _client.Supports(stream switch
+    {
+        TradingStream.Orders => "watchOrders", TradingStream.MyTrades => "watchMyTrades", _ => "watchBalance"
+    });
+
+    /// <summary>Copies a native private order batch.</summary>
+    public Task<IReadOnlyList<OrderSnapshot>> WatchOrdersAsync(InstrumentKey key, CancellationToken ct = default) =>
+        ReadAsync<IReadOnlyList<OrderSnapshot>>(async () => (IsFutures
+            ? await _client.WatchUsdtFuturesOrdersAsync(key.SecurityId, ct)
+            : await _client.WatchSpotOrdersAsync(key.SecurityId, ct)).Where(o => o.symbol == key.SecurityId)
+            .Select(o => MapOrder(o, key)).ToArray());
+
+    /// <summary>Copies a native private execution batch.</summary>
+    public Task<IReadOnlyList<ExecutionSnapshot>> WatchMyTradesAsync(InstrumentKey key, CancellationToken ct = default) =>
+        ReadAsync<IReadOnlyList<ExecutionSnapshot>>(async () => (IsFutures
+            ? await _client.WatchUsdtFuturesMyTradesAsync(key.SecurityId, ct)
+            : await _client.WatchSpotMyTradesAsync(key.SecurityId, ct)).Where(t => t.symbol == key.SecurityId)
+            .Select(t => Execution(t, key)).ToArray());
+
+    /// <summary>Copies a native balance stream update.</summary>
+    public Task<AccountSnapshot> WatchBalanceAsync(CancellationToken ct = default) => ReadAsync(async () => Balance(IsFutures
+        ? await _client.WatchUsdtFuturesBalanceAsync(ct) : await _client.WatchSpotBalanceAsync(ct), Account));
+
+    /// <summary>Checks required fields and account routing without imposing exchange trading policy.</summary>
+    private void ValidateRequest(InstrumentKey key, OrderSide side, decimal quantity)
+    {
+        if (!Enum.IsDefined(side) || quantity <= 0 || string.IsNullOrWhiteSpace(key.SecurityId))
+            throw new ArgumentException("A symbol, direction and positive native quantity are required.");
+        if (key.Venue != Account.Venue || key.Market != (IsFutures ? "futures" : "spot"))
+            throw new ArgumentException("The instrument belongs to another account market.");
+    }
+
+    /// <summary>Converts errors once; it never repeats the underlying operation.</summary>
+    private static async Task<T> ReadAsync<T>(Func<Task<T>> operation)
+    {
+        try { return await operation().ConfigureAwait(false); }
+        catch (TradingServiceException) { throw; }
+        catch (OperationCanceledException) { throw; }
+        catch (Exception ex) { throw new TradingServiceException(new("ExchangeError", TradingErrorText.FromException(ex))); }
+    }
+
+    /// <summary>Releases the configured CCXT connection without canceling orders.</summary>
+    public ValueTask DisposeAsync() => _client.DisposeAsync();
 }
diff --git a/BotView/Services/Integrations/Ccxt/OrderBookService.cs b/BotView/Services/Integrations/Ccxt/OrderBookService.cs
index 57e2038..08a0123 100644
--- a/BotView/Services/Integrations/Ccxt/OrderBookService.cs
+++ b/BotView/Services/Integrations/Ccxt/OrderBookService.cs
@@ -204,7 +204,7 @@ internal sealed class ProOrderBookClient : IOrderBookClient
         {
             instrument = instrument with
             {
-                TradingRules = instrument.TradingRules! with { QuantityUnit = QuantityUnit.Contracts, ContractSize = SpotOrderMath.Number(market.contractSize), SettlementCurrency = "USDT" }
+                TradingRules = instrument.TradingRules! with { QuantityUnit = QuantityUnit.Contracts, ContractSize = CcxtTradingMapper.Number(market.contractSize), SettlementCurrency = "USDT" }
             };
         }
 
diff --git a/BotView/Services/Trading/ITradingService.cs b/BotView/Services/Trading/ITradingService.cs
index 47ab264..2609b5d 100644
--- a/BotView/Services/Trading/ITradingService.cs
+++ b/BotView/Services/Trading/ITradingService.cs
@@ -2,54 +2,53 @@ using BotView.Models;
 
 namespace BotView.Services.Trading;
 
-/// <summary>Account-bound contract for CCXT and broker/QUIK adapters, using application models only.
-/// Dispose releases the connection without canceling live orders. Read failures throw TradingServiceException; only confirmed
-/// absence returns null. Unsupported capabilities throw NotSupportedException. Mutation timeouts after
-/// dispatch return Unknown and require reconciliation, not automatic resubmission.</summary>
+/// <summary>Order-book adapter contract. Calls return exchange data or throw; no recovery or local trade policy.</summary>
 public interface ITradingService : IAsyncDisposable
 {
     TradingAccountContext Account { get; }
 
-    /// <summary>Loads trading metadata including units, steps, limits and monetary valuation parameters.</summary>
-    Task<Instrument> GetTradingInstrumentAsync(InstrumentKey instrument, CancellationToken ct = default);
+    /// <summary>Loads native units and precision metadata for the selected listing.</summary>
+    Task<Instrument> FetchInstrumentAsync(InstrumentKey instrument, CancellationToken ct = default);
 
-    /// <summary>Returns options actually supported for this listing and the bound account.</summary>
-    Task<TradingCapabilities> GetCapabilitiesAsync(InstrumentKey instrument, CancellationToken ct = default);
+    /// <summary>Formats native order quantity with the provider's precision rules.</summary>
+    decimal AmountToPrecision(InstrumentKey instrument, decimal quantity);
 
-    /// <summary>Checks instrument/provider/account constraints without sending an order.
-    /// PlaceOrderAsync must check again because balances and limits can change.</summary>
-    Task<OrderValidationResult> ValidateOrderAsync(PlaceOrderRequest request, CancellationToken ct = default);
+    /// <summary>Formats a limit price with the provider's precision rules.</summary>
+    decimal PriceToPrecision(InstrumentKey instrument, decimal price);
 
-    /// <summary>Sends with an ID recorded before dispatch. Accepted does not mean filled.
-    /// Cancellation before dispatch may throw; after dispatch preserve/reconcile the unknown outcome.</summary>
-    Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default);
+    /// <summary>Sends one limit order, returning the provider's acknowledgement.</summary>
+    Task<OrderSnapshot> CreateLimitOrderAsync(LimitOrderRequest request, CancellationToken ct = default);
 
-    /// <summary>Cancels by listing and exchange ID, preserving partial fills and uncertain outcomes.</summary>
-    Task<CancelOrderResult> CancelOrderAsync(OrderReference order, CancellationToken ct = default);
+    /// <summary>Sends one market order without a price or maker flag.</summary>
+    Task<OrderSnapshot> CreateMarketOrderAsync(MarketOrderRequest request, CancellationToken ct = default);
 
-    /// <summary>Returns latest state, filled quantity and remaining quantity; null only confirms absence.</summary>
-    Task<OrderSnapshot?> GetOrderAsync(OrderReference order, CancellationToken ct = default);
+    /// <summary>Requests cancellation once; a returned acknowledgement is not a promise of no fills.</summary>
+    Task<OrderSnapshot> CancelOrderAsync(OrderReference order, CancellationToken ct = default);
 
-    /// <summary>Reconciles a lost placement response where client-ID lookup is supported.
-    /// Unsupported lookup or a failed request must not be represented as an absent order.</summary>
-    Task<OrderSnapshot?> FindOrderByClientIdAsync(InstrumentKey instrument, string clientOrderId, CancellationToken ct = default);
+    /// <summary>Fetches a specific exchange order.</summary>
+    Task<OrderSnapshot> FetchOrderAsync(OrderReference order, CancellationToken ct = default);
 
-    /// <summary>Returns a bounded page of open orders with continuation instead of silent truncation.</summary>
-    Task<TradingPage<OrderSnapshot>> GetOpenOrdersAsync(TradingQuery query, CancellationToken ct = default);
+    /// <summary>Fetches the selected instrument's current open orders.</summary>
+    Task<IReadOnlyList<OrderSnapshot>> FetchOpenOrdersAsync(InstrumentKey instrument, CancellationToken ct = default);
 
-    /// <summary>Returns history with filters and provider-owned continuation where supported.</summary>
-    Task<TradingPage<OrderSnapshot>> GetOrderHistoryAsync(TradingQuery query, CancellationToken ct = default);
+    /// <summary>Fetches recent executions for display.</summary>
+    Task<IReadOnlyList<ExecutionSnapshot>> FetchMyTradesAsync(InstrumentKey instrument, CancellationToken ct = default);
 
-    /// <summary>Returns actual fills and fees; deduplicate by account, listing and execution identity.</summary>
-    Task<TradingPage<ExecutionSnapshot>> GetExecutionsAsync(TradingQuery query, CancellationToken ct = default);
+    /// <summary>Fetches balances and available collateral reported by the provider.</summary>
+    Task<AccountSnapshot> FetchBalanceAsync(CancellationToken ct = default);
 
-    /// <summary>Returns funds, reservations, positions and margin. Unknown values remain null.</summary>
-    Task<AccountSnapshot> GetAccountStateAsync(CancellationToken ct = default);
+    /// <summary>Fetches contract positions; spot returns an empty list.</summary>
+    Task<IReadOnlyList<PositionSnapshot>> FetchPositionsAsync(InstrumentKey instrument, CancellationToken ct = default);
 
-    /// <summary>Returns cash holdings or futures positions, including direction and quantity unit.</summary>
-    Task<IReadOnlyList<PositionSnapshot>> GetPositionsAsync(InstrumentKey? instrument = null, CancellationToken ct = default);
+    /// <summary>Reports whether this connection supports a private update source.</summary>
+    bool SupportsStream(TradingStream stream);
 
-    /// <summary>Streams order, execution, account and connection updates to each subscriber.
-    /// Refresh snapshots/reconcile history after reconnect; the stream is not durable history.</summary>
-    IAsyncEnumerable<TradingUpdate> WatchUpdatesAsync(CancellationToken ct = default);
+    /// <summary>Reads one detached order batch from a private stream.</summary>
+    Task<IReadOnlyList<OrderSnapshot>> WatchOrdersAsync(InstrumentKey instrument, CancellationToken ct = default);
+
+    /// <summary>Reads one detached execution batch from a private stream.</summary>
+    Task<IReadOnlyList<ExecutionSnapshot>> WatchMyTradesAsync(InstrumentKey instrument, CancellationToken ct = default);
+
+    /// <summary>Reads one detached account balance update.</summary>
+    Task<AccountSnapshot> WatchBalanceAsync(CancellationToken ct = default);
 }
diff --git a/BotView/Services/Trading/SpotTradingClient.cs b/BotView/Services/Trading/SpotTradingClient.cs
deleted file mode 100644
index b6096f5..0000000
--- a/BotView/Services/Trading/SpotTradingClient.cs
+++ /dev/null
@@ -1,44 +0,0 @@
-using System.Globalization;
-using BotView.Models;
-
-namespace BotView.Services.Trading;
-
-/// <summary>Small exchange boundary used by the account service and deterministic tests.</summary>
-internal interface ISpotTradingClient : IAsyncDisposable
-{
-    Task<(string Identity, bool? CanTrade)> ConnectAsync(CancellationToken ct);
-    Task<IReadOnlyList<string>> LoadTradingPairsAsync(string spotSymbol, CancellationToken ct = default);
-    Task<SpotMarket> MarketAsync(InstrumentKey key, CancellationToken ct);
-    decimal NormalizeAmount(string symbol, decimal amount);
-    decimal NormalizePrice(string symbol, decimal price);
-    Task<OrderSnapshot> PlaceAsync(PlaceOrderRequest request, CancellationToken ct);
-    Task<OrderSnapshot?> CancelAsync(OrderReference order, CancellationToken ct);
-    Task<OrderSnapshot?> OrderAsync(OrderReference order, CancellationToken ct);
-    Task<OrderSnapshot?> FindAsync(InstrumentKey key, string clientId, CancellationToken ct);
-    Task<TradingPage<OrderSnapshot>> OrdersAsync(TradingQuery query, bool open, CancellationToken ct);
-    Task<TradingPage<ExecutionSnapshot>> ExecutionsAsync(TradingQuery query, CancellationToken ct);
-    Task<AccountSnapshot> BalanceAsync(TradingAccountContext account, CancellationToken ct);
-    Task<IReadOnlyList<PositionSnapshot>> PositionsAsync(InstrumentKey? key, CancellationToken ct) => throw new NotSupportedException();
-    bool IsFutures => false;
-    Task<IReadOnlyList<TradingError>> ValidateFuturesAsync(PlaceOrderRequest request, SpotMarket market, AccountSnapshot balance, CancellationToken ct) => throw new NotSupportedException();
-    Task<IReadOnlyList<TradingError>> ValidateMarketAsync(PlaceOrderRequest request, CancellationToken ct) => Task.FromResult<IReadOnlyList<TradingError>>([]);
-    bool SupportsStream(string kind);
-    Task<IReadOnlyList<TradingUpdate>> WatchAsync(string kind, InstrumentKey key, TradingAccountContext account, CancellationToken ct);
-}
-
-internal sealed record SpotMarket(Instrument Instrument, string Base, string Quote, decimal? FeeRate, string? FeeSide = null);
-
-/// <summary>Decimal arithmetic and order-entry conversion shared by the UI and validation.</summary>
-internal static class SpotOrderMath
-{
-    /// <summary>Converts a quote budget into base units, rounding down through the exchange precision function.</summary>
-    internal static decimal Quantity(decimal input, decimal price, OrderQuantityCurrency unit, Func<decimal, decimal> normalize)
-    {
-        if (input <= 0 || price <= 0) throw new ArgumentOutOfRangeException(nameof(input));
-        return unit == OrderQuantityCurrency.Base ? input : normalize(input / price);
-    }
-
-    /// <summary>Parses detached exchange numbers without treating missing values as zero.</summary>
-    internal static decimal? Number(double? number) => number.HasValue && double.IsFinite(number.Value)
-        ? decimal.Parse(number.Value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : null;
-}
diff --git a/BotView/Services/Trading/TradingAccountSession.cs b/BotView/Services/Trading/TradingAccountSession.cs
new file mode 100644
index 0000000..a97ac96
--- /dev/null
+++ b/BotView/Services/Trading/TradingAccountSession.cs
@@ -0,0 +1,333 @@
+using BotView.Models;
+
+namespace BotView.Services.Trading;
+
+/// <summary>Application-owned display state and private subscriptions for one profile. No trading recovery policy.</summary>
+public sealed class TradingAccountSession : IAsyncDisposable
+{
+    private readonly ITradingService _service;
+    private readonly CancellationTokenSource _stop = new();
+    private readonly SemaphoreSlim _tracking = new(1, 1);
+    private readonly object _sync = new();
+    private readonly Dictionary<InstrumentKey, Instrument> _instruments = new();
+    private readonly Dictionary<OrderReference, OrderSnapshot> _orders = new();
+    private readonly Dictionary<(OrderReference Order, string Id), ExecutionSnapshot> _executions = new();
+    private readonly Dictionary<InstrumentKey, IReadOnlyList<PositionSnapshot>> _positions = new();
+    private readonly List<Task> _workers = new();
+    private readonly Dictionary<(TradingStream Stream, InstrumentKey? Key), Resource> _resources = new();
+    private readonly TimeSpan _pollInterval;
+    private AccountSnapshot? _balance;
+    private string _message = "Connecting account…";
+    private bool _connected;
+    private bool _disposed;
+    public TradingAccountContext Account => _service.Account;
+
+    /// <summary>Owns the adapter; an injectable polling interval keeps session tests deterministic and fast.</summary>
+    public TradingAccountSession(ITradingService service, TimeSpan? pollInterval = null)
+    {
+        _service = service;
+        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
+    }
+
+    /// <summary>Copies the shared display collections without exposing mutable caches.</summary>
+    public TradingAccountState Current
+    {
+        get
+        {
+            lock (_sync)
+            {
+                var balance = _balance is null ? null : _balance with { Positions = _positions.Values.SelectMany(p => p).ToArray() };
+                var message = _resources.Values.Select(r => r.Error).FirstOrDefault(e => e is not null) ?? _message;
+                return new(message, _orders.Values.ToArray(), _executions.Values.OrderByDescending(e => e.ExecutedAt).Take(100).ToArray(),
+                    balance, _connected && !_stop.IsCancellationRequested);
+            }
+        }
+    }
+
+    /// <summary>Loads precision metadata and starts each private resource only once per tracked instrument.</summary>
+    public async Task TrackAsync(InstrumentKey key, CancellationToken ct = default)
+    {
+        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
+        await _tracking.WaitAsync(linked.Token).ConfigureAwait(false);
+        try
+        {
+            ObjectDisposedException.ThrowIf(_disposed, this);
+            lock (_sync) if (_instruments.ContainsKey(key)) return;
+            var instrument = await _service.FetchInstrumentAsync(key, linked.Token).ConfigureAwait(false);
+            // Authentication is independent of order/execution endpoint availability.
+            var balance = await _service.FetchBalanceAsync(linked.Token).ConfigureAwait(false);
+            lock (_sync)
+            {
+                var first = _instruments.Count == 0;
+                _instruments[key] = instrument;
+                _balance = balance;
+                _connected = true;
+                _message = "Connected";
+                StartResource(TradingStream.Orders, key);
+                StartResource(TradingStream.MyTrades, key);
+                if (first) StartResource(TradingStream.Balance, key);
+                if (Account.Market == TradingMarket.UsdtFutures) _workers.Add(Task.Run(() => PollPositionsAsync(key)));
+            }
+        }
+        finally { _tracking.Release(); }
+    }
+
+    /// <summary>Starts independent watchers and fallback polling for a single display resource.</summary>
+    private void StartResource(TradingStream stream, InstrumentKey key)
+    {
+        var resource = new Resource();
+        _resources[(stream, stream == TradingStream.Balance ? null : key)] = resource;
+        _workers.Add(Task.Run(() => PollAsync(stream, key, resource)));
+        if (_service.SupportsStream(stream)) _workers.Add(Task.Run(() => WatchAsync(stream, key, resource)));
+    }
+
+    /// <summary>Tracks stream health and prevents a slow REST snapshot from overwriting a newer stream batch.</summary>
+    private sealed class Resource
+    {
+        public bool Healthy;
+        public long Version;
+        public string? Error;
+    }
+
+    /// <summary>Refreshes only when the corresponding stream is unsupported or disconnected.</summary>
+    private async Task PollAsync(TradingStream stream, InstrumentKey key, Resource resource)
+    {
+        var initial = true;
+        while (!_stop.IsCancellationRequested)
+        {
+            try
+            {
+                bool poll;
+                lock (_sync) poll = initial || !resource.Healthy;
+                if (poll) await SnapshotAsync(stream, key, resource).ConfigureAwait(false);
+                initial = false;
+            }
+            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
+            catch (Exception ex) { Report(ex); }
+            try { await Task.Delay(_pollInterval, _stop.Token).ConfigureAwait(false); }
+            catch (OperationCanceledException) { break; }
+        }
+    }
+
+    /// <summary>Reads one REST resource and publishes it unless a newer private event already arrived.</summary>
+    private async Task SnapshotAsync(TradingStream stream, InstrumentKey key, Resource resource)
+    {
+        long version;
+        lock (_sync) version = resource.Version;
+        object result = stream switch
+        {
+            TradingStream.Orders => await _service.FetchOpenOrdersAsync(key, _stop.Token).ConfigureAwait(false),
+            TradingStream.MyTrades => await _service.FetchMyTradesAsync(key, _stop.Token).ConfigureAwait(false),
+            _ => await _service.FetchBalanceAsync(_stop.Token).ConfigureAwait(false)
+        };
+        lock (_sync)
+        {
+            if (version != resource.Version) return;
+            Apply(stream, key, result, snapshot: true);
+            resource.Version++;
+            _message = "Connected";
+        }
+    }
+
+    /// <summary>Restarts read-only streams and refreshes their snapshots after reconnection.</summary>
+    private async Task WatchAsync(TradingStream stream, InstrumentKey key, Resource resource)
+    {
+        while (!_stop.IsCancellationRequested)
+        {
+            try
+            {
+                object result = stream switch
+                {
+                    TradingStream.Orders => await _service.WatchOrdersAsync(key, _stop.Token).ConfigureAwait(false),
+                    TradingStream.MyTrades => await _service.WatchMyTradesAsync(key, _stop.Token).ConfigureAwait(false),
+                    _ => await _service.WatchBalanceAsync(_stop.Token).ConfigureAwait(false)
+                };
+                bool recovered;
+                lock (_sync)
+                {
+                    recovered = !resource.Healthy;
+                    resource.Healthy = true;
+                    resource.Error = null;
+                    resource.Version++;
+                    Apply(stream, key, result, snapshot: false);
+                }
+                if (recovered) await SnapshotAsync(stream, key, resource).ConfigureAwait(false);
+            }
+            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
+            catch (Exception ex)
+            {
+                lock (_sync)
+                {
+                    resource.Healthy = false;
+                    resource.Error = $"{stream} stream unavailable; updating via REST. {TradingErrorText.FromException(ex)}";
+                }
+                Report(ex);
+                try { await Task.Delay(_pollInterval, _stop.Token).ConfigureAwait(false); }
+                catch (OperationCanceledException) { break; }
+            }
+        }
+    }
+
+    /// <summary>Merges detached display data; terminal private events cannot reopen an order from an older batch.</summary>
+    private void Apply(TradingStream stream, InstrumentKey key, object value, bool snapshot)
+    {
+        if (stream == TradingStream.Orders)
+        {
+            var orders = (IReadOnlyList<OrderSnapshot>)value;
+            if (snapshot)
+            {
+                var ids = orders.Select(o => o.Reference).ToHashSet();
+                foreach (var id in _orders.Where(p => p.Key.Instrument == key && !Terminal(p.Value.State) && !ids.Contains(p.Key))
+                    .Select(p => p.Key).ToArray()) _orders.Remove(id);
+            }
+            foreach (var order in orders)
+            {
+                if (_orders.TryGetValue(order.Reference, out var old) &&
+                    (old.UpdatedAt > order.UpdatedAt || Terminal(old.State) && !Terminal(order.State))) continue;
+                _orders[order.Reference] = order;
+            }
+            foreach (var id in _orders.Values.Where(o => Terminal(o.State)).OrderByDescending(o => o.UpdatedAt)
+                .Skip(500).Select(o => o.Reference).ToArray()) _orders.Remove(id);
+        }
+        else if (stream == TradingStream.MyTrades)
+        {
+            foreach (var fill in (IReadOnlyList<ExecutionSnapshot>)value) _executions[(fill.Order, fill.ExecutionId)] = fill;
+            foreach (var id in _executions.OrderByDescending(p => p.Value.ExecutedAt).Skip(500).Select(p => p.Key).ToArray())
+                _executions.Remove(id);
+        }
+        else _balance = (AccountSnapshot)value;
+    }
+
+    /// <summary>Refreshes positions independently when there is no dedicated private position stream.</summary>
+    private async Task PollPositionsAsync(InstrumentKey key)
+    {
+        while (!_stop.IsCancellationRequested)
+        {
+            try
+            {
+                var positions = await _service.FetchPositionsAsync(key, _stop.Token).ConfigureAwait(false);
+                lock (_sync) _positions[key] = positions;
+            }
+            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
+            catch (Exception ex) { Report(ex); }
+            try { await Task.Delay(_pollInterval, _stop.Token).ConfigureAwait(false); }
+            catch (OperationCanceledException) { break; }
+        }
+    }
+
+    /// <summary>Publishes a safe display error while allowing supported operations to remain usable.</summary>
+    private void Report(Exception error)
+    {
+        lock (_sync) _message = TradingErrorText.FromException(error);
+    }
+
+    /// <summary>Converts the selected quantity unit and formats the actual amount sent to the provider.</summary>
+    public decimal ConvertQuantity(InstrumentKey key, decimal input, decimal price, OrderQuantityCurrency currency)
+    {
+        var quantity = input;
+        if (currency == OrderQuantityCurrency.Quote) quantity /= price * ContractSize(key);
+        return _service.AmountToPrecision(key, quantity);
+    }
+
+    /// <summary>Formats the actual limit price sent to the provider.</summary>
+    public decimal PriceToPrecision(InstrumentKey key, decimal price) => _service.PriceToPrecision(key, price);
+
+    /// <summary>Calculates order notional for preview; this is not a margin check.</summary>
+    public decimal OrderNotional(InstrumentKey key, decimal quantity, decimal price) => quantity * price * ContractSize(key);
+
+    /// <summary>Reads native contract valuation metadata loaded when the instrument was attached.</summary>
+    private decimal ContractSize(InstrumentKey key)
+    {
+        lock (_sync) return _instruments[key].TradingRules?.ContractSize ?? 1m;
+    }
+
+    /// <summary>Sends one explicit limit request and schedules a read-only refresh.</summary>
+    public async Task<OrderSnapshot> CreateLimitOrderAsync(LimitOrderRequest request, CancellationToken ct = default)
+    {
+        _stop.Token.ThrowIfCancellationRequested();
+        var order = await _service.CreateLimitOrderAsync(request, ct).ConfigureAwait(false);
+        lock (_sync) _orders[order.Reference] = order;
+        await RefreshAfterActionAsync(request.Instrument, ct).ConfigureAwait(false);
+        return order;
+    }
+
+    /// <summary>Sends one explicit market request and schedules a read-only refresh.</summary>
+    public async Task<OrderSnapshot> CreateMarketOrderAsync(MarketOrderRequest request, CancellationToken ct = default)
+    {
+        _stop.Token.ThrowIfCancellationRequested();
+        var order = await _service.CreateMarketOrderAsync(request, ct).ConfigureAwait(false);
+        lock (_sync) _orders[order.Reference] = order;
+        await RefreshAfterActionAsync(request.Instrument, ct).ConfigureAwait(false);
+        return order;
+    }
+
+    /// <summary>Requests cancellation once, then refreshes exchange data without assuming final cancellation.</summary>
+    public async Task<OrderSnapshot> CancelOrderAsync(OrderReference reference, CancellationToken ct = default)
+    {
+        _stop.Token.ThrowIfCancellationRequested();
+        var order = await _service.CancelOrderAsync(reference, ct).ConfigureAwait(false);
+        lock (_sync) _orders[order.Reference] = order;
+        await RefreshAfterActionAsync(reference.Instrument, ct).ConfigureAwait(false);
+        return order;
+    }
+
+    /// <summary>Refreshes display state after a successful action; a refresh error does not change the action's result.</summary>
+    private async Task RefreshAfterActionAsync(InstrumentKey key, CancellationToken ct)
+    {
+        try
+        {
+            foreach (var stream in Enum.GetValues<TradingStream>())
+            {
+                Resource? resource;
+                lock (_sync) _resources.TryGetValue((stream, stream == TradingStream.Balance ? null : key), out resource);
+                if (resource is not null) await SnapshotAsync(stream, key, resource).ConfigureAwait(false);
+            }
+            if (Account.Market == TradingMarket.UsdtFutures)
+            {
+                var positions = await _service.FetchPositionsAsync(key, ct).ConfigureAwait(false);
+                lock (_sync) _positions[key] = positions;
+            }
+        }
+        catch (Exception ex) { Report(ex); }
+    }
+
+    /// <summary>Fetches and freezes the current list before the UI asks the user to confirm cancellation.</summary>
+    public async Task<CancelBatch> PrepareCancelAllAsync(InstrumentKey key, CancellationToken ct = default) =>
+        new(Account, key, (await _service.FetchOpenOrdersAsync(key, ct).ConfigureAwait(false)).Select(o => o.Reference).ToArray());
+
+    /// <summary>Attempts each confirmed cancellation once and reports its independent result.</summary>
+    public async Task<IReadOnlyList<string>> CancelBatchAsync(CancelBatch batch, CancellationToken ct = default)
+    {
+        if (batch.Account != Account) throw new ArgumentException("Cancellation batch belongs to another account.");
+        var results = new List<string>();
+        foreach (var order in batch.Orders)
+        {
+            ct.ThrowIfCancellationRequested();
+            try { results.Add($"{order.ExchangeOrderId}: {(await CancelOrderAsync(order, ct).ConfigureAwait(false)).State}"); }
+            catch (OperationCanceledException) { throw; }
+            catch (Exception ex) { results.Add($"{order.ExchangeOrderId}: {TradingErrorText.FromException(ex)}"); }
+        }
+        return results;
+    }
+
+    /// <summary>Recognizes final exchange statuses without depending on a provider adapter.</summary>
+    public static bool Terminal(OrderState state) => state is OrderState.Filled or OrderState.Canceled or OrderState.Expired or OrderState.Rejected;
+
+    /// <summary>Stops further actions immediately when keys or profile settings change.</summary>
+    public void Suspend() => _stop.Cancel();
+
+    /// <summary>Stops subscriptions and releases the transport without canceling any live exchange orders.</summary>
+    public async ValueTask DisposeAsync()
+    {
+        await _tracking.WaitAsync().ConfigureAwait(false);
+        try
+        {
+            if (_disposed) return;
+            _disposed = true;
+            await _stop.CancelAsync().ConfigureAwait(false);
+            await Task.WhenAll(_workers).ConfigureAwait(false);
+            await _service.DisposeAsync().ConfigureAwait(false);
+        }
+        finally { _tracking.Release(); }
+    }
+}
diff --git a/BotView/Services/Trading/TradingErrorText.cs b/BotView/Services/Trading/TradingErrorText.cs
new file mode 100644
index 0000000..02619e5
--- /dev/null
+++ b/BotView/Services/Trading/TradingErrorText.cs
@@ -0,0 +1,42 @@
+using System.Text.Json;
+using System.Text.RegularExpressions;
+
+namespace BotView.Services.Trading;
+
+/// <summary>Produces display errors without signed URLs, request bodies or authentication material.</summary>
+public static class TradingErrorText
+{
+    /// <summary>Extracts only the exchange's message field, or a safe category for transport errors.</summary>
+    public static string FromException(Exception exception)
+    {
+        var error = exception.GetBaseException();
+        if (error is TradingServiceException) return error.Message;
+        if (error is OperationCanceledException or TimeoutException or ccxt.NetworkError)
+            return "The response was not received. The operation outcome is unknown; check exchange orders before trying again.";
+        if (error is ccxt.AuthenticationError) return "Authentication failed. Check API keys, network and permissions.";
+        if (error is ccxt.ExchangeError)
+        {
+            var start = error.Message.IndexOf('{');
+            if (start >= 0)
+            {
+                try
+                {
+                    using var json = JsonDocument.Parse(error.Message[start..]);
+                    foreach (var field in new[] { "msg", "retMsg", "message" })
+                    {
+                        if (json.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
+                        {
+                            var message = value.GetString() ?? "";
+                            if (message.Length <= 300 && !Regex.IsMatch(message, "https?://|api.?key|secret|signature|token", RegexOptions.IgnoreCase))
+                                return message;
+                        }
+                    }
+                }
+                catch (JsonException) { }
+            }
+            return "The exchange rejected the request. Check order parameters and account permissions.";
+        }
+        if (error is NotSupportedException) return "This operation is not supported by the selected connection.";
+        return "The request could not be completed. Check the connection and order parameters.";
+    }
+}
diff --git a/BotView/Services/Trading/TradingJournal.cs b/BotView/Services/Trading/TradingJournal.cs
deleted file mode 100644
index 6cb3bab..0000000
--- a/BotView/Services/Trading/TradingJournal.cs
+++ /dev/null
@@ -1,89 +0,0 @@
-using System.IO;
-using System.Text.Json;
-using Microsoft.Data.Sqlite;
-using BotView.Models;
-
-namespace BotView.Services.Trading;
-
-/// <summary>Crash recovery journal. Only application models are serialized; keys and raw API data never enter this database.</summary>
-public sealed class TradingJournal
-{
-    private readonly string _path;
-    private readonly object _sync = new();
-
-    /// <summary>Creates a private journal independent of market-data databases.</summary>
-    public TradingJournal(string? path = null)
-    {
-        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotView", "trading-journal.db");
-        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
-        using var db = Open();
-        using var command = db.CreateCommand();
-        command.CommandText = "CREATE TABLE IF NOT EXISTS journal (account TEXT NOT NULL, kind TEXT NOT NULL, id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(account,kind,id));";
-        command.ExecuteNonQuery();
-    }
-
-    /// <summary>Commits an operation before the network boundary; every write is its own durable transaction.</summary>
-    public void Save(TradingOperation operation) => Write(operation.AccountKey, "intent", operation.Id, operation);
-
-    /// <summary>Persists authoritative order state, including partially filled terminal orders.</summary>
-    internal void Save(string account, OrderSnapshot order) => Write(account, "order", Identity(order.Reference), order);
-
-    /// <summary>Upserts executions by exchange identity so repeated stream/history events cannot duplicate a fill.</summary>
-    internal void Save(string account, ExecutionSnapshot execution) => Write(account, "fill", Identity(execution.Order) + ":" + execution.ExecutionId, execution);
-
-    /// <summary>Loads account-scoped durable intents after account identity verification.</summary>
-    public IReadOnlyList<TradingOperation> Operations(string account) => Read<TradingOperation>(account, "intent");
-
-    /// <summary>Restores order state without claiming that a saved snapshot is current.</summary>
-    internal IReadOnlyList<OrderSnapshot> Orders(string account) => Read<OrderSnapshot>(account, "order");
-
-    /// <summary>Restores execution identities for deduplication and history watermarks.</summary>
-    internal IReadOnlyList<ExecutionSnapshot> Executions(string account) => Read<ExecutionSnapshot>(account, "fill");
-
-    /// <summary>Scopes order identities by the complete instrument listing.</summary>
-    private static string Identity(OrderReference reference) => JsonSerializer.Serialize(reference);
-
-    /// <summary>Uses parameterized writes and never interpolates application data into SQL.</summary>
-    private void Write<T>(string account, string kind, string id, T value)
-    {
-        lock (_sync)
-        {
-            using var db = Open();
-            using var command = db.CreateCommand();
-            command.CommandText = "INSERT INTO journal(account,kind,id,payload) VALUES($account,$kind,$id,$payload) ON CONFLICT(account,kind,id) DO UPDATE SET payload=excluded.payload";
-            command.Parameters.AddWithValue("$account", account);
-            command.Parameters.AddWithValue("$kind", kind);
-            command.Parameters.AddWithValue("$id", id);
-            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value));
-            command.ExecuteNonQuery();
-        }
-    }
-
-    /// <summary>Reads only the selected verified account and record category.</summary>
-    private IReadOnlyList<T> Read<T>(string account, string kind)
-    {
-        lock (_sync)
-        {
-            using var db = Open();
-            using var command = db.CreateCommand();
-            command.CommandText = "SELECT payload FROM journal WHERE account=$account AND kind=$kind";
-            command.Parameters.AddWithValue("$account", account);
-            command.Parameters.AddWithValue("$kind", kind);
-            using var reader = command.ExecuteReader();
-            var result = new List<T>();
-            while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))!);
-            return result;
-        }
-    }
-
-    /// <summary>Opens a short-lived connection with durable synchronous commits.</summary>
-    private SqliteConnection Open()
-    {
-        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
-        db.Open();
-        using var command = db.CreateCommand();
-        command.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
-        command.ExecuteNonQuery();
-        return db;
-    }
-}
diff --git a/BotView/Services/Trading/TradingOrderValidator.cs b/BotView/Services/Trading/TradingOrderValidator.cs
deleted file mode 100644
index d25c145..0000000
--- a/BotView/Services/Trading/TradingOrderValidator.cs
+++ /dev/null
@@ -1,90 +0,0 @@
-using BotView.Models;
-
-namespace BotView.Services.Trading;
-
-/// <summary>Common pure validation. Adapters also check provider precision, rights, sessions,
-/// balances/margin and provider-specific rules immediately before dispatch.</summary>
-public static class TradingOrderValidator
-{
-    /// <summary>Checks a request in the instrument's native order units without silently rounding it.</summary>
-    /// <param name="request">Order to validate. Quantity uses the instrument's declared unit; limit and trigger prices stay separate.</param>
-    /// <param name="instrument">Listing whose venue, lot size, price step, and trading rules are checked.</param>
-    /// <param name="capabilities">Order types, durations, and flags this listing and account actually support.</param>
-    /// <param name="account">Bound connection. Its venue must match the instrument.</param>
-    /// <param name="now">Current time. A GTD order must expire after this instant.</param>
-    public static OrderValidationResult Validate(PlaceOrderRequest request, Instrument instrument,
-        TradingCapabilities capabilities, TradingAccountContext account, DateTimeOffset now)
-    {
-        var errors = new List<TradingError>();
-        // Adds safe application errors, without provider request bodies or credentials.
-        void Error(string code, string message) => errors.Add(new(code, message));
-
-        if (request.Instrument != instrument.Key || !string.Equals(instrument.Key.Venue, account.Venue, StringComparison.OrdinalIgnoreCase))
-            Error("InstrumentMismatch", "The instrument does not belong to this request/account venue.");
-        if (string.IsNullOrWhiteSpace(request.ClientOrderId)) Error("ClientOrderIdRequired", "A client order ID is required before dispatch.");
-        if (!Enum.IsDefined(request.Side)) Error("InvalidSide", "Invalid order side.");
-        if (!Enum.IsDefined(request.Type) || !capabilities.OrderTypes.Contains(request.Type)) Error("UnsupportedOrderType", "The order type is not supported.");
-        if (!Enum.IsDefined(request.TimeInForce) || !capabilities.TimeInForces.Contains(request.TimeInForce)) Error("UnsupportedTimeInForce", "The order duration is not supported.");
-        if (request.PostOnly && (!capabilities.SupportsPostOnly || request.Type is not (OrderType.Limit or OrderType.StopLimit) ||
-            request.TimeInForce is OrderTimeInForce.Ioc or OrderTimeInForce.Fok))
-            Error("InvalidPostOnly", "Post-only requires a supported limit order and compatible duration.");
-        if (request.ReduceOnly && !capabilities.SupportsReduceOnly) Error("UnsupportedReduceOnly", "Reduce-only is not supported.");
-        if (request.PositionSide.HasValue && (!Enum.IsDefined(request.PositionSide.Value) || !capabilities.SupportsPositionSide))
-            Error("UnsupportedPositionSide", "Explicit position side is not supported.");
-        if (request.TimeInForce == OrderTimeInForce.Gtd ? request.ExpiresAt is null || request.ExpiresAt <= now : request.ExpiresAt is not null)
-            Error("InvalidExpiration", "An expiration in the future is required only for GTD orders.");
-
-        var rules = instrument.TradingRules;
-        if (rules is null) { Error("TradingMetadataRequired", "Trading rules must be loaded before placing an order."); return new(errors); }
-        if (!Enum.IsDefined(rules.QuantityUnit) || !Enum.IsDefined(rules.PriceQuotation) ||
-            rules.QuantityUnit == QuantityUnit.Lots && instrument.LotSize <= 0)
-            Error("InvalidTradingMetadata", "Instrument units and lot size must be valid.");
-        if (request.Quantity <= 0) Error("InvalidQuantity", "Quantity must be positive.");
-        // MOEX lots/contracts are positive integers; their fixed minimum and step need no per-instrument fields.
-        var moexLotsOrContracts = string.Equals(instrument.Key.Venue, "MOEX", StringComparison.OrdinalIgnoreCase)
-            && rules.QuantityUnit is QuantityUnit.Lots or QuantityUnit.Contracts;
-        if (moexLotsOrContracts)
-        {
-            if (request.Quantity != decimal.Truncate(request.Quantity))
-                Error("WholeQuantityRequired", "MOEX lot and contract quantities must be whole positive numbers.");
-        }
-        else
-        {
-            if (rules.QuantityStep is > 0 && request.Quantity % rules.QuantityStep.Value != 0)
-                Error("QuantityStep", "Quantity does not match the instrument step.");
-            if (rules.MinQuantity.HasValue && request.Quantity < rules.MinQuantity)
-                Error("MinQuantity", "Quantity is below the instrument minimum.");
-        }
-        if (rules.MaxQuantity.HasValue && request.Quantity > rules.MaxQuantity) Error("MaxQuantity", "Quantity exceeds the instrument maximum.");
-
-        var hasLimit = request.Type is OrderType.Limit or OrderType.StopLimit;
-        var hasTrigger = request.Type is OrderType.Stop or OrderType.StopLimit;
-        if (hasLimit != request.Price.HasValue) Error("LimitPrice", "A limit price is required only for limit and stop-limit orders.");
-        if (hasTrigger != request.TriggerPrice.HasValue || hasTrigger != request.TriggerCondition.HasValue ||
-            request.TriggerCondition.HasValue && !Enum.IsDefined(request.TriggerCondition.Value))
-            Error("TriggerPrice", "Conditional orders require a trigger price and direction.");
-        // Checks both prices in quote units, independently of monetary valuation.
-        void ValidatePrice(decimal? price)
-        {
-            if (!price.HasValue) return;
-            if (!rules.AllowNonPositivePrices && price <= 0) Error("InvalidPrice", "Price must be positive for this instrument.");
-            if (instrument.PriceStep is > 0 && price.Value % instrument.PriceStep.Value != 0)
-                Error("PriceStep", "Price does not match the instrument step.");
-            if (rules.MinPrice.HasValue && price < rules.MinPrice) Error("MinPrice", "Price is below the permitted minimum.");
-            if (rules.MaxPrice.HasValue && price > rules.MaxPrice) Error("MaxPrice", "Price exceeds the permitted maximum.");
-        }
-        ValidatePrice(request.Price);
-        ValidatePrice(request.TriggerPrice);
-        if (hasLimit && request.Price.HasValue && request.Quantity > 0 && (rules.MinNotional.HasValue || rules.MaxNotional.HasValue))
-        {
-            var value = TradingValueCalculator.Calculate(instrument, request.Quantity, request.Price.Value);
-            if (value is null) Error("ValuationUnavailable", "Monetary limits cannot be checked without valuation metadata.");
-            else
-            {
-                if (rules.MinNotional.HasValue && Math.Abs(value.Amount) < rules.MinNotional) Error("MinNotional", "Order value is below the minimum.");
-                if (rules.MaxNotional.HasValue && Math.Abs(value.Amount) > rules.MaxNotional) Error("MaxNotional", "Order value exceeds the maximum.");
-            }
-        }
-        return new(errors);
-    }
-}
diff --git a/BotView/Services/Trading/TradingSessionManager.cs b/BotView/Services/Trading/TradingSessionManager.cs
index 946ce1e..017c026 100644
--- a/BotView/Services/Trading/TradingSessionManager.cs
+++ b/BotView/Services/Trading/TradingSessionManager.cs
@@ -8,26 +8,28 @@ public sealed class TradingSessionManager : IAsyncDisposable
 {
     private readonly ITradingAccountStore _profiles;
     private readonly ICredentialStore _credentials;
-    private readonly Func<TradingAccountProfile, AccountCredentials, ISpotTradingClient> _factory;
+    private readonly Func<TradingAccountProfile, AccountCredentials, ITradingService> _factory;
     private readonly SemaphoreSlim _gate = new(1, 1);
     private readonly object _sync = new();
-    private readonly Dictionary<Guid, CcxtTradingService> _sessions = new();
+    private readonly Dictionary<Guid, TradingAccountSession> _sessions = new();
     private readonly HashSet<Guid> _invalidated = new();
+    private readonly Dictionary<Guid, TradingAccountProfile> _sessionProfiles = new();
     private readonly CancellationTokenSource _stop = new();
-    private readonly Lazy<TradingJournal> _journal;
     private int _revision;
     private bool _disposed;
     public int Revision => Volatile.Read(ref _revision);
 
     /// <summary>Builds the application manager without reading credentials or connecting until requested.</summary>
     public TradingSessionManager(ITradingAccountStore profiles, ICredentialStore credentials)
-        : this(profiles, credentials, (p, c) => new CcxtSpotTradingClient(p, c), new(() => new TradingJournal())) { }
+        : this(profiles, credentials, (p, c) => new CcxtTradingService(p, CcxtTradingClientFactory.Create(p, c))) { }
 
-    /// <summary>Injects a deterministic exchange boundary and journal for tests.</summary>
+    /// <summary>Injects a deterministic exchange adapter factory for tests.</summary>
     internal TradingSessionManager(ITradingAccountStore profiles, ICredentialStore credentials,
-        Func<TradingAccountProfile, AccountCredentials, ISpotTradingClient> factory, Lazy<TradingJournal> journal)
+        Func<TradingAccountProfile, AccountCredentials, ITradingService> factory)
     {
-        _profiles = profiles; _credentials = credentials; _factory = factory; _journal = journal;
+        _profiles = profiles;
+        _credentials = credentials;
+        _factory = factory;
         if (profiles is ITradingProfileNotifications changes) changes.Changed += ProfilesChanged;
         if (credentials is ICredentialNotifications keys) keys.CredentialsChanged += CredentialsChanged;
     }
@@ -35,8 +37,8 @@ public sealed class TradingSessionManager : IAsyncDisposable
     /// <summary>Returns profile metadata only; missing keys remain an explicit UI state.</summary>
     public TradingAccountsSettings Settings() => _profiles.Load();
 
-    /// <summary>Authenticates on a background task, then reuses one session per verified owner/network.</summary>
-    public Task<CcxtTradingService> GetAsync(Guid profileId, CancellationToken ct = default) => Task.Run(async () =>
+    /// <summary>Authenticates on a background task, then reuses one session per profile and network.</summary>
+    public Task<TradingAccountSession> GetAsync(Guid profileId, CancellationToken ct = default) => Task.Run(async () =>
     {
         using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
         var token = linked.Token;
@@ -51,31 +53,24 @@ public sealed class TradingSessionManager : IAsyncDisposable
                 ?? throw new TradingServiceException(new("ProfileMissing", "The selected account no longer exists."));
             using var credentials = _credentials.Read(profileId)
                 ?? throw new TradingServiceException(new("KeysRequired", "Enter and save this account's API keys first."));
-            ISpotTradingClient? client = null;
+            ITradingService? client = null;
             try
             {
                 client = _factory(profile, credentials);
-                var connected = await client.ConnectAsync(token).ConfigureAwait(false);
+                await client.FetchBalanceAsync(token).ConfigureAwait(false);
                 if (revision != Revision) throw new TradingServiceException(new("AccountChanged", "Account settings changed; select the account again."));
-                // A read-only profile must never acquire a session created using another profile's trading key.
-                CcxtTradingService? shared;
-                lock (_sync) shared = connected.CanTrade == false ? null : _sessions.Values.FirstOrDefault(s =>
-                    s.Account.Venue == profile.Exchange && s.Account.Environment == profile.Environment && s.Account.Market == profile.Market && s.Account.AccountId == connected.Identity && s.KeyCanTrade == connected.CanTrade);
-                if (shared is not null)
+                var service = new TradingAccountSession(client);
+                client = null;
+                lock (_sync)
                 {
-                    await client.DisposeAsync().ConfigureAwait(false);
-                    client = null;
-                    lock (_sync) _sessions[profileId] = shared;
-                    return shared;
+                    _sessions[profileId] = service;
+                    _sessionProfiles[profileId] = profile;
                 }
-                var service = new CcxtTradingService(profile, client, _journal.Value, connected.Identity, connected.CanTrade);
-                client = null;
-                lock (_sync) _sessions[profileId] = service;
                 return service;
             }
             catch (OperationCanceledException) { throw; }
             catch (TradingServiceException) { throw; }
-            catch (Exception ex) { throw new TradingServiceException(new("ConnectionFailed", AccountConnectionVerifier.SafeError(ex))); }
+            catch (Exception ex) { throw new TradingServiceException(new("ConnectionFailed", TradingErrorText.FromException(ex))); }
             finally { if (client is not null) try { await client.DisposeAsync().ConfigureAwait(false); } catch (Exception) { } }
         }
         finally { _gate.Release(); }
@@ -93,13 +88,23 @@ public sealed class TradingSessionManager : IAsyncDisposable
         _ = RetireAsync();
     }
 
-    /// <summary>Profile removal retires its sessions; selecting an active profile leaves other sessions supervised.</summary>
+    /// <summary>Profile changes retire affected connections; selecting an active profile leaves unchanged sessions supervised.</summary>
     private void ProfilesChanged()
     {
         try
         {
-            var ids = _profiles.Load().Profiles.Select(p => p.Id).ToHashSet();
-            lock (_sync) foreach (var pair in _sessions.Where(p => !ids.Contains(p.Key))) { _invalidated.Add(pair.Key); pair.Value.Suspend(); }
+            var profiles = _profiles.Load().Profiles.ToDictionary(p => p.Id);
+            lock (_sync)
+            {
+                foreach (var pair in _sessions)
+                {
+                    if (!profiles.TryGetValue(pair.Key, out var profile) || _sessionProfiles[pair.Key] != profile)
+                    {
+                        _invalidated.Add(pair.Key);
+                        pair.Value.Suspend();
+                    }
+                }
+            }
         }
         catch (Exception) { }
         Interlocked.Increment(ref _revision);
@@ -117,11 +122,15 @@ public sealed class TradingSessionManager : IAsyncDisposable
     /// <summary>Disposes invalidated clients and removes all aliases to a shared retired session.</summary>
     private async Task RetireCoreAsync()
     {
-        CcxtTradingService[] retired;
+        TradingAccountSession[] retired;
         lock (_sync)
         {
             retired = _sessions.Where(p => _invalidated.Contains(p.Key)).Select(p => p.Value).Distinct().ToArray();
-            foreach (var id in _sessions.Where(p => retired.Contains(p.Value)).Select(p => p.Key).ToArray()) _sessions.Remove(id);
+            foreach (var id in _sessions.Where(p => retired.Contains(p.Value)).Select(p => p.Key).ToArray())
+            {
+                _sessions.Remove(id);
+                _sessionProfiles.Remove(id);
+            }
             _invalidated.Clear();
         }
         foreach (var session in retired) try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
@@ -137,7 +146,7 @@ public sealed class TradingSessionManager : IAsyncDisposable
         await _gate.WaitAsync().ConfigureAwait(false);
         try
         {
-            CcxtTradingService[] sessions;
+            TradingAccountSession[] sessions;
             lock (_sync) { sessions = _sessions.Values.Distinct().ToArray(); _sessions.Clear(); }
             foreach (var session in sessions) try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
         }
diff --git a/BotView/ViewModels/OrderBookTradingViewModel.cs b/BotView/ViewModels/OrderBookTradingViewModel.cs
index e37f679..05f9e2e 100644
--- a/BotView/ViewModels/OrderBookTradingViewModel.cs
+++ b/BotView/ViewModels/OrderBookTradingViewModel.cs
@@ -1,14 +1,13 @@
 ﻿using System.Collections.ObjectModel;
 using BotView.Models;
 using BotView.Services;
-using BotView.Services.Integrations.Ccxt;
 
 namespace BotView.ViewModels;
 
 public sealed partial class OrderBookViewModel
 {
     private TradingSessionManager? _tradingManager;
-    private CcxtTradingService? _trading;
+    private TradingAccountSession? _trading;
     private TradingAccountProfile? _selectedAccount;
     private bool _tradeBusy;
     private bool _bindingAccount;
@@ -17,7 +16,7 @@ public sealed partial class OrderBookViewModel
     private int _managerRevision = -1;
     private OrderQuantityCurrency _quantityCurrency;
     private OrderSnapshot? _selectedOrder;
-    private SpotTradingState? _lastTradingState;
+    private TradingAccountState? _lastTradingState;
     private Task _bindTask = Task.CompletedTask;
 
     public TradingEnvironment Environment { get; private set; }
@@ -45,9 +44,6 @@ public sealed partial class OrderBookViewModel
     public string AccountStatus => _bindingAccount ? "Connecting account…" : _trading?.Current.Message ?? "Select an account with keys stored in memory.";
     public string AccountIdentity => _trading is null ? "" : _trading.Account.AccountId.StartsWith("key:", StringComparison.Ordinal)
         ? "Account verified by this API key" : _trading.Account.AccountId;
-    public string PendingStatus => _lastTradingState is null ? "" : string.Join("\n", _lastTradingState.Pending
-        .Where(o => o.Request.Instrument == TradingInstrument)
-        .Select(o => $"Unknown {(o.CancelTarget is null ? "placement" : "cancellation")}: {o.Id}. Reconciling; no automatic resend."));
     public string BalanceText => IsFutures ? $"Available margin: {_trading?.Current.Balance?.Margins?.FirstOrDefault(m => m.Currency == "USDT")?.AvailableFunds?.ToString() ?? "?"} USDT" : _trading?.Current.Balance?.Balances is { } balances
         ? string.Join(" · ", balances.Where(b => b.Currency == Symbol.Split('/')[0] || b.Currency == Symbol.Split('/').Last().Split(':')[0])
             .Select(b => $"{b.Currency}: {b.Available?.ToString() ?? "?"} free")) : "Balance unavailable";
@@ -69,7 +65,7 @@ public sealed partial class OrderBookViewModel
         get => _quantityCurrency;
         set
         {
-            if (!IsTradeEditable || value == _quantityCurrency || !Enum.IsDefined(value)) return;
+            if (!CanSelectQuantityCurrency || value == _quantityCurrency || !Enum.IsDefined(value)) return;
             try
             {
                 if (_trading is not null && TryPositive(PriceText, out var price) && TryPositive(AmountText, out var input))
@@ -87,31 +83,71 @@ public sealed partial class OrderBookViewModel
 
     public string QuantityHeader => IsFutures && QuantityCurrency == OrderQuantityCurrency.Base ? "Quantity (contracts)" : $"Quantity ({(QuantityCurrency == OrderQuantityCurrency.Base ? Symbol.Split('/')[0] : Symbol.Split('/').Last().Split(':')[0])})";
     public OrderSnapshot? SelectedOrder { get => _selectedOrder; set { _selectedOrder = value; Changed(); } }
-    private bool TradingInputValid
+    private OrderType _selectedOrderType = OrderType.Limit;
+    public OrderType[] OrderTypes { get; } = [OrderType.Limit, OrderType.Market];
+    public bool IsLimitOrder => SelectedOrderType == OrderType.Limit;
+    public bool CanSelectQuantityCurrency => IsTradeEditable && IsLimitOrder;
+
+    public OrderType SelectedOrderType
+    {
+        get => _selectedOrderType;
+        set
+        {
+            if (!IsTradeEditable || !OrderTypes.Contains(value) || value == _selectedOrderType) return;
+            _selectedOrderType = value;
+            _amountText = "";
+            _quantityCurrency = OrderQuantityCurrency.Base;
+            _postOnly = false;
+            Changed();
+        }
+    }
+
+    /// <summary>Prepares and validates only required input and exchange precision, without trading policy checks.</summary>
+    private (decimal Quantity, decimal Price) PreparedInput()
+    {
+        if (_trading is null) throw new InvalidOperationException("Select a connected account.");
+        if (!TryPositive(AmountText, out var input)) throw new ArgumentException("Enter a quantity greater than zero.");
+        decimal price = 1;
+        if (IsLimitOrder)
+        {
+            if (!TryPositive(PriceText, out price)) throw new ArgumentException("Enter a limit price greater than zero.");
+            price = _trading.PriceToPrecision(TradingInstrument, price);
+            if (price <= 0) throw new ArgumentException("Price rounds to zero at exchange precision.");
+        }
+        var quantity = _trading.ConvertQuantity(TradingInstrument, input, price,
+            IsLimitOrder ? QuantityCurrency : OrderQuantityCurrency.Base);
+        if (quantity <= 0) throw new ArgumentException("Quantity rounds to zero at exchange precision.");
+        return (quantity, price);
+    }
+
+    public string TradingInputHint
     {
         get
         {
-            try
-            {
-                if (_trading is null || !TryPositive(PriceText, out var price) || !TryPositive(AmountText, out var amount)) return false;
-                var quantity = _trading.ConvertQuantity(TradingInstrument, amount, price, QuantityCurrency);
-                return _trading.ValidateInput(new(TradingInstrument, OrderSide.Buy, OrderType.Limit, quantity, "bvpreview", price, PostOnly: PostOnly, ReduceOnly: ReduceOnly, PositionSide: SelectedPositionSide)).IsValid;
-            }
-            catch (Exception) { return false; }
+            if (_bindingAccount) return "Connecting account…";
+            if (_trading is null) return "Select an account with API keys.";
+            if (!_trading.Current.Connected) return "Reconnect the trading account.";
+            if (_tradeBusy) return "Waiting for the exchange response…";
+            try { PreparedInput(); return ""; }
+            catch (ArgumentException ex) { return ex.Message; }
+            catch (Exception) { return "Could not format order quantity or price."; }
         }
     }
 
+    private bool TradingInputValid => TradingInputHint.Length == 0;
+
     public string OrderPreview
     {
         get
         {
-            if (_trading is null || !TryPositive(PriceText, out var price) || !TryPositive(AmountText, out var input)) return "";
             try
             {
-                var amount = _trading.ConvertQuantity(TradingInstrument, input, price, QuantityCurrency);
-                return $"{Format(amount)} {(IsFutures ? "contracts" : Symbol.Split('/')[0])} · {Format(_trading.OrderNotional(TradingInstrument, amount, price))} {Symbol.Split('/').Last().Split(':')[0]} (excluding commission)";
+                var input = PreparedInput();
+                var unit = IsFutures ? "contracts" : Symbol.Split('/')[0];
+                if (!IsLimitOrder) return $"{Format(input.Quantity)} {unit} · Market; execution price is determined by the exchange";
+                return $"{Format(input.Quantity)} {unit} @ {Format(input.Price)} · {Format(_trading!.OrderNotional(TradingInstrument, input.Quantity, input.Price))} {Symbol.Split('/').Last().Split(':')[0]} (excluding commission)";
             }
-            catch (Exception) { return "Invalid quantity or precision."; }
+            catch (Exception) { return ""; }
         }
     }
 
@@ -120,7 +156,7 @@ public sealed partial class OrderBookViewModel
     {
         _tradingManager = manager;
         Environment = environment;
-        CancelSelectedCommand = new RelayCommand(CancelSelectedAsync, _ => !_tradeBusy && _trading is not null && SelectedOrder is { } order && !CcxtTradingService.Terminal(order.State));
+        CancelSelectedCommand = new RelayCommand(CancelSelectedAsync, _ => !_tradeBusy && _trading is not null && SelectedOrder is { } order && !TradingAccountSession.Terminal(order.State));
         CancelAllCommand = new RelayCommand(CancelAllAsync, _ => !_tradeBusy && !_bindingAccount && _trading is not null);
         ReconnectCommand = new RelayCommand(_ => BindAccountAsync(), _ => !_tradeBusy && !_bindingAccount && SelectedAccount is not null);
         ReloadAccounts();
@@ -160,7 +196,7 @@ public sealed partial class OrderBookViewModel
         {
             var service = await _tradingManager.GetAsync(selected.Id, _stop.Token);
             if (_disposed || generation != _accountGeneration) return;
-            // Keep the session attached even if the first snapshot fails; its worker continues recovery.
+            // Keep display subscriptions separate from the order-book feed.
             _trading = service;
             await Task.Run(() => service.TrackAsync(TradingInstrument, _stop.Token));
         }
@@ -180,14 +216,14 @@ public sealed partial class OrderBookViewModel
         _lastTradingState = _trading.Current;
         var selectedId = SelectedOrder?.Reference;
         var orders = _lastTradingState.Orders.Where(o => o.Reference.Instrument == TradingInstrument)
-            .OrderBy(o => CcxtTradingService.Terminal(o.State)).ThenByDescending(o => o.UpdatedAt).ToArray();
+            .OrderBy(o => TradingAccountSession.Terminal(o.State)).ThenByDescending(o => o.UpdatedAt).ToArray();
         if (!Orders.SequenceEqual(orders)) Orders = orders;
         var fills = _lastTradingState.Executions.Where(f => f.Order.Instrument == TradingInstrument).Take(100).ToArray();
         if (!Executions.SequenceEqual(fills)) Executions = fills;
         _selectedOrder = Orders.FirstOrDefault(o => o.Reference == selectedId);
     }
 
-    /// <summary>Freezes a request before awaiting, preventing edits or another Buy/Sell from changing the dispatched intent.</summary>
+    /// <summary>Freezes a request before awaiting, preventing edits or another Buy/Sell from changing the request.</summary>
     internal async Task TradeAsync(OrderSide side)
     {
         if (!CanTrade || _trading is null) return;
@@ -200,25 +236,22 @@ public sealed partial class OrderBookViewModel
 
         try
         {
-            if (!TryPositive(PriceText, out var price) || !TryPositive(AmountText, out var input)) return;
-
-            var quantity = service.ConvertQuantity(TradingInstrument, input, price, QuantityCurrency);
-            var request = new PlaceOrderRequest(TradingInstrument, side, OrderType.Limit, quantity,
-                "bv" + Guid.NewGuid().ToString("N"), price, PostOnly: PostOnly, ReduceOnly: ReduceOnly, PositionSide: SelectedPositionSide);
-            var result = await Task.Run(() => service.PlaceOrderAsync(request, _stop.Token));
+            var input = PreparedInput();
+            var result = IsLimitOrder
+                ? await Task.Run(() => service.CreateLimitOrderAsync(new(TradingInstrument, side, input.Quantity, input.Price,
+                    PostOnly, ReduceOnly, SelectedPositionSide), _stop.Token))
+                : await Task.Run(() => service.CreateMarketOrderAsync(new(TradingInstrument, side, input.Quantity,
+                    ReduceOnly, SelectedPositionSide), _stop.Token));
 
-            if (!_disposed && generation == _accountGeneration) {
-                _tradeStatus = $@"{side}: {result.Status} {result.Order?.State} ·
-                    {result.ExchangeOrderId ?? result.ClientOrderId} {result.Order?.StatusReason}" +
-                    (result.Error is null ? "" : $" · {result.Error.Message}");
-            }
+            if (!_disposed && generation == _accountGeneration)
+                _tradeStatus = $"{side}: {result.State} · {result.Reference.ExchangeOrderId} {result.StatusReason}";
         }
-        catch (OperationCanceledException) { }
+        catch (OperationCanceledException) { if (!_disposed) _tradeStatus = "Response not received; check exchange orders before trying again."; }
         catch (Exception ex) {
             if (!_disposed) {
                 _tradeStatus = ex is TradingServiceException safe ?
                     safe.Message :
-                    "Order could not be prepared. Check values and precision.";
+                    TradingErrorText.FromException(ex);
             }
         }
         finally {
@@ -238,10 +271,10 @@ public sealed partial class OrderBookViewModel
         try
         {
             var result = await Task.Run(() => service.CancelOrderAsync(order.Reference, _stop.Token));
-            if (!_disposed) _tradeStatus = $"Cancel {order.Reference.ExchangeOrderId}: {result.Status} {result.Error?.Message}";
+            if (!_disposed) _tradeStatus = $"Cancel {order.Reference.ExchangeOrderId}: {result.State} {result.StatusReason}";
         }
-        catch (OperationCanceledException) { }
-        catch (Exception) { if (!_disposed) _tradeStatus = "Cancellation failed; check the account status."; }
+        catch (OperationCanceledException) { if (!_disposed) _tradeStatus = "Response not received; check exchange orders before trying again."; }
+        catch (Exception ex) { if (!_disposed) _tradeStatus = TradingErrorText.FromException(ex); }
         finally { _tradeBusy = false; if (!_disposed) Changed(); }
     }
 
@@ -256,7 +289,7 @@ public sealed partial class OrderBookViewModel
             var batch = await Task.Run(() => service.PrepareCancelAllAsync(TradingInstrument, _stop.Token));
             if (_disposed || !await ConfirmCancelAllAsync(batch)) return;
             var results = await Task.Run(() => service.CancelBatchAsync(batch, _stop.Token));
-            if (!_disposed) _tradeStatus = string.Join("; ", results.Select(r => $"{r.Reference.ExchangeOrderId}: {r.Status} {r.Error?.Message}"));
+            if (!_disposed) _tradeStatus = string.Join("; ", results);
         }
         catch (OperationCanceledException) { }
         catch (Exception) { if (!_disposed) _tradeStatus = "Could not complete batch cancellation. Check individual order states."; }
diff --git a/BotView/ViewModels/OrderBookViewModel.cs b/BotView/ViewModels/OrderBookViewModel.cs
index 99c54ea..e69cbc2 100644
--- a/BotView/ViewModels/OrderBookViewModel.cs
+++ b/BotView/ViewModels/OrderBookViewModel.cs
@@ -2,7 +2,6 @@ using System.ComponentModel;
 using System.Globalization;
 using BotView.Models;
 using BotView.Services;
-using BotView.Services.Integrations.Ccxt;
 
 namespace BotView.ViewModels;
 
@@ -107,8 +106,8 @@ public sealed partial class OrderBookViewModel : INotifyPropertyChanged, IAsyncD
     public string PriceText { get => _priceText; set { _priceText = value; Changed(); } }
     public string AmountText { get => _amountText; set { _amountText = value; Changed(); } }
     public string TradeStatus => _tradeStatus;
-    public bool CanTrade => !_disposed && !_tradeBusy && !_bindingAccount && _trading?.IsReady(TradingInstrument) == true
-        && TradingInputValid && !_trading.Current.Pending.Any(o => o.Request.Instrument == TradingInstrument);
+    public bool CanTrade => !_disposed && !_tradeBusy && !_bindingAccount && _trading?.Current.Connected == true
+        && TradingInputValid;
     public RelayCommand BuyCommand { get; }
     public RelayCommand SellCommand { get; }
     public RelayCommand CenterCommand { get; }
@@ -150,7 +149,7 @@ public sealed partial class OrderBookViewModel : INotifyPropertyChanged, IAsyncD
         if (_followMarket || _center is null) _center = marketCenter;
         var rows = OrderBookLadder.Build(book, Multiplier, _center.Value, count, _maxVolumes[(int)VolumeCurrency], VolumeCurrency);
         var step = book.Market.TickSize * Multiplier;
-        return rows.Select(row => row with { OwnOrderCount = Orders.Count(o => !CcxtTradingService.Terminal(o.State) && o.Price is { } price &&
+        return rows.Select(row => row with { OwnOrderCount = Orders.Count(o => !TradingAccountSession.Terminal(o.State) && o.Price is { } price &&
             (o.Side == OrderSide.Buy ? decimal.Floor(price / step) : decimal.Ceiling(price / step)) * step == row.Price) }).ToArray();
     }
 
diff --git a/BotView/Windows/OrderBookWindow.xaml b/BotView/Windows/OrderBookWindow.xaml
index 73e3359..65ce7f5 100644
--- a/BotView/Windows/OrderBookWindow.xaml
+++ b/BotView/Windows/OrderBookWindow.xaml
@@ -111,15 +111,17 @@
         <Button Content="Reconnect account" Command="{Binding ReconnectCommand}" HorizontalAlignment="Left" Padding="5,2" Margin="0,3"/>
         <TextBlock Text="{Binding AccountIdentity}" FontSize="10" Foreground="#94A3B8"/>
         <TextBlock Text="{Binding BalanceText}" FontSize="10" TextWrapping="Wrap" Margin="0,2,0,5"/>
+        <ComboBox ItemsSource="{Binding OrderTypes}" SelectedItem="{Binding SelectedOrderType}"
+                  IsEnabled="{Binding IsTradeEditable}" ToolTip="Order type: Limit or Market" Margin="0,4"/>
         <Grid IsEnabled="{Binding IsTradeEditable}">
             <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
             <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="10"/><ColumnDefinition/></Grid.ColumnDefinitions>
             <TextBlock Text="Order price" Foreground="#94A3B8" FontSize="11"/>
             <TextBlock Grid.Column="2" Text="{Binding QuantityHeader}" Foreground="#94A3B8" FontSize="11"/>
-            <TextBox x:Name="PriceInput" Grid.Row="1" Text="{Binding PriceText, UpdateSourceTrigger=PropertyChanged}" Margin="0,3,0,7"/>
+            <TextBox x:Name="PriceInput" IsEnabled="{Binding IsLimitOrder}" Grid.Row="1" Text="{Binding PriceText, UpdateSourceTrigger=PropertyChanged}" Margin="0,3,0,7"/>
             <TextBox Grid.Row="1" Grid.Column="2" Text="{Binding AmountText, UpdateSourceTrigger=PropertyChanged}" Margin="0,3,0,7"/>
-            <CheckBox Grid.Row="2" Content="Post-only" IsChecked="{Binding PostOnly}" Foreground="#E5E7EB" ToolTip="Unchecked: ordinary GTC limit order"/>
-            <ComboBox Grid.Row="2" Grid.Column="2" ItemsSource="{Binding QuantityCurrencies}" SelectedItem="{Binding QuantityCurrency}" ToolTip="Order quantity unit; independent of depth volume bars"/>
+            <CheckBox Grid.Row="2" Content="Post-only" IsEnabled="{Binding IsLimitOrder}" IsChecked="{Binding PostOnly}" Foreground="#E5E7EB" ToolTip="Unchecked: ordinary GTC limit order"/>
+            <ComboBox Grid.Row="2" Grid.Column="2" IsEnabled="{Binding CanSelectQuantityCurrency}" ItemsSource="{Binding QuantityCurrencies}" SelectedItem="{Binding QuantityCurrency}" ToolTip="Order quantity unit; independent of depth volume bars"/>
             <TextBlock Grid.Row="3" Grid.ColumnSpan="3" Text="{Binding OrderPreview}" FontSize="10" TextWrapping="Wrap" Margin="0,4"/>
         </Grid>
         <StackPanel IsEnabled="{Binding IsTradeEditable}" Margin="0,4">
@@ -136,7 +138,7 @@
             <ComboBox ItemsSource="{Binding PositionTargets}" SelectedItem="{Binding PositionTarget}" ToolTip="Auto for one-way mode; Long or Short for hedge mode"/>
             <TextBlock Text="{Binding PositionsText}" TextWrapping="Wrap" FontSize="10"/>
         </StackPanel>
-        <TextBlock Text="{Binding PendingStatus}" TextWrapping="Wrap" Foreground="#FBBF24" FontSize="11"/>
+        <TextBlock Text="{Binding TradingInputHint}" TextWrapping="Wrap" Foreground="#FBBF24" FontSize="11"/>
         <TextBlock Text="{Binding TradeStatus}" TextWrapping="Wrap" Foreground="#FBBF24" FontSize="11" Margin="0,4"/>
         <TextBlock Text="Account orders for this pair" FontSize="11" Margin="0,5"/>
         <DataGrid ItemsSource="{Binding Orders}" SelectedItem="{Binding SelectedOrder}" IsReadOnly="True" AutoGenerateColumns="False"
diff --git a/docs/FuturesTrading.md b/docs/FuturesTrading.md
index cb1d92c..539eec5 100644
--- a/docs/FuturesTrading.md
+++ b/docs/FuturesTrading.md
@@ -1,49 +1,17 @@
 # Торговля USDT-фьючерсами
 
-В торговых счетах создайте профиль Binance или Bybit и выберите рынок `UsdtFutures`.
-Сеть и рынок фиксируются в профиле; существующие профили без поля Market считаются
-спотовыми. Ключи хранятся только в памяти и вводятся заново после перезапуска.
-
-Выберите профиль для биржи и откройте «Стакан» через контекстное меню пары.
-Приложение загрузит публичный каталог выбранной сети и предложит реальные символы
-линейных контрактов с расчетом в USDT: бессрочные и срочные, если биржа их предоставляет.
-Стакан и торговая сессия используют один рынок и сеть. Графики продолжают использовать
-свои существующие подключения.
-
-Поддерживаются лимитные GTC и Post-only заявки, отмена выбранной заявки и всех
-заявок по контракту, собственные заявки на лестнице, исполнения и REST-сверка.
-Журнал фьючерсных операций отделен от спотовых. Тайм-аут не вызывает повторную отправку.
-Закрытие окна не отменяет заявки.
-
-Количество задается в нативных контрактах либо суммой USDT без комиссии. Для
-пересчета и стоимости используется contractSize из метаданных CCXT. Шаг количества
-берется у биржи, дробные количества допускаются там, где это поддерживает контракт.
-
-В one-way режиме выберите `Auto`. Для hedge-режима укажите `Long` или `Short`.
-`Buy`/`Sell` определяет направление заявки; выбор позиции определяет ее hedge-сторону.
-Для закрытия включите Reduce-only: количество не может превышать наблюдаемый размер
-позиции, сторона должна ее уменьшать. Binance в hedge-режиме получает LONG/SHORT
-без запрещенного там флага reduceOnly; Bybit получает positionIdx и reduceOnly.
-
-Плечо и режим маржи читаются с биржи и автоматически не переключаются. Проверка
-средств использует свободное обеспечение, текущее плечо, размер контракта, текущую
-цену, возможный убыток открытия по лимитной цене и покрытие комиссии открытия/закрытия.
-Это предварительная оценка; биржа дополнительно применяет собственные уровни риска.
-Если режим позиций, плечо или обеспечение неизвестны, новые позиции блокируются.
-
-Первая реализация ограничена стандартными счетами с обеспечением в USDT.
-Binance Multi-Assets и Bybit с несколькими валютами обеспечения не поддерживаются;
-portfolio margin с неизвестным плечом не используется для увеличения позиции.
-Инверсные и USDC-контракты не входят в этот рынок. Для Binance без доступного UID
-восстановление журнала привязано к отпечатку того же проверенного API key.
-
-Автоматические проверки используют вымышленные ключи и подставные ответы.
-Реальные сделки в Testnet требуют отдельных ключей и ручной приемки: открытие Long
-и Short, частичное исполнение, Reduce-only, отмена, Post-only с пересечением цены,
-разрыв соединения и восстановление после перезапуска. По умолчанию тесты не отправляют
-реальные заявки и не меняют плечо или режим позиций.
-
-Документация: [CCXT](https://docs.ccxt.com/docs/manual),
-[Bybit positions](https://bybit-exchange.github.io/docs/v5/position),
-[Bybit orders](https://bybit-exchange.github.io/docs/v5/order/create-order),
-[Binance USD-M](https://developers.binance.com/docs/derivatives/usds-margined-futures/trade/rest-api/New-Order).
+Создайте профиль Binance/Bybit с рынком `UsdtFutures` и нужной сетью. Профили без поля Market остаются спотовыми. Ключи хранятся только в памяти.
+
+Публичный каталог выбранной сети предлагает линейные контракты с расчётом в USDT. Инверсные и USDC-контракты в эту реализацию не входят. Стакан и профиль закреплены за одним рынком и сетью.
+
+Доступны Limit, Post-only Limit, Market, отмена выбранной заявки и подтверждаемая отмена списка заявок по контракту. Limit принимает количество контрактов или бюджет USDT; Market — только контракты. Размер контракта учитывается при пересчёте и в превью, точность форматирует CCXT.
+
+Buy/Sell задаёт направление. Auto передаёт BOTH для Binance и positionIdx=0 для Bybit; Long/Short выбирает hedge-сторону. Приложение не меняет и не проверяет заранее режим позиций, плечо или маржу. Неподдерживаемые сочетания возвращаются как ошибки биржи.
+
+Reduce-only передаётся в запросе явно. Если Binance запрещает этот флаг в выбранном режиме, биржа отклонит запрос; приложение не снимает флаг молча и не подменяет операцию.
+
+Баланс, обеспечение, позиции и исполнения отображаются по API. Предварительных проверок маржи, комиссии, плеча и локальных резервов нет. Окончательные ограничения применяет биржа.
+
+Приватные потоки обновляют заявки, исполнения и баланс. Недоступные потоки заменяются REST-опросом раз в две секунды; позиции опрашиваются отдельно. Намерения не сохраняются, потерянные отправки не восстанавливаются, автоматической повторной отправки нет. Закрытие окна не отменяет заявки.
+
+Архитектура и автономные проверки описаны в [TradingService.md](TradingService.md).
diff --git a/docs/TradingService.md b/docs/TradingService.md
index 0ecdce7..95d084e 100644
--- a/docs/TradingService.md
+++ b/docs/TradingService.md
@@ -1,158 +1,45 @@
-# Общий торговый контракт
-
-`Services/ITradingService.cs` определяет общий контракт. `CcxtTradingService`
-реализует торговлю спотом и USDT-фьючерсами Binance/Bybit в Testnet и Mainnet; QUIK пока предоставляет
-каталог без торгового адаптера. Модели находятся в `Models/Trading.cs`, правила
-инструмента — в `Models/TradingInstrumentRules.cs`.
-
-USDT-контракты, режимы позиций и ограничения обеспечения описаны в [FuturesTrading.md](FuturesTrading.md).
-
-## Торговля из стакана
-
-`TradingSessionManager` создает авторизованный CCXT.Pro-клиент в фоне и закрепляет
-сессию за проверенным владельцем счета и сетью. Окна одного счета используют общие
-заявки и свободные средства. Профиль с явным запретом торговли не получает сессию
-с торговыми правами другого ключа. Если биржа не возвращает ID владельца, журнал
-привязан к SHA-256 отпечатку проверенного API key: новые ключи не получают старые
-намерения автоматически. Сам ключ и секрет остаются только в ОЗУ.
-
-Buy/Sell отправляют Limit GTC или Post-only без дополнительного подтверждения.
-Количество можно ввести в базовой валюте либо суммой котировки без комиссии;
-во втором случае количество округляется вниз средствами CCXT. Цена не округляется
-молча. Проверяются точность, фильтры, текущие ценовые диапазоны, разрешения и баланс.
-При неизвестной комиссии или свободных средствах отправка блокируется. Для известной
-валюты комиссии учитывается ее сторона; при неизвестной стороне используется
-консервативное покрытие комиссии в расходуемой валюте заявки.
-
-Сессия сериализует проверку и отправку. При неопределенной операции блокируется
-повторное выставление на этой паре и конкурирующее расходование той же валюты.
-Так неизвестный резерв не вычитается второй раз из уже уменьшенного free balance.
-Подтвержденные заявки учитываются через свежий биржевой баланс, а не второй локальный
-резерв. Неподтвержденная отмена не означает освобождение средств.
-
-Журнал `%LOCALAPPDATA%/BotView/trading-journal.db` сохраняет намерение до отправки,
-фазы Prepared/Dispatching/Unknown/Resolved, заявки и исполнения. После сбоя Dispatching
-восстанавливается как Unknown, Prepared никогда автоматически не отправляется.
-Ключи, заголовки и исходные ответы API не записываются. Пустой результат поиска
-не снимает Unknown; повторной автоматической отправки нет.
-
-Приватные подписки дополняются REST-сверкой раз в 2 секунды с rate limiter CCXT.
-После разрыва выполняются полная пагинация открытых заявок, запросы исчезнувших
-заявок, сверка клиентских ID, исполнения с перекрытием watermark и баланс.
-История читается окнами до семи дней; отсутствие старой истории не считается отказом
-заявки. Дубликаты исполнений подавляются, завершенная заявка не открывается старым
-событием. История API ограничена самой биржей: недоказанный результат остается Unknown.
-
-Массовая отмена включает все заявки счета по паре, включая внешние. Подтверждение
-показывает счет, сеть и число заранее зафиксированных идентификаторов; новые заявки
-в пакет не включаются. Каждая отмена имеет самостоятельный результат.
-
-Закрытие стакана не отменяет заявки и не завершает сессию. Смена ключей закрывает
-старый клиент; после повторной авторизации восстанавливается журнал этого владельца
-и сети. Завершение приложения закрывает транспорт без отмены биржевых заявок.
-
-Проверки без торговли: собрать `BotView.Tests`, затем запустить
-`BotView.Tests.exe -class BotView.Tests.SpotTradingTests`.
-Реальные сделки автоматическими тестами не отправляются. Приемка Testnet требует
-явного запуска приложения и ключей: Limit, Post-only с пересечением, частичное
-исполнение, отмена, отключение сети и перезапуск с повторным вводом ключей.
-
-Документация: [CCXT.Pro](https://docs.ccxt.com/docs/pro-manual),
-[Bybit orders](https://bybit-exchange.github.io/docs/v5/order/create-order),
-[Bybit price limits](https://bybit-exchange.github.io/docs/v5/market/order-price-limit),
-[Binance filters](https://developers.binance.com/docs/binance-spot-api-docs/filters).
-
-## Подключение и идентификаторы
-
-- Экземпляр сервиса закреплен за `TradingAccountContext`: подключением, счетом,
-  площадкой и Testnet/Production. При смене счета нужен другой экземпляр.
-- Инструмент идентифицируется полным `InstrumentKey`: Venue, Market, Board, SecurityId.
-  Символ для отображения не используется как глобальный ключ.
-- `ClientOrderId` создается и сохраняется до отправки. Это идентификатор намерения
-  приложения, а не гарантия идемпотентности API. Поддержка его передачи и поиска
-  определяется адаптером.
-- `ProviderRequestId` обозначает, например, номер транзакции QUIK, а `ExchangeOrderId` —
-  номер биржевой заявки. Адаптер хранит их связь; эти значения не подменяют друг друга.
+# Торговля из стакана
+
+Торговый код разделён по обязанностям:
+
+- `CcxtTradingClient` — тонкая обёртка над настроенным CCXT. Методы возвращают нативные типы библиотеки; фоновых задач и состояния заявок нет.
+- `CcxtTradingClientFactory` — создание CCXT.Pro-клиентов Binance/Bybit, выбор Spot/USDT Futures и Testnet/Mainnet до первого запроса.
+- `ITradingService` — контракт стакана с моделями приложения. `CcxtTradingService` выбирает метод обёртки и преобразует результаты и ошибки.
+- `TradingAccountSession` — снимок данных для UI, приватные подписки и резервный REST-опрос.
+- `TradingSessionManager` — одна сессия на профиль для всех его окон, замена соединения при изменении ключей или профиля.
+
+Торговля через QUIK пока не реализована. Будущая обёртка QuikSharp и её адаптер смогут использовать контракт стакана. Каталог QUIK работает независимо.
+
+## Заявки
+
+По умолчанию выбран Limit. Он принимает цену и количество монет/контрактов либо бюджет в валюте котировки. Бюджет пересчитывается по форматированной цене с учётом размера контракта. Превью показывает подготовленные количество, цену и стоимость без комиссии. Post-only доступен только для Limit.
+
+Market принимает только количество монет или контрактов. Поле цены, бюджет в USDT и Post-only отключены. При переключении типа количество очищается, чтобы бюджет случайно не стал количеством монет. Точная цена и стоимость Market не обещаются.
+
+Обёртка содержит отдельные методы `CreateSpotLimitOrderAsync`, `CreateSpotMarketOrderAsync`, `CreateUsdtFuturesLimitOrderAsync`, `CreateUsdtFuturesMarketOrderAsync`. Контракт стакана принимает отдельные `LimitOrderRequest` и `MarketOrderRequest`.
+
+Проверяются обязательные поля, положительные числа, направление и преобразование точности средствами CCXT. Баланс, комиссии, маржу, лимиты и режим позиций проверяет биржа. Ошибки показываются в форме без ключей, подписанных URL и исходных тел запросов.
+
+Один клик отправляет один запрос. На время запроса форма блокируется. Тайм-аут означает неизвестный результат и не вызывает автоматическую повторную отправку. Перед повторным действием следует проверить заявки на бирже.
+
+## Данные и подписки
+
+Начальная загрузка получает инструмент, баланс, открытые заявки и последние исполнения. Приватные `WatchOrders`, `WatchMyTrades`, `WatchBalance` обновляют снимок интерфейса. Для фьючерсов позиции опрашиваются REST раз в две секунды.
+
+Если поток не поддерживается или завершился ошибкой, REST раз в две секунды обновляет только соответствующие данные. После восстановления потока загружается свежий снимок, резервный опрос прекращается. Медленный REST-ответ не заменяет более новые приватные события. Заявки объединяются по инструменту и биржевому ID, исполнения — также по ID исполнения. Это текущие данные UI, а не долговременная история.
+
+После успешного создания/отмены обновляются данные аккаунта. Ошибка чтения не превращает успешную отправку в отказ. Массовая отмена подтверждает зафиксированный список ID и показывает индивидуальный результат каждой отмены.
 
 ## Жизненный цикл
 
-`PlaceOrderStatus.Submitted` подтверждает отправку, `Accepted` — принятие заявки,
-`Rejected` — явный отказ, `Unknown` — отсутствие достоверного результата. Принятие
-не означает исполнения. В QUIK результат отправки транзакции сопоставляется с
-`OnTransReply` и обновлениями `OnOrder`; CCXT адаптер приводит ответы API к тем же моделям.
-
-После тайм-аута/отмены ожидания уже отправленной операции запрещено автоматически
-создавать новую заявку. Нужно сверить биржевой ID, client ID или собственную таблицу
-соответствия транзакций. До отправки `CancellationToken` может отменить операцию обычным
-исключением; после отправки должен сохраняться доступный для сверки результат.
-
-Состояние самой заявки — отдельный `OrderState`. Частичное исполнение отражается
-`FilledQuantity` и `RemainingQuantity`. Отмененная заявка может иметь исполнения.
-Отмена возвращает свой результат: подтверждение отправки запроса не приравнивается
-к окончательной отмене; заявка могла исполниться одновременно с запросом отмены.
-
-Чтение заявки возвращает `null` только при подтвержденном отсутствии. Ошибки сети
-и неподдерживаемый поиск не означают отсутствия заявки. Неподдерживаемые операции
-выдают `NotSupportedException`, а не пустой успешный ответ.
-Остальные ошибки чтения адаптер нормализует в `TradingServiceException` с безопасным
-`TradingError`: наружу не передаются исключения CCXT/QUIK с исходными запросами.
-
-## Количество и цена
-
-Количество всегда выражено в `TradingInstrumentRules.QuantityUnit`:
-
-| Тип | Единицы заявки | Котировка |
-|---|---|---|
-| Криптовалюта, спот | Базовая валюта | Валюта котирования за единицу |
-| Акции через QUIK | Лоты | Цена за бумагу |
-| Облигации через QUIK | Лоты | Проценты текущего номинала |
-| Фьючерсы MOEX через QUIK | Контракты | Пункты котировки |
-
-Для лотов и контрактов MOEX количество проверяется как целое положительное число.
-QUIK-каталог не заполняет избыточные QuantityStep и MinQuantity; минимум 1 и шаг 1
-следуют из этого правила. Криптоадаптер берет шаг количества,
-лимиты и шаг цены из CCXT. Режим significant digits не превращается в выдуманный
-постоянный шаг: точность дополнительно проверяет торговый адаптер средствами CCXT.
-
-Рыночная заявка не требует лимитной цены. Stop требует отдельные `TriggerPrice`
-и `TriggerCondition`, StopLimit — также лимитную `Price`. Возможности инструмента
-определяют доступность типов, сроков действия, PostOnly, ReduceOnly и стороны позиции.
-GTD требует будущую дату истечения; IOC/FOK несовместимы с PostOnly.
-
-`TradingOrderValidator` проверяет общие правила без округления. Полная реализация
-`ValidateOrderAsync`/`PlaceOrderAsync` дополнительно проверяет текущие данные провайдера,
-права, торговую сессию, баланс/обеспечение и особенности брокера. Успех локальной
-проверки не гарантирует принятия заявки биржей.
-
-## Денежная оценка и счет
-
-`TradingValueCalculator` использует метаданные инструмента:
-
-- Спот/акции: цена × количество единиц; лоты переводятся через LotSize.
-- Линейный криптоконтракт: цена × число контрактов × ContractSize.
-- Облигации: число бумаг × (цена / 100 × текущий номинал + НКД на бумагу).
-- Фьючерсы в пунктах: число контрактов × цена / шаг цены × стоимость шага.
-
-Результат — стоимость в объявленной валюте, не требуемое обеспечение. ГО, вариационная
-маржа и свободные средства возвращаются отдельно в состоянии счета. Inverse/quanto
-контракты помечаются `ProviderSpecific` и рассчитываются адаптером.
-Если валюта котировки/номинала и валюта расчетов различаются, без явной конвертации
-адаптером сумма остается неизвестной; смена обозначения валюты не заменяет конвертацию.
-
-Неизвестные номинал, НКД, стоимость шага или валюта дают `null`, а не нулевую сумму.
-Каталог QUIK пока не загружает эти финансовые параметры: торговый адаптер должен
-дополнить их при `GetTradingInstrumentAsync`. Денежные поля стакана принимают такое
-описание инструмента и отображают неизвестную оценку как «—». Публичный криптостакан
-сохраняет существующее поведение.
-
-Состояние счета содержит денежные балансы/резервы, позиции и маржинальные показатели.
-`null` в разделе снимка означает отсутствие достоверных данных, пустой список —
-подтвержденное отсутствие записей.
-У позиции явно заданы направление и единицы количества. Фактические исполнения
-имеют отдельные ID и комиссии; ключ дедупликации — счет + инструмент + ID исполнения.
-
-История, исполнения и открытые заявки используют `TradingPage<T>` с непрозрачным
-курсором, чтобы не терять записи при ограничениях API. Адаптеры не игнорируют
-неподдерживаемые фильтры. Поток `WatchUpdatesAsync` публикует изменения заявок,
-исполнений, счета и соединения; после переподключения `RequiresResync` требует
-сверки со снимками/историей. Закрытие сервиса освобождает соединение без отмены заявок.
+Журнал намерений, фазы операций, локальные резервы и восстановление отправок удалены. Старый `trading-journal.db` не читается и не удаляется. Формат профилей сохранён; ключи остаются только в памяти.
+
+Закрытие стакана оставляет общую сессию приложения работающей. Смена ключей/профиля закрывает старое соединение; завершение приложения освобождает транспорт. Эти действия не отменяют биржевые заявки.
+
+## Автономные проверки
+
+Сборка: `dotnet build BotView.Tests/BotView.Tests.csproj --no-restore`.
+
+Тесты: `dotnet run --project BotView.Tests/BotView.Tests.csproj --no-build -- -class BotView.Tests.CcxtTradingClientTests -class BotView.Tests.TradingAccountSessionTests -class BotView.Tests.BinanceRequestParametersTests -class BotView.Tests.FuturesTradingTests`.
+
+Тесты используют вымышленные ключи и подменённые транспорты. Реальные заявки не отправляются.
```

## Snapshot before merging the CCXT client and service

Captured from the working tree before introducing SpotTradingService and FuturesTradingService. Includes local comments and formatting that were not committed.

### BotView/Services/Integrations/Ccxt/CcxtTradingClient.cs

SHA-256: `74a0dbe09233d258899a672b4310ba1b2ab05444f14bddc4e790716b30eaacd9`

```csharp
using System.Globalization;
using BotView.Models;

namespace BotView.Services.Integrations.Ccxt;

/// <summary>Thin, stateless boundary around a configured CCXT exchange. Responses remain native CCXT types.</summary>
public sealed class CcxtTradingClient : IAsyncDisposable
{
    private readonly ccxt.Exchange _exchange;
    private readonly string _venue; // exchange name
    internal ccxt.Exchange Transport => _exchange;

    /// <summary>Uses an already configured transport; construction never makes a network request.</summary>
    public CcxtTradingClient(ccxt.Exchange exchange, string venue)
    {
        _exchange = exchange;
        _venue = venue;
    }

    /// <summary>Loads the public instrument catalog using CCXT caching.</summary>
    public Task<Dictionary<string, ccxt.MarketInterface>> LoadMarketsAsync(CancellationToken ct = default) =>
        CallAsync(() => _exchange.LoadMarkets(), ct);

    /// <summary>Returns whether the configured transport supports a native operation.</summary>
    public bool Supports(string operation) => _exchange.has.GetValueOrDefault(operation) is true;

    /// <summary>Formats amount precision using the loaded market metadata.</summary>
    public decimal AmountToPrecision(string symbol, decimal amount) => decimal.Parse(
        _exchange.amountToPrecision(symbol, amount.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);

    /// <summary>Formats price precision using the loaded market metadata.</summary>
    public decimal PriceToPrecision(string symbol, decimal price) => decimal.Parse(
        _exchange.priceToPrecision(symbol, price.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);

    /// <summary>Returns the precision convention required to map market metadata.</summary>
    public int PrecisionMode => Convert.ToInt32(_exchange.precisionMode);

    /// <summary>Creates one spot limit order, optionally maker-only.</summary>
    public Task<ccxt.Order> CreateSpotLimitOrderAsync(string symbol, string side, decimal amount, decimal price,
        bool postOnly = false, CancellationToken ct = default) =>
        CallAsync(() => _exchange.CreateOrder(symbol, "limit", side, (double)amount, (double)price, SpotLimitParameters(postOnly)), ct);

    /// <summary>Creates one spot market order with a base-asset quantity and no limit price.</summary>
    public Task<ccxt.Order> CreateSpotMarketOrderAsync(string symbol, string side, decimal amount, CancellationToken ct = default) =>
        CallAsync(() => _exchange.CreateOrder(symbol, "market", side, (double)amount, null, SpotParameters()), ct);

    /// <summary>Creates one USDT contract limit order with explicit position parameters.</summary>
    public Task<ccxt.Order> CreateUsdtFuturesLimitOrderAsync(string symbol, string side, decimal contracts, decimal price,
        bool postOnly = false, bool reduceOnly = false, PositionSide? positionSide = null, CancellationToken ct = default)
    {
        var parameters = FuturesOrderParameters(reduceOnly, positionSide);
        parameters["timeInForce"] = postOnly ? _venue == "binance" ? "GTX" : "PostOnly" : "GTC";
        return CallAsync(() => _exchange.CreateOrder(symbol, "limit", side, (double)contracts, (double)price, parameters), ct);
    }

    /// <summary>Creates one USDT contract market order without a price or maker-only flag.</summary>
    public Task<ccxt.Order> CreateUsdtFuturesMarketOrderAsync(string symbol, string side, decimal contracts,
        bool reduceOnly = false, PositionSide? positionSide = null, CancellationToken ct = default) =>
        CallAsync(() => _exchange.CreateOrder(symbol, "market", side, (double)contracts, null,
            FuturesOrderParameters(reduceOnly, positionSide)), ct);

    /// <summary>Builds spot routing without derivative-only options.</summary>
    private Dictionary<string, object> SpotParameters() => _venue == "bybit"
        ? new() { ["category"] = "spot" } : new();

    /// <summary>Builds maker parameters without changing a rejected maker request into a taker order.</summary>
    private Dictionary<string, object> SpotLimitParameters(bool postOnly)
    {
        var parameters = SpotParameters();
        if (postOnly) parameters["postOnly"] = true;
        else parameters["timeInForce"] = "GTC";
        return parameters;
    }

    /// <summary>Selects linear USDT routing; exchange-specific options stay at this boundary.</summary>
    private Dictionary<string, object> UsdtFuturesParameters() => _venue == "binance"
        ? new() { ["type"] = "future" } : new() { ["category"] = "linear", ["type"] = "swap" };

    /// <summary>Maps the user's position selection directly; the exchange checks the account's position mode.</summary>
    private Dictionary<string, object> FuturesOrderParameters(bool reduceOnly, PositionSide? side)
    {
        var parameters = UsdtFuturesParameters();
        if (_venue == "binance")
        {
            parameters["positionSide"] = side == PositionSide.Long ? "LONG" : side == PositionSide.Short ? "SHORT" : "BOTH";
            if (reduceOnly) parameters["reduceOnly"] = true;
        }
        else
        {
            parameters["positionIdx"] = side == PositionSide.Long ? 1 : side == PositionSide.Short ? 2 : 0;
            if (reduceOnly) parameters["reduceOnly"] = true;
        }
        return parameters;
    }

    /// <summary>Cancels a spot order by exchange ID without assuming it was fully unfilled.</summary>
    public Task<ccxt.Order> CancelSpotOrderAsync(string id, string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.CancelOrder(id, symbol, SpotParameters()), ct);

    /// <summary>Cancels a contract order using its linear account route.</summary>
    public Task<ccxt.Order> CancelUsdtFuturesOrderAsync(string id, string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.CancelOrder(id, symbol, UsdtFuturesParameters()), ct);

    /// <summary>Fetches one spot order by its exchange ID.</summary>
    public Task<ccxt.Order> FetchSpotOrderAsync(string id, string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.FetchOrder(id, symbol, SpotParameters()), ct);

    /// <summary>Fetches one contract order by its exchange ID.</summary>
    public Task<ccxt.Order> FetchUsdtFuturesOrderAsync(string id, string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.FetchOrder(id, symbol, UsdtFuturesParameters()), ct);

    /// <summary>Fetches the current spot open-order snapshot using CCXT pagination.</summary>
    public Task<List<ccxt.Order>> FetchSpotOpenOrdersAsync(string symbol, CancellationToken ct = default)
    {
        var parameters = SpotParameters();
        parameters["paginate"] = true;
        return CallAsync(() => _exchange.FetchOpenOrders(symbol, null, null, parameters), ct);
    }

    /// <summary>Fetches the current contract open-order snapshot using CCXT pagination.</summary>
    public Task<List<ccxt.Order>> FetchUsdtFuturesOpenOrdersAsync(string symbol, CancellationToken ct = default)
    {
        var parameters = UsdtFuturesParameters();
        parameters["paginate"] = true;
        return CallAsync(() => _exchange.FetchOpenOrders(symbol, null, null, parameters), ct);
    }

    /// <summary>Fetches recent spot executions; this is a display window, not a durable history.</summary>
    public Task<List<ccxt.Trade>> FetchSpotMyTradesAsync(string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.FetchMyTrades(symbol, null, 100, SpotParameters()), ct);

    /// <summary>Fetches recent executions for a USDT contract.</summary>
    public Task<List<ccxt.Trade>> FetchUsdtFuturesMyTradesAsync(string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.FetchMyTrades(symbol, null, 100, UsdtFuturesParameters()), ct);

    /// <summary>Fetches native spot balances.</summary>
    public Task<ccxt.Balances> FetchSpotBalanceAsync(CancellationToken ct = default) =>
        CallAsync(() => _exchange.FetchBalance(SpotParameters()), ct);

    /// <summary>Fetches native futures collateral balances.</summary>
    public Task<ccxt.Balances> FetchUsdtFuturesBalanceAsync(CancellationToken ct = default) =>
        CallAsync(() => _exchange.FetchBalance(UsdtFuturesParameters()), ct);

    /// <summary>Fetches positions for one USDT contract.</summary>
    public Task<List<ccxt.Position>> FetchUsdtFuturesPositionsAsync(string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.FetchPositions(new List<string> { symbol }, UsdtFuturesParameters()), ct);

    /// <summary>Reads one private spot order batch.</summary>
    public Task<List<ccxt.Order>> WatchSpotOrdersAsync(string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.WatchOrders(symbol, null, null, SpotParameters()), ct);

    /// <summary>Reads one private contract order batch.</summary>
    public Task<List<ccxt.Order>> WatchUsdtFuturesOrdersAsync(string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.WatchOrders(symbol, null, null, UsdtFuturesParameters()), ct);

    /// <summary>Reads one private spot execution batch.</summary>
    public Task<List<ccxt.Trade>> WatchSpotMyTradesAsync(string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.WatchMyTrades(symbol, null, null, SpotParameters()), ct);

    /// <summary>Reads one private contract execution batch.</summary>
    public Task<List<ccxt.Trade>> WatchUsdtFuturesMyTradesAsync(string symbol, CancellationToken ct = default) =>
        CallAsync(() => _exchange.WatchMyTrades(symbol, null, null, UsdtFuturesParameters()), ct);

    /// <summary>Reads one private spot balance update.</summary>
    public Task<ccxt.Balances> WatchSpotBalanceAsync(CancellationToken ct = default) =>
        CallAsync(() => _exchange.WatchBalance(SpotParameters()), ct);

    /// <summary>Reads one private futures balance update.</summary>
    public Task<ccxt.Balances> WatchUsdtFuturesBalanceAsync(CancellationToken ct = default) =>
        CallAsync(() => _exchange.WatchBalance(UsdtFuturesParameters()), ct);

    /// <summary>Checks cancellation before dispatch and waits without repeating the native call.</summary>
    private static Task<T> CallAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return operation().WaitAsync(ct);
    }

    /// <summary>Closes local transports without canceling exchange orders.</summary>
    public async ValueTask DisposeAsync()
    {
        try { await _exchange.Close().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        finally { _exchange.apiKey = ""; _exchange.secret = ""; }
    }
}
```

### BotView/Services/Integrations/Ccxt/CcxtTradingService.cs

SHA-256: `d458f596d43e6cc51fe857af5a827f623065c414496625b0c68fbd6f57281d80`

```csharp
using BotView.Models;
using static BotView.Services.Integrations.Ccxt.CcxtTradingMapper;

namespace BotView.Services.Integrations.Ccxt;

/// <summary>Converts order-book calls and native CCXT results. Owns no background work or order state.</summary>
public sealed class CcxtTradingService : ITradingService
{
    private readonly CcxtTradingClient _client;
    private bool IsFutures => Account.Market == TradingMarket.UsdtFutures;
    public TradingAccountContext Account { get; }

    /// <summary>Binds a configured CCXT boundary to a single profile and network.</summary>
    public CcxtTradingService(TradingAccountProfile profile, CcxtTradingClient client)
    {
        _client = client;
        Account = new(profile.Id.ToString(), profile.Name, profile.Exchange, profile.Environment, profile.Market);
    }

    /// <summary>Loads and maps units without querying fees or pre-validating account funds.</summary>
    public Task<Instrument> FetchInstrumentAsync(InstrumentKey key, CancellationToken ct = default) => ReadAsync(async () =>
    {
        if (key.Venue != Account.Venue || key.Market != (IsFutures ? "futures" : "spot"))
            throw new NotSupportedException("The instrument belongs to another account market.");
        var market = (await _client.LoadMarketsAsync(ct))[key.SecurityId];
        var instrument = CcxtMarketDataProvider.MapInstrument(Account.Venue, market, _client.PrecisionMode) with { Key = key };
        return IsFutures ? instrument with
        {
            TradingRules = instrument.TradingRules! with
            {
                QuantityUnit = QuantityUnit.Contracts, ContractSize = Number(market.contractSize), SettlementCurrency = "USDT"
            }
        } : instrument;
    });

    /// <summary>Uses the exchange's native amount formatter.</summary>
    public decimal AmountToPrecision(InstrumentKey key, decimal quantity) => _client.AmountToPrecision(key.SecurityId, quantity);

    /// <summary>Uses the exchange's native price formatter.</summary>
    public decimal PriceToPrecision(InstrumentKey key, decimal price) => _client.PriceToPrecision(key.SecurityId, price);

    /// <summary>Routes one limit order to a named market operation.</summary>
    public Task<OrderSnapshot> CreateLimitOrderAsync(LimitOrderRequest request, CancellationToken ct = default) => ReadAsync(async () =>
    {
        ValidateRequest(request.Instrument, request.Side, request.Quantity);
        var amount = AmountToPrecision(request.Instrument, request.Quantity);
        if (amount <= 0) throw new ArgumentException("Quantity rounds to zero.");
        if (request.Price <= 0) throw new ArgumentException("A positive limit price is required.");
        var price = PriceToPrecision(request.Instrument, request.Price);
        if (price <= 0) throw new ArgumentException("Price rounds to zero.");
        var side = request.Side == OrderSide.Buy ? "buy" : "sell";
        var order = IsFutures
            ? await _client.CreateUsdtFuturesLimitOrderAsync(request.Instrument.SecurityId, side, amount, price,
                request.PostOnly, request.ReduceOnly, request.PositionSide, ct)
            : await _client.CreateSpotLimitOrderAsync(request.Instrument.SecurityId, side, amount, price, request.PostOnly, ct);
        return MapOrder(order, request.Instrument);
    });

    /// <summary>Routes one market order without manufacturing a limit price.</summary>
    public Task<OrderSnapshot> CreateMarketOrderAsync(MarketOrderRequest request, CancellationToken ct = default) => ReadAsync(async () =>
    {
        ValidateRequest(request.Instrument, request.Side, request.Quantity);
        var amount = AmountToPrecision(request.Instrument, request.Quantity);
        if (amount <= 0) throw new ArgumentException("Quantity rounds to zero.");
        var side = request.Side == OrderSide.Buy ? "buy" : "sell";
        var order = IsFutures
            ? await _client.CreateUsdtFuturesMarketOrderAsync(request.Instrument.SecurityId, side, amount,
                request.ReduceOnly, request.PositionSide, ct)
            : await _client.CreateSpotMarketOrderAsync(request.Instrument.SecurityId, side, amount, ct);
        return MapOrder(order, request.Instrument);
    });

    /// <summary>Requests cancellation through the profile's market route.</summary>
    public Task<OrderSnapshot> CancelOrderAsync(OrderReference order, CancellationToken ct = default) => ReadAsync(async () =>
        MapOrder(IsFutures
            ? await _client.CancelUsdtFuturesOrderAsync(order.ExchangeOrderId, order.Instrument.SecurityId, ct)
            : await _client.CancelSpotOrderAsync(order.ExchangeOrderId, order.Instrument.SecurityId, ct), order.Instrument));

    /// <summary>Fetches the provider's current view of one order.</summary>
    public Task<OrderSnapshot> FetchOrderAsync(OrderReference order, CancellationToken ct = default) => ReadAsync(async () =>
        MapOrder(IsFutures
            ? await _client.FetchUsdtFuturesOrderAsync(order.ExchangeOrderId, order.Instrument.SecurityId, ct)
            : await _client.FetchSpotOrderAsync(order.ExchangeOrderId, order.Instrument.SecurityId, ct), order.Instrument));

    /// <summary>Maps a fresh open-order snapshot.</summary>
    public Task<IReadOnlyList<OrderSnapshot>> FetchOpenOrdersAsync(InstrumentKey key, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<OrderSnapshot>>(async () => (IsFutures
            ? await _client.FetchUsdtFuturesOpenOrdersAsync(key.SecurityId, ct)
            : await _client.FetchSpotOpenOrdersAsync(key.SecurityId, ct)).Select(o => MapOrder(o, key)).ToArray());

    /// <summary>Maps recent exchange executions for the selected listing.</summary>
    public Task<IReadOnlyList<ExecutionSnapshot>> FetchMyTradesAsync(InstrumentKey key, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<ExecutionSnapshot>>(async () => (IsFutures
            ? await _client.FetchUsdtFuturesMyTradesAsync(key.SecurityId, ct)
            : await _client.FetchSpotMyTradesAsync(key.SecurityId, ct)).Select(t => Execution(t, key)).ToArray());

    /// <summary>Maps current balances and reported collateral.</summary>
    public Task<AccountSnapshot> FetchBalanceAsync(CancellationToken ct = default) => ReadAsync(async () => Balance(IsFutures
        ? await _client.FetchUsdtFuturesBalanceAsync(ct) : await _client.FetchSpotBalanceAsync(ct), Account));

    /// <summary>Maps native contract positions; spot has no separate contract position table.</summary>
    public Task<IReadOnlyList<PositionSnapshot>> FetchPositionsAsync(InstrumentKey key, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<PositionSnapshot>>(async () => IsFutures
            ? (await _client.FetchUsdtFuturesPositionsAsync(key.SecurityId, ct)).Select(p => Position(p, key)).ToArray() : []);

    /// <summary>Checks typed stream support without exposing CCXT strings to the order book.</summary>
    public bool SupportsStream(TradingStream stream) => _client.Supports(stream switch
    {
        TradingStream.Orders => "watchOrders", TradingStream.MyTrades => "watchMyTrades", _ => "watchBalance"
    });

    /// <summary>Copies a native private order batch.</summary>
    public Task<IReadOnlyList<OrderSnapshot>> WatchOrdersAsync(InstrumentKey key, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<OrderSnapshot>>(async () => (IsFutures
            ? await _client.WatchUsdtFuturesOrdersAsync(key.SecurityId, ct)
            : await _client.WatchSpotOrdersAsync(key.SecurityId, ct)).Where(o => o.symbol == key.SecurityId)
            .Select(o => MapOrder(o, key)).ToArray());

    /// <summary>Copies a native private execution batch.</summary>
    public Task<IReadOnlyList<ExecutionSnapshot>> WatchMyTradesAsync(InstrumentKey key, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<ExecutionSnapshot>>(async () => (IsFutures
            ? await _client.WatchUsdtFuturesMyTradesAsync(key.SecurityId, ct)
            : await _client.WatchSpotMyTradesAsync(key.SecurityId, ct)).Where(t => t.symbol == key.SecurityId)
            .Select(t => Execution(t, key)).ToArray());

    /// <summary>Copies a native balance stream update.</summary>
    public Task<AccountSnapshot> WatchBalanceAsync(CancellationToken ct = default) => ReadAsync(async () => Balance(IsFutures
        ? await _client.WatchUsdtFuturesBalanceAsync(ct) : await _client.WatchSpotBalanceAsync(ct), Account));

    /// <summary>Checks required fields and account routing without imposing exchange trading policy.</summary>
    private void ValidateRequest(InstrumentKey key, OrderSide side, decimal quantity)
    {
        if (!Enum.IsDefined(side) || quantity <= 0 || string.IsNullOrWhiteSpace(key.SecurityId))
            throw new ArgumentException("A symbol, direction and positive native quantity are required.");
        if (key.Venue != Account.Venue || key.Market != (IsFutures ? "futures" : "spot"))
            throw new ArgumentException("The instrument belongs to another account market.");
    }

    /// <summary>Converts errors once; it never repeats the underlying operation.</summary>
    private static async Task<T> ReadAsync<T>(Func<Task<T>> operation)
    {
        try { return await operation().ConfigureAwait(false); }
        catch (TradingServiceException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new TradingServiceException(new("ExchangeError", TradingErrorText.FromException(ex))); }
    }

    /// <summary>Releases the configured CCXT connection without canceling orders.</summary>
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
```

### BotView/Services/Integrations/Ccxt/CcxtTradingClientFactory.cs

SHA-256: `8f6f7de51c796b12a42315464e5b31b26b989ff9468fa6c579e59dbd197a4f81`

```csharp
using BotView.Models;

namespace BotView.Services.Integrations.Ccxt;

/// <summary>Creates isolated transports and selects the network before any request.</summary>
public static class CcxtTradingClientFactory
{
    /// <summary>Creates a configured spot or linear USDT CCXT.Pro client without connecting.</summary>
    public static CcxtTradingClient Create(TradingAccountProfile profile, AccountCredentials? credentials = null)
    {
        if (!Enum.IsDefined(profile.Market) || !Enum.IsDefined(profile.Environment))
            throw new NotSupportedException("Unsupported trading market or network.");

        var settings = ProOrderBookClient.CreateSettings();
        settings["apiKey"] = credentials?.ApiKey ?? "";
        settings["secret"] = credentials?.Secret ?? "";
        settings["verbose"] = false;
        var options = (Dictionary<string, object>)settings["options"];
        if (profile.Market == TradingMarket.UsdtFutures)
        {
            options["defaultType"] = profile.Exchange == "binance" ? "future" : "swap";
            options["defaultSubType"] = "linear";
            options["fetchMarkets"] = new Dictionary<string, object> { ["types"] = new List<object> { "linear" } };
        }
        else options["defaultType"] = "spot";

        ccxt.Exchange exchange = profile.Exchange switch
        {
            "binance" when profile.Market == TradingMarket.UsdtFutures => new ccxt.pro.binanceusdm(settings),
            "binance" => new ccxt.pro.binance(settings),
            "bybit" => new ccxt.pro.bybit(settings),
            _ => throw new NotSupportedException("Trading supports Binance and Bybit only.")
        };
        if (profile.Environment == TradingEnvironment.Testnet) exchange.setSandboxMode(true);
        return new(exchange, profile.Exchange);
    }
}
```

### BotView/Services/Trading/TradingSessionManager.cs

SHA-256: `e3894e18b6f36c91d8cd6d26b50e556f3e93217b5b293caf9a500408963eda45`

```csharp
using BotView.Models;
using BotView.Services.Integrations.Ccxt;

namespace BotView.Services.Trading;

/// <summary>Application-owned sessions survive window closure. Only explicit account changes retire them.</summary>
public sealed class TradingSessionManager : IAsyncDisposable
{
    private readonly ITradingAccountStore _profiles;
    private readonly ICredentialStore _credentials;
    private readonly Func<TradingAccountProfile, AccountCredentials, ITradingService> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, TradingAccountSession> _sessions = new();
    private readonly HashSet<Guid> _invalidated = new();
    private readonly Dictionary<Guid, TradingAccountProfile> _sessionProfiles = new();
    private readonly CancellationTokenSource _stop = new();
    private int _revision;
    private bool _disposed;
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>Builds the application manager without reading credentials or connecting until requested.</summary>
    public TradingSessionManager(ITradingAccountStore profiles, ICredentialStore credentials)
        : this(profiles, credentials, (p, c) => new CcxtTradingService(p, CcxtTradingClientFactory.Create(p, c))) { }

    /// <summary>Injects a deterministic exchange adapter factory for tests.</summary>
    internal TradingSessionManager(ITradingAccountStore profiles, ICredentialStore credentials,
        Func<TradingAccountProfile, AccountCredentials, ITradingService> factory)
    {
        _profiles = profiles;
        _credentials = credentials;
        _factory = factory;
        if (profiles is ITradingProfileNotifications changes) changes.Changed += ProfilesChanged;
        if (credentials is ICredentialNotifications keys) keys.CredentialsChanged += CredentialsChanged;
    }

    /// <summary>Returns profile metadata only; missing keys remain an explicit UI state.</summary>
    public TradingAccountsSettings Settings() => _profiles.Load();

    /// <summary>Authenticates on a background task, then reuses one session per profile and network.</summary>
    public Task<TradingAccountSession> GetAsync(Guid profileId, CancellationToken ct = default) => Task.Run(async () =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await RetireCoreAsync().ConfigureAwait(false);
            lock (_sync) if (_sessions.TryGetValue(profileId, out var existing)) return existing;
            var revision = Revision;
            var profile = _profiles.Load().Profiles.SingleOrDefault(p => p.Id == profileId)
                ?? throw new TradingServiceException(new("ProfileMissing", "The selected account no longer exists."));
            using var credentials = _credentials.Read(profileId)
                ?? throw new TradingServiceException(new("KeysRequired", "Enter and save this account's API keys first."));
            ITradingService? client = null;
            try
            {
                client = _factory(profile, credentials);
                await client.FetchBalanceAsync(token).ConfigureAwait(false);
                if (revision != Revision) throw new TradingServiceException(new("AccountChanged", "Account settings changed; select the account again."));
                var service = new TradingAccountSession(client);
                client = null;
                lock (_sync)
                {
                    _sessions[profileId] = service;
                    _sessionProfiles[profileId] = profile;
                }
                return service;
            }
            catch (OperationCanceledException) { throw; }
            catch (TradingServiceException) { throw; }
            catch (Exception ex) { throw new TradingServiceException(new("ConnectionFailed", TradingErrorText.FromException(ex))); }
            finally { if (client is not null) try { await client.DisposeAsync().ConfigureAwait(false); } catch (Exception) { } }
        }
        finally { _gate.Release(); }
    }, ct);

    /// <summary>Invalidates credentials immediately, before a window can send through a stale session.</summary>
    private void CredentialsChanged(Guid id)
    {
        lock (_sync)
        {
            _invalidated.Add(id);
            if (_sessions.TryGetValue(id, out var service)) service.Suspend();
        }
        Interlocked.Increment(ref _revision);
        _ = RetireAsync();
    }

    /// <summary>Profile changes retire affected connections; selecting an active profile leaves unchanged sessions supervised.</summary>
    private void ProfilesChanged()
    {
        try
        {
            var profiles = _profiles.Load().Profiles.ToDictionary(p => p.Id);
            lock (_sync)
            {
                foreach (var pair in _sessions)
                {
                    if (!profiles.TryGetValue(pair.Key, out var profile) || _sessionProfiles[pair.Key] != profile)
                    {
                        _invalidated.Add(pair.Key);
                        pair.Value.Suspend();
                    }
                }
            }
        }
        catch (Exception) { }
        Interlocked.Increment(ref _revision);
        _ = RetireAsync();
    }

    /// <summary>Serializes asynchronous retirement after store notifications.</summary>
    private async Task RetireAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await RetireCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Disposes invalidated clients and removes all aliases to a shared retired session.</summary>
    private async Task RetireCoreAsync()
    {
        TradingAccountSession[] retired;
        lock (_sync)
        {
            retired = _sessions.Where(p => _invalidated.Contains(p.Key)).Select(p => p.Value).Distinct().ToArray();
            foreach (var id in _sessions.Where(p => retired.Contains(p.Value)).Select(p => p.Key).ToArray())
            {
                _sessions.Remove(id);
                _sessionProfiles.Remove(id);
            }
            _invalidated.Clear();
        }
        foreach (var session in retired) try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
    }

    /// <summary>Ends local supervision without canceling any exchange order.</summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_profiles is ITradingProfileNotifications changes) changes.Changed -= ProfilesChanged;
        if (_credentials is ICredentialNotifications keys) keys.CredentialsChanged -= CredentialsChanged;
        await _stop.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            TradingAccountSession[] sessions;
            lock (_sync) { sessions = _sessions.Values.Distinct().ToArray(); _sessions.Clear(); }
            foreach (var session in sessions) try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
        }
        finally { _gate.Release(); }
    }
}
```

### BotView/MainWindow.xaml.cs

SHA-256: `b9af639ab78917dd612fa9766a43a2b269944a6b47ce8a25166d4208e0c0c772`

```csharp
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BotView.Chart;
using BotView.Chart.TechnicalAnalysis;
using BotView.Database;
using BotView.Models;
using BotView.Services;
using BotView.Services.Integrations.Ccxt;
using BotView.ViewModels;
using System.Windows.Threading;

namespace BotView
{
    /// <summary>Interaction logic for MainWindow.xaml — View adapter for ChartView and dialogs.</summary>
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly DispatcherTimer _renderTimeUpdateTimer;
        private readonly DispatcherTimer _quoteUpdateTimer;
        private ContextMenu? _pairContextMenu;
        private Point _favoriteDragStart;
        private TradingPairModel? _draggedFavorite;
        private readonly Dictionary<(string Exchange, string Symbol, TradingEnvironment Environment, TradingMarket Market), Windows.OrderBookWindow> _orderBooks = new();
        private readonly List<Task> _orderBookCleanup = new();
        private Windows.TradingAccountsWindow? _tradingAccountsWindow;

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

        private void OpenTradingAccounts(object sender, RoutedEventArgs e)
        {
            if (_tradingAccountsWindow is not null)
            {
                if (_tradingAccountsWindow.WindowState == WindowState.Minimized) _tradingAccountsWindow.WindowState = WindowState.Normal;
                _tradingAccountsWindow.Activate();
                return;
            }
            var window = new Windows.TradingAccountsWindow(App.TradingAccounts, App.TradingCredentials, App.AccountVerifier) { Owner = this };
            _tradingAccountsWindow = window;
            window.Closed += (_, _) => _tradingAccountsWindow = null;
            window.Show();
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

        private void lstTradingPairs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _draggedFavorite = null;
            if (e.OriginalSource is not DependencyObject source || FindAncestor<Button>(source) != null)
            {
                return;
            }

            var item = ItemsControl.ContainerFromElement(lstTradingPairs, source) as ListBoxItem;
            if (item?.DataContext is not TradingPairModel pair)
            {
                return;
            }

            _favoriteDragStart = e.GetPosition(null);
            _draggedFavorite = pair;
        }

        private void lstTradingPairs_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _draggedFavorite is null)
            {
                return;
            }

            Point position = e.GetPosition(null);
            if (Math.Abs(position.Y - _favoriteDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            TradingPairModel dragged = _draggedFavorite;
            _draggedFavorite = null;
            DragDrop.DoDragDrop(lstTradingPairs, dragged, DragDropEffects.Move);
        }

        private void lstTradingPairs_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(TradingPairModel))
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void lstTradingPairs_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(TradingPairModel)) is not TradingPairModel dragged)
            {
                return;
            }

            int oldIndex = _viewModel.favTradingPairs.IndexOf(dragged);
            if (oldIndex < 0)
            {
                return;
            }

            int insertIndex = _viewModel.favTradingPairs.Count;
            if (e.OriginalSource is DependencyObject source &&
                ItemsControl.ContainerFromElement(lstTradingPairs, source) is ListBoxItem container &&
                container.DataContext is TradingPairModel target)
            {
                insertIndex = _viewModel.favTradingPairs.IndexOf(target);
                if (e.GetPosition(container).Y > container.ActualHeight / 2)
                {
                    insertIndex++;
                }
            }

            if (oldIndex < insertIndex)
            {
                insertIndex--;
            }

            insertIndex = Math.Clamp(insertIndex, 0, _viewModel.favTradingPairs.Count - 1);
            if (oldIndex != insertIndex)
            {
                _viewModel.MoveFavorite(oldIndex, insertIndex);
            }

            e.Handled = true;
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match)
                {
                    return match;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private void Pair_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel.IsInstrumentCatalogMode) return;
            if (sender is not FrameworkElement target) return;
            var menu = Views.CoinInfoContextMenu.Create(target, pair =>
            {
                _viewModel.IsSearchDropdownOpen = false;
                _viewModel.IsComparisonDropdownOpen = false;
                new Windows.CoinInfoWindow(pair, App.CoinInfoProvider) { Owner = this }.Show();
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

        /// <summary>Opens one book per venue/pair/network, using the active profile's environment.</summary>
        private async void OpenOrderBook(string symbol, TradingMarket market)
        {
            if (_viewModel.IsInstrumentCatalogMode) return;
            _viewModel.IsSearchDropdownOpen = false;
            _viewModel.IsComparisonDropdownOpen = false;
            var environment = TradingEnvironment.Production;
            try
            {
                var settings = App.TradingAccounts.Load();
                var active = settings.Profiles.FirstOrDefault(p => p.Id == settings.ActiveProfiles.GetValueOrDefault(_viewModel.SelectedExchange));
                environment = active?.Environment ?? environment;
            }
            catch (Exception) { }
            var exchange = _viewModel.SelectedExchange;
            if (market == TradingMarket.UsdtFutures)
            {
                try
                {
                    var profile = new TradingAccountProfile(Guid.NewGuid(), "Catalog", exchange, environment, TradingMarket.UsdtFutures);
                    await using var client = CcxtTradingClientFactory.Create(profile);
                    var catalog = await client.LoadMarketsAsync();
                    var contracts = catalog.Values.Where(m => m.contract == true && m.linear == true && m.settle == "USDT"
                        && m.active != false && m.symbol!.Split(':')[0] == symbol.Split(':')[0])
                        .OrderBy(m => m.expiry.HasValue).ThenBy(m => m.expiry).Select(m => m.symbol!).ToArray();

                    if (!this.IsLoaded)
                    {
                        return;
                    }

                    if (contracts.Length == 0)
                    {
                        MessageBox.Show(this, "No active USDT contracts are available for this pair on the selected network.");
                        return;
                    }

                    var selection = new Windows.ContractSelectionWindow(contracts) { Owner = this };
                    if (selection.ShowDialog() != true)
                    {
                        return;
                    }

                    symbol = selection.SelectedContract!;
                }
                catch (Exception)
                {
                    MessageBox.Show(this, "Could not load the futures catalog. Check the exchange and network.");
                    return;
                }
            }

            var key = (Exchange: exchange, Symbol: symbol, Environment: environment, Market: market);
            if (_orderBooks.TryGetValue(key, out var existing))
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }
            var window = new Windows.OrderBookWindow(key.Exchange, symbol, App.OrderBookService, App.TradingSessions, environment, market) { Owner = this };
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
            var metricsWindow = new Windows.MetricsWindow(summary)
            {
                Owner = this
            };
            metricsWindow.ShowDialog();
        }

        private void OnExportMetricsRequested(string csvData)
        {
            var saveFileDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export performance metrics",
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
                $"Performance metrics successfully exported to:\n{saveFileDialog.FileName}",
                "Export complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void OnErrorOccurred(string title, string message)
        {
            void Show() => MessageBox.Show(message, title, MessageBoxButton.OK,
                title.Contains("exchange", StringComparison.OrdinalIgnoreCase)
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
```

### docs/TradingService.md

SHA-256: `e84807223843bae33daa16626e24d1a04c6570c002f79f4e02bd3bf762766b03`

```markdown
# Торговля из стакана

Торговый код разделён по обязанностям:

- `CcxtTradingClient` — тонкая обёртка над настроенным CCXT. Методы возвращают нативные типы библиотеки; фоновых задач и состояния заявок нет.
- `CcxtTradingClientFactory` — создание CCXT.Pro-клиентов Binance/Bybit, выбор Spot/USDT Futures и Testnet/Mainnet до первого запроса.
- `ITradingService` — контракт стакана с моделями приложения. `CcxtTradingService` выбирает метод обёртки и преобразует результаты и ошибки.
- `TradingAccountSession` — снимок данных для UI, приватные подписки и резервный REST-опрос.
- `TradingSessionManager` — одна сессия на профиль для всех его окон, замена соединения при изменении ключей или профиля.

Торговля через QUIK пока не реализована. Будущая обёртка QuikSharp и её адаптер смогут использовать контракт стакана. Каталог QUIK работает независимо.

## Заявки

По умолчанию выбран Limit. Он принимает цену и количество монет/контрактов либо бюджет в валюте котировки. Бюджет пересчитывается по форматированной цене с учётом размера контракта. Превью показывает подготовленные количество, цену и стоимость без комиссии. Post-only доступен только для Limit.

Market принимает только количество монет или контрактов. Поле цены, бюджет в USDT и Post-only отключены. При переключении типа количество очищается, чтобы бюджет случайно не стал количеством монет. Точная цена и стоимость Market не обещаются.

Обёртка содержит отдельные методы `CreateSpotLimitOrderAsync`, `CreateSpotMarketOrderAsync`, `CreateUsdtFuturesLimitOrderAsync`, `CreateUsdtFuturesMarketOrderAsync`. Контракт стакана принимает отдельные `LimitOrderRequest` и `MarketOrderRequest`.

Проверяются обязательные поля, положительные числа, направление и преобразование точности средствами CCXT. Баланс, комиссии, маржу, лимиты и режим позиций проверяет биржа. Ошибки показываются в форме без ключей, подписанных URL и исходных тел запросов.

Один клик отправляет один запрос. На время запроса форма блокируется. Тайм-аут означает неизвестный результат и не вызывает автоматическую повторную отправку. Перед повторным действием следует проверить заявки на бирже.

## Данные и подписки

Начальная загрузка получает инструмент, баланс, открытые заявки и последние исполнения. Приватные `WatchOrders`, `WatchMyTrades`, `WatchBalance` обновляют снимок интерфейса. Для фьючерсов позиции опрашиваются REST раз в две секунды.

Если поток не поддерживается или завершился ошибкой, REST раз в две секунды обновляет только соответствующие данные. После восстановления потока загружается свежий снимок, резервный опрос прекращается. Медленный REST-ответ не заменяет более новые приватные события. Заявки объединяются по инструменту и биржевому ID, исполнения — также по ID исполнения. Это текущие данные UI, а не долговременная история.

После успешного создания/отмены обновляются данные аккаунта. Ошибка чтения не превращает успешную отправку в отказ. Массовая отмена подтверждает зафиксированный список ID и показывает индивидуальный результат каждой отмены.

## Жизненный цикл

Журнал намерений, фазы операций, локальные резервы и восстановление отправок удалены. Старый `trading-journal.db` не читается и не удаляется. Формат профилей сохранён; ключи остаются только в памяти.

Закрытие стакана оставляет общую сессию приложения работающей. Смена ключей/профиля закрывает старое соединение; завершение приложения освобождает транспорт. Эти действия не отменяют биржевые заявки.

## Автономные проверки

Сборка: `dotnet build BotView.Tests/BotView.Tests.csproj --no-restore`.

Тесты: `dotnet run --project BotView.Tests/BotView.Tests.csproj --no-build -- -class BotView.Tests.CcxtTradingClientTests -class BotView.Tests.TradingAccountSessionTests -class BotView.Tests.BinanceRequestParametersTests -class BotView.Tests.FuturesTradingTests`.

Тесты используют вымышленные ключи и подменённые транспорты. Реальные заявки не отправляются.
```

## Snapshot before extracting AbstractTradingService

Complete working-tree versions before sharing the Spot/Futures implementation through a base class.

### BotView/Services/Trading/SpotTradingService.cs

SHA-256: `abeeb898216a7c680852ed87d9268f97e4b874420db0d3827e10b70a3dbe47ed`

```csharp
using System.Globalization;
using BotView.Models;
using BotView.Services.Integrations.Ccxt;
using static BotView.Services.Integrations.Ccxt.CcxtTradingMapper;

namespace BotView.Services.Trading;

/// <summary>Direct CCXT trading for cryptocurrency spot markets; equity connectivity will be added separately. Inputs and results use application models.</summary>
public sealed class SpotTradingService : ITradingService
{
    private readonly ccxt.Exchange _exchange;
    private readonly string _venue; // exchange name
    public TradingAccountContext Account { get; }

    /// <summary>Binds an already configured transport to one profile without making a network request.</summary>
    public SpotTradingService(TradingAccountProfile profile, ccxt.Exchange exchange)
    {
        if (profile.Market != TradingMarket.Spot)
        {
            throw new ArgumentException("This service requires a Spot profile.", nameof(profile));
        }

        _exchange = exchange;
        _venue = profile.Exchange;
        Account = new(profile.Id.ToString(), profile.Name, profile.Exchange, profile.Environment, profile.Market);
    }

    /// <summary>Loads the instrument's units and precision metadata without checking trading funds or limits.</summary>
    public Task<Instrument> FetchInstrumentAsync(InstrumentKey key, CancellationToken ct = default) => ExecuteAsync(async () =>
    {
        ValidateInstrument(key);
        var markets = await _exchange.LoadMarkets().ConfigureAwait(false);
        var market = markets[key.SecurityId];
        var instrument = CcxtMarketDataProvider.MapInstrument(_venue, market, Convert.ToInt32(_exchange.precisionMode)) with { Key = key };
        return instrument;
    }, ct);

    /// <summary>Formats the native quantity using the loaded CCXT precision rules.</summary>
    public decimal AmountToPrecision(InstrumentKey key, decimal quantity) => decimal.Parse(
        _exchange.amountToPrecision(key.SecurityId, quantity.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);

    /// <summary>Formats the limit price using the loaded CCXT precision rules.</summary>
    public decimal PriceToPrecision(InstrumentKey key, decimal price) => decimal.Parse(
        _exchange.priceToPrecision(key.SecurityId, price.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);

    /// <summary>Creates one limit order directly through CCXT and returns its detached acknowledgement.</summary>
    public Task<OrderSnapshot> CreateLimitOrderAsync(LimitOrderRequest request, CancellationToken ct = default) => ExecuteAsync(async () =>
    {
        ValidateRequest(request.Instrument, request.Side, request.Quantity);
        var amount = AmountToPrecision(request.Instrument, request.Quantity);
        if (amount <= 0)
        {
            throw new ArgumentException("Quantity rounds to zero.");
        }

        if (request.Price <= 0)
        {
            throw new ArgumentException("A positive limit price is required.");
        }

        var price = PriceToPrecision(request.Instrument, request.Price);
        if (price <= 0)
        {
            throw new ArgumentException("Price rounds to zero.");
        }

        var side = request.Side == OrderSide.Buy ? "buy" : "sell";
        var parameters = LimitParameters(request.PostOnly);
        var order = await _exchange.CreateOrder(request.Instrument.SecurityId, "limit", side,
            (double)amount, (double)price, parameters).ConfigureAwait(false);
        return MapOrder(order, request.Instrument);
    }, ct);

    /// <summary>Creates one market order in native units without supplying a price or maker flag.</summary>
    public Task<OrderSnapshot> CreateMarketOrderAsync(MarketOrderRequest request, CancellationToken ct = default) => ExecuteAsync(async () =>
    {
        ValidateRequest(request.Instrument, request.Side, request.Quantity);
        var amount = AmountToPrecision(request.Instrument, request.Quantity);
        if (amount <= 0)
        {
            throw new ArgumentException("Quantity rounds to zero.");
        }

        var side = request.Side == OrderSide.Buy ? "buy" : "sell";
        var order = await _exchange.CreateOrder(request.Instrument.SecurityId, "market", side,
            (double)amount, null, Parameters()).ConfigureAwait(false);
        return MapOrder(order, request.Instrument);
    }, ct);

    /// <summary>Requests cancellation once; the exchange response is not treated as a guarantee of no fills.</summary>
    public Task<OrderSnapshot> CancelOrderAsync(OrderReference order, CancellationToken ct = default) => ExecuteAsync(async () =>
    {
        ValidateInstrument(order.Instrument);
        var response = await _exchange.CancelOrder(order.ExchangeOrderId, order.Instrument.SecurityId, Parameters()).ConfigureAwait(false);
        return MapOrder(response, order.Instrument);
    }, ct);

    /// <summary>Fetches one order by its exchange identity.</summary>
    public Task<OrderSnapshot> FetchOrderAsync(OrderReference order, CancellationToken ct = default) => ExecuteAsync(async () =>
    {
        ValidateInstrument(order.Instrument);
        var response = await _exchange.FetchOrder(order.ExchangeOrderId, order.Instrument.SecurityId, Parameters()).ConfigureAwait(false);
        return MapOrder(response, order.Instrument);
    }, ct);

    /// <summary>Fetches the current open-order snapshot with CCXT pagination enabled.</summary>
    public Task<IReadOnlyList<OrderSnapshot>> FetchOpenOrdersAsync(InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<OrderSnapshot>>(async () =>
        {
            ValidateInstrument(key);
            var parameters = Parameters();
            parameters["paginate"] = true;
            var orders = await _exchange.FetchOpenOrders(key.SecurityId, null, null, parameters).ConfigureAwait(false);
            return orders.Select(order => MapOrder(order, key)).ToArray();
        }, ct);

    /// <summary>Fetches the latest executions for display, preserving provider execution IDs and fees.</summary>
    public Task<IReadOnlyList<ExecutionSnapshot>> FetchMyTradesAsync(InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<ExecutionSnapshot>>(async () =>
        {
            ValidateInstrument(key);
            var trades = await _exchange.FetchMyTrades(key.SecurityId, null, 100, Parameters()).ConfigureAwait(false);
            return trades.Select(trade => Execution(trade, key)).ToArray();
        }, ct);

    /// <summary>Fetches balances and collateral as reported by the exchange.</summary>
    public Task<AccountSnapshot> FetchBalanceAsync(CancellationToken ct = default) => ExecuteAsync(async () =>
        Balance(await _exchange.FetchBalance(Parameters()).ConfigureAwait(false), Account), ct);

    /// <summary>Spot holdings are balances; no separate futures positions are returned.</summary>
    public Task<IReadOnlyList<PositionSnapshot>> FetchPositionsAsync(InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<PositionSnapshot>>(() =>
        {
            ValidateInstrument(key);
            return Task.FromResult<IReadOnlyList<PositionSnapshot>>([]);
        }, ct);

    /// <summary>Reports availability of a typed private stream without leaking CCXT method names into the UI.</summary>
    public bool SupportsStream(TradingStream stream) => _exchange.has.GetValueOrDefault(stream switch
    {
        TradingStream.Orders => "watchOrders",
        TradingStream.MyTrades => "watchMyTrades",
        _ => "watchBalance"
    }) is true;

    /// <summary>Copies one private order batch into application models.</summary>
    public Task<IReadOnlyList<OrderSnapshot>> WatchOrdersAsync(InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<OrderSnapshot>>(async () =>
        {
            ValidateInstrument(key);
            var orders = await _exchange.WatchOrders(key.SecurityId, null, null, Parameters()).ConfigureAwait(false);
            return orders.Where(order => order.symbol == key.SecurityId).Select(order => MapOrder(order, key)).ToArray();
        }, ct);

    /// <summary>Copies one private execution batch into application models.</summary>
    public Task<IReadOnlyList<ExecutionSnapshot>> WatchMyTradesAsync(InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<ExecutionSnapshot>>(async () =>
        {
            ValidateInstrument(key);
            var trades = await _exchange.WatchMyTrades(key.SecurityId, null, null, Parameters()).ConfigureAwait(false);
            return trades.Where(trade => trade.symbol == key.SecurityId).Select(trade => Execution(trade, key)).ToArray();
        }, ct);

    /// <summary>Copies one private balance update into application models.</summary>
    public Task<AccountSnapshot> WatchBalanceAsync(CancellationToken ct = default) => ExecuteAsync(async () =>
        Balance(await _exchange.WatchBalance(Parameters()).ConfigureAwait(false), Account), ct);

    /// <summary>Uses the spot route without derivative-only request parameters.</summary>
    private Dictionary<string, object> Parameters() => _venue == "bybit"
        ? new() { ["category"] = "spot" } : new();

    /// <summary>Preserves maker-only intent and otherwise requests an ordinary GTC limit order.</summary>
    private Dictionary<string, object> LimitParameters(bool postOnly)
    {
        var parameters = Parameters();
        if (postOnly)
        {
            parameters["postOnly"] = true;
        }
        else
        {
            parameters["timeInForce"] = "GTC";
        }

        return parameters;
    }

    /// <summary>Rejects missing fields and incorrectly routed instruments without imposing exchange trading policy.</summary>
    private void ValidateRequest(InstrumentKey key, OrderSide side, decimal quantity)
    {
        ValidateInstrument(key);
        if (!Enum.IsDefined(side) || quantity <= 0)
        {
            throw new ArgumentException("A direction and positive native quantity are required.");
        }
    }

    /// <summary>Ensures requests remain bound to the profile's venue and market.</summary>
    private void ValidateInstrument(InstrumentKey key)
    {
        if (key.Venue != _venue || key.Market != "spot" || string.IsNullOrWhiteSpace(key.SecurityId))
        {
            throw new ArgumentException("The instrument must belong to this account's market and include a symbol.");
        }
    }

    /// <summary>Checks cancellation before dispatch, invokes once and converts errors without retrying.</summary>
    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            return await operation().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TradingServiceException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new TradingServiceException(new("ExchangeError", TradingErrorText.FromException(ex)));
        }
    }

    /// <summary>Closes the transport without canceling exchange orders and clears its credentials.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _exchange.Close().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            _exchange.apiKey = "";
            _exchange.secret = "";
        }
    }
}
```

### BotView/Services/Trading/FuturesTradingService.cs

SHA-256: `2538701804b09a385ff6bfd31223dec96407b9cc7aa3898eda76d25de5600573`

```csharp
using System.Globalization;
using BotView.Models;
using BotView.Services.Integrations.Ccxt;
using static BotView.Services.Integrations.Ccxt.CcxtTradingMapper;

namespace BotView.Services.Trading;

/// <summary>Direct CCXT trading for USDT-margined cryptocurrency futures. Inputs and results use application models.</summary>
public sealed class FuturesTradingService : ITradingService
{
    private readonly ccxt.Exchange _exchange;
    private readonly string _venue; // exchange name
    public TradingAccountContext Account { get; }

    /// <summary>Binds an already configured transport to one profile without making a network request.</summary>
    public FuturesTradingService(TradingAccountProfile profile, ccxt.Exchange exchange)
    {
        if (profile.Market != TradingMarket.UsdtFutures)
        {
            throw new ArgumentException("This service requires a UsdtFutures profile.", nameof(profile));
        }

        _exchange = exchange;
        _venue = profile.Exchange;
        Account = new(profile.Id.ToString(), profile.Name, profile.Exchange, profile.Environment, profile.Market);
    }

    /// <summary>Loads the instrument's units and precision metadata without checking trading funds or limits.</summary>
    public Task<Instrument> FetchInstrumentAsync(InstrumentKey key, CancellationToken ct = default) => ExecuteAsync(async () =>
    {
        ValidateInstrument(key);
        var markets = await _exchange.LoadMarkets().ConfigureAwait(false);
        var market = markets[key.SecurityId];
        var instrument = CcxtMarketDataProvider.MapInstrument(_venue, market, Convert.ToInt32(_exchange.precisionMode)) with { Key = key };
        return instrument with
        {
            TradingRules = instrument.TradingRules! with
            {
                QuantityUnit = QuantityUnit.Contracts,
                ContractSize = Number(market.contractSize),
                SettlementCurrency = "USDT"
            }
        };
    }, ct);

    /// <summary>Formats the native quantity using the loaded CCXT precision rules.</summary>
    public decimal AmountToPrecision(InstrumentKey key, decimal quantity) => decimal.Parse(
        _exchange.amountToPrecision(key.SecurityId, quantity.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);

    /// <summary>Formats the limit price using the loaded CCXT precision rules.</summary>
    public decimal PriceToPrecision(InstrumentKey key, decimal price) => decimal.Parse(
        _exchange.priceToPrecision(key.SecurityId, price.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);

    /// <summary>Creates one limit order directly through CCXT and returns its detached acknowledgement.</summary>
    public Task<OrderSnapshot> CreateLimitOrderAsync(LimitOrderRequest request, CancellationToken ct = default) => ExecuteAsync(async () =>
    {
        ValidateRequest(request.Instrument, request.Side, request.Quantity);
        var amount = AmountToPrecision(request.Instrument, request.Quantity);
        if (amount <= 0)
        {
            throw new ArgumentException("Quantity rounds to zero.");
        }

        if (request.Price <= 0)
        {
            throw new ArgumentException("A positive limit price is required.");
        }

        var price = PriceToPrecision(request.Instrument, request.Price);
        if (price <= 0)
        {
            throw new ArgumentException("Price rounds to zero.");
        }

        var side = request.Side == OrderSide.Buy ? "buy" : "sell";
        var parameters = LimitParameters(request.PostOnly, request.ReduceOnly, request.PositionSide);
        var order = await _exchange.CreateOrder(request.Instrument.SecurityId, "limit", side,
            (double)amount, (double)price, parameters).ConfigureAwait(false);
        return MapOrder(order, request.Instrument);
    }, ct);

    /// <summary>Creates one market order in native units without supplying a price or maker flag.</summary>
    public Task<OrderSnapshot> CreateMarketOrderAsync(MarketOrderRequest request, CancellationToken ct = default) => ExecuteAsync(async () =>
    {
        ValidateRequest(request.Instrument, request.Side, request.Quantity);
        var amount = AmountToPrecision(request.Instrument, request.Quantity);
        if (amount <= 0)
        {
            throw new ArgumentException("Quantity rounds to zero.");
        }

        var side = request.Side == OrderSide.Buy ? "buy" : "sell";
        var order = await _exchange.CreateOrder(request.Instrument.SecurityId, "market", side,
            (double)amount, null, OrderParameters(request.ReduceOnly, request.PositionSide)).ConfigureAwait(false);
        return MapOrder(order, request.Instrument);
    }, ct);

    /// <summary>Requests cancellation once; the exchange response is not treated as a guarantee of no fills.</summary>
    public Task<OrderSnapshot> CancelOrderAsync(OrderReference order, CancellationToken ct = default) => ExecuteAsync(async () =>
    {
        ValidateInstrument(order.Instrument);
        var response = await _exchange.CancelOrder(order.ExchangeOrderId, order.Instrument.SecurityId, Parameters()).ConfigureAwait(false);
        return MapOrder(response, order.Instrument);
    }, ct);

    /// <summary>Fetches one order by its exchange identity.</summary>
    public Task<OrderSnapshot> FetchOrderAsync(OrderReference order, CancellationToken ct = default) => ExecuteAsync(async () =>
    {
        ValidateInstrument(order.Instrument);
        var response = await _exchange.FetchOrder(order.ExchangeOrderId, order.Instrument.SecurityId, Parameters()).ConfigureAwait(false);
        return MapOrder(response, order.Instrument);
    }, ct);

    /// <summary>Fetches the current open-order snapshot with CCXT pagination enabled.</summary>
    public Task<IReadOnlyList<OrderSnapshot>> FetchOpenOrdersAsync(InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<OrderSnapshot>>(async () =>
        {
            ValidateInstrument(key);
            var parameters = Parameters();
            parameters["paginate"] = true;
            var orders = await _exchange.FetchOpenOrders(key.SecurityId, null, null, parameters).ConfigureAwait(false);
            return orders.Select(order => MapOrder(order, key)).ToArray();
        }, ct);

    /// <summary>Fetches the latest executions for display, preserving provider execution IDs and fees.</summary>
    public Task<IReadOnlyList<ExecutionSnapshot>> FetchMyTradesAsync(InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<ExecutionSnapshot>>(async () =>
        {
            ValidateInstrument(key);
            var trades = await _exchange.FetchMyTrades(key.SecurityId, null, 100, Parameters()).ConfigureAwait(false);
            return trades.Select(trade => Execution(trade, key)).ToArray();
        }, ct);

    /// <summary>Fetches balances and collateral as reported by the exchange.</summary>
    public Task<AccountSnapshot> FetchBalanceAsync(CancellationToken ct = default) => ExecuteAsync(async () =>
        Balance(await _exchange.FetchBalance(Parameters()).ConfigureAwait(false), Account), ct);

    /// <summary>Fetches contract positions, keeping hedge legs and margin information.</summary>
    public Task<IReadOnlyList<PositionSnapshot>> FetchPositionsAsync(InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<PositionSnapshot>>(async () =>
        {
            ValidateInstrument(key);
            var positions = await _exchange.FetchPositions(new List<string> { key.SecurityId }, Parameters()).ConfigureAwait(false);
            return positions.Select(position => Position(position, key)).ToArray();
        }, ct);

    /// <summary>Reports availability of a typed private stream without leaking CCXT method names into the UI.</summary>
    public bool SupportsStream(TradingStream stream) => _exchange.has.GetValueOrDefault(stream switch
    {
        TradingStream.Orders => "watchOrders",
        TradingStream.MyTrades => "watchMyTrades",
        _ => "watchBalance"
    }) is true;

    /// <summary>Copies one private order batch into application models.</summary>
    public Task<IReadOnlyList<OrderSnapshot>> WatchOrdersAsync(InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<OrderSnapshot>>(async () =>
        {
            ValidateInstrument(key);
            var orders = await _exchange.WatchOrders(key.SecurityId, null, null, Parameters()).ConfigureAwait(false);
            return orders.Where(order => order.symbol == key.SecurityId).Select(order => MapOrder(order, key)).ToArray();
        }, ct);

    /// <summary>Copies one private execution batch into application models.</summary>
    public Task<IReadOnlyList<ExecutionSnapshot>> WatchMyTradesAsync(InstrumentKey key, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<ExecutionSnapshot>>(async () =>
        {
            ValidateInstrument(key);
            var trades = await _exchange.WatchMyTrades(key.SecurityId, null, null, Parameters()).ConfigureAwait(false);
            return trades.Where(trade => trade.symbol == key.SecurityId).Select(trade => Execution(trade, key)).ToArray();
        }, ct);

    /// <summary>Copies one private balance update into application models.</summary>
    public Task<AccountSnapshot> WatchBalanceAsync(CancellationToken ct = default) => ExecuteAsync(async () =>
        Balance(await _exchange.WatchBalance(Parameters()).ConfigureAwait(false), Account), ct);

    /// <summary>Selects the linear USDT futures account route.</summary>
    private Dictionary<string, object> Parameters() => _venue == "binance"
        ? new() { ["type"] = "future" } : new() { ["category"] = "linear", ["type"] = "swap" };

    /// <summary>Maps the selected position side directly; the exchange validates account mode and Reduce-only.</summary>
    private Dictionary<string, object> OrderParameters(bool reduceOnly, PositionSide? side)
    {
        var parameters = Parameters();
        if (_venue == "binance")
        {
            parameters["positionSide"] = side == PositionSide.Long ? "LONG" : side == PositionSide.Short ? "SHORT" : "BOTH";
        }
        else
        {
            parameters["positionIdx"] = side == PositionSide.Long ? 1 : side == PositionSide.Short ? 2 : 0;
        }

        if (reduceOnly)
        {
            parameters["reduceOnly"] = true;
        }

        return parameters;
    }

    /// <summary>Applies the venue's maker-only duration to a futures limit order.</summary>
    private Dictionary<string, object> LimitParameters(bool postOnly, bool reduceOnly, PositionSide? side)
    {
        var parameters = OrderParameters(reduceOnly, side);
        parameters["timeInForce"] = postOnly ? _venue == "binance" ? "GTX" : "PostOnly" : "GTC";
        return parameters;
    }

    /// <summary>Loads the public list of linear USDT contracts matching a spot pair without requiring keys.</summary>
    public Task<IReadOnlyList<string>> FetchTradingPairsAsync(string spotSymbol, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<string>>(async () =>
        {
            var markets = await _exchange.LoadMarkets().ConfigureAwait(false);
            var pair = spotSymbol.Split(':')[0];
            return markets.Values.Where(m => m.contract == true && m.linear == true && m.settle == "USDT"
                    && m.active != false && m.symbol!.Split(':')[0] == pair)
                .OrderBy(m => m.expiry.HasValue).ThenBy(m => m.expiry).Select(m => m.symbol!).ToArray();
        }, ct);

    /// <summary>Rejects missing fields and incorrectly routed instruments without imposing exchange trading policy.</summary>
    private void ValidateRequest(InstrumentKey key, OrderSide side, decimal quantity)
    {
        ValidateInstrument(key);
        if (!Enum.IsDefined(side) || quantity <= 0)
        {
            throw new ArgumentException("A direction and positive native quantity are required.");
        }
    }

    /// <summary>Ensures requests remain bound to the profile's venue and market.</summary>
    private void ValidateInstrument(InstrumentKey key)
    {
        if (key.Venue != _venue || key.Market != "futures" || string.IsNullOrWhiteSpace(key.SecurityId))
        {
            throw new ArgumentException("The instrument must belong to this account's market and include a symbol.");
        }
    }

    /// <summary>Checks cancellation before dispatch, invokes once and converts errors without retrying.</summary>
    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            return await operation().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TradingServiceException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new TradingServiceException(new("ExchangeError", TradingErrorText.FromException(ex)));
        }
    }

    /// <summary>Closes the transport without canceling exchange orders and clears its credentials.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _exchange.Close().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            _exchange.apiKey = "";
            _exchange.secret = "";
        }
    }
}
```

### BotView/Services/Trading/TradingServiceFactory.cs

SHA-256: `f272f9fc53c196f6291ae1f9cc09c3362e5d8e3cb07bf6df4eb5c27cee660db6`

```csharp
using BotView.Models;

using BotView.Services.Integrations.Ccxt;

namespace BotView.Services.Trading;

/// <summary>Creates isolated transports and selects the network before any request.</summary>
public static class TradingServiceFactory
{
    /// <summary>Creates one direct trading service for the profile's market.</summary>
    public static ITradingService Create(TradingAccountProfile profile, AccountCredentials? credentials = null) => profile.Market switch
    {
        TradingMarket.Spot => new SpotTradingService(profile, CreateExchange(profile, credentials)),
        TradingMarket.UsdtFutures => CreateFutures(profile, credentials),
        _ => throw new NotSupportedException("Unsupported trading market.")
    };

    /// <summary>Creates the futures service, including public contract catalog access without API keys.</summary>
    public static FuturesTradingService CreateFutures(TradingAccountProfile profile, AccountCredentials? credentials = null)
    {
        if (profile.Market != TradingMarket.UsdtFutures)
        {
            throw new ArgumentException("A futures profile is required.", nameof(profile));
        }

        return new(profile, CreateExchange(profile, credentials));
    }

    /// <summary>Creates a configured spot or linear USDT CCXT.Pro client without connecting.</summary>
    internal static ccxt.Exchange CreateExchange(TradingAccountProfile profile, AccountCredentials? credentials = null)
    {
        if (!Enum.IsDefined(profile.Market) || !Enum.IsDefined(profile.Environment))
            throw new NotSupportedException("Unsupported trading market or network.");

        var settings = ProOrderBookClient.CreateSettings();
        settings["apiKey"] = credentials?.ApiKey ?? "";
        settings["secret"] = credentials?.Secret ?? "";
        settings["verbose"] = false;
        var options = (Dictionary<string, object>)settings["options"];
        if (profile.Market == TradingMarket.UsdtFutures)
        {
            options["defaultType"] = profile.Exchange == "binance" ? "future" : "swap";
            options["defaultSubType"] = "linear";
            options["fetchMarkets"] = new Dictionary<string, object> { ["types"] = new List<object> { "linear" } };
        }
        else options["defaultType"] = "spot";

        ccxt.Exchange exchange = profile.Exchange switch
        {
            "binance" when profile.Market == TradingMarket.UsdtFutures => new ccxt.pro.binanceusdm(settings),
            "binance" => new ccxt.pro.binance(settings),
            "bybit" => new ccxt.pro.bybit(settings),
            _ => throw new NotSupportedException("Trading supports Binance and Bybit only.")
        };
        if (profile.Environment == TradingEnvironment.Testnet) exchange.setSandboxMode(true);
        return exchange;
    }
}
```


## Extraction of OrderInputCalculator

Previous calculation methods from TradingAccountSession.cs; moved to OrderInputCalculator without changing the formulas.

```csharp
    /// <summary>Converts the selected quantity unit and formats the actual amount sent to the provider.</summary>
    public decimal ConvertQuantity(InstrumentKey key, decimal input, decimal price, OrderQuantityCurrency currency)
    {
        var quantity = input;
        if (currency == OrderQuantityCurrency.Quote) quantity /= price * ContractSize(key);
        return _service.AmountToPrecision(key, quantity);
    }

    /// <summary>Formats the actual limit price sent to the provider.</summary>
    public decimal PriceToPrecision(InstrumentKey key, decimal price) => _service.PriceToPrecision(key, price);

    /// <summary>Calculates order notional for preview; this is not a margin check.</summary>
    public decimal OrderNotional(InstrumentKey key, decimal quantity, decimal price) => quantity * price * ContractSize(key);

    /// <summary>Reads native contract valuation metadata loaded when the instrument was attached.</summary>
    private decimal ContractSize(InstrumentKey key)
    {
        lock (_sync) return _instruments[key].TradingRules?.ContractSize ?? 1m;
    }

```


## Before adding futures leverage controls

### BotView/ViewModels/OrderBookTradingViewModel.cs

```csharp
﻿using System.Collections.ObjectModel;
using BotView.Models;
using BotView.Services;

namespace BotView.ViewModels;

public sealed partial class OrderBookViewModel
{
    private TradingSessionManager? _tradingManager;
    private TradingAccountSession? _trading;
    private TradingAccountProfile? _selectedAccount;
    private bool _tradeBusy;
    private bool _bindingAccount;
    private bool _postOnly;
    private int _accountGeneration;
    private int _managerRevision = -1;
    private OrderQuantityCurrency _quantityCurrency;
    private OrderSnapshot? _selectedOrder;
    private TradingAccountState? _lastTradingState;
    private Task _bindTask = Task.CompletedTask;

    public TradingEnvironment Environment { get; private set; }
    public string EnvironmentText => Environment == TradingEnvironment.Testnet ? "Testnet" : "MAINNET · live trading";
    public bool IsFutures => Market == TradingMarket.UsdtFutures;
    public InstrumentKey TradingInstrument => new(Exchange, IsFutures ? "futures" : "spot", "", Symbol);
    private bool _reduceOnly;
    private string _positionTarget = "Auto";
    public string[] PositionTargets { get; } = ["Auto", "Long", "Short"];
    public bool ReduceOnly { get => _reduceOnly; set { if (IsTradeEditable) { _reduceOnly = value; Changed(); } } }
    public string PositionTarget { get => _positionTarget; set { if (IsTradeEditable && PositionTargets.Contains(value)) { _positionTarget = value; Changed(); } } }
    private PositionSide? SelectedPositionSide => PositionTarget == "Long" ? PositionSide.Long : PositionTarget == "Short" ? PositionSide.Short : null;
    public string PositionsText => !IsFutures ? "" : string.Join(" · ", (_trading?.Current.Balance?.Positions ?? [])
        .Where(p => p.Instrument == TradingInstrument)
        .Select(p => $"{p.Side}: {p.Quantity} contracts · {p.Leverage}x · {p.MarginMode} · PnL {p.UnrealizedPnl?.Amount} USDT"));
    public ObservableCollection<TradingAccountProfile> Accounts { get; } = new();
    public IReadOnlyList<OrderSnapshot> Orders { get; private set; } = [];
    public IReadOnlyList<ExecutionSnapshot> Executions { get; private set; } = [];
    public OrderQuantityCurrency[] QuantityCurrencies { get; } = [OrderQuantityCurrency.Base, OrderQuantityCurrency.Quote];
    public RelayCommand CancelSelectedCommand { get; private set; } = null!;
    public RelayCommand CancelAllCommand { get; private set; } = null!;
    public RelayCommand ReconnectCommand { get; private set; } = null!;
    public Func<CancelBatch, Task<bool>>? ConfirmCancelAllAsync { get; set; }
    public bool IsTradeEditable => !_tradeBusy && !_bindingAccount;
    public string AccountStatus => _bindingAccount ? "Connecting account…" : _trading?.Current.Message ?? "Select an account with keys stored in memory.";
    public string AccountIdentity => _trading is null ? "" : _trading.Account.AccountId.StartsWith("key:", StringComparison.Ordinal)
        ? "Account verified by this API key" : _trading.Account.AccountId;
    public string BalanceText => IsFutures ? $"Available margin: {_trading?.Current.Balance?.Margins?.FirstOrDefault(m => m.Currency == "USDT")?.AvailableFunds?.ToString() ?? "?"} USDT" : _trading?.Current.Balance?.Balances is { } balances
        ? string.Join(" · ", balances.Where(b => b.Currency == Symbol.Split('/')[0] || b.Currency == Symbol.Split('/').Last().Split(':')[0])
            .Select(b => $"{b.Currency}: {b.Available?.ToString() ?? "?"} free")) : "Balance unavailable";

    public TradingAccountProfile? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (_tradeBusy || value?.Id == _selectedAccount?.Id) return;
            _selectedAccount = value;
            _bindTask = BindAccountAsync();
        }
    }

    public bool PostOnly { get => _postOnly; set { if (!IsTradeEditable) return; _postOnly = value; Changed(); } }
    public OrderQuantityCurrency QuantityCurrency
    {
        get => _quantityCurrency;
        set
        {
            if (!CanSelectQuantityCurrency || value == _quantityCurrency || !Enum.IsDefined(value)) return;
            try
            {
                if (_trading is not null && TryPositive(PriceText, out var price) && TryPositive(AmountText, out var input))
                {
                    var quantity = _trading.GetOrderInputCalculator(TradingInstrument).ConvertQuantity(input, price, _quantityCurrency);
                    _amountText = Format(value == OrderQuantityCurrency.Base ? quantity : _trading.GetOrderInputCalculator(TradingInstrument).OrderNotional(quantity, price));
                }
                else _amountText = "";
            }
            catch (Exception) { _amountText = ""; }
            _quantityCurrency = value;
            Changed();
        }
    }

    public string QuantityHeader => IsFutures && QuantityCurrency == OrderQuantityCurrency.Base ? "Quantity (contracts)" : $"Quantity ({(QuantityCurrency == OrderQuantityCurrency.Base ? Symbol.Split('/')[0] : Symbol.Split('/').Last().Split(':')[0])})";
    public OrderSnapshot? SelectedOrder { get => _selectedOrder; set { _selectedOrder = value; Changed(); } }
    private OrderType _selectedOrderType = OrderType.Limit;
    public OrderType[] OrderTypes { get; } = [OrderType.Limit, OrderType.Market];
    public bool IsLimitOrder => SelectedOrderType == OrderType.Limit;
    public bool CanSelectQuantityCurrency => IsTradeEditable && IsLimitOrder;

    public OrderType SelectedOrderType
    {
        get => _selectedOrderType;
        set
        {
            if (!IsTradeEditable || !OrderTypes.Contains(value) || value == _selectedOrderType) return;
            _selectedOrderType = value;
            _amountText = "";
            _quantityCurrency = OrderQuantityCurrency.Base;
            _postOnly = false;
            Changed();
        }
    }

    /// <summary>Prepares and validates only required input and exchange precision, without trading policy checks.</summary>
    private (decimal Quantity, decimal Price) PreparedInput()
    {
        if (_trading is null) throw new InvalidOperationException("Select a connected account.");
        if (!TryPositive(AmountText, out var input)) throw new ArgumentException("Enter a quantity greater than zero.");
        decimal price = 1;
        if (IsLimitOrder)
        {
            if (!TryPositive(PriceText, out price)) throw new ArgumentException("Enter a limit price greater than zero.");
            price = _trading.GetOrderInputCalculator(TradingInstrument).PriceToPrecision(price);
            if (price <= 0) throw new ArgumentException("Price rounds to zero at exchange precision.");
        }
        var quantity = _trading.GetOrderInputCalculator(TradingInstrument).ConvertQuantity(input, price,
            IsLimitOrder ? QuantityCurrency : OrderQuantityCurrency.Base);
        if (quantity <= 0) throw new ArgumentException("Quantity rounds to zero at exchange precision.");
        return (quantity, price);
    }

    public string TradingInputHint
    {
        get
        {
            if (_bindingAccount) return "Connecting account…";
            if (_trading is null) return "Select an account with API keys.";
            if (!_trading.Current.Connected) return "Reconnect the trading account.";
            if (_tradeBusy) return "Waiting for the exchange response…";
            try { PreparedInput(); return ""; }
            catch (ArgumentException ex) { return ex.Message; }
            catch (Exception) { return "Could not format order quantity or price."; }
        }
    }

    private bool TradingInputValid => TradingInputHint.Length == 0;

    public string OrderPreview
    {
        get
        {
            try
            {
                var input = PreparedInput();
                var unit = IsFutures ? "contracts" : Symbol.Split('/')[0];
                if (!IsLimitOrder) return $"{Format(input.Quantity)} {unit} · Market; execution price is determined by the exchange";
                return $"{Format(input.Quantity)} {unit} @ {Format(input.Price)} · {Format(_trading!.GetOrderInputCalculator(TradingInstrument).OrderNotional(input.Quantity, input.Price))} {Symbol.Split('/').Last().Split(':')[0]} (excluding commission)";
            }
            catch (Exception) { return ""; }
        }
    }

    /// <summary>Initializes trade controls without contacting any exchange or starting an order.</summary>
    private void InitializeTrading(TradingSessionManager? manager, TradingEnvironment environment)
    {
        _tradingManager = manager;
        Environment = environment;
        CancelSelectedCommand = new RelayCommand(CancelSelectedAsync, _ => !_tradeBusy && _trading is not null && SelectedOrder is { } order && !TradingAccountSession.Terminal(order.State));
        CancelAllCommand = new RelayCommand(CancelAllAsync, _ => !_tradeBusy && !_bindingAccount && _trading is not null);
        ReconnectCommand = new RelayCommand(_ => BindAccountAsync(), _ => !_tradeBusy && !_bindingAccount && SelectedAccount is not null);
        ReloadAccounts();
    }

    /// <summary>Updates available profiles on the UI timer and rebinds after keys/profile changes.</summary>
    private void ReloadAccounts()
    {
        if (_tradingManager is null) return;
        _managerRevision = _tradingManager.Revision;
        try
        {
            var settings = _tradingManager.Settings();
            var oldId = _selectedAccount?.Id;
            Accounts.Clear();
            foreach (var profile in settings.Profiles.Where(p => p.Exchange == Exchange && p.Environment == Environment && p.Market == Market)) Accounts.Add(profile);
            _selectedAccount = Accounts.FirstOrDefault(p => p.Id == oldId)
                ?? Accounts.FirstOrDefault(p => p.Id == settings.ActiveProfiles.GetValueOrDefault(Exchange));
            _bindTask = BindAccountAsync();
        }
        catch (Exception) { _tradeStatus = "Could not load trading account profiles."; }
    }

    /// <summary>Guards late authentication results and clears order input whenever account identity changes.</summary>
    private async Task BindAccountAsync()
    {
        var generation = ++_accountGeneration;
        var selected = _selectedAccount;
        _trading = null;
        _lastTradingState = null;
        Orders = []; Executions = []; SelectedOrder = null;
        _priceText = ""; _amountText = ""; _tradeStatus = "";
        _bindingAccount = selected is not null;
        Changed();
        if (selected is null || _tradingManager is null) { _bindingAccount = false; return; }
        try
        {
            var service = await _tradingManager.GetAsync(selected.Id, _stop.Token);
            if (_disposed || generation != _accountGeneration) return;
            // Keep display subscriptions separate from the order-book feed.
            _trading = service;
            await Task.Run(() => service.TrackAsync(TradingInstrument, _stop.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && generation == _accountGeneration) _tradeStatus = ex is TradingServiceException safe ? safe.Message : "Could not connect the trading account."; }
        finally
        {
            if (!_disposed && generation == _accountGeneration) { _bindingAccount = false; Changed(); }
        }
    }

    /// <summary>Applies the latest detached account state without accumulating Dispatcher callbacks.</summary>
    private void RefreshTrading()
    {
        if (!_tradeBusy && _tradingManager is not null && _managerRevision != _tradingManager.Revision) ReloadAccounts();
        if (_trading is null) return;
        _lastTradingState = _trading.Current;
        var selectedId = SelectedOrder?.Reference;
        var orders = _lastTradingState.Orders.Where(o => o.Reference.Instrument == TradingInstrument)
            .OrderBy(o => TradingAccountSession.Terminal(o.State)).ThenByDescending(o => o.UpdatedAt).ToArray();
        if (!Orders.SequenceEqual(orders)) Orders = orders;
        var fills = _lastTradingState.Executions.Where(f => f.Order.Instrument == TradingInstrument).Take(100).ToArray();
        if (!Executions.SequenceEqual(fills)) Executions = fills;
        _selectedOrder = Orders.FirstOrDefault(o => o.Reference == selectedId);
    }

    /// <summary>Freezes a request before awaiting, preventing edits or another Buy/Sell from changing the request.</summary>
    internal async Task TradeAsync(OrderSide side)
    {
        if (!CanTrade || _trading is null) return;

        var service = _trading;
        var generation = _accountGeneration;
        _tradeBusy = true;

        Changed();

        try
        {
            var input = PreparedInput();
            var result = IsLimitOrder
                ? await Task.Run(() => service.CreateLimitOrderAsync(new(TradingInstrument, side, input.Quantity, input.Price,
                    PostOnly, ReduceOnly, SelectedPositionSide), _stop.Token))
                : await Task.Run(() => service.CreateMarketOrderAsync(new(TradingInstrument, side, input.Quantity,
                    ReduceOnly, SelectedPositionSide), _stop.Token));

            if (!_disposed && generation == _accountGeneration)
                _tradeStatus = $"{side}: {result.State} · {result.Reference.ExchangeOrderId} {result.StatusReason}";
        }
        catch (OperationCanceledException) { if (!_disposed) _tradeStatus = "Response not received; check exchange orders before trying again."; }
        catch (Exception ex) {
            if (!_disposed) {
                _tradeStatus = ex is TradingServiceException safe ?
                    safe.Message :
                    TradingErrorText.FromException(ex);
            }
        }
        finally {
            _tradeBusy = false;
            if (!_disposed) {
                Changed();
            }
        }
    }

    /// <summary>Cancels exactly the selected account order, preserving a possible partial fill.</summary>
    private async Task CancelSelectedAsync(object? parameter)
    {
        if (_tradeBusy || _trading is null || SelectedOrder is not { } order) return;
        var service = _trading;
        _tradeBusy = true; Changed();
        try
        {
            var result = await Task.Run(() => service.CancelOrderAsync(order.Reference, _stop.Token));
            if (!_disposed) _tradeStatus = $"Cancel {order.Reference.ExchangeOrderId}: {result.State} {result.StatusReason}";
        }
        catch (OperationCanceledException) { if (!_disposed) _tradeStatus = "Response not received; check exchange orders before trying again."; }
        catch (Exception ex) { if (!_disposed) _tradeStatus = TradingErrorText.FromException(ex); }
        finally { _tradeBusy = false; if (!_disposed) Changed(); }
    }

    /// <summary>Confirms a frozen account/pair snapshot, including external orders, before starting batch cancellation.</summary>
    private async Task CancelAllAsync(object? parameter)
    {
        if (_tradeBusy || _trading is null || ConfirmCancelAllAsync is null) return;
        var service = _trading;
        _tradeBusy = true; Changed();
        try
        {
            var batch = await Task.Run(() => service.PrepareCancelAllAsync(TradingInstrument, _stop.Token));
            if (_disposed || !await ConfirmCancelAllAsync(batch)) return;
            var results = await Task.Run(() => service.CancelBatchAsync(batch, _stop.Token));
            if (!_disposed) _tradeStatus = string.Join("; ", results);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed) _tradeStatus = "Could not complete batch cancellation. Check individual order states."; }
        finally { _tradeBusy = false; if (!_disposed) Changed(); }
    }
}

```

### BotView/ViewModels/OrderBookViewModel.cs

```csharp
using System.ComponentModel;
using System.Globalization;
using BotView.Models;
using BotView.Services;

namespace BotView.ViewModels;

public sealed record OrderBookVolumeOption(OrderBookVolumeCurrency Currency, string Label);

public sealed partial class OrderBookViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IOrderBookService _service;
    private readonly CancellationTokenSource _stop = new();
    private IOrderBookSubscription? _subscription;
    private Task? _startTask;
    private Task? _disposeTask;
    private readonly object _disposeSync = new();
    private bool _disposed;
    private OrderBookUpdate? _update;
    private string _priceText = "";
    private string _amountText = "";
    private string _tradeStatus = "";
    private readonly string[] _maxVolumeTexts = ["", ""];
    private readonly decimal?[] _maxVolumes = new decimal?[2];
    private OrderBookVolumeCurrency _volumeCurrency;
    private int _multiplier = 1;
    private decimal? _center;
    private bool _followMarket = true;

    /// <summary>Creates a book fixed to one network; trading sessions are owned by the application manager.</summary>
    public OrderBookViewModel(string exchange, string symbol, IOrderBookService service,
        TradingSessionManager? trading = null, TradingEnvironment environment = TradingEnvironment.Production, TradingMarket market = TradingMarket.Spot)
    {
        Market = market;
        Exchange = exchange;
        Symbol = symbol;
        VolumeCurrencyOptions =
        [
            new(OrderBookVolumeCurrency.Base, IsFutures ? "Contracts" : $"Base ({symbol.Split('/')[0]})"),
            new(OrderBookVolumeCurrency.Quote, $"Quote ({symbol.Split('/').Last().Split(':')[0]})")
        ];
        _service = service;
        BuyCommand = new RelayCommand(_ => TradeAsync(OrderSide.Buy), _ => CanTrade);
        SellCommand = new RelayCommand(_ => TradeAsync(OrderSide.Sell), _ => CanTrade);
        CenterCommand = new RelayCommand(() => { _followMarket = true; _center = null; Changed(); });
        InitializeTrading(trading, environment);
    }

    public TradingMarket Market { get; }
    public string Exchange { get; }
    public string Symbol { get; }
    public string Title => $"Order book · {Symbol} · {Exchange} · {Market} · {EnvironmentText}";
    public string BaseHeader => IsFutures ? "Volume (contracts)" : $"Volume ({_update?.Snapshot?.Market.BaseCurrency ?? Symbol.Split('/')[0]})";
    public string QuoteHeader => $"Total ({_update?.Snapshot?.Market.QuoteCurrency ?? Symbol.Split('/').Last().Split(':')[0]})";
    public OrderBookVolumeOption[] VolumeCurrencyOptions { get; }
    public OrderBookVolumeCurrency VolumeCurrency
    {
        get => _volumeCurrency;
        set
        {
            if (value is not (OrderBookVolumeCurrency.Base or OrderBookVolumeCurrency.Quote) || value == _volumeCurrency) return;
            _volumeCurrency = value;
            Changed();
        }
    }
    public string MaxVolumeHeader => $"Volume for 100% ({(VolumeCurrency == OrderBookVolumeCurrency.Base
        ? IsFutures ? "contracts" : _update?.Snapshot?.Market.BaseCurrency ?? Symbol.Split('/')[0]
        : _update?.Snapshot?.Market.QuoteCurrency ?? Symbol.Split('/').Last().Split(':')[0])})";
    public string MaxVolumeText
    {
        get => _maxVolumeTexts[(int)VolumeCurrency];
        set
        {
            _maxVolumeTexts[(int)VolumeCurrency] = value;
            if (string.IsNullOrWhiteSpace(value)) _maxVolumes[(int)VolumeCurrency] = null;
            else if (TryPositive(value, out var volume)) _maxVolumes[(int)VolumeCurrency] = volume;
            Changed();
        }
    }
    public bool HasVolumeScaleError => !string.IsNullOrWhiteSpace(MaxVolumeText) && !TryPositive(MaxVolumeText, out _);
    public string VolumeScaleHint => HasVolumeScaleError
        ? "Enter a number greater than zero; the previous scale is retained."
        : string.IsNullOrWhiteSpace(MaxVolumeText) ? "Blank uses the maximum visible volume for automatic scaling."
        : "Volumes above the threshold fill the bar to 100%.";
    public int[] Multipliers { get; } = [1, 5, 10];
    public int Multiplier
    {
        get => _multiplier;
        set { if (Multipliers.Contains(value)) { _multiplier = value; Changed(); } }
    }
    public bool IsFollowing => _followMarket;
    public string FollowText => _followMarket ? "Following market" : "Manual scrolling";
    public string StatusText
    {
        get
        {
            if (_update is null) return "Connecting…";
            var snapshot = _update.Snapshot;
            var age = snapshot is null ? "" : $" · {snapshot.ReceivedAt.ToLocalTime():HH:mm:ss.fff}";
            bool stale = snapshot is not null && DateTimeOffset.UtcNow - snapshot.ReceivedAt > TimeSpan.FromSeconds(10);
            return _update.Message + (stale && _update.Connection == OrderBookConnection.Live ? " · No recent data" : "") + age;
        }
    }

    public string StepText => _update?.Snapshot is { } b ? $"Step: {Format(b.Market.TickSize * Multiplier)}" : "Step: —";
    public string PriceText { get => _priceText; set { _priceText = value; Changed(); } }
    public string AmountText { get => _amountText; set { _amountText = value; Changed(); } }
    public string TradeStatus => _tradeStatus;
    public bool CanTrade => !_disposed && !_tradeBusy && !_bindingAccount && _trading?.Current.Connected == true
        && TradingInputValid;
    public RelayCommand BuyCommand { get; }
    public RelayCommand SellCommand { get; }
    public RelayCommand CenterCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public Task StartAsync() => _disposed ? Task.CompletedTask : _startTask ??= StartCoreAsync();

    private async Task StartCoreAsync()
    {
        try
        {
            var subscription = IsFutures
                ? await _service.SubscribeAsync(Exchange, Symbol, Environment, Market, _stop.Token).ConfigureAwait(false)
                : Environment == TradingEnvironment.Production
                ? await _service.SubscribeAsync(Exchange, Symbol, _stop.Token).ConfigureAwait(false)
                : await _service.SubscribeAsync(Exchange, Symbol, Environment, _stop.Token).ConfigureAwait(false);
            _subscription = subscription;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { _update = new(null, OrderBookConnection.Error, ex.Message); }
    }

    /// <summary>Called only by the UI's 50 ms timer; no dispatcher queue is accumulated by the feed.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        if (_subscription is not null) _update = _subscription.Current;
        RefreshTrading();
        Changed();
    }

    public IReadOnlyList<OrderBookRow> GetRows(int count)
    {
        var book = _update?.Snapshot;
        if (book is null) return [];
        var marketCenter = book.BestBid is { } bid && book.BestAsk is { } ask ? (bid + ask) / 2
            : book.BestBid ?? book.BestAsk;
        if (marketCenter is null) return [];
        if (_followMarket || _center is null) _center = marketCenter;
        var rows = OrderBookLadder.Build(book, Multiplier, _center.Value, count, _maxVolumes[(int)VolumeCurrency], VolumeCurrency);
        var step = book.Market.TickSize * Multiplier;
        return rows.Select(row => row with { OwnOrderCount = Orders.Count(o => !TradingAccountSession.Terminal(o.State) && o.Price is { } price &&
            (o.Side == OrderSide.Buy ? decimal.Floor(price / step) : decimal.Ceiling(price / step)) * step == row.Price) }).ToArray();
    }

    public void Scroll(int levels)
    {
        if (_update?.Snapshot is not { } book) return;
        if (_center is null) GetRows(1);
        if (_center is null) return;
        _followMarket = false;
        _center = Math.Max(book.Market.TickSize, _center.Value + levels * book.Market.TickSize * Multiplier);
        Changed();
    }

    /// <summary>Copies the exact clicked price only while the order draft is editable.</summary>
    public void SelectPrice(decimal price) { if (IsTradeEditable) PriceText = Format(price); }

    internal static bool TryPositive(string text, out decimal value) =>
        decimal.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out value) && value > 0;

    internal static string Format(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    private void Changed()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        BuyCommand.RaiseCanExecuteChanged();
        SellCommand.RaiseCanExecuteChanged();
        CancelSelectedCommand?.RaiseCanExecuteChanged();
        CancelAllCommand?.RaiseCanExecuteChanged();
        ReconnectCommand?.RaiseCanExecuteChanged();
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        ++_accountGeneration;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_startTask is not null) await _startTask.ConfigureAwait(false);
        if (_subscription is not null) await _subscription.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}

```

### BotView/Windows/OrderBookWindow.xaml

```xml
﻿<Window x:Class="BotView.Windows.OrderBookWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:views="clr-namespace:BotView.Views"
        Title="{Binding Title}" Width="420" Height="820" MinWidth="340" MinHeight="480"
        Background="#0B1120" Foreground="#E5E7EB" FontFamily="Segoe UI"
        WindowStartupLocation="CenterOwner">
    <Window.Resources>
        <Style TargetType="Button">
            <Setter Property="Padding" Value="10,6"/>
            <Setter Property="Background" Value="#263449"/>
            <Setter Property="Foreground" Value="#F3F4F6"/>
            <Setter Property="BorderBrush" Value="#475569"/>
            <Setter Property="Cursor" Value="Hand"/>
        </Style>
        <Style TargetType="TextBox">
            <Setter Property="Padding" Value="6,5"/>
            <Setter Property="Background" Value="#182235"/>
            <Setter Property="Foreground" Value="#F3F4F6"/>
            <Setter Property="BorderBrush" Value="#475569"/>
        </Style>
    </Window.Resources>
    <Grid Margin="10">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*" MinHeight="80"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="1.1*" MinHeight="190"/>
        </Grid.RowDefinitions>
        <Grid Margin="0,0,0,8">
            <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
            <StackPanel>
                <TextBlock Text="{Binding Symbol}" FontSize="19" FontWeight="SemiBold"/>
                <StackPanel Orientation="Horizontal">
                    <TextBlock Text="{Binding Exchange}" Foreground="#94A3B8" FontSize="11"/>
                    <TextBlock Text="{Binding EnvironmentText}" Foreground="#FBBF24" FontWeight="Bold" FontSize="11" Margin="5,0,0,0"/>
                </StackPanel>
                <TextBlock Text="{Binding SelectedAccount.Name}" Foreground="#94A3B8" FontSize="11" TextTrimming="CharacterEllipsis">
                    <TextBlock.Style><Style TargetType="TextBlock"><Style.Triggers>
                        <DataTrigger Binding="{Binding SelectedAccount}" Value="{x:Null}"><Setter Property="Visibility" Value="Collapsed"/></DataTrigger>
                    </Style.Triggers></Style></TextBlock.Style>
                </TextBlock>
            </StackPanel>
            <Button Grid.Column="1" Content="Center on market" Command="{Binding CenterCommand}" VerticalAlignment="Center"/>
        </Grid>
        <Expander Grid.Row="1" Header="Depth display settings" Foreground="#94A3B8" Margin="0,0,0,5">
        <StackPanel>
        <Grid Margin="0,0,0,8">
            <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="65"/></Grid.ColumnDefinitions>

            <TextBlock Grid.Column="1" Text="{Binding StepText}" VerticalAlignment="Center" Margin="4,0,8,0" FontSize="11"/>
            <ComboBox Grid.Column="2" ItemsSource="{Binding Multipliers}" SelectedItem="{Binding Multiplier}" VerticalContentAlignment="Center"
                      ToolTip="Exchange price step multiplier">
                <ComboBox.ItemTemplate><DataTemplate><TextBlock Text="{Binding StringFormat=×{0}}"/></DataTemplate></ComboBox.ItemTemplate>
            </ComboBox>
        </Grid>
        <Grid Margin="0,0,0,8">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>
            <Grid.ColumnDefinitions>
                <ColumnDefinition />
                <ColumnDefinition Width="105"/>
            </Grid.ColumnDefinitions>
            <StackPanel Margin="0,0,8,0">
                <TextBlock Text="{Binding MaxVolumeHeader}" FontSize="10" Margin="0,0,0,2"/>
                <ComboBox x:Name="VolumeCurrencySelector" ItemsSource="{Binding VolumeCurrencyOptions}"
                          SelectedValuePath="Currency" DisplayMemberPath="Label" SelectedValue="{Binding VolumeCurrency}"
                          MinHeight="24" VerticalContentAlignment="Center" ToolTip="Currency used for volume bars and the fill threshold"/>
            </StackPanel>
            <TextBox x:Name="MaxVolumeInput" Grid.Column="1" Text="{Binding MaxVolumeText, UpdateSourceTrigger=PropertyChanged}"
                     VerticalAlignment="Center" ToolTip="Volume in the selected currency for a full-width bar. Leave blank for automatic scaling.">
                <TextBox.Style>
                    <Style TargetType="TextBox" BasedOn="{StaticResource {x:Type TextBox}}">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding HasVolumeScaleError}" Value="True">
                                <Setter Property="BorderBrush" Value="#F87171"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBox.Style>
            </TextBox>
            <TextBlock Grid.Row="1" Grid.ColumnSpan="2" Text="{Binding VolumeScaleHint}" FontSize="10" Foreground="#94A3B8"
                       TextWrapping="Wrap" Margin="0,3,0,0"/>
        </Grid>
        </StackPanel>
        </Expander>
        <Grid Grid.Row="2" Background="#182235" Height="26">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="34*"/>
                <ColumnDefinition Width="34*"/>
                <ColumnDefinition Width="32*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <TextBlock Text="{Binding BaseHeader}" FontSize="10" VerticalAlignment="Center" HorizontalAlignment="Right" Margin="4,0,6,0"/>
            <TextBlock Grid.Column="1" Text="Price" FontSize="10" VerticalAlignment="Center" HorizontalAlignment="Right" Margin="4,0,6,0"/>
            <TextBlock Grid.Column="2" Text="{Binding QuoteHeader}" FontSize="10" VerticalAlignment="Center" HorizontalAlignment="Right" Margin="4,0,6,0"/>
        </Grid>
        <views:OrderBookLadderControl x:Name="Ladder" Grid.Row="3" ToolTip="Scroll to browse prices. Click to select an order price. An em dash means depth or monetary valuation data is unavailable."/>
        <TextBlock Grid.Row="4" Text="{Binding StatusText}" Foreground="#94A3B8" FontSize="10" TextWrapping="Wrap" MaxHeight="44" Margin="0,6"/>
        <Grid Grid.Row="5">
        <Grid.RowDefinitions><RowDefinition/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
        <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
        <StackPanel>
        <ComboBox ItemsSource="{Binding Accounts}" SelectedItem="{Binding SelectedAccount}" DisplayMemberPath="DisplayName"
                  IsEnabled="{Binding IsTradeEditable}" ToolTip="Accounts for this exchange and network" Margin="0,0,0,4"/>
        <TextBlock Text="{Binding AccountStatus}" FontSize="10" TextWrapping="Wrap" Foreground="#94A3B8"/>
        <Button Content="Reconnect account" Command="{Binding ReconnectCommand}" HorizontalAlignment="Left" Padding="5,2" Margin="0,3"/>
        <TextBlock Text="{Binding AccountIdentity}" FontSize="10" Foreground="#94A3B8"/>
        <TextBlock Text="{Binding BalanceText}" FontSize="10" TextWrapping="Wrap" Margin="0,2,0,5"/>
        <ComboBox ItemsSource="{Binding OrderTypes}" SelectedItem="{Binding SelectedOrderType}"
                  IsEnabled="{Binding IsTradeEditable}" ToolTip="Order type: Limit or Market" Margin="0,4"/>
        <Grid IsEnabled="{Binding IsTradeEditable}">
            <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
            <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="10"/><ColumnDefinition/></Grid.ColumnDefinitions>
            <TextBlock Text="Order price" Foreground="#94A3B8" FontSize="11"/>
            <TextBlock Grid.Column="2" Text="{Binding QuantityHeader}" Foreground="#94A3B8" FontSize="11"/>
            <TextBox x:Name="PriceInput" IsEnabled="{Binding IsLimitOrder}" Grid.Row="1" Text="{Binding PriceText, UpdateSourceTrigger=PropertyChanged}" Margin="0,3,0,7"/>
            <TextBox Grid.Row="1" Grid.Column="2" Text="{Binding AmountText, UpdateSourceTrigger=PropertyChanged}" Margin="0,3,0,7"/>
            <CheckBox Grid.Row="2" Content="Post-only" IsEnabled="{Binding IsLimitOrder}" IsChecked="{Binding PostOnly}" Foreground="#E5E7EB" ToolTip="Unchecked: ordinary GTC limit order"/>
            <ComboBox Grid.Row="2" Grid.Column="2" IsEnabled="{Binding CanSelectQuantityCurrency}" ItemsSource="{Binding QuantityCurrencies}" SelectedItem="{Binding QuantityCurrency}" ToolTip="Order quantity unit; independent of depth volume bars"/>
            <TextBlock Grid.Row="3" Grid.ColumnSpan="3" Text="{Binding OrderPreview}" FontSize="10" TextWrapping="Wrap" Margin="0,4"/>
        </Grid>
        <StackPanel IsEnabled="{Binding IsTradeEditable}" Margin="0,4">
            <StackPanel.Style>
                <Style TargetType="StackPanel">
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding IsFutures}" Value="False">
                            <Setter Property="Visibility" Value="Collapsed"/>
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </StackPanel.Style>
            <CheckBox Content="Reduce-only" IsChecked="{Binding ReduceOnly}" Foreground="#E5E7EB"/>
            <ComboBox ItemsSource="{Binding PositionTargets}" SelectedItem="{Binding PositionTarget}" ToolTip="Auto for one-way mode; Long or Short for hedge mode"/>
            <TextBlock Text="{Binding PositionsText}" TextWrapping="Wrap" FontSize="10"/>
        </StackPanel>
        <TextBlock Text="{Binding TradingInputHint}" TextWrapping="Wrap" Foreground="#FBBF24" FontSize="11"/>
        <TextBlock Text="{Binding TradeStatus}" TextWrapping="Wrap" Foreground="#FBBF24" FontSize="11" Margin="0,4"/>
        <TextBlock Text="Account orders for this pair" FontSize="11" Margin="0,5"/>
        <DataGrid ItemsSource="{Binding Orders}" SelectedItem="{Binding SelectedOrder}" IsReadOnly="True" AutoGenerateColumns="False"
                  MaxHeight="150" MinHeight="55" Background="#182235" Foreground="#172033" SelectionMode="Single"
                  CanUserAddRows="False" HeadersVisibility="Column" HorizontalScrollBarVisibility="Auto">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Side" Binding="{Binding Side}"/>
                <DataGridTextColumn Header="Position" Binding="{Binding PositionSide}"/>
                <DataGridTextColumn Header="Reduce-only" Binding="{Binding ReduceOnly}"/>
                <DataGridTextColumn Header="Price" Binding="{Binding Price}"/>
                <DataGridTextColumn Header="Quantity" Binding="{Binding Quantity}"/>
                <DataGridTextColumn Header="Filled" Binding="{Binding FilledQuantity}"/>
                <DataGridTextColumn Header="Remaining" Binding="{Binding RemainingQuantity}"/>
                <DataGridTextColumn Header="State" Binding="{Binding State}"/>
                <DataGridTextColumn Header="Reason" Binding="{Binding StatusReason}"/>
                <DataGridTextColumn Header="Order ID" Binding="{Binding Reference.ExchangeOrderId}"/>
            </DataGrid.Columns>
        </DataGrid>
        <WrapPanel Margin="0,5">
            <Button Content="Cancel selected" Command="{Binding CancelSelectedCommand}" Margin="0,0,5,0"/>
            <Button Content="Cancel all for pair" Command="{Binding CancelAllCommand}"/>
        </WrapPanel>
        <TextBlock Text="Recent executions" FontSize="11" Margin="0,5"/>
        <ItemsControl ItemsSource="{Binding Executions}">
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <TextBlock FontSize="10" TextWrapping="Wrap">
                        <Run Text="{Binding ExecutedAt, StringFormat=HH:mm:ss}"/><Run Text=" "/>
                        <Run Text="{Binding Side}"/><Run Text=" "/><Run Text="{Binding Quantity}"/>
                        <Run Text=" @ "/><Run Text="{Binding Price}"/>
                        <Run Text=" · Fee: "/><Run Text="{Binding Fee.Amount}"/><Run Text=" "/>
                        <Run Text="{Binding Fee.Currency}"/>
                    </TextBlock>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
        </StackPanel>
        </ScrollViewer>
        <Grid Grid.Row="1" Margin="0,4,0,0">
            <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="10"/><ColumnDefinition/></Grid.ColumnDefinitions>
            <Button Content="Buy" Command="{Binding BuyCommand}" Background="#22613B"/>
            <Button Grid.Column="2" Content="Sell" Command="{Binding SellCommand}" Background="#873C43"/>
        </Grid>
        </Grid>
    </Grid>
</Window>

```


## Before removing one-way order routing

Previous FuturesTradingService.OrderParameters and order-book position selection.

```csharp
    /// <summary>Maps the selected position side directly; the exchange validates account mode and Reduce-only.</summary>
    private Dictionary<string, object> OrderParameters(bool reduceOnly, PositionSide? side)
    {
        var parameters = CreateParameters();
        if (Venue == "binance")
        {
            parameters["positionSide"] = side == PositionSide.Long ? "LONG" : side == PositionSide.Short ? "SHORT" : "BOTH";
        }
        else
        {
            parameters["positionIdx"] = side == PositionSide.Long ? 1 : side == PositionSide.Short ? 2 : 0;
        }

        if (reduceOnly)
        {
            parameters["reduceOnly"] = true;
        }

        return parameters;
    }

    private string _positionTarget = "Auto";
    public string[] PositionTargets { get; } = ["Auto", "Long", "Short"];
    public bool ReduceOnly { get => _reduceOnly; set { if (IsTradeEditable) { _reduceOnly = value; Changed(); } } }
    public string PositionTarget { get => _positionTarget; set { if (IsTradeEditable && PositionTargets.Contains(value)) { _positionTarget = value; Changed(); } } }
    private PositionSide? SelectedPositionSide => PositionTarget == "Long" ? PositionSide.Long : PositionTarget == "Short" ? PositionSide.Short : null;
```
