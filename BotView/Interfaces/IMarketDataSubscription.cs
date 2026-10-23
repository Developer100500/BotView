using BotView.Models;
using BotView.Services;

public interface IMarketDataSubscription : IDisposable
{
    CandleCacheKey Key { get; }
    ICandleSeriesReader Series { get; }
    /// <summary> Последняя (живая) свеча была обновлена (тикнула цена/объём). </summary>
    event Action<Candle>? LiveCandleTicked;
    /// <summary> Свеча закрылась по сообщению поставщика или при переходе к следующей. </summary>
    event Action<Candle>? CandleClosed;
    /// <summary> Подгружен пакет старых свечей слева. </summary>
    event Action<Candle[]>? OlderCandlesLoaded;
}
