using BotView.Models;

namespace BotView.Services.MarketData;

public interface IMarketDataService : IAsyncDisposable
{
    /// <summary>Подписывается на обновления общей серии. Первый подписчик загружает initialHistory свечей;
    /// следующие используют уже загруженную серию.</summary>
    Task<IMarketDataSubscription> SubscribeAsync(CandleCacheKey key, int initialHistory = 500, CancellationToken ct = default);
    
    /// <summary> Догружает <paramref name="count"/> свечей старее текущей самой левой; идемпотентно. </summary>
    Task<int> LoadOlderAsync(CandleCacheKey key, int count, CancellationToken ct = default);

    /// <summary>Returns the shared candle series without flattening it.</summary>
    ICandleSeriesReader GetSeries(CandleCacheKey key);
}
