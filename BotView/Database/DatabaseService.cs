using System.IO;
using Microsoft.Data.Sqlite;
using BotView.Database.Models;

namespace BotView.Database
{
    /// <summary>Сервис для работы с SQLite базой данных</summary>
    public class DatabaseService
    {
        private readonly string _connectionString;

        public DatabaseService()
        {
            string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "botview.db");
            _connectionString = $"Data Source={dbPath}";
        }

        /// <summary>Создаёт соединение с базой данных</summary>
        private SqliteConnection CreateConnection()
        {
            return new SqliteConnection(_connectionString);
        }

        /// <summary>Инициализирует базу данных: создаёт таблицы если их нет</summary>
        public void Initialize()
        {
            using var connection = CreateConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Users (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Username TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS TradingPairs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Symbol TEXT NOT NULL UNIQUE
                );

                CREATE TABLE IF NOT EXISTS UserFavoritePairs (
                    UserId INTEGER NOT NULL,
                    TradingPairId INTEGER NOT NULL,
                    PRIMARY KEY (UserId, TradingPairId),
                    FOREIGN KEY (UserId) REFERENCES Users(Id),
                    FOREIGN KEY (TradingPairId) REFERENCES TradingPairs(Id)
                );
            ";
            command.ExecuteNonQuery();
        }

        /// <summary>Проверяет, есть ли данные в таблице TradingPairs</summary>
        public bool HasTradingPairs()
        {
            using var connection = CreateConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM TradingPairs";
            var count = Convert.ToInt32(command.ExecuteScalar());
            return count > 0;
        }

        /// <summary>Добавляет пользователя в базу данных</summary>
        public int AddUser(string username)
        {
            using var connection = CreateConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Users (Username) VALUES (@username); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("@username", username);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        /// <summary>Добавляет торговую пару в базу данных</summary>
        public int AddTradingPair(string symbol)
        {
            using var connection = CreateConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO TradingPairs (Symbol) VALUES (@symbol); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("@symbol", symbol);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        /// <summary>Добавляет торговую пару в избранное пользователя</summary>
        public void AddFavoritePair(int userId, int tradingPairId)
        {
            using var connection = CreateConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO UserFavoritePairs (UserId, TradingPairId) VALUES (@userId, @pairId)";
            command.Parameters.AddWithValue("@userId", userId);
            command.Parameters.AddWithValue("@pairId", tradingPairId);
            command.ExecuteNonQuery();
        }

        /// <summary>Получает все торговые пары</summary>
        public List<TradingPair> GetAllTradingPairs()
        {
            var pairs = new List<TradingPair>();

            using var connection = CreateConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, Symbol FROM TradingPairs ORDER BY Symbol";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                pairs.Add(new TradingPair
                {
                    Id = reader.GetInt32(0),
                    Symbol = reader.GetString(1)
                });
            }

            return pairs;
        }

        /// <summary>Получает избранные торговые пары пользователя</summary>
        public List<TradingPair> GetUserFavoritePairs(int userId)
        {
            var pairs = new List<TradingPair>();

            using var connection = CreateConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT tp.Id, tp.Symbol 
                FROM TradingPairs tp
                INNER JOIN UserFavoritePairs ufp ON tp.Id = ufp.TradingPairId
                WHERE ufp.UserId = @userId
                ORDER BY tp.Symbol";
            command.Parameters.AddWithValue("@userId", userId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                pairs.Add(new TradingPair
                {
                    Id = reader.GetInt32(0),
                    Symbol = reader.GetString(1)
                });
            }

            return pairs;
        }

        /// <summary>Получает ID торговой пары по символу</summary>
        public int? GetTradingPairIdBySymbol(string symbol)
        {
            using var connection = CreateConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM TradingPairs WHERE Symbol = @symbol";
            command.Parameters.AddWithValue("@symbol", symbol);

            var result = command.ExecuteScalar();
            return result != null ? Convert.ToInt32(result) : null;
        }

        /// <summary>Заполняет базу данных тестовыми данными</summary>
        public void SeedTestData()
        {
            // Добавляем тестового пользователя
            int userId = AddUser("TestUser");

            // Добавляем торговые пары
            string[] symbols = { "BTC/USDT", "ETH/USDT", "BNB/USDT", "ADA/USDT", "SOL/USDT", 
                                 "TON/USDT", "XRP/USDT", "DOGE/USDT", "AVAX/USDT", "LINK/USDT" };
            
            var pairIds = new Dictionary<string, int>();
            foreach (var symbol in symbols)
            {
                pairIds[symbol] = AddTradingPair(symbol);
            }

            // Добавляем избранные пары для тестового пользователя
            string[] favorites = { "BTC/USDT", "ETH/USDT", "SOL/USDT" };
            foreach (var symbol in favorites)
            {
                AddFavoritePair(userId, pairIds[symbol]);
            }
        }

        /// <summary>Тестовое подключение к базе данных</summary>
        public bool TestConnection()
        {
            try
            {
                using var connection = CreateConnection();
                connection.Open();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

