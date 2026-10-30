using System.Net;
using System.Net.Sockets;
using BotView.Configuration;
using BotView.Services;
using BotView.Services.Integrations.Quik;

namespace BotView.Tests;

public sealed class QuikInstrumentProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Preserves listing identity and native price/quantity metadata without guessing valuation parameters.</summary>
    [Fact]
    public async Task CatalogIsLazyFiltersClassesAndPreservesTheListingAndMetadata()
    {
        var client = new FakeQuikClient();
        var created = 0;
        await using var provider = new QuikInstrumentProvider(() => { created++; return client; });
        Assert.Equal(0, created);
        Assert.Empty(provider.Definition.SupportedTimeframes);

        var instruments = await provider.GetInstrumentsAsync(Ct);

        Assert.Equal(1, created);
        Assert.Equal(3, instruments.Count);
        Assert.Equal(new[] { "TQBR", "TQOB", "SPBFUT" }, client.RequestedClasses);
        var share = Assert.Single(instruments, i => i.Key.Market == "shares");
        Assert.Equal(new("MOEX", "shares", "TQBR", "SAME"), share.Key);
        Assert.Equal("TQBR:SAME", share.Symbol);
        Assert.Equal("Sberbank", share.Name);
        Assert.Equal(10m, share.LotSize);
        Assert.Equal(0.01m, share.PriceStep);
        Assert.Equal(QuantityUnit.Lots, share.TradingRules!.QuantityUnit);
        Assert.Equal(PriceQuotation.CurrencyPerUnit, share.TradingRules.PriceQuotation);
        Assert.Equal(PriceQuotation.PercentOfFaceValue, instruments.Single(i => i.Key.Market == "bonds").TradingRules!.PriceQuotation);
        var futures = instruments.Single(i => i.Key.Market == "futures");
        Assert.Equal(QuantityUnit.Contracts, futures.TradingRules!.QuantityUnit);
        Assert.Equal(PriceQuotation.Points, futures.TradingRules.PriceQuotation);
        Assert.Null(futures.TradingRules.TickValue);
        Assert.All(instruments, i =>
        {
            Assert.Null(i.TradingRules!.QuantityStep);
            Assert.Null(i.TradingRules.MinQuantity);
        });
        Assert.Equal(3, instruments.Select(i => i.Symbol).Distinct().Count());
        Assert.Equal("Full bond name", Assert.Single(instruments, i => i.Key.Market == "bonds").Name);
        Assert.Equal("SAME", Assert.Single(instruments, i => i.Key.Market == "futures").Name);
        Assert.False(client.Disposed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SPBOPT")]
    [InlineData("TQOB")]
    public async Task MissingClassesAreEmptyAndUnsupportedClassesAreNeverQueried(string available)
    {
        var client = new FakeQuikClient { Classes = [available] };
        await using var provider = new QuikInstrumentProvider(() => client);
        var result = await provider.GetInstrumentsAsync(Ct);
        Assert.Equal(available == "TQOB" ? 1 : 0, result.Count);
        Assert.DoesNotContain("SPBOPT", client.RequestedClasses);
    }

    [Fact]
    public async Task BrokerDisconnectionDoesNotPublishCachedOrPartialInstruments()
    {
        var client = new FakeQuikClient { Connected = false };
        await using var provider = new QuikInstrumentProvider(() => client);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetInstrumentsAsync(Ct));
        Assert.Contains("not connected", error.Message);
        Assert.Empty(client.RequestedClasses);
        Assert.True(client.Disposed);

        client = new FakeQuikClient { DisconnectAfterCatalog = true };
        error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetInstrumentsAsync(Ct));
        Assert.Contains("lost", error.Message);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task CancellationClosesTheClientAndTheNextLoadCreatesANewConnection()
    {
        var first = new FakeQuikClient { BlockClasses = true };
        var second = new FakeQuikClient();
        var calls = 0;
        await using var provider = new QuikInstrumentProvider(() => ++calls == 1 ? first : second);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = provider.GetInstrumentsAsync(cancellation.Token);
        await first.ClassesStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(first.Disposed);
        Assert.Equal(3, (await provider.GetInstrumentsAsync(Ct)).Count);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task TimeoutIsActionableAndFailedClientsAreRecreated()
    {
        var failed = new FakeQuikClient { Failure = new TimeoutException() };
        var next = new FakeQuikClient();
        var calls = 0;
        await using var provider = new QuikInstrumentProvider(() => ++calls == 1 ? failed : next);
        var error = await Assert.ThrowsAsync<TimeoutException>(() => provider.GetInstrumentsAsync(Ct));
        Assert.Contains("QuikSharp.lua", error.Message);
        Assert.True(failed.Disposed);
        Assert.Equal(3, (await provider.GetInstrumentsAsync(Ct)).Count);
    }

    [Fact]
    public async Task DisposalCancelsAnActiveLoadAndRejectsNewRequests()
    {
        var client = new FakeQuikClient { BlockClasses = true };
        var provider = new QuikInstrumentProvider(() => client);
        var pending = provider.GetInstrumentsAsync(Ct);
        await client.ClassesStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        await provider.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(client.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.GetInstrumentsAsync(Ct));
    }

    [Fact]
    public async Task InstrumentOnlyProviderWorksInTheRegistryAndRejectsAllCandlePaths()
    {
        await using var exchange = new ExchangeService([new QuikInstrumentProvider(() => new FakeQuikClient())], maxRetryAttempts: 1);
        Assert.Equal("quik", Assert.Single(exchange.GetExchanges()).Id);
        Assert.Equal(3, (await exchange.GetAvailableSymbolsAsync("QUIK", Ct)).Count);
        await Assert.ThrowsAsync<NotSupportedException>(() => exchange.GetCandlestickDataAsync("quik", "TQBR:SAME", "1m"));
        await Assert.ThrowsAsync<NotSupportedException>(() => exchange.GetCandlesAsync("quik", "TQBR:SAME", "1m", null, 1, Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in exchange.WatchCandlesAsync("quik", "TQBR:SAME", "1m", Ct)) { }
        });
    }

    [Fact]
    public async Task ActualSharpClientTimesOutAndStopsWhenTheLuaBridgeIsAbsent()
    {
        // An OS-selected local port keeps this test independent of any installed QUIK.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var client = new QuikSharpCatalogClient(new() { Port = port, RequestTimeoutSeconds = 1 });
            try
            {
                await Assert.ThrowsAsync<TimeoutException>(() => client.IsConnectedAsync(Ct));
            }
            finally { await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct); }
        }
    }

    [Fact]
    [Trait("Category", "QuikLive")]
    public async Task LiveCatalogFromAnExplicitlyEnabledTerminal()
    {
        if (Environment.GetEnvironmentVariable("BOTVIEW_LIVE_QUIK") != "1")
            Assert.Skip("Set BOTVIEW_LIVE_QUIK=1 and start QUIK with QuikSharp.lua to check the live catalog.");
        await using var provider = new QuikInstrumentProvider();
        var instruments = await provider.GetInstrumentsAsync(Ct);
        Assert.NotEmpty(instruments);
        Assert.All(instruments, i => Assert.Contains(i.Key.Board, new[] { "TQBR", "TQOB", "SPBFUT" }));
    }

    internal sealed class FakeQuikClient : IQuikCatalogClient
    {
        public string[] Classes { get; init; } = ["TQBR", "TQOB", "SPBFUT", "SPBOPT", "OTHER"];
        public List<string> RequestedClasses { get; } = [];
        public bool Disposed { get; private set; }
        public bool Connected { get; init; } = true;
        public bool DisconnectAfterCatalog { get; init; }
        public bool BlockClasses { get; init; }
        public Exception? Failure { get; init; }
        public TaskCompletionSource ClassesStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectionChecks;

        public Task<bool> IsConnectedAsync(CancellationToken ct) =>
            Task.FromResult(Connected && !(DisconnectAfterCatalog && ++_connectionChecks > 1));
        public async Task<string[]> GetClassesAsync(CancellationToken ct)
        {
            ClassesStarted.TrySetResult();
            if (Failure is not null) throw Failure;
            if (BlockClasses) await Task.Delay(Timeout.Infinite, ct);
            return Classes;
        }
        public Task<string[]> GetSecuritiesAsync(string classCode, CancellationToken ct)
        {
            RequestedClasses.Add(classCode);
            return Task.FromResult(new[] { "SAME", "SAME", "" });
        }
        public Task<QuikSecurityDescription> GetSecurityAsync(string classCode, string code, CancellationToken ct) =>
            Task.FromResult(new QuikSecurityDescription(classCode == "TQBR" ? "Sberbank" : "",
                classCode == "TQOB" ? "Full bond name" : "", 10, 0.01));
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
