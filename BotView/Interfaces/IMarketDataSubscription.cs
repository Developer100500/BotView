using BotView.Models;
using BotView.Services;

public interface IMarketDataSubscription : IDisposable
{
    CandleCacheKey Key { get; }
    ICandleSeriesReader Series { get; }
    /// <summary> Последняя (живая) свеча была обновлена (тикнула цена/объём). </summary>
    event Action<OHLCV>? LiveCandleTicked;
    /// <summary> Закрылась текущая свеча и появилась новая (right edge продвинулся на 1). </summary>
    event Action<OHLCV /*closed*/, OHLCV /*newOpen*/>? CandleClosed;
    /// <summary> Подгружен пакет старых свечей слева. </summary>
    event Action<OHLCV[]>? OlderCandlesLoaded;
}
