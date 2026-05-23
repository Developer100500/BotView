using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BotView.Database;
using BotView.Interfaces;
using BotView.Models;
using BotView.Services;

namespace BotView.ViewModels
{
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        private readonly DatabaseService _databaseService;
        private readonly IDataProvider _dataProvider;
        private readonly MetricsController _metricsController;

        private string _selectedExchange = "binance";
        private string _selectedSymbol = "BTC/USDT";
        private string _selectedTimeframe = "1d";
        private double _renderTime;

        public MainWindowViewModel(
            DatabaseService databaseService,
            IDataProvider dataProvider,
            MetricsController metricsController)
        {
            _databaseService = databaseService;
            _dataProvider = dataProvider;
            _metricsController = metricsController;

            TradingPairs = new ObservableCollection<TradingPairModel>();
        }

        public ObservableCollection<TradingPairModel> TradingPairs { get; }

        public string SelectedExchange
        {
            get => _selectedExchange;
            set
            {
                if (_selectedExchange != value)
                {
                    _selectedExchange = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SelectedSymbol
        {
            get => _selectedSymbol;
            set
            {
                if (_selectedSymbol != value)
                {
                    _selectedSymbol = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SelectedTimeframe
        {
            get => _selectedTimeframe;
            set
            {
                if (_selectedTimeframe != value)
                {
                    _selectedTimeframe = value;
                    OnPropertyChanged();
                }
            }
        }

        public double RenderTime
        {
            get => _renderTime;
            set
            {
                if (Math.Abs(_renderTime - value) > 0.01)
                {
                    _renderTime = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary> Initializes the local database (schema and seed when empty). </summary>
        public void InitializeDatabase()
        {
            _databaseService.EnsureInitialized();
        }

        public void LoadTradingPairs()
        {
            TradingPairs.Clear();
            var pairs = _databaseService.GetTradingPairModels();
            foreach (var pair in pairs)
            {
                TradingPairs.Add(pair);
            }
        }

        public async Task<CandlestickData?> LoadDataAsync()
        {
            return await _dataProvider.LoadDataAsync(SelectedExchange, SelectedSymbol, SelectedTimeframe);
        }

        public CandlestickData LoadDemoData()
        {
            return _dataProvider.LoadDemoData(SelectedTimeframe);
        }

        public string GetFormattedMetrics()
        {
            return _metricsController.GetFormattedMetrics();
        }

        public string ExportMetricsToCSV()
        {
            return _metricsController.ExportMetricsToCSV();
        }

        public void LogMetrics()
        {
            _metricsController.LogMetrics();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
