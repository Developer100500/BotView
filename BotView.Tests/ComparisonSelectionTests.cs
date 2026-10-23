using BotView.Database;
using BotView.Chart;
using BotView.Interfaces;
using BotView.Models;
using BotView.Services;
using BotView.ViewModels;
using Moq;

namespace BotView.Tests;

public sealed class ComparisonSelectionTests
{
    /// <summary>Verifies comparison uses independent subscription and releases it on removal.</summary>
    [Fact]
    public async Task ComparisonUsesIndependentSubscriptionAndReleasesItOnRemoval()
    {
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetAvailableSymbolsAsync("binance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "BTC/USDT", "ETH/USDT" });
        var primary = Subscription("BTC/USDT", "1d");
        var comparison = Subscription("ETH/USDT", "1d");
        var market = new Mock<IMarketDataService>();
        market.Setup(x => x.SubscribeAsync(It.IsAny<CandleCacheKey>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CandleCacheKey key, int _, CancellationToken _) =>
                key.Symbol == "ETH/USDT" ? comparison.Object : primary.Object);
        using var vm = new MainWindowViewModel(new DatabaseService(), new Mock<IDataProvider>().Object,
            exchange.Object, market.Object, new MetricsController(exchange.Object));
        await vm.StartAsync();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ComparisonSeriesReady += (_, _, _) => ready.TrySetResult();

        vm.SelectComparisonResultCommand.Execute("ETH/USDT");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("BTC/USDT", vm.SelectedSymbol);
        Assert.Equal("ETH/USDT", vm.ComparisonSymbol);
        Assert.Same(comparison.Object.Series, vm.CurrentComparisonSeries);
        vm.RemoveComparisonCommand.Execute(null);
        Assert.Null(vm.CurrentComparisonSeries);
        comparison.Verify(x => x.Dispose(), Times.Once);
        primary.Verify(x => x.Dispose(), Times.Never);
    }

    /// <summary>Verifies timeframe change cancels pending comparison and starts current one.</summary>
    [Fact]
    public async Task TimeframeChangeCancelsPendingComparisonAndStartsCurrentOne()
    {
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetAvailableSymbolsAsync("binance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "BTC/USDT", "ETH/USDT" });
        var pending = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var market = new Mock<IMarketDataService>();
        market.Setup(x => x.SubscribeAsync(It.IsAny<CandleCacheKey>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns((CandleCacheKey key, int _, CancellationToken ct) =>
            {
                if (key.Symbol == "ETH/USDT" && key.Timeframe == "1d")
                {
                    pending.TrySetResult(ct);
                    return WaitForCancellation(ct);
                }
                return Task.FromResult(Subscription(key.Symbol, key.Timeframe).Object);
            });
        using var vm = new MainWindowViewModel(new DatabaseService(), new Mock<IDataProvider>().Object,
            exchange.Object, market.Object, new MetricsController(exchange.Object));
        await vm.StartAsync();
        vm.SelectComparisonResultCommand.Execute("ETH/USDT");
        var staleToken = await pending.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ComparisonSeriesReady += (_, _, _) => ready.TrySetResult();

        vm.SelectedTimeframe = "1h";
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(staleToken.IsCancellationRequested);
        Assert.Equal("ETH/USDT", vm.ComparisonSymbol);
        Assert.NotNull(vm.CurrentComparisonSeries);
        market.Verify(x => x.SubscribeAsync(It.Is<CandleCacheKey>(key =>
            key.Symbol == "ETH/USDT" && key.Timeframe == "1h"), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Verifies selecting compared symbol as primary clears comparison.</summary>
    [Fact]
    public async Task SelectingComparedSymbolAsPrimaryClearsComparison()
    {
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetAvailableSymbolsAsync("binance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "BTC/USDT", "ETH/USDT" });
        var comparison = Subscription("ETH/USDT", "1d");
        var market = new Mock<IMarketDataService>();
        market.Setup(x => x.SubscribeAsync(It.IsAny<CandleCacheKey>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CandleCacheKey key, int _, CancellationToken _) =>
                key.Symbol == "ETH/USDT" ? comparison.Object : Subscription(key.Symbol, key.Timeframe).Object);
        using var vm = new MainWindowViewModel(new DatabaseService(), new Mock<IDataProvider>().Object,
            exchange.Object, market.Object, new MetricsController(exchange.Object));
        await vm.StartAsync();
        vm.SelectComparisonResultCommand.Execute("ETH/USDT");
        Assert.NotNull(vm.CurrentComparisonSeries);

        vm.SelectedSymbol = "ETH/USDT";

        Assert.Null(vm.ComparisonSymbol);
        Assert.Null(vm.CurrentComparisonSeries);
        comparison.Verify(x => x.Dispose(), Times.Once);
    }

    /// <summary>Verifies visible range backfills comparison history.</summary>
    [Fact]
    public async Task VisibleRangeBackfillsComparisonHistory()
    {
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetAvailableSymbolsAsync("binance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "BTC/USDT", "ETH/USDT" });
        var now = DateTime.UtcNow;
        var comparisonSeries = new CandleSeries();
        comparisonSeries.LoadInitial(new[] { Candle(now.AddDays(-2), 10), Candle(now.AddDays(-1), 11) });
        var comparison = Subscription("ETH/USDT", "1d");
        comparison.SetupGet(x => x.Series).Returns(comparisonSeries);
        var fetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var market = new Mock<IMarketDataService>();
        market.Setup(x => x.SubscribeAsync(It.IsAny<CandleCacheKey>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CandleCacheKey key, int _, CancellationToken _) =>
                key.Symbol == "ETH/USDT" ? comparison.Object : Subscription(key.Symbol, key.Timeframe).Object);
        market.Setup(x => x.LoadOlderAsync(It.Is<CandleCacheKey>(key => key.Symbol == "ETH/USDT"),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                comparisonSeries.PrependHistory(new[] { Candle(now.AddDays(-11), 8) });
                fetched.TrySetResult();
                return 1;
            });
        using var vm = new MainWindowViewModel(new DatabaseService(), new Mock<IDataProvider>().Object,
            exchange.Object, market.Object, new MetricsController(exchange.Object));
        await vm.StartAsync();
        vm.SelectComparisonResultCommand.Execute("ETH/USDT");

        vm.RequestComparisonHistory(now.AddDays(-10));
        await fetched.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(comparisonSeries.GetSnapshot().OldestTimestamp <=
            ComparisonSeriesMath.ToUnixMilliseconds(now.AddDays(-10)));
        market.Verify(x => x.LoadOlderAsync(It.Is<CandleCacheKey>(key => key.Symbol == "ETH/USDT"),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Verifies comparison load failure keeps primary chart available.</summary>
    [Fact]
    public async Task ComparisonLoadFailureKeepsPrimaryChartAvailable()
    {
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetAvailableSymbolsAsync("binance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "BTC/USDT", "ETH/USDT" });
        var market = new Mock<IMarketDataService>();
        market.Setup(x => x.SubscribeAsync(It.IsAny<CandleCacheKey>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns((CandleCacheKey key, int _, CancellationToken _) =>
                key.Symbol == "ETH/USDT"
                    ? Task.FromException<IMarketDataSubscription>(new InvalidOperationException("market unavailable"))
                    : Task.FromResult(Subscription(key.Symbol, key.Timeframe).Object));
        using var vm = new MainWindowViewModel(new DatabaseService(), new Mock<IDataProvider>().Object,
            exchange.Object, market.Object, new MetricsController(exchange.Object));
        await vm.StartAsync();
        var primary = vm.CurrentSeries;

        vm.SelectComparisonResultCommand.Execute("ETH/USDT");

        Assert.Same(primary, vm.CurrentSeries);
        Assert.Null(vm.CurrentComparisonSeries);
        Assert.Contains("market unavailable", vm.ComparisonStatusText);
    }

    /// <summary>Checks that a late comparison result is released after the user removes the overlay.</summary>
    [Fact]
    public async Task LateComparisonResultIsDisposedAfterRemoval()
    {
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetAvailableSymbolsAsync("binance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "BTC/USDT", "ETH/USDT" });
        var pending = new TaskCompletionSource<IMarketDataSubscription>(TaskCreationOptions.RunContinuationsAsynchronously);
        var market = new Mock<IMarketDataService>();
        market.Setup(x => x.SubscribeAsync(It.IsAny<CandleCacheKey>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((CandleCacheKey key, int _, CancellationToken _) => key.Symbol == "ETH/USDT"
                ? pending.Task : Task.FromResult(Subscription(key.Symbol, key.Timeframe).Object));
        using var vm = new MainWindowViewModel(new DatabaseService(), new Mock<IDataProvider>().Object,
            exchange.Object, market.Object, new MetricsController(exchange.Object));
        await vm.StartAsync();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = Subscription("ETH/USDT", "1d");
        stale.Setup(x => x.Dispose()).Callback(() => released.TrySetResult());
        int readyCount = 0;
        vm.ComparisonSeriesReady += (_, _, _) => readyCount++;
        vm.SelectComparisonResultCommand.Execute("ETH/USDT");
        vm.RemoveComparisonCommand.Execute(null);
        pending.SetResult(stale.Object);
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Null(vm.CurrentComparisonSeries);
        Assert.Null(vm.ComparisonSymbol);
        Assert.Equal(0, readyCount);
        stale.Verify(x => x.Dispose(), Times.Once);
    }

    /// <summary>Checks that synchronous history updates raised during comparison attachment reach the chart.</summary>
    [Fact]
    public async Task HistoryRequestedFromReadyEventPublishesItsDataChange()
    {
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetAvailableSymbolsAsync("binance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "BTC/USDT", "ETH/USDT" });
        var comparison = Subscription("ETH/USDT", "1d");
        var series = new CandleSeries();
        var now = DateTime.UtcNow;
        series.LoadInitial(new[] { Candle(now, 50) });
        comparison.SetupGet(x => x.Series).Returns(series);
        var market = new Mock<IMarketDataService>();
        market.Setup(x => x.SubscribeAsync(It.IsAny<CandleCacheKey>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CandleCacheKey key, int _, CancellationToken _) => key.Symbol == "ETH/USDT"
                ? comparison.Object : Subscription(key.Symbol, key.Timeframe).Object);
        market.Setup(x => x.LoadOlderAsync(It.IsAny<CandleCacheKey>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var history = new[] { Candle(now.AddDays(-10), 45) };
                series.PrependHistory(history);
                comparison.Raise(x => x.OlderCandlesLoaded += null, history);
                return 1;
            });
        using var vm = new MainWindowViewModel(new DatabaseService(), new Mock<IDataProvider>().Object,
            exchange.Object, market.Object, new MetricsController(exchange.Object));
        await vm.StartAsync();
        int changes = 0;
        vm.ComparisonDataChanged += _ => changes++;
        vm.ComparisonSeriesReady += (_, _, _) => vm.RequestComparisonHistory(now.AddDays(-5));
        vm.SelectComparisonResultCommand.Execute("ETH/USDT");
        Assert.Equal(1, changes);
    }

    /// <summary>Creates a mock subscription with the requested symbol and timeframe and an empty candle series.</summary>
    private static Mock<IMarketDataSubscription> Subscription(string symbol, string timeframe)
    {
        var mock = new Mock<IMarketDataSubscription>();
        mock.SetupGet(x => x.Key).Returns(new CandleCacheKey("binance", symbol, timeframe));
        mock.SetupGet(x => x.Series).Returns(new CandleSeries());
        return mock;
    }

    /// <summary>Simulates a pending subscription which completes only when ct cancels the request.</summary>
    private static async Task<IMarketDataSubscription> WaitForCancellation(CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        throw new InvalidOperationException();
    }

    /// <summary>Creates a candle at UTC time with all prices set to close.</summary>
    private static Candle Candle(DateTime time, double close) =>
        new Candle { Timestamp = ComparisonSeriesMath.ToUnixMilliseconds(time), Open = close, High = close, Low = close, Close = close, Volume = 1 };
}
