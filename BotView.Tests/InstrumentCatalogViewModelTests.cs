using System.Runtime.CompilerServices;
using BotView.Chart;
using BotView.Configuration;
using BotView.Database;
using BotView.Interfaces;
using BotView.Models;
using BotView.Services;
using BotView.ViewModels;

namespace BotView.Tests;

public sealed class InstrumentCatalogViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Instrument[] Instruments =
    [
        new(new("MOEX", "shares", "TQBR", "SBER"), "TQBR:SBER", "Sberbank", LotSize: 10, PriceStep: 0.01m),
        new(new("MOEX", "bonds", "TQOB", "SU26238RMFS4"), "TQOB:SU26238RMFS4", "OFZ 26238"),
        new(new("MOEX", "futures", "SPBFUT", "SiZ6"), "SPBFUT:SiZ6", "Dollar December")
    ];

    [Fact]
    public async Task CatalogOnlyStartupSearchAndCategoriesNeverRequestCandlesOrDemoData()
    {
        var provider = new CatalogProvider();
        await using var exchange = new ExchangeService([provider], maxRetryAttempts: 1);
        await using var marketData = new MarketDataService(exchange);
        using var vm = Create(exchange, marketData);
        var errors = new List<string>();
        vm.ErrorOccurred += (_, message) => errors.Add(message);

        await vm.StartAsync();

        Assert.True(vm.IsInstrumentCatalogMode);
        Assert.False(vm.IsChartMode);
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Timeframes);
        Assert.Null(vm.CurrentSeries);
        Assert.Empty(errors);
        Assert.Equal(3, vm.CatalogRows.Count);
        Assert.Equal("All", vm.SelectedCatalogCategory);
        vm.SelectedCatalogCategory = "Bonds";
        Assert.Equal("SU26238RMFS4", Assert.Single(vm.CatalogRows).Ticker);
        vm.SelectedCatalogCategory = "All";
        vm.SearchText = "sBeR";
        Assert.Equal("SBER", Assert.Single(vm.CatalogRows).Ticker);
        vm.SearchText = "siz6";
        Assert.Equal("Futures", Assert.Single(vm.CatalogRows).Type);
        vm.SearchText = "does not exist";
        Assert.Empty(vm.CatalogRows);
        Assert.Equal("Found: 0 of 3", vm.CatalogCountText);
        vm.IsSearchDropdownOpen = true;
        Assert.False(vm.IsSearchDropdownOpen);
        Assert.Equal(1, provider.Requests);
        await vm.LoadOlderAsync();
        Assert.Null(vm.CurrentSeries);
    }

    [Fact]
    public async Task FailedRefreshPreservesThePreviousSnapshotAndReportsAnInlineError()
    {
        var provider = new CatalogProvider();
        await using var exchange = new ExchangeService([provider], maxRetryAttempts: 1);
        await using var data = new MarketDataService(exchange);
        using var vm = Create(exchange, data);
        var modalErrors = 0;
        vm.ErrorOccurred += (_, _) => modalErrors++;
        await vm.StartAsync();
        var original = vm.CatalogRows;
        provider.Load = _ => throw new TimeoutException("QUIK unavailable");

        await vm.RefreshCatalogAsync();

        Assert.Same(original, vm.CatalogRows);
        Assert.Contains("List was not refreshed", vm.CatalogErrorText);
        Assert.Contains("QUIK unavailable", vm.CatalogErrorText);
        Assert.Equal(0, modalErrors);
        Assert.False(vm.IsCatalogLoading);
        provider.Load = _ => Task.FromResult<IReadOnlyList<Instrument>>([]);
        await vm.RefreshCatalogAsync();
        Assert.Empty(vm.CatalogRows);
        Assert.Empty(vm.CatalogErrorText);
    }

    [Fact]
    public async Task FailedInitialLoadIsInlineAndCanBeRetried()
    {
        var provider = new CatalogProvider { Load = _ => throw new InvalidOperationException("Start Lua") };
        await using var exchange = new ExchangeService([provider], maxRetryAttempts: 1);
        await using var data = new MarketDataService(exchange);
        using var vm = Create(exchange, data);
        var errors = 0;
        vm.ErrorOccurred += (_, _) => errors++;
        await vm.StartAsync();
        Assert.Empty(vm.CatalogRows);
        Assert.Contains("Start Lua", vm.CatalogErrorText);
        Assert.False(vm.IsBlockingBusy);
        Assert.True(vm.RefreshCatalogCommand.CanExecute(null));
        Assert.Equal(0, errors);
        provider.Load = _ => Task.FromResult<IReadOnlyList<Instrument>>(Instruments);
        await vm.RefreshCatalogAsync();
        Assert.Equal(3, vm.CatalogRows.Count);
        Assert.Empty(vm.CatalogErrorText);
    }

    [Fact]
    public async Task SwitchingBackToCryptoCancelsTheCatalogAndIgnoresItsLateResult()
    {
        var crypto = new CandleProvider();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<IReadOnlyList<Instrument>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken catalogToken = default;
        var catalog = new CatalogProvider
        {
            Load = token => { catalogToken = token; requested.TrySetResult(); return finish.Task; }
        };
        await using var exchange = new ExchangeService([crypto, catalog], maxRetryAttempts: 1);
        await using var data = new MarketDataService(exchange);
        using var vm = Create(exchange, data);
        var failures = new List<string>();
        vm.ErrorOccurred += (_, message) => failures.Add(message);
        await vm.StartAsync();
        Assert.NotNull(vm.CurrentSeries);
        var initialRequests = crypto.CandleRequests;

        vm.SelectedExchange = "quik";
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        Assert.True(vm.IsCatalogLoading);
        Assert.False(vm.IsBlockingBusy);
        Assert.Null(vm.CurrentSeries);
        Assert.Empty(vm.Timeframes);
        Assert.Equal(initialRequests, crypto.CandleRequests);

        vm.SelectedExchange = "binance";
        Assert.True(catalogToken.IsCancellationRequested);
        await Until(() => vm.CurrentSeries is not null && !vm.IsBusy);
        finish.SetResult(Instruments);
        await Task.Yield();
        Assert.Equal("binance", vm.SelectedExchange);
        Assert.False(vm.IsInstrumentCatalogMode);
        Assert.Equal(new[] { "1m" }, vm.Timeframes);
        Assert.Empty(vm.CatalogRows);
        Assert.Empty(vm.CatalogErrorText);
        Assert.NotNull(vm.CurrentSeries);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task EnteringCatalogCancelsAnUnfinishedCandleLoad()
    {
        var crypto = new CandleProvider { BlockHistory = true };
        var catalog = new CatalogProvider();
        await using var exchange = new ExchangeService([crypto, catalog], maxRetryAttempts: 1);
        await using var data = new MarketDataService(exchange);
        using var vm = Create(exchange, data);
        var pending = vm.StartAsync();
        await crypto.HistoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        vm.SelectedExchange = "quik";
        await Until(() => vm.CatalogRows.Count == 3 && !vm.IsCatalogLoading);
        await pending;
        Assert.True(crypto.HistoryToken.IsCancellationRequested);
        Assert.Null(vm.CurrentSeries);
        Assert.Empty(vm.CatalogErrorText);
    }

    private static MainWindowViewModel Create(IExchangeService exchange, IMarketDataService data) =>
        new(new DatabaseService(), new DataProvider(exchange), exchange, data, new MetricsController(exchange));

    private static async Task Until(Func<bool> done)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!done()) await Task.Delay(10, timeout.Token);
    }

    private sealed class CatalogProvider : IInstrumentProvider
    {
        public ExchangeDefinition Definition { get; } = new("quik", "QUIK / MOEX", "", true, [], "quiksharp", "MOEX");
        public Func<CancellationToken, Task<IReadOnlyList<Instrument>>> Load { get; set; } =
            _ => Task.FromResult<IReadOnlyList<Instrument>>(Instruments);
        public int Requests { get; private set; }
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct = default)
        { Requests++; return Load(ct); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CandleProvider : IMarketDataProvider
    {
        public ExchangeDefinition Definition { get; } = new("binance", "Crypto", "", true, ["1m"]);
        public int CandleRequests { get; private set; }
        public bool BlockHistory { get; init; }
        public TaskCompletionSource HistoryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken HistoryToken { get; private set; }
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Instrument>>([new(new("binance", "spot", "", "BTC/USDT"), "BTC/USDT", "Bitcoin")]);
        public async Task<Candle[]> GetCandlesAsync(InstrumentKey instrument, string timeframe, DateTimeOffset? beforeExclusive,
            int limit, CancellationToken ct = default)
        {
            CandleRequests++;
            HistoryToken = ct;
            HistoryStarted.TrySetResult();
            if (BlockHistory) await Task.Delay(Timeout.Infinite, ct);
            return [new() { Timestamp = 60_000, Open = 1, High = 2, Low = 1, Close = 2, Volume = 1 }];
        }
        public async IAsyncEnumerable<CandleUpdate> WatchCandlesAsync(InstrumentKey instrument, string timeframe,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
