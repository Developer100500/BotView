using BotView.Services.Integrations.Ccxt;
using BotView.Chart;
using BotView.Configuration;
using BotView.Database;
using BotView.Exceptions;
using BotView.Models;
using BotView.Services;
using BotView.ViewModels;
using Moq;

namespace BotView.Tests;

public sealed class CandleDataFlowTests
{
    private static Candle Candle(long timestamp) => new Candle { Timestamp = timestamp, Open = 100, High = 102, Low = 99, Close = 101, Volume = 10 };

    [Fact]
    public void CcxtConstructorCandlesRemainStableAcrossStoreUpdates()
    {
        var first = Assert.Single(CcxtCandleConverter.ConvertFromCCXT(
            new List<ccxt.OHLCV> { new(new List<object> { 60_000L, 100d, 102d, 99d, 101d, 10d }) }, "1m"));
        var store = new CandleStore();
        var key = new CandleCacheKey("binance", "BTC/USDT", "1m");
        store.LoadInitial(key, new[] { first });
        var before = store.GetSeries(key).GetSnapshot();
        var next = Assert.Single(CcxtCandleConverter.ConvertFromCCXT(
            new List<ccxt.OHLCV> { new(new List<object> { 120_000L, 101d, 103d, 100d, 102d, 11d }) }, "1m"));

        Assert.Equal(LiveCandleChange.Closed, store.UpdateLive(key, next, out var closed));

        Assert.Equal(first, closed);
        Assert.Equal(first, before.Live!.Value);
        Assert.Equal(next, store.GetSeries(key).GetSnapshot().Live!.Value);
        Assert.Equal(new[] { first }, store.GetRange(key, 0, 120_000));
    }

    [Fact]
    public void TypedConversionPreservesNumericFallbackWithoutMutatingCcxtInput()
    {
        var source = new List<ccxt.OHLCV> { new() { timestamp = 60_000 } };

        var candle = Assert.Single(CcxtCandleConverter.ConvertFromCCXT(source, "1m"));

        Assert.Equal(0d, candle.Open);
        Assert.Equal(0d, candle.High);
        Assert.Equal(0d, candle.Low);
        Assert.Equal(0d, candle.Close);
        Assert.Equal(0d, candle.Volume);
        Assert.Null(source[0].open);
        Assert.Null(source[0].close);
        Assert.Null(source[0].volume);
    }

    [Fact]
    public void MissingCcxtTimestampIsRejectedAtProviderBoundary()
    {
        var missingTime = new ccxt.OHLCV { open = 100, high = 102, low = 99, close = 101, volume = 10 };
        var error = Assert.Throws<DataConversionException>(() =>
            CcxtCandleConverter.ConvertFromCCXT(new List<ccxt.OHLCV> { missingTime }, "1m"));
        Assert.Equal(DataConversionErrorType.InvalidFormat, error.ErrorType);
    }

    [Fact]
    public async Task ProviderReturnsExchangeCandlesWithoutCopyingOrLosingRequestParameters()
    {
        var candles = new[] { Candle(60_000), Candle(120_000) };
        var exchange = new Mock<IExchangeService>(MockBehavior.Strict);
        exchange.Setup(x => x.GetCandlestickDataAsync("bybit", "ETH/USDT", "4h", 250))
            .ReturnsAsync(candles);

        var result = await new DataProvider(exchange.Object).LoadDataAsync("bybit", "ETH/USDT", "4h", 250);

        Assert.Same(candles, result);
        exchange.VerifyAll();
    }

    [Fact]
    public async Task ProviderReturnsNullWhenExchangeLoadingFails()
    {
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetCandlestickDataAsync("binance", "BTC/USDT", "1m", 500))
            .ThrowsAsync(new InvalidOperationException("Offline"));

