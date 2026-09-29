using System.IO;
using System.Net.Http;
using System.Text.Json;
using BotView.Interfaces;
using BotView.Models;

namespace BotView.Services;

/// <summary>Yahoo Finance chart adapter; provider-specific JSON stays here.</summary>
public sealed class YahooFinanceQuoteProvider : IQuoteProvider
{
    private readonly HttpClient _httpClient;

    public YahooFinanceQuoteProvider(HttpClient httpClient) =>
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<MarketQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range=1d&interval=5m";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "BotView/1.0");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var chart = document.RootElement.GetProperty("chart");
        if (chart.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException($"Yahoo Finance: {error.GetProperty("description").GetString()}");

        var results = chart.GetProperty("result");
        if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            throw new InvalidDataException("Yahoo Finance returned no quote data.");

        var result = results[0];
        var meta = result.GetProperty("meta");
        var price = meta.GetProperty("regularMarketPrice").GetDecimal();
        var asOf = DateTimeOffset.FromUnixTimeSeconds(meta.GetProperty("regularMarketTime").GetInt64());
        decimal? previousClose = null;
        foreach (var field in new[] { "previousClose", "chartPreviousClose" })
        {
            if (meta.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetDecimal(out var close) && close > 0)
            {
                previousClose = close;
                break;
            }
        }
        if (price <= 0)
            throw new InvalidDataException("Yahoo Finance returned an invalid price.");

        var timestamps = result.GetProperty("timestamp");
        var opens = result.GetProperty("indicators").GetProperty("quote")[0].GetProperty("open");
        if (timestamps.ValueKind != JsonValueKind.Array || opens.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Yahoo Finance returned invalid intraday bars.");

        var bars = new List<QuoteBar>();
        for (var i = 0; i < Math.Min(timestamps.GetArrayLength(), opens.GetArrayLength()); i++)
        {
            if (timestamps[i].ValueKind == JsonValueKind.Number && opens[i].ValueKind == JsonValueKind.Number)
                bars.Add(new QuoteBar(DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()), opens[i].GetDecimal()));
        }

        return new MarketQuote(symbol, price, asOf, bars, "Yahoo Finance", IsDelayed: true,
            PreviousClose: previousClose);
    }
}
