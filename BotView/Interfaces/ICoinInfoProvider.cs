using BotView.Models;

namespace BotView.Interfaces;

public interface ICoinInfoProvider
{
    Task<IReadOnlyList<CoinSearchResult>> SearchAsync(string symbol, CancellationToken ct = default);
    Task<CoinInfo> GetInfoAsync(string id, CancellationToken ct = default);
}
