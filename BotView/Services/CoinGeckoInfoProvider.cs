using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using BotView.Interfaces;
using BotView.Models;

namespace BotView.Services;

/// <summary>CoinGecko metadata, cached for a day independently of exchange quotes.</summary>
public sealed class CoinGeckoInfoProvider : ICoinInfoProvider
{
    private const string ApiRoot = "https://api.coingecko.com/api/v3/";
    private readonly HttpClient _client;
    private readonly string? _demoApiKey;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, IReadOnlyList<CoinSearchResult> Value)> _searchCache = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, CoinInfo Value)> _infoCache = new();

    public CoinGeckoInfoProvider(HttpClient client, string? demoApiKey = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _demoApiKey = demoApiKey;
    }

    public async Task<IReadOnlyList<CoinSearchResult>> SearchAsync(string symbol, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        symbol = symbol.Trim().ToUpperInvariant();
        ct.ThrowIfCancellationRequested();
        if (_searchCache.TryGetValue(symbol, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Value;

        using var json = await GetJsonAsync($"search?query={Uri.EscapeDataString(symbol)}", ct);
        if (!json.RootElement.TryGetProperty("coins", out var coins) || coins.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("CoinGecko вернул некорректные результаты поиска.");

        var matches = new List<CoinSearchResult>();
        foreach (var coin in coins.EnumerateArray())
        {
            var id = GetString(coin, "id");
            var coinSymbol = GetString(coin, "symbol");
            if (string.IsNullOrWhiteSpace(id) || !symbol.Equals(coinSymbol, StringComparison.OrdinalIgnoreCase)) continue;
            int? rank = coin.TryGetProperty("market_cap_rank", out var rankJson) && rankJson.ValueKind == JsonValueKind.Number && rankJson.TryGetInt32(out var value)
                ? value : null;
            matches.Add(new CoinSearchResult(id, GetString(coin, "name") ?? id, coinSymbol!, rank));
        }
        var result = matches.DistinctBy(m => m.Id).OrderBy(m => m.MarketCapRank ?? int.MaxValue)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        // Do not cache an empty search, so a newly listed coin can be retried.
        if (result.Length > 0) _searchCache[symbol] = (DateTimeOffset.UtcNow.AddDays(1), result);
        return result;
    }

    public async Task<CoinInfo> GetInfoAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ct.ThrowIfCancellationRequested();
        if (_infoCache.TryGetValue(id, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Value;

        using var json = await GetJsonAsync($"coins/{Uri.EscapeDataString(id)}?localization=true&tickers=false&market_data=false", ct);
        var root = json.RootElement;
        if (GetString(root, "id") != id || string.IsNullOrWhiteSpace(GetString(root, "symbol")))
            throw new InvalidDataException("CoinGecko вернул данные другой монеты.");

        var description = "";
        var language = "";
        if (root.TryGetProperty("description", out var descriptions) && descriptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var locale in new[] { "ru", "en" })
            {
                description = ToPlainText(GetString(descriptions, locale) ?? "");
                if (description.Length == 0) continue;
                language = locale;
                break;
            }
        }
        string? website = null;
        if (root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object && links.TryGetProperty("homepage", out var homepages) &&
            homepages.ValueKind == JsonValueKind.Array)
            website = homepages.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String)
                .Select(p => p.GetString()).FirstOrDefault(IsWebUrl);

        var categories = root.TryGetProperty("categories", out var categoryJson) && categoryJson.ValueKind == JsonValueKind.Array
            ? categoryJson.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String)
                .Select(c => c.GetString()!).Where(c => !string.IsNullOrWhiteSpace(c)).ToArray()
            : Array.Empty<string>();
        var info = new CoinInfo(id, GetString(root, "name") ?? id, GetString(root, "symbol")!.ToUpperInvariant(),
            description, language, website, categories);
        _infoCache[id] = (DateTimeOffset.UtcNow.AddDays(1), info);
        return info;
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoot + path);
        request.Headers.TryAddWithoutValidation("User-Agent", "BotView/1.0");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!string.IsNullOrWhiteSpace(_demoApiKey))
            request.Headers.TryAddWithoutValidation("x-cg-demo-api-key", _demoApiKey);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("Лимит запросов CoinGecko исчерпан. Повторите попытку позже.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new HttpRequestException("CoinGecko отклонил доступ. Проверьте Demo API key (COINGECKO_DEMO_API_KEY) и перезапустите BotView.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException("Монета не найдена в CoinGecko.");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    internal static bool IsWebUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http";

    internal static string ToPlainText(string html)
    {
        var text = Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<br\s*/?>|</(?:p|div|li|h[1-6])\s*>", "\n\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = WebUtility.HtmlDecode(text).Replace("\\r\\n", "\n").Replace("\r\n", "\n").Replace('\r', '\n');
        text = Regex.Replace(text, @"[^\S\n]+", " ");
        return Regex.Replace(text, @"\n\s*\n(?:\s*\n)+", "\n\n").Trim();
    }
}
