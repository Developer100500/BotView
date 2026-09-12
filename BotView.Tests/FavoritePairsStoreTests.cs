using System.IO;
using System.Text.Json;
using BotView.Services;

namespace BotView.Tests
{
    public sealed class FavoritePairsStoreTests : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "BotView.Tests",
            Guid.NewGuid().ToString("N"));

        [Fact]
        public void AddAndRemove_PersistsFavoritesPerExchange()
        {
            string filePath = Path.Combine(_directory, "favorites.json");
            var store = new FavoritePairsStore(filePath);

            store.Add("Binance", "btc/usdt");
            store.Add("bybit", "ETH/USDT");

            var reloaded = new FavoritePairsStore(filePath);
            Assert.Equal(new[] { "BTC/USDT" }, reloaded.GetFavorites("binance"));
            Assert.Equal(new[] { "ETH/USDT" }, reloaded.GetFavorites("BYBIT"));

            reloaded.Remove("BINANCE", "Btc/Usdt");

            Assert.Empty(new FavoritePairsStore(filePath).GetFavorites("binance"));
            Assert.Equal(new[] { "ETH/USDT" }, reloaded.GetFavorites("bybit"));
        }

        [Fact]
        public void Add_DoesNotCreateDuplicates_AndWritesValidJson()
        {
            string filePath = Path.Combine(_directory, "favorites.json");
            var store = new FavoritePairsStore(filePath);

            store.Add("okx", "SOL/USDT");
            store.Add("OKX", "sol/usdt");

            Assert.Single(store.GetFavorites("okx"));
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(filePath));
            Assert.Equal(1, json.RootElement.GetProperty("Version").GetInt32());
            Assert.Equal(
                "SOL/USDT",
                json.RootElement.GetProperty("Exchanges").GetProperty("okx")[0].GetString());
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }
    }
}
