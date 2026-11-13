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

            if (document.Version is not (1 or 2)) throw new InvalidDataException("Unsupported account document version.");

            return Validate(new(document.Profiles, document.ActiveProfiles)); // Validate() does not change the document
        }
    }

    /// <summary>Atomically saves metadata and then notifies account selectors and session supervision.</summary>
    public void Save(TradingAccountsSettings settings)
    {
        lock (_sync)
        {
            Validate(settings);
            string tmpFile = null!;
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
                Directory.CreateDirectory(directory);
                tmpFile = Path.Combine(directory, $".trading-accounts-{Guid.NewGuid():N}.tmp");
                // Explicit projection prevents future profile additions from accidentally writing secrets.
                var document = new
                {
                    Version = 2,
                    Profiles = settings.Profiles.Select(p =>
                        new { p.Id, p.Name, p.Exchange, p.Environment, p.Market, p.Quik })
                        .ToArray(),

                    settings.ActiveProfiles
                };
                File.WriteAllText(tmpFile, JsonSerializer.Serialize(document,
                        new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmpFile, _path, overwrite: true);
            }
            finally { if (File.Exists(tmpFile)) File.Delete(tmpFile); }
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Rejects missing collections, invalid profiles, duplicate ids, and active entries that do not match a profile.
    /// </summary>
    private static TradingAccountsSettings Validate(TradingAccountsSettings settings)
    {
        if (settings.Profiles is null || settings.ActiveProfiles is null)
            throw new InvalidDataException("Invalid account settings.");

        if (settings.Profiles.Any(profile =>
                profile is null || profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name) ||
                string.IsNullOrWhiteSpace(profile.Name) ||
                profile.Exchange is not ("binance" or "bybit" or "quik") ||
                !Enum.IsDefined(profile.Environment) ||
                !Enum.IsDefined(profile.Market)))
            throw new InvalidDataException("Invalid account settings.");

        foreach (var profile in settings.Profiles)
        {
            if (profile.Exchange == "quik")
            {
                var quik = profile.Quik;
                if (profile.Market is not (TradingMarket.MoexShares or TradingMarket.MoexFutures) || quik is null ||
                    string.IsNullOrWhiteSpace(quik.Host) || quik.Port is < 1 or > 65534 ||
                    string.IsNullOrWhiteSpace(quik.FirmId) || string.IsNullOrWhiteSpace(quik.TradeAccount) ||
                    quik.ClientCode is null || quik.LimitKind < 0)
                    throw new InvalidDataException("Select QUIK routing and limit kind explicitly.");
            }
            else if (profile.Market is not (TradingMarket.Spot or TradingMarket.UsdtFutures) || profile.Quik is not null)
                throw new InvalidDataException("The profile market does not match its connection.");
        }

        if (settings.Profiles.Select(profile => profile.Id).Distinct().Count() != settings.Profiles.Count)
            throw new InvalidDataException("Invalid account settings.");

        if (settings.ActiveProfiles.Any(active =>
                !settings.Profiles.Any(profile => profile.Id == active.Value && profile.Exchange == active.Key)))
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
