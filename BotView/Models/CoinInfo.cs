namespace BotView.Models;

public sealed record CoinSearchResult(string Id, string Name, string Symbol, int? MarketCapRank)
{
    public string DisplayName => $"{Name} ({Symbol.ToUpperInvariant()}) · {Id}";
}

public sealed record CoinInfo(
    string Id, string Name, string Symbol, string Description, string Language,
    string? WebsiteUrl, IReadOnlyList<string> Categories)
{
    public string SourceUrl => $"https://www.coingecko.com/en/coins/{Uri.EscapeDataString(Id)}";
}
