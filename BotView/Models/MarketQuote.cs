namespace BotView.Models;

/// <summary>An intraday bar start and its opening price.</summary>
public sealed record QuoteBar(DateTimeOffset Start, decimal Open);

/// <summary>Current quote and intraday bars, independent of the quote provider.</summary>
public sealed record MarketQuote(
    string Symbol,
    decimal LastPrice,
    DateTimeOffset AsOf,
    IReadOnlyList<QuoteBar> Bars,
    string Source,
    bool IsDelayed = false,
    decimal? PreviousClose = null);

/// <summary>Price movement measured from an explicitly chosen reference price.</summary>
public sealed record PriceMovement(decimal ReferencePrice, decimal LastPrice, DateTimeOffset AsOf)
{
    public decimal ChangePoints => LastPrice - ReferencePrice;
    public decimal ChangePercent => ChangePoints / ReferencePrice * 100m;
}
