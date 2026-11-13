using BotView.Configuration;
using BotView.Services.Integrations.Quik;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace BotView.Tests;

[CollectionDefinition("QUIK live reads", DisableParallelization = true)]
public sealed class QuikLiveReadCollection;

/// <summary>Opt-in terminal checks: only catalog, subscriptions and account reads; no mutation APIs are invoked.</summary>
[Collection("QUIK live reads")]
public sealed class QuikLiveReadTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Verifies real stock/futures depth and continued reading after another consumer closes.</summary>
    [Fact]
    [Trait("Category", "QuikLive")]
    public async Task LiveDepthAndSharedConnectionReadOnly()
    {
        RequireOptIn();
        using var assertions = new AssertionListener();
        Trace.Listeners.Add(assertions);
        var options = QuikConnectionOptions.Load();
        await using var catalog = new QuikInstrumentProvider();
        var instruments = await catalog.GetInstrumentsAsync(Ct);
        output.WriteLine($"Catalog: {instruments.Count} listings; TQBR={instruments.Count(i => i.Key.Board == "TQBR")}; SPBFUT={instruments.Count(i => i.Key.Board == "SPBFUT")}.");
        await using var reader = new QuikReadClient(options);
        Assert.True(await reader.ConnectedAsync(Ct));

        foreach (var board in new[] { "TQBR", "SPBFUT" })
        {
            var instrument = await FindDepthAsync(reader, instruments, board);
            var service = new QuikOrderBookService(options);
            var market = board == "SPBFUT" ? TradingMarket.MoexFutures : TradingMarket.MoexShares;
            var connection = $"quik:{options.Host}:{options.Port}";
            await using var first = await service.SubscribeAsync(connection, instrument, TradingEnvironment.Production, market, Ct);
            await using var second = await service.SubscribeAsync(connection, instrument, TradingEnvironment.Production, market, Ct);
            await WaitLiveAsync(first);
            await WaitLiveAsync(second);
            await first.DisposeAsync();
            await Task.Delay(TimeSpan.FromSeconds(15), Ct);
            await WaitLiveAsync(second);
            Assert.True(await reader.ConnectedAsync(Ct));
            var snapshot = second.Current.Snapshot!;
            Assert.Equal(instrument.Key, snapshot.Market.Instrument!.Key);
            Assert.Empty(assertions.Failures);
            output.WriteLine($"{instrument.Symbol}: bid levels={snapshot.Bids.Count}, ask levels={snapshot.Asks.Count}; closing another consumer preserved live reading.");
        }
    }

    private sealed class AssertionListener : TraceListener
    {
        internal ConcurrentQueue<string> Failures { get; } = new();

        /// <summary>Collects dependency assertions so a live smoke test fails instead of overlooking them.</summary>
        public override void Fail(string? message) => Failures.Enqueue(message ?? "Trace assertion");

        /// <summary>Collects assertion details without writing broker data to test output.</summary>
        public override void Fail(string? message, string? detailMessage) => Fail(message);

        /// <summary>Ignores routine library tracing.</summary>
        public override void Write(string? message) { }

        /// <summary>Ignores routine library tracing.</summary>
        public override void WriteLine(string? message) { }

        /// <summary>Restores listener ownership after the opt-in test completes.</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing) Trace.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }

    /// <summary>Discovers routing in memory and reads available account tables without saving profiles or logging identifiers.</summary>
    [Fact]
    [Trait("Category", "QuikLive")]
    public async Task LiveAccountTablesReadOnly()
    {
        RequireOptIn();
        var options = QuikConnectionOptions.Load();
        await using var catalog = new QuikInstrumentProvider();
        var instruments = await catalog.GetInstrumentsAsync(Ct);
        await using var reader = new QuikReadClient(options);
        var routes = await StepAsync("routing discovery", () => reader.RoutesAsync(Ct));
        output.WriteLine($"Available routing tuples: {routes.Count}. Account identifiers and financial values are omitted.");
        Assert.NotEmpty(routes);

        foreach (var board in new[] { "TQBR", "SPBFUT" })
        {
            var route = routes.FirstOrDefault(r => r.Classes.Contains(board, StringComparison.Ordinal));
            if (route is null)
            {
                output.WriteLine($"{board}: the terminal exposes no account route; account reads for this market were not verified.");
                continue;
            }

            var instrument = instruments.Where(i => i.Key.Board == board)
                .OrderByDescending(i => i.Key.SecurityId == "SBER" || i.Key.SecurityId.StartsWith("Si", StringComparison.OrdinalIgnoreCase)).First();
            var profile = new TradingAccountProfile(Guid.NewGuid(), "Read-only live verification", "quik",
                TradingEnvironment.Production, board == "SPBFUT" ? TradingMarket.MoexFutures : TradingMarket.MoexShares,
                new(options.Host, options.Port, route.Firm, route.Client, route.Account, route.LimitKind));
            await using var service = new QuikReadOnlyTradingService(profile);
            Assert.False(service.CanExecuteTrades);
            await StepAsync($"{board} instrument", () => service.FetchInstrumentAsync(instrument.Key, Ct));
            var balance = await StepAsync($"{board} balance", () => service.FetchBalanceAsync(Ct));
            var orders = await StepAsync($"{board} regular/conditional orders", () => service.FetchOpenOrdersAsync(instrument.Key, Ct));
            var trades = await StepAsync($"{board} executions", () => service.FetchMyTradesAsync(instrument.Key, Ct));
            var positions = await StepAsync($"{board} positions", () => service.FetchPositionsAsync(instrument.Key, Ct));
            Assert.Equal(service.Account, balance.Account);
            Assert.All(orders, o => Assert.Equal(instrument.Key, o.Reference.Instrument));
            Assert.All(trades, t => Assert.Equal(instrument.Key, t.Order.Instrument));
            Assert.All(positions, p => Assert.Equal(instrument.Key, p.Instrument));
            output.WriteLine($"{board}: balances={balance.Balances?.Count ?? 0}, margins={balance.Margins?.Count ?? 0}, " +
                $"regular orders={orders.Count(o => o.Reference.Category == OrderCategory.Regular)}, " +
                $"conditional orders={orders.Count(o => o.Reference.Category == OrderCategory.Conditional)}, executions={trades.Count}, positions={positions.Count}.");
        }
    }

    /// <summary>Requires explicit user-authorized opt-in before contacting any terminal.</summary>
    private static void RequireOptIn()
    {
        if (Environment.GetEnvironmentVariable("BOTVIEW_LIVE_QUIK") != "1")
            Assert.Skip("Set BOTVIEW_LIVE_QUIK=1 to enable read-only checks against a running QUIK terminal.");
    }

    /// <summary>Prefers liquid instruments but accepts empty depth when the market is closed.</summary>
    private async Task<Instrument> FindDepthAsync(IQuikReadClient reader, IReadOnlyList<Instrument> instruments, string board)
    {
        var candidates = instruments.Where(i => i.Key.Board == board && i.PriceStep is > 0 && i.LotSize > 0)
            .OrderByDescending(i => i.Key.SecurityId == "SBER" || i.Key.SecurityId.StartsWith("Si", StringComparison.OrdinalIgnoreCase))
            .Take(8).ToArray();
        Assert.NotEmpty(candidates);
        foreach (var candidate in candidates)
        {
            var book = await StepAsync($"{board} depth", () => reader.BookAsync(candidate.Key, Ct));
            if (book.Bids.Count > 0 || book.Asks.Count > 0) return candidate;
        }
        output.WriteLine($"{board}: sampled depth is empty; verifying subscription and heartbeat only.");
        return candidates[0];
    }

    /// <summary>Bounds subscription startup while preserving the useful provider status on a failure.</summary>
    private static async Task WaitLiveAsync(IOrderBookSubscription subscription)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (subscription.Current.Connection != OrderBookConnection.Live)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, $"QUIK subscription did not become live: {subscription.Current.Connection}.");
            await Task.Delay(50, Ct);
        }
        Assert.NotNull(subscription.Current.Snapshot);
    }

    /// <summary>Reports the failed read stage without exposing account identifiers or provider payloads.</summary>
    private static async Task<T> StepAsync<T>(string stage, Func<Task<T>> read)
    {
        try { return await read(); }
        catch (Exception ex)
        {
            Assert.Fail($"Read-only QUIK stage '{stage}' failed: {ex.GetType().Name}.");
            throw;
        }
    }
}
