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

    private static Mock<IMarketDataSubscription> Subscription(string symbol, string timeframe)
    {
        var mock = new Mock<IMarketDataSubscription>();
        mock.SetupGet(x => x.Key).Returns(new CandleCacheKey("binance", symbol, timeframe, 250));
        mock.SetupGet(x => x.Series).Returns(new CandleSeries());
        return mock;
    }

    private static async Task<IMarketDataSubscription> WaitForCancellation(CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        throw new InvalidOperationException();
    }

    private static OHLCV Candle(DateTime time, double close) =>
        new(ComparisonSeriesMath.ToUnixMilliseconds(time), close, close, close, close, 1);
}
