namespace BotView.Configuration;

/// <summary>One definition for each timeframe used by the UI, chart and data services.</summary>
public sealed record TimeframeDefinition(string Id, TimeSpan Duration, int DemoCandleCount);

/// <summary>Transport-independent metadata for one provider connection.</summary>
public sealed class ExchangeDefinition
{
    public ExchangeDefinition(
        string id, string displayName, string apiUrl, bool isActive,
        IReadOnlyList<string> supportedTimeframes, string providerId = "custom", string? venue = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (supportedTimeframes.Count == 0)
            throw new ArgumentException("At least one timeframe is required.", nameof(supportedTimeframes));
        Id = id;
        DisplayName = displayName;
        ApiUrl = apiUrl;
        IsActive = isActive;
        ProviderId = providerId;
        Venue = venue ?? id;
        SupportedTimeframes = Array.AsReadOnly(supportedTimeframes.ToArray());
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string ApiUrl { get; }
    public bool IsActive { get; }
    public IReadOnlyList<string> SupportedTimeframes { get; }
    public string ProviderId { get; }
    public string Venue { get; }

    public bool SupportsTimeframe(string timeframe) =>
        SupportedTimeframes.Contains(timeframe, StringComparer.Ordinal);
}

/// <summary>Default connection metadata and fixed-duration timeframe definitions.</summary>
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
        new ExchangeDefinition("binance", "Binance", "https://api.binance.com", true, TimeframeIds, "ccxt"),
        new ExchangeDefinition("bybit", "Bybit", "https://api.bybit.com", true, TimeframeIds, "ccxt"),
        new ExchangeDefinition("okx", "OKX", "https://www.okx.com/api", false, TimeframeIds, "ccxt"),
        new ExchangeDefinition("kraken", "Kraken", "https://api.kraken.com", false, TimeframeIds, "ccxt")
    });

    private static readonly IReadOnlyDictionary<string, ExchangeDefinition> ExchangesById =
        Exchanges.ToDictionary(exchange => exchange.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, TimeframeDefinition> TimeframesById =
        Timeframes.ToDictionary(timeframe => timeframe.Id, StringComparer.Ordinal);

    public static bool TryGetExchange(string? id, out ExchangeDefinition? exchange) =>
        ExchangesById.TryGetValue(id ?? string.Empty, out exchange);

    public static ExchangeDefinition GetExchange(string id) =>
        TryGetExchange(id, out var exchange)
            ? exchange!
            : throw new NotSupportedException($"Exchange '{id}' is not supported.");

    /// <summary>Resolves a standard interval or a provider's fixed interval (e.g. MOEX 10m).
    /// Calendar months are not fixed durations and must not be confused with lowercase m.</summary>
    public static bool TryGetTimeframe(string? id, out TimeframeDefinition? timeframe)
    {
        if (TimeframesById.TryGetValue(id ?? string.Empty, out timeframe)) return true;
        if (string.IsNullOrEmpty(id) || id.Length < 2 ||
            !int.TryParse(id.AsSpan(0, id.Length - 1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var count) || count <= 0) return false;
        var unitTicks = id[^1] switch
        {
            's' => TimeSpan.TicksPerSecond,
            'm' => TimeSpan.TicksPerMinute,
            'h' => TimeSpan.TicksPerHour,
            'd' => TimeSpan.TicksPerDay,
            'w' => TimeSpan.TicksPerDay * 7,
            _ => 0L
        };
        if (unitTicks == 0) return false;
        try
        {
            var duration = TimeSpan.FromTicks(checked(unitTicks * count));
            var demoCount = (int)Math.Clamp(TimeSpan.TicksPerDay / duration.Ticks, 30L, 1440L);
            timeframe = new TimeframeDefinition(id, duration, demoCount);
            return true;
        }
        catch (OverflowException) { return false; }
    }

    public static TimeframeDefinition GetTimeframe(string id) =>
        TryGetTimeframe(id, out var timeframe)
            ? timeframe!
            : throw new NotSupportedException($"Unsupported timeframe '{id}'.");
}
