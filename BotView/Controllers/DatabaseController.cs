using System.Diagnostics;
using BotView.Database;
using BotView.Models;

namespace BotView.Controllers
{
    public class DatabaseController
    {
        private readonly DatabaseService _databaseService;

        public DatabaseController(DatabaseService databaseService)
        {
            _databaseService = databaseService;
        }

        public bool Initialize()
        {
            try
            {
                if (!_databaseService.TestConnection())
                    return false;

                _databaseService.Initialize();

                if (!_databaseService.HasTradingPairs())
                {
                    _databaseService.SeedTestData();
                    Debug.WriteLine("Database seeded with test data.");
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error initializing database: {ex.Message}");
                return false;
            }
        }

        public List<TradingPairModel> GetTradingPairs()
        {
            try
            {
                var pairs = _databaseService.GetAllTradingPairs();
                var models = new List<TradingPairModel>();
                bool isFirst = true;

                foreach (var pair in pairs)
                {
                    models.Add(new TradingPairModel(pair.Symbol, isFirst));
                    isFirst = false;
                }

                Debug.WriteLine($"Loaded {models.Count} trading pairs from database.");
                return models;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading trading pairs: {ex.Message}");
                return new List<TradingPairModel>();
            }
        }
    }
}
