using System.IO;
using BotView.Database;
using BotView.Interfaces;
using BotView.Models;
using BotView.Services;
using BotView.ViewModels;
using Moq;

namespace BotView.Tests;

public sealed class SelectionCancellationTests
{
    [Fact]
    public async Task ChangingExchangeReleasesDisplayedSeriesBeforeMarketsFinishLoading()
    {
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetAvailableSymbolsAsync("binance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "BTC/USDT" });
        var marketsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        exchange.Setup(x => x.GetAvailableSymbolsAsync("bybit", It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken ct) => WaitForCancellation(ct));

        async Task<List<string>> WaitForCancellation(CancellationToken ct)
        {
            marketsStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        }

        var oldSubscription = new Mock<IMarketDataSubscription>();
        oldSubscription.SetupGet(x => x.Key)
            .Returns(new CandleCacheKey("binance", "BTC/USDT", "1d", CandleSeries.CandleChunkSize));
        oldSubscription.SetupGet(x => x.Series).Returns(new CandleSeries());
        var marketData = new Mock<IMarketDataService>();
        marketData.Setup(x => x.SubscribeAsync(It.IsAny<CandleCacheKey>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(oldSubscription.Object);

        using var viewModel = new MainWindowViewModel(
            new DatabaseService(), new Mock<IDataProvider>().Object, exchange.Object, marketData.Object,
            new MetricsController(exchange.Object));
        await viewModel.StartAsync();
        Assert.NotNull(viewModel.CurrentSeries);
        var cleared = false;
        viewModel.ChartSeriesCleared += (_, _) => cleared = true;

        viewModel.SelectedExchange = "bybit";
        await marketsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.True(cleared);
        Assert.Null(viewModel.CurrentSeries);
        oldSubscription.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public async Task ChangingSelectionCancelsOlderHistoryLoad()
    {
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetAvailableSymbolsAsync("binance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "BTC/USDT" });

        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var marketData = new Mock<IMarketDataService>();
        marketData.Setup(x => x.SubscribeAsync(
                It.IsAny<CandleCacheKey>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((CandleCacheKey key, int _, CancellationToken _) =>
            {
                var subscription = new Mock<IMarketDataSubscription>();
                subscription.SetupGet(x => x.Key).Returns(key);
                subscription.SetupGet(x => x.Series).Returns(new CandleSeries());
                return Task.FromResult(subscription.Object);
            });
        marketData.Setup(x => x.LoadOlderAsync(
                It.IsAny<CandleCacheKey>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((CandleCacheKey _, int _, CancellationToken ct) => WaitForCancellation(ct));

        async Task<int> WaitForCancellation(CancellationToken ct)
        {
            started.TrySetResult(ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("The cancelled request must not complete.");
        }

        using var viewModel = new MainWindowViewModel(
            new DatabaseService(), new Mock<IDataProvider>().Object, exchange.Object, marketData.Object,
            new MetricsController(exchange.Object));
        await viewModel.StartAsync();

        var olderLoad = viewModel.LoadOlderAsync();
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        viewModel.SelectedTimeframe = "1h";
        await olderLoad.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task ChangingTimeframeCancelsPendingSubscription()
    {
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetAvailableSymbolsAsync("binance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "BTC/USDT" });

        var firstRequest = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextSubscription = new TaskCompletionSource<CandleCacheKey>(TaskCreationOptions.RunContinuationsAsynchronously);
        var marketData = new Mock<IMarketDataService>();
        marketData.Setup(x => x.SubscribeAsync(
                It.IsAny<CandleCacheKey>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((CandleCacheKey key, int _, CancellationToken ct) =>
            {
                if (key.Timeframe == "1d")
                {
                    firstRequest.TrySetResult(ct);
                    return WaitForCancellation(ct);
                }

                nextSubscription.TrySetResult(key);
                var subscription = new Mock<IMarketDataSubscription>();
                subscription.SetupGet(x => x.Key).Returns(key);
                subscription.SetupGet(x => x.Series).Returns(new CandleSeries());
                return Task.FromResult(subscription.Object);
            });

        static async Task<IMarketDataSubscription> WaitForCancellation(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("The cancelled request must not complete.");
        }

        using var viewModel = new MainWindowViewModel(
            new DatabaseService(), new Mock<IDataProvider>().Object, exchange.Object, marketData.Object,
            new MetricsController(exchange.Object));

        var initialLoad = viewModel.StartAsync();
        var firstToken = await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        viewModel.SelectedTimeframe = "1h";
        var key = await nextSubscription.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await initialLoad.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(firstToken.IsCancellationRequested);
        Assert.Equal("1h", key.Timeframe);
        Assert.NotNull(viewModel.CurrentSeries);
        marketData.Verify(x => x.SubscribeAsync(
            It.IsAny<CandleCacheKey>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData("exchange")]
    [InlineData("symbol")]
    [InlineData("timeframe")]
    public async Task ChangingSelectionCancelsPreviousLoad(string changedField)
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exchange = new Mock<IExchangeService>();
        var requestCount = 0;
        exchange.Setup(x => x.GetAvailableSymbolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken ct) =>
            {
                if (Interlocked.Increment(ref requestCount) == 1)
                {
                    started.TrySetResult(ct);
                    return WaitForCancellation(ct);
                }

                return Task.FromResult(new List<string> { "BTC/USDT", "ETH/USDT" });
            });

        static async Task<List<string>> WaitForCancellation(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("The cancelled request must not complete.");
        }

        var marketData = new Mock<IMarketDataService>();
        var subscribedKey = new TaskCompletionSource<CandleCacheKey>(TaskCreationOptions.RunContinuationsAsynchronously);
        marketData.Setup(x => x.SubscribeAsync(
                It.IsAny<CandleCacheKey>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((CandleCacheKey key, int _, CancellationToken _) =>
            {
                subscribedKey.TrySetResult(key);
                var subscription = new Mock<IMarketDataSubscription>();
                subscription.SetupGet(x => x.Key).Returns(key);
                subscription.SetupGet(x => x.Series).Returns(new CandleSeries());
                return Task.FromResult(subscription.Object);
            });

        var dataProvider = new Mock<IDataProvider>();
        var favoritesPath = Path.Combine(Path.GetTempPath(), $"botview-cancellation-{Guid.NewGuid():N}.json");
        using var viewModel = new MainWindowViewModel(
            new DatabaseService(), dataProvider.Object, exchange.Object, marketData.Object,
            new MetricsController(exchange.Object), new FavoritePairsStore(favoritesPath));

        var initialLoad = viewModel.StartAsync();
        var firstToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        switch (changedField)
        {
            case "exchange": viewModel.SelectedExchange = "bybit"; break;
            case "symbol": viewModel.SelectedSymbol = "ETH/USDT"; break;
            case "timeframe": viewModel.SelectedTimeframe = "1h"; break;
        }

        var key = await subscribedKey.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await initialLoad.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(firstToken.IsCancellationRequested);
        Assert.Equal(viewModel.SelectedExchange, key.Exchange);
        Assert.Equal(viewModel.SelectedSymbol, key.Symbol);
        Assert.Equal(viewModel.SelectedTimeframe, key.Timeframe);
        marketData.Verify(x => x.SubscribeAsync(
            It.IsAny<CandleCacheKey>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
