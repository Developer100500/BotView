using BotView.Services.Integrations.Ccxt;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BotView.Chart;
using BotView.Configuration;
using BotView.Database;
using BotView.Models;
using BotView.Services;
using BotView.ViewModels;

namespace BotView.Tests;

public sealed class MarketDataProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Candle Bar(long time, double price = 100, double volume = 10) => new()
    {
        Timestamp = time, Open = price, High = price + 1, Low = price - 1, Close = price, Volume = volume
    };

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("binance")]
    [InlineData("bybit")]
    public async Task CcxtAdapterReturnsTheNearestHistoryBeforeAnExclusiveBoundary(string id)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        await using var exchange = new ExchangeService(CcxtMarketDataProvider.CreateDefaults(), maxRetryAttempts: 1);
        var latest = await exchange.GetCandlesAsync(id, "BTC/USDT", "1m", null, 10, timeout.Token);
        Assert.Equal(10, latest.Length);
        var boundary = latest[0].OpenTimeUtc;
        var older = await exchange.GetCandlesAsync(id, "BTC/USDT", "1m", boundary, 10, timeout.Token);
        Assert.Equal(10, older.Length);
        Assert.All(older, c => Assert.True(c.OpenTimeUtc < boundary));
        Assert.Equal(older.OrderBy(c => c.Timestamp), older);
        Assert.Equal(latest[0].Timestamp - 60_000, older[^1].Timestamp);
    }

    [Fact]
    public async Task IndependentProviderWorksThroughTheRegistryViewModelSharedSeriesAndChart()
    {
        var provider = new TestProvider("vtb-quik", [Bar(60_000), Bar(120_000)]);
        await using var exchange = new ExchangeService(providers: [provider]);
        await using var data = new MarketDataService(exchange);
        using var vm = new MainWindowViewModel(new DatabaseService(), new DataProvider(exchange), exchange,
            data, new MetricsController(exchange));
        vm.SelectedTimeframe = "1m";

        await vm.StartAsync();

        Assert.Equal("vtb-quik", vm.SelectedExchange);
        Assert.Equal("TESTF", vm.SelectedSymbol);
        Assert.Equal(new[] { "1m", "1d" }, vm.Timeframes);
        Assert.Equal("MOEX", Assert.Single(exchange.GetExchanges()).Venue);
        Assert.Equal(provider.Instrument.Key, Assert.Single(await exchange.GetInstrumentsAsync("VTB-QUIK", Ct)).Key);
        Assert.NotNull(vm.CurrentSeries);
        var controller = new ChartController();
        controller.SetViewSize(870, 550);
        controller.SetSeries(vm.CurrentSeries, "1m");
        Assert.True(controller.IsReady);
        Assert.Equal(2, controller.State.Series.GetSnapshot().Count);
        Assert.All(provider.Requests, r => Assert.Null(r.Before));
    }

    [Fact]
    public async Task DifferentConnectionsToTheSameVenueKeepIndependentCachesAndSeries()
    {
        var first = new TestProvider("moex-iss", [Bar(60_000, 100)]);
        var second = new TestProvider("vtb-quik", [Bar(60_000, 105)]);
        await using var exchange = new ExchangeService(providers: [first, second]);
        await using var data = new MarketDataService(exchange);
        using var iss = await data.SubscribeAsync(new("moex-iss", "TESTF", "1m"), 250, Ct);
        using var quik = await data.SubscribeAsync(new("vtb-quik", "TESTF", "1m"), 250, Ct);
        Assert.NotSame(iss.Series, quik.Series);
        Assert.Equal(100, iss.Series.GetSnapshot()[0].Close);
        Assert.Equal(105, quik.Series.GetSnapshot()[0].Close);
    }

    [Fact]
    public async Task BoundedHistoryIsOrderedExclusiveAndDoesNotStopAtAWeekend()
    {
        var monday = new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.FromHours(3));
        var friday = monday.AddDays(-3);
        var provider = new TestProvider("test", [Bar(friday.ToUnixTimeMilliseconds()),
            Bar(friday.AddMinutes(1).ToUnixTimeMilliseconds()), Bar(monday.ToUnixTimeMilliseconds()),
            Bar(monday.AddMinutes(1).ToUnixTimeMilliseconds())]);
        await using var exchange = new ExchangeService(providers: [provider]);
        await using var data = new MarketDataService(exchange);
        using var subscription = await data.SubscribeAsync(new("test", "TESTF", "1m"), 2, Ct);
        Assert.Single(provider.Requests);
        Assert.Equal(2, await data.LoadOlderAsync(subscription.Key, 2, Ct));
        Assert.Equal(monday, provider.Requests.Last().Before);
        Assert.Equal(friday.ToUnixTimeMilliseconds(), subscription.Series.GetSnapshot()[0].Timestamp);
        Assert.Equal(0, await data.LoadOlderAsync(subscription.Key, 2, Ct));
    }

    [Fact]
    public async Task StreamReplacesVolumeAcceptsNegativeFuturesPricesAndCancelsWhenUnused()
    {
        var provider = new TestProvider("test", [Bar(60_000, -5, 10)]);
        await using var exchange = new ExchangeService(providers: [provider]);
        await using var data = new MarketDataService(exchange);
        var subscription = await data.SubscribeAsync(new("test", "TESTF", "1m"), 2, Ct);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscription.CandleClosed += _ => closed.TrySetResult();
        provider.Updates.Writer.TryWrite(new(Bar(60_000, -4, 20), false));
        provider.Updates.Writer.TryWrite(new(Bar(60_000, -3, 25), true));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        var snapshot = subscription.Series.GetSnapshot();
        Assert.Equal(-3, snapshot[0].Close);
        Assert.Equal(25, snapshot[0].Volume);
        Assert.Null(snapshot.Live);
        subscription.Dispose();
        await provider.StreamStopped.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
    }

    [Fact]
    public async Task InstrumentBoardIsPreservedInsteadOfReducingAListingToItsTicker()
    {
        var provider = new TestProvider("test", [Bar(60_000)]);
        await using var exchange = new ExchangeService(providers: [provider]);
        await exchange.GetCandlesAsync("test", "TESTF", "1m", null, 1, Ct);
        Assert.Equal(new InstrumentKey("MOEX", "futures", "RFUD", "TESTF"),
            Assert.Single(provider.Requests).Instrument);
    }

    [Fact]
    public async Task ProviderCanDeclareItsOwnFixedIntervalAndItIsSelectedOnStartup()
    {
        var provider = new TestProvider("test", [Bar(60_000), Bar(660_000)], ["10m"]);
        await using var exchange = new ExchangeService(providers: [provider]);
        await using var data = new MarketDataService(exchange);
        using var vm = new MainWindowViewModel(new DatabaseService(), new DataProvider(exchange), exchange,
            data, new MetricsController(exchange));
        await vm.StartAsync();
        Assert.Equal("10m", vm.SelectedTimeframe);
        Assert.Equal(TimeSpan.FromMinutes(10), new ChartController().ParseTimeframe(vm.SelectedTimeframe));
        Assert.False(MarketCatalog.TryGetTimeframe("1M", out _));
    }

    private sealed class TestProvider(string id, Candle[] history, IReadOnlyList<string>? timeframes = null) : IMarketDataProvider
    {
        public ExchangeDefinition Definition { get; } = new(id, id, "", true, timeframes ?? ["1m", "1d"], "test", "MOEX");
        public Instrument Instrument { get; } = new(new("MOEX", "futures", "RFUD", "TESTF"), "TESTF", "Test future", "RUB");
        public ConcurrentQueue<(InstrumentKey Instrument, DateTimeOffset? Before, int Limit)> Requests { get; } = new();
        public Channel<CandleUpdate> Updates { get; } = Channel.CreateUnbounded<CandleUpdate>();
        public TaskCompletionSource StreamStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Instrument>>([Instrument]);

        public Task<Candle[]> GetCandlesAsync(InstrumentKey instrument, string timeframe,
            DateTimeOffset? beforeExclusive, int limit, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Enqueue((instrument, beforeExclusive, limit));
            return Task.FromResult(history.Where(c => !beforeExclusive.HasValue || c.Timestamp < beforeExclusive.Value.ToUnixTimeMilliseconds())
                .OrderBy(c => c.Timestamp).TakeLast(limit).ToArray());
        }

        public async IAsyncEnumerable<CandleUpdate> WatchCandlesAsync(InstrumentKey instrument, string timeframe,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                await foreach (var update in Updates.Reader.ReadAllAsync(ct)) yield return update;
            }
            finally { StreamStopped.TrySetResult(); }
        }

        public ValueTask DisposeAsync() { Updates.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
