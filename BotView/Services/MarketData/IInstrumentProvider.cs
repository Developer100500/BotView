using BotView.Configuration;
using BotView.Models;

namespace BotView.Services.MarketData;

/// <summary>One connection exposing instruments independently of its candle capabilities.</summary>
public interface IInstrumentProvider : IAsyncDisposable
{
    ExchangeDefinition Definition { get; }
    Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct = default);
}
