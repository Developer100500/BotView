using BotView.Interfaces;
using BotView.Models;
using BotView.Services;
using Moq;

namespace BotView.Tests;

public sealed class MarketDataSubscriptionTests
{
    [Fact]
    public async Task CancellingInitialHistoryDoesNotCreateSubscription()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetCandlestickDataAsync(
                "binance", "BTC/USDT", "1m", 250, It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string _, int _, CancellationToken ct) => WaitForCancellation(ct));

        async Task<CandlestickData> WaitForCancellation(CancellationToken ct)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("The cancelled request must not complete.");
        }

        await using var service = new MarketDataService(exchange.Object);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var key = new CandleCacheKey("binance", "BTC/USDT", "1m", 250);
        var pending = service.SubscribeAsync(key, 250, cts.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(service.GetSeries(key).GetSnapshot()
            .Enumerate(long.MinValue, long.MaxValue));
    }

    [Fact]
    public async Task DisposingOneSubscriberKeepsSharedSeriesPollingForTheOther()
    {
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetCandlestickDataAsync("binance", "BTC/USDT", "1m", 250, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CandlestickData("1m", DateTime.UtcNow, DateTime.UtcNow,
                new[] { Candle(1000), Candle(2000) }));
        exchange.Setup(x => x.FetchOHLCVAsync("binance", "BTC/USDT", "1m", null, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new List<ccxt.OHLCV>
            {
                new() { timestamp = 2000, open = 1, high = 2, low = 1, close = 2, volume = 1 }
            });

        await using var service = new MarketDataService(exchange.Object, pollInterval: TimeSpan.FromMilliseconds(10));
        var key = new CandleCacheKey("binance", "BTC/USDT", "1m", 250);
        using var first = await service.SubscribeAsync(key, 250, TestContext.Current.CancellationToken);
        using var second = await service.SubscribeAsync(key, 250, TestContext.Current.CancellationToken);

        Assert.NotSame(first, second);
        Assert.Same(first.Series, second.Series);

        var tickAfterDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        second.LiveCandleTicked += _ => tickAfterDispose.TrySetResult();
        first.Dispose();
        await tickAfterDispose.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LastSubscriberReleasesOnlyItsMarketHistoryAndInitialCache()
    {
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetCandlestickDataAsync(
                It.IsAny<string>(), It.IsAny<string>(), "1m", 250, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string _, int _, CancellationToken _) =>
                new CandlestickData("1m", DateTime.UtcNow, DateTime.UtcNow,
                    new[] { Candle(60_000), Candle(120_000) }));
        var store = new CandleStore();
        await using var service = new MarketDataService(exchange.Object, store, TimeSpan.FromHours(1));
        var btc = new CandleCacheKey("binance", "BTC/USDT", "1m", 250);
        var eth = new CandleCacheKey("binance", "ETH/USDT", "1m", 250);
        var first = await service.SubscribeAsync(btc, 250, TestContext.Current.CancellationToken);
        var second = await service.SubscribeAsync(btc, 250, TestContext.Current.CancellationToken);
        using var otherMarket = await service.SubscribeAsync(eth, 250, TestContext.Current.CancellationToken);

        first.Dispose();
        Assert.Equal(2, store.GetKeyCount());
        Assert.Equal(1, store.GetCount(btc));
        exchange.Verify(x => x.EvictCandlestickData("binance", "BTC/USDT", "1m", 250), Times.Never);

        second.Dispose();
        await WaitForEvictionAsync(store, btc);
        Assert.Equal(1, store.GetKeyCount());
        Assert.Equal(1, store.GetCount(eth));
        exchange.Verify(x => x.EvictCandlestickData("binance", "BTC/USDT", "1m", 250), Times.Once);
    }

    [Fact]
    public async Task InFlightOlderHistoryCannotRestoreAnUnsubscribedSeries()
    {
        var fetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFetch = new TaskCompletionSource<List<ccxt.OHLCV>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetCandlestickDataAsync("binance", "BTC/USDT", "1m", 250,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CandlestickData("1m", DateTime.UtcNow, DateTime.UtcNow,
                new[] { Candle(180_000), Candle(240_000) }));
        exchange.Setup(x => x.FetchOHLCVAsync("binance", "BTC/USDT", "1m", 0, 250,
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                fetchStarted.TrySetResult();
                return finishFetch.Task;
            });
        var store = new CandleStore();
        await using var service = new MarketDataService(exchange.Object, store, TimeSpan.FromHours(1));
        var key = new CandleCacheKey("binance", "BTC/USDT", "1m", 250);
        var subscription = await service.SubscribeAsync(key, 250, TestContext.Current.CancellationToken);

        var pending = service.LoadOlderAsync(key, 250, TestContext.Current.CancellationToken);
        await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        subscription.Dispose();
        finishFetch.SetResult(new List<ccxt.OHLCV> { ExchangeCandle(60_000), ExchangeCandle(120_000) });

        Assert.Equal(0, await pending);
        await WaitForEvictionAsync(store, key);
        Assert.Equal(0, store.GetKeyCount());
    }

    private static async Task WaitForEvictionAsync(CandleStore store, CandleCacheKey key)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        while (store.GetCount(key) != 0)
            await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task LoadingOlderCandlesPrependsSharedBlocksAndPublishesOnlyNewCandles()
    {
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetCandlestickDataAsync("binance", "BTC/USDT", "1m", 250, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CandlestickData("1m", DateTime.UtcNow, DateTime.UtcNow,
                new[] { Candle(180_000), Candle(240_000) }));
        exchange.Setup(x => x.FetchOHLCVAsync("binance", "BTC/USDT", "1m", 0, 250, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ccxt.OHLCV>
            {
                ExchangeCandle(60_000), ExchangeCandle(120_000), ExchangeCandle(120_000),
                ExchangeCandle(180_000)
            });

        await using var service = new MarketDataService(exchange.Object, pollInterval: TimeSpan.FromHours(1));
        var key = new CandleCacheKey("binance", "BTC/USDT", "1m", 250);
        using var subscription = await service.SubscribeAsync(key, 250, TestContext.Current.CancellationToken);
        OHLCV[]? published = null;
        subscription.OlderCandlesLoaded += candles => published = candles;

        Assert.Equal(2, await service.LoadOlderAsync(key, 250, TestContext.Current.CancellationToken));
        Assert.Equal(new long[] { 60_000, 120_000 }, published!.Select(c => c.timestamp));
        Assert.Equal(new long[] { 60_000, 120_000, 180_000, 240_000 },
            subscription.Series.GetSnapshot().Enumerate(long.MinValue, long.MaxValue)
                .Select(item => item.Candle.timestamp));
    }

    [Fact]
    public async Task PollingCatchesUpMissingCandlesBeforePublishingTheNewLiveCandle()
    {
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetCandlestickDataAsync("binance", "BTC/USDT", "1m", 250, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CandlestickData("1m", DateTime.UtcNow, DateTime.UtcNow,
                new[] { Candle(60_000) }));
        var pollingResponse = new TaskCompletionSource<List<ccxt.OHLCV>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        exchange.Setup(x => x.FetchOHLCVAsync("binance", "BTC/USDT", "1m", null, 3, It.IsAny<CancellationToken>()))
            .Returns(() => pollingResponse.Task);
        exchange.Setup(x => x.FetchOHLCVAsync("binance", "BTC/USDT", "1m", 60_000, 250, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, 10)
                .Select(i => ExchangeCandle(i * 60_000L)).ToList());

        await using var service = new MarketDataService(exchange.Object, pollInterval: TimeSpan.FromHours(1));
        var key = new CandleCacheKey("binance", "BTC/USDT", "1m", 250);
        using var subscription = await service.SubscribeAsync(key, 250, TestContext.Current.CancellationToken);
        var closedEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscription.CandleClosed += (_, _) => closedEvent.TrySetResult();

        pollingResponse.SetResult(new List<ccxt.OHLCV>
        {
            ExchangeCandle(540_000), ExchangeCandle(600_000)
        });
        await closedEvent.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var snapshot = subscription.Series.GetSnapshot();
        Assert.Equal(10, snapshot.Count);
        Assert.Equal(9, snapshot.ClosedCount);
        Assert.Equal(600_000, snapshot.Live!.Value.timestamp);
    }

    private static OHLCV Candle(long timestamp) => new(timestamp, 1, 2, 1, 2, 1);
    private static ccxt.OHLCV ExchangeCandle(long timestamp) =>
        new() { timestamp = timestamp, open = 1, high = 2, low = 1, close = 2, volume = 1 };
}
