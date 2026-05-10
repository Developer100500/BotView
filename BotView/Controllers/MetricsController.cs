using System.Diagnostics;
using BotView.Interfaces;

namespace BotView.Controllers
{
    public class MetricsController
    {
        private readonly IExchangeService _exchangeService;

        public MetricsController(IExchangeService exchangeService)
        {
            _exchangeService = exchangeService;
        }

        public string GetFormattedMetrics()
        {
            var performanceMetrics = _exchangeService.GetPerformanceMetrics();
            return performanceMetrics.GetFormattedSummary();
        }

        public string ExportMetricsToCSV()
        {
            var performanceMetrics = _exchangeService.GetPerformanceMetrics();
            return performanceMetrics.ExportMetricsToCSV();
        }

        public void LogMetrics()
        {
            try
            {
                _exchangeService.LogDetailedPerformanceMetrics();
                _exchangeService.ClearExpiredCache();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error logging performance metrics: {ex.Message}");
            }
        }
    }
}
