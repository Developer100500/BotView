using BotView.Models;

namespace BotView.Interfaces;

/// <summary>Provides provider-independent intraday prices for a market symbol.</summary>
public interface IQuoteProvider
{
    Task<MarketQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default);
}