        Assert.Null(await new DataProvider(exchange.Object).LoadDataAsync("binance", "BTC/USDT", "1m"));
    }

    [Fact]
    public void DemoCandlesKeepCountAndTimestampSpacingForEveryTimeframe()
    {
        var provider = new DataProvider();
        foreach (var timeframe in MarketCatalog.Timeframes)
        {
            var candles = provider.LoadDemoData(timeframe.Id);
            Assert.Equal(timeframe.DemoCandleCount, candles.Length);
            for (int i = 1; i < candles.Length; i++)
                Assert.Equal((long)timeframe.Duration.TotalMilliseconds,
                    candles[i].Timestamp - candles[i - 1].Timestamp);
        }

        Assert.Empty(DemoDataGenerator.Generate("1m", 0));
    }

    [Fact]
    public void ChartAcceptsCandlesWithAnExplicitTimeframeAndCanBeCleared()
    {
        var controller = new ChartController();
        controller.SetViewSize(870, 550);
        var candles = new[] { Candle(60_000), Candle(120_000) };

        controller.SetCandles(candles, "1m");

        Assert.True(controller.IsReady);
        Assert.Equal("1m", controller.State.Timeframe);
        Assert.Equal(candles, controller.State.Series.GetSnapshot()
            .Enumerate(long.MinValue, long.MaxValue).Select(item => item.Candle));

        controller.SetCandles(Array.Empty<Candle>(), "4h");
        Assert.Equal("4h", controller.State.Timeframe);
        Assert.Equal(0, controller.State.Series.GetSnapshot().Count);
        Assert.False(controller.IsReady);
    }

    [Fact]
    public async Task FailedSubscriptionPublishesDemoSeriesWithTheRequestedTimeframe()
    {
        var exchange = new Mock<IExchangeService>();
        exchange.Setup(x => x.GetAvailableSymbolsAsync("binance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "BTC/USDT" });
        var marketData = new Mock<IMarketDataService>();
        marketData.Setup(x => x.SubscribeAsync(It.IsAny<CandleCacheKey>(), 250, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Offline"));
        using var viewModel = new MainWindowViewModel(new DatabaseService(), new DataProvider(),
            exchange.Object, marketData.Object, new MetricsController(exchange.Object));
        ICandleSeriesReader? published = null;
        string? publishedTimeframe = null;
        bool? useLiveLayout = null;
        viewModel.ChartSeriesReady += (series, timeframe, live, _) =>
        {
            published = series;
            publishedTimeframe = timeframe;
            useLiveLayout = live;
        };

        await viewModel.StartAsync();

        Assert.Same(viewModel.CurrentSeries, published);
        Assert.NotNull(published);
        Assert.Equal(viewModel.SelectedTimeframe, publishedTimeframe);
        Assert.False(useLiveLayout);
        var snapshot = published.GetSnapshot();
        var definition = MarketCatalog.GetTimeframe(publishedTimeframe!);
        Assert.Equal(definition.DemoCandleCount, snapshot.Count);
        Assert.Equal((long)definition.Duration.TotalMilliseconds, snapshot[1].Timestamp - snapshot[0].Timestamp);
    }

    [Fact]
    public void EmptyObjectRowsProduceAnEmptyArrayWhileEmptyTypedInputRemainsInvalid()
    {
        Assert.Empty(CcxtCandleConverter.ConvertFromCCXT(Array.Empty<object[]>(), "1m"));
        var error = Assert.Throws<DataConversionException>(() =>
            CcxtCandleConverter.ConvertFromCCXT(new List<ccxt.OHLCV>(), "1m"));
        Assert.Equal(DataConversionErrorType.EmptyData, error.ErrorType);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    public void ConversionStillRejectsEndpointTimestampsOutsideDateTimeRange(bool typed, int index)
    {
        var source = new List<ccxt.OHLCV>
        {
            new() { timestamp = 60_000, open = 100, high = 102, low = 99, close = 101, volume = 10 },
            new() { timestamp = 120_000, open = 100, high = 102, low = 99, close = 101, volume = 10 }
        };
        var invalid = source[index];
        invalid.timestamp = long.MaxValue;
        source[index] = invalid;

        var error = Assert.Throws<DataConversionException>(() => typed
            ? CcxtCandleConverter.ConvertFromCCXT(source, "1m")
            : CcxtCandleConverter.ConvertFromCCXT(source.Select(c => new object[]
                { c.timestamp!.Value, c.open!.Value, c.high!.Value, c.low!.Value, c.close!.Value, c.volume!.Value })
                .ToArray(), "1m"));

        Assert.Equal(DataConversionErrorType.InvalidFormat, error.ErrorType);
        Assert.IsType<ArgumentOutOfRangeException>(error.InnerException);
    }

    [Theory]
    [InlineData(0, 102, 99, 10)]
    [InlineData(60_000, 98, 99, 10)]
    [InlineData(60_000, 102, 99, -1)]
    [InlineData(60_000, double.NaN, 99, 10)]
    public void TypedConversionStillRejectsInvalidCandleValues(long timestamp, double high, double low, double volume)
    {
        var source = new List<ccxt.OHLCV>
        {
            new() { timestamp = timestamp, open = 100, high = high, low = low, close = 101, volume = volume }
        };
        var error = Assert.Throws<DataConversionException>(() =>
            CcxtCandleConverter.ConvertFromCCXT(source, "1m"));
        Assert.Equal(DataConversionErrorType.InvalidFormat, error.ErrorType);
        Assert.Equal(0, error.CandleIndex);
    }
}
