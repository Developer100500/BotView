using ccxt;

namespace BotView.Configuration;

/// <summary>One definition for each timeframe used by the UI, chart and data services.</summary>
public sealed record TimeframeDefinition(string Id, TimeSpan Duration, int DemoCandleCount);

/// <summary>Metadata and client factory for one supported exchange.</summary>
public sealed class ExchangeDefinition
{
    internal ExchangeDefinition(
        string id, string displayName, string apiUrl, bool isActive,
        Func<Exchange> createClient, IReadOnlyList<string> supportedTimeframes)
    {
        Id = id;
        DisplayName = displayName;
        ApiUrl = apiUrl;
        IsActive = isActive;
        CreateClient = createClient;
        SupportedTimeframes = supportedTimeframes;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string ApiUrl { get; }
    public bool IsActive { get; }
    public IReadOnlyList<string> SupportedTimeframes { get; }
    internal Func<Exchange> CreateClient { get; }

    public bool SupportsTimeframe(string timeframe) =>
        SupportedTimeframes.Contains(timeframe, StringComparer.OrdinalIgnoreCase);
}

/// <summary>The single source of supported exchanges and timeframe capabilities.</summary>
public static class MarketCatalog
{
    public const string DefaultExchangeId = "binance";
    public const string DefaultTimeframeId = "1d";

    public static IReadOnlyList<TimeframeDefinition> Timeframes { get; } = Array.AsReadOnly(new[]
    {
        new TimeframeDefinition("1m", TimeSpan.FromMinutes(1), 1440),
        new TimeframeDefinition("5m", TimeSpan.FromMinutes(5), 288),
        new TimeframeDefinition("15m", TimeSpan.FromMinutes(15), 96),
        new TimeframeDefinition("30m", TimeSpan.FromMinutes(30), 48),
        new TimeframeDefinition("1h", TimeSpan.FromHours(1), 24),
        new TimeframeDefinition("4h", TimeSpan.FromHours(4), 42),
        new TimeframeDefinition("1d", TimeSpan.FromDays(1), 30),
        new TimeframeDefinition("1w", TimeSpan.FromDays(7), 12)
    });

    public static IReadOnlyList<string> TimeframeIds { get; } =
        Array.AsReadOnly(Timeframes.Select(item => item.Id).ToArray());

    public static IReadOnlyList<ExchangeDefinition> Exchanges { get; } = Array.AsReadOnly(new[]
    {
        new ExchangeDefinition("binance", "Binance", "https://api.binance.com", true, () => new binance(), TimeframeIds),
        new ExchangeDefinition("bybit", "Bybit", "https://api.bybit.com", true, () => new bybit(), TimeframeIds),
        new ExchangeDefinition("okx", "OKX", "https://www.okx.com/api", false, () => new okx(), TimeframeIds),
        new ExchangeDefinition("kraken", "Kraken", "https://api.kraken.com", false, () => new kraken(), TimeframeIds)
    });

    private static readonly IReadOnlyDictionary<string, ExchangeDefinition> ExchangesById =
        Exchanges.ToDictionary(exchange => exchange.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, TimeframeDefinition> TimeframesById =
        Timeframes.ToDictionary(timeframe => timeframe.Id, StringComparer.OrdinalIgnoreCase);

    public static bool TryGetExchange(string? id, out ExchangeDefinition? exchange) =>
        ExchangesById.TryGetValue(id ?? string.Empty, out exchange);

    public static ExchangeDefinition GetExchange(string id) =>
        TryGetExchange(id, out var exchange)
            ? exchange!
            : throw new NotSupportedException($"Exchange '{id}' is not supported.");

    public static bool TryGetTimeframe(string? id, out TimeframeDefinition? timeframe) =>
        TimeframesById.TryGetValue(id ?? string.Empty, out timeframe);

    public static TimeframeDefinition GetTimeframe(string id) =>
        TryGetTimeframe(id, out var timeframe)
            ? timeframe!
            : throw new NotSupportedException($"Unsupported timeframe '{id}'.");
}
