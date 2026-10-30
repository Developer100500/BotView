using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BotView.Models;
using BotView.Services;
using Moq;

namespace BotView.Tests;

public sealed class MarketDataSubscriptionTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;
    private static Candle Bar(long timestamp, double close = 2) =>
        new() { Timestamp = timestamp, Open = 1, High = 3, Low = 1, Close = close, Volume = 10 };

    private static Mock<IExchangeService> Exchange(Candle[] initial, Channel<CandleUpdate>? updates = null)
    {
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetCandlestickDataAsync(It.IsAny<string>(), It.IsAny<string>(), "1m",
                It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(initial);
        exchange.Setup(x => x.GetCandlesAsync(It.IsAny<string>(), It.IsAny<string>(), "1m",
                It.IsAny<DateTimeOffset?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Candle>());
        exchange.Setup(x => x.WatchCandlesAsync(It.IsAny<string>(), It.IsAny<string>(), "1m", It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string _, CancellationToken ct) => updates is null ? Idle(ct) : updates.Reader.ReadAllAsync(ct));
        return exchange;
    }

    private static async IAsyncEnumerable<CandleUpdate> Idle([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    [Fact]
    public async Task CancellingInitialHistoryDoesNotCreateSubscription()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exchange = Exchange([]);
        exchange.Setup(x => x.GetCandlestickDataAsync("binance", "BTC/USDT", "1m", 250, It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string _, int _, CancellationToken ct) => WaitForCancellation(ct));
        async Task<Candle[]> WaitForCancellation(CancellationToken ct)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return [];
        }
        await using var service = new MarketDataService(exchange.Object);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var key = new CandleCacheKey("binance", "BTC/USDT", "1m");
        var pending = service.SubscribeAsync(key, 250, cts.Token);
        await started.Task.WaitAsync(TestToken);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, service.GetSeries(key).GetSnapshot().Count);
    }

    [Fact]
    public async Task SubscribersWithDifferentInitialSizesShareOneSeriesAndOneStream()
    {
        var updates = Channel.CreateUnbounded<CandleUpdate>();
        var exchange = Exchange([Bar(60_000), Bar(120_000)], updates);
        await using var service = new MarketDataService(exchange.Object);
        var key = new CandleCacheKey("binance", "BTC/USDT", "1m");
        using var first = await service.SubscribeAsync(key, 250, TestToken);
        using var second = await service.SubscribeAsync(key, 500, TestToken);
        Assert.Same(first.Series, second.Series);
        var tick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        second.LiveCandleTicked += _ => tick.TrySetResult();
        first.Dispose();
        updates.Writer.TryWrite(new(Bar(120_000, 2.5), false));
        await tick.Task.WaitAsync(TimeSpan.FromSeconds(2), TestToken);
        Assert.Equal(2.5, second.Series.GetSnapshot().Live!.Value.Close);
        exchange.Verify(x => x.GetCandlestickDataAsync("binance", "BTC/USDT", "1m", 250, It.IsAny<CancellationToken>()), Times.Once);
        exchange.Verify(x => x.WatchCandlesAsync("binance", "BTC/USDT", "1m", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LastSubscriberReleasesOnlyItsMarketHistoryAndInitialCache()
    {
        var exchange = Exchange([Bar(60_000), Bar(120_000)]);
        var store = new CandleStore();
        await using var service = new MarketDataService(exchange.Object, store);
        var btc = new CandleCacheKey("binance", "BTC/USDT", "1m");
        var eth = new CandleCacheKey("binance", "ETH/USDT", "1m");
        var first = await service.SubscribeAsync(btc, 250, TestToken);
        var second = await service.SubscribeAsync(btc, 250, TestToken);
        using var other = await service.SubscribeAsync(eth, 250, TestToken);
        first.Dispose();
        Assert.Equal(2, store.GetKeyCount());
        exchange.Verify(x => x.EvictCandlestickData("binance", "BTC/USDT", "1m"), Times.Never);
        second.Dispose();
        await WaitForEvictionAsync(store, btc);
        Assert.Equal(1, store.GetKeyCount());
        Assert.Equal(1, store.GetCount(eth));
        exchange.Verify(x => x.EvictCandlestickData("binance", "BTC/USDT", "1m"), Times.Once);
    }

    [Fact]
    public async Task InFlightOlderHistoryCannotRestoreAnUnsubscribedSeries()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<Candle[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exchange = Exchange([Bar(180_000), Bar(240_000)]);
        exchange.Setup(x => x.GetCandlesAsync("binance", "BTC/USDT", "1m",
                DateTimeOffset.FromUnixTimeMilliseconds(180_000), 250, It.IsAny<CancellationToken>()))
            .Returns(() => { started.TrySetResult(); return finish.Task; });
        var store = new CandleStore();
        await using var service = new MarketDataService(exchange.Object, store);
        var key = new CandleCacheKey("binance", "BTC/USDT", "1m");
        var subscription = await service.SubscribeAsync(key, 250, TestToken);
        var pending = service.LoadOlderAsync(key, 250, TestToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestToken);
        subscription.Dispose();
        finish.SetResult([Bar(60_000), Bar(120_000)]);
        Assert.Equal(0, await pending);
        await WaitForEvictionAsync(store, key);
        Assert.Equal(0, store.GetKeyCount());
    }

    [Fact]
    public async Task OlderHistoryLoadsOnlyOnRequestAndUsesTheActualLeftBoundaryAcrossTradingBreaks()
    {
        var monday = new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.FromHours(3));
        var friday = monday.AddDays(-3);
        var exchange = Exchange([Bar(monday.ToUnixTimeMilliseconds()), Bar(monday.AddMinutes(1).ToUnixTimeMilliseconds())]);
        exchange.Setup(x => x.GetCandlesAsync("binance", "BTC/USDT", "1m", monday, 250, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Bar(friday.ToUnixTimeMilliseconds()), Bar(friday.AddMinutes(1).ToUnixTimeMilliseconds())]);
        await using var service = new MarketDataService(exchange.Object);
        var key = new CandleCacheKey("binance", "BTC/USDT", "1m");
        using var subscription = await service.SubscribeAsync(key, 250, TestToken);
        exchange.Verify(x => x.GetCandlesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTimeOffset?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        Candle[]? published = null;
        subscription.OlderCandlesLoaded += candles => published = candles;
        Assert.Equal(2, await service.LoadOlderAsync(key, 250, TestToken));
        Assert.Equal(friday.ToUnixTimeMilliseconds(), published![0].Timestamp);
        Assert.Equal(4, subscription.Series.GetSnapshot().Count);
    }

    [Fact]
    public async Task StreamCatchesUpMissingCandlesBeforePublishingTheNewLiveCandle()
    {
        var updates = Channel.CreateUnbounded<CandleUpdate>();
        var exchange = Exchange([Bar(60_000)], updates);
        exchange.Setup(x => x.GetCandlesAsync("binance", "BTC/USDT", "1m",
                DateTimeOffset.FromUnixTimeMilliseconds(600_000), 250, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, 9).Select(i => Bar(i * 60_000L)).ToArray());
        await using var service = new MarketDataService(exchange.Object);
        var key = new CandleCacheKey("binance", "BTC/USDT", "1m");
        using var subscription = await service.SubscribeAsync(key, 250, TestToken);
        var tick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscription.LiveCandleTicked += c => { if (c.Timestamp == 600_000) tick.TrySetResult(); };
        updates.Writer.TryWrite(new(Bar(600_000), false));
        await tick.Task.WaitAsync(TimeSpan.FromSeconds(2), TestToken);
        var snapshot = subscription.Series.GetSnapshot();
        Assert.Equal(10, snapshot.Count);
        Assert.Equal(9, snapshot.ClosedCount);
        Assert.Equal(600_000, snapshot.Live!.Value.Timestamp);
    }

    [Fact]
    public async Task ExplicitCloseSealsTheLastCandleWithoutWaitingForTheNextSession()
    {
        var updates = Channel.CreateUnbounded<CandleUpdate>();
        var exchange = Exchange([Bar(60_000), Bar(120_000)], updates);
        await using var service = new MarketDataService(exchange.Object);
        using var subscription = await service.SubscribeAsync(new("binance", "BTC/USDT", "1m"), 250, TestToken);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscription.CandleClosed += _ => closed.TrySetResult();
        updates.Writer.TryWrite(new(Bar(120_000, 2.5), true));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestToken);
        var snapshot = subscription.Series.GetSnapshot();
        Assert.Null(snapshot.Live);
        Assert.Equal(2, snapshot.ClosedCount);
        Assert.Equal(2.5, snapshot[1].Close);
    }

    [Fact]
    public async Task NewSnapshotRecoversFinalValuesIfTheCloseWasMissedBetweenHistoryAndSubscription()
    {
        var updates = Channel.CreateUnbounded<CandleUpdate>();
        var exchange = Exchange([Bar(60_000)], updates);
        var final = Bar(60_000) with { Volume = 25 };
        exchange.Setup(x => x.GetCandlesAsync("binance", "BTC/USDT", "1m",
                DateTimeOffset.FromUnixTimeMilliseconds(120_000), 250, It.IsAny<CancellationToken>()))
            .ReturnsAsync([final]);
        await using var service = new MarketDataService(exchange.Object);
        using var subscription = await service.SubscribeAsync(new("binance", "BTC/USDT", "1m"), 250, TestToken);
        var tick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscription.LiveCandleTicked += _ => tick.TrySetResult();
        updates.Writer.TryWrite(new(Bar(120_000), false));
        await tick.Task.WaitAsync(TimeSpan.FromSeconds(2), TestToken);
        Assert.Equal(25, subscription.Series.GetSnapshot()[0].Volume);
        Assert.Equal(120_000, subscription.Series.GetSnapshot().Live!.Value.Timestamp);
    }

    private static async Task WaitForEvictionAsync(CandleStore store, CandleCacheKey key)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        while (store.GetCount(key) != 0) await Task.Delay(10, timeout.Token);
    }
}
