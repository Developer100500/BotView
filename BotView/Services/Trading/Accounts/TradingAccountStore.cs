using System.IO;
using System.Text.Json;
using BotView.Models;

namespace BotView.Services.Trading.Accounts;

/// <summary>Persists an explicit allowlist of profile metadata, never key material.</summary>
public sealed class TradingAccountStore : ITradingAccountStore, ITradingProfileNotifications
{
    public event Action? Changed;
    private readonly string _path;
    private readonly object _sync = new();

    public TradingAccountStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotView", "trading-accounts.json");

    public TradingAccountsSettings Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_path)) return new([], new Dictionary<string, Guid>());
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("Invalid account document.");
            if (document.Version != 1) throw new InvalidDataException("Unsupported account document version.");
            return Validate(new(document.Profiles, document.ActiveProfiles));
        }
    }

    /// <summary>Atomically saves metadata and then notifies account selectors and session supervision.</summary>
    public void Save(TradingAccountsSettings settings)
    {
        lock (_sync)
        {
            Validate(settings);
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".trading-accounts-{Guid.NewGuid():N}.tmp");
            try
            {
                // Explicit projection prevents future profile additions from accidentally writing secrets.
                var document = new
                {
                    Version = 1,
                    Profiles = settings.Profiles.Select(p => new { p.Id, p.Name, p.Exchange, p.Environment, p.Market }).ToArray(),
                    settings.ActiveProfiles
                };
                File.WriteAllText(temporary, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, _path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        Changed?.Invoke();
    }

    private static TradingAccountsSettings Validate(TradingAccountsSettings settings)
    {
        if (settings.Profiles is null || settings.ActiveProfiles is null ||
            settings.Profiles.Any(p => p is null || p.Id == Guid.Empty || string.IsNullOrWhiteSpace(p.Name) ||
                p.Exchange is not ("binance" or "bybit") || !Enum.IsDefined(p.Environment) || !Enum.IsDefined(p.Market)) ||
            settings.Profiles.Select(p => p.Id).Distinct().Count() != settings.Profiles.Count ||
            settings.ActiveProfiles.Any(a => !settings.Profiles.Any(p => p.Id == a.Value && p.Exchange == a.Key)))
            throw new InvalidDataException("Invalid account settings.");
        return settings;
    }

    private sealed class Document
    {
        public int Version { get; set; }
        public List<TradingAccountProfile> Profiles { get; set; } = [];
        public Dictionary<string, Guid> ActiveProfiles { get; set; } = new();
    }
}
