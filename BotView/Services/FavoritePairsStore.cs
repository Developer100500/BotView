using System.IO;
using System.Text.Json;

namespace BotView.Services
{
    /// <summary>Persists favorite trading pairs, grouped by exchange, in a local JSON file.</summary>
    public sealed class FavoritePairsStore
    {
        private const int CurrentVersion = 1;
        private readonly object _syncRoot = new();
        private readonly string _filePath;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true
        };

        public FavoritePairsStore(string? filePath = null)
        {
            _filePath = filePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BotView",
                "favorites.json");
        }

        public string FilePath => _filePath;

        public IReadOnlyList<string> GetFavorites(string exchange)
        {
            string exchangeKey = NormalizeExchange(exchange);

            lock (_syncRoot)
            {
                var document = LoadDocument();
                return document.Exchanges.TryGetValue(exchangeKey, out var symbols)
                    ? symbols.ToArray()
                    : Array.Empty<string>();
            }
        }

        public bool IsFavorite(string exchange, string symbol)
        {
            string normalizedSymbol = NormalizeSymbol(symbol);
            return GetFavorites(exchange).Contains(normalizedSymbol, StringComparer.OrdinalIgnoreCase);
        }

        public void Add(string exchange, string symbol)
        {
            string exchangeKey = NormalizeExchange(exchange);
            string normalizedSymbol = NormalizeSymbol(symbol);

            lock (_syncRoot)
            {
                var document = LoadDocument();
                if (!document.Exchanges.TryGetValue(exchangeKey, out var symbols))
                {
                    symbols = new List<string>();
                    document.Exchanges[exchangeKey] = symbols;
                }

                if (symbols.Contains(normalizedSymbol, StringComparer.OrdinalIgnoreCase))
                {
                    return;
                }

                symbols.Add(normalizedSymbol);
                symbols.Sort(StringComparer.OrdinalIgnoreCase);
                SaveDocument(document);
            }
        }

        public void Remove(string exchange, string symbol)
        {
            string exchangeKey = NormalizeExchange(exchange);
            string normalizedSymbol = NormalizeSymbol(symbol);

            lock (_syncRoot)
            {
                var document = LoadDocument();
                if (!document.Exchanges.TryGetValue(exchangeKey, out var symbols))
                {
                    return;
                }

                int removed = symbols.RemoveAll(item =>
                    item.Equals(normalizedSymbol, StringComparison.OrdinalIgnoreCase));
                if (removed == 0)
                {
                    return;
                }

                if (symbols.Count == 0)
                {
                    document.Exchanges.Remove(exchangeKey);
                }

                SaveDocument(document);
            }
        }

        private FavoritePairsDocument LoadDocument()
        {
            if (!File.Exists(_filePath))
            {
                return new FavoritePairsDocument();
            }

            string json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<FavoritePairsDocument>(json, _jsonOptions)
                         ?? new FavoritePairsDocument();

            var normalized = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (exchange, symbols) in loaded.Exchanges ?? new Dictionary<string, List<string>>())
            {
                if (string.IsNullOrWhiteSpace(exchange) || symbols is null)
                {
                    continue;
                }

                normalized[NormalizeExchange(exchange)] = symbols
                    .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
                    .Select(NormalizeSymbol)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            loaded.Version = CurrentVersion;
            loaded.Exchanges = normalized;
            return loaded;
        }

        private void SaveDocument(FavoritePairsDocument document)
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = _filePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, _jsonOptions));
            File.Move(temporaryPath, _filePath, true);
        }

        private static string NormalizeExchange(string exchange)
        {
            if (string.IsNullOrWhiteSpace(exchange))
            {
                throw new ArgumentException("Exchange cannot be empty.", nameof(exchange));
            }

            return exchange.Trim().ToLowerInvariant();
        }

        private static string NormalizeSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                throw new ArgumentException("Symbol cannot be empty.", nameof(symbol));
            }

            return symbol.Trim().ToUpperInvariant();
        }

        private sealed class FavoritePairsDocument
        {
            public int Version { get; set; } = CurrentVersion;
            public Dictionary<string, List<string>> Exchanges { get; set; } =
                new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
