using System.IO;
using System.Threading.Channels;
using BotView.Models;
using BotView.ViewModels;
using Moq;

namespace BotView.Tests;

public sealed class TradingAccountSessionTests
{
    private static readonly InstrumentKey Key = new("binance", "spot", "", "BTC/USDT");

    /// <summary>Creates a detached order update with a stable identity.</summary>
    private static OrderSnapshot Order(OrderState state = OrderState.Open) => new(new(Key, "1"), null,
        OrderSide.Buy, OrderType.Limit, state, 1, 0, 1, 100, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    /// <summary>Provides deterministic reads and precision without network access or journal files.</summary>
    private static Mock<ITradingService> Adapter(TradingAccountProfile? profile = null)
    {
        var adapter = new Mock<ITradingService>();
        var account = new TradingAccountContext(profile?.Id.ToString() ?? "profile", "Fake", "binance", TradingEnvironment.Testnet);
        adapter.SetupGet(s => s.Account).Returns(account);
        adapter.Setup(s => s.FetchInstrumentAsync(It.IsAny<InstrumentKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Instrument(Key, "BTC/USDT", "Bitcoin", "USDT", TradingRules: new(QuantityUnit.BaseAsset, PriceQuotation.CurrencyPerUnit)));
        adapter.Setup(s => s.FetchBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountSnapshot(account, [new("USDT", 100, 100, 0)], null, null, DateTimeOffset.UtcNow));
        adapter.Setup(s => s.FetchOpenOrdersAsync(It.IsAny<InstrumentKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<OrderSnapshot>());
        adapter.Setup(s => s.FetchMyTradesAsync(It.IsAny<InstrumentKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ExecutionSnapshot>());
        adapter.Setup(s => s.AmountToPrecision(It.IsAny<InstrumentKey>(), It.IsAny<decimal>()))
            .Returns((InstrumentKey _, decimal value) => decimal.Floor(value * 1000) / 1000);
        adapter.Setup(s => s.PriceToPrecision(It.IsAny<InstrumentKey>(), It.IsAny<decimal>()))
            .Returns((InstrumentKey _, decimal value) => decimal.Round(value, 2));
        adapter.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return adapter;
    }

    /// <summary>Waits for an observable condition with a bounded deadline rather than assuming scheduling order.</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    /// <summary>Unsupported streams get snapshots and continue polling; duplicate tracking starts no extra workers.</summary>
    [Fact]
    public async Task InitialReadsAndFallbackPollRemainIndependentOfOrderPlacement()
    {
        var adapter = Adapter();
        adapter.Setup(s => s.FetchOpenOrdersAsync(Key, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { Order() });
        await using var session = new TradingAccountSession(adapter.Object, TimeSpan.FromMilliseconds(20));
        await session.TrackAsync(Key);
        await session.TrackAsync(Key);
        await UntilAsync(() => session.Current.Orders.Count == 1);
        Assert.True(session.Current.Connected);
        Assert.Equal(100, session.Current.Balance!.Balances![0].Available);
        adapter.Verify(s => s.FetchInstrumentAsync(Key, It.IsAny<CancellationToken>()), Times.Once);
        await UntilAsync(() => adapter.Invocations.Count(i => i.Method.Name == nameof(ITradingService.FetchOpenOrdersAsync)) >= 2);
        adapter.Verify(s => s.CreateLimitOrderAsync(It.IsAny<LimitOrderRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Private batches update the display and stop fallback REST once the stream is healthy.</summary>
    [Fact]
    public async Task PrivateStreamUpdatesAndRecoversAfterFailure()
    {
        var adapter = Adapter();
        var events = Channel.CreateUnbounded<IReadOnlyList<OrderSnapshot>>();
        adapter.Setup(s => s.SupportsStream(TradingStream.Orders)).Returns(true);
        var failNext = 0;
        adapter.Setup(s => s.WatchOrdersAsync(Key, It.IsAny<CancellationToken>()))
            .Returns((InstrumentKey _, CancellationToken ct) => Interlocked.Exchange(ref failNext, 0) == 1
                ? Task.FromException<IReadOnlyList<OrderSnapshot>>(new IOException("offline"))
                : events.Reader.ReadAsync(ct).AsTask());
        await using var session = new TradingAccountSession(adapter.Object, TimeSpan.FromMilliseconds(30));
        await session.TrackAsync(Key);
        await events.Writer.WriteAsync(new[] { Order(OrderState.Filled) });
        await UntilAsync(() => session.Current.Orders.Any(o => o.State == OrderState.Filled));
        await Task.Delay(80);
        var reads = adapter.Invocations.Count(i => i.Method.Name == nameof(ITradingService.FetchOpenOrdersAsync));
        await Task.Delay(100);
        Assert.Equal(reads, adapter.Invocations.Count(i => i.Method.Name == nameof(ITradingService.FetchOpenOrdersAsync)));

        // One disconnected read causes polling; a subsequent batch restores the stream.
        Interlocked.Exchange(ref failNext, 1);
        await events.Writer.WriteAsync(Array.Empty<OrderSnapshot>());
        await UntilAsync(() => adapter.Invocations.Count(i => i.Method.Name == nameof(ITradingService.FetchOpenOrdersAsync)) > reads);
        await events.Writer.WriteAsync(new[] { Order(OrderState.Filled) });
        await UntilAsync(() => session.Current.Orders.Any(o => o.State == OrderState.Filled));
    }

    /// <summary>A slow REST response must not overwrite a fill that arrived from the private stream.</summary>
    [Fact]
    public async Task PrivateEventWinsOverAnOlderSnapshot()
    {
        var adapter = Adapter();
        var snapshot = new TaskCompletionSource<IReadOnlyList<OrderSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = Channel.CreateUnbounded<IReadOnlyList<OrderSnapshot>>();
        var fetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.Setup(s => s.FetchOpenOrdersAsync(Key, It.IsAny<CancellationToken>())).Returns(() =>
        {
            fetchStarted.TrySetResult();
            return snapshot.Task;
        });
        adapter.Setup(s => s.SupportsStream(TradingStream.Orders)).Returns(true);
        adapter.Setup(s => s.WatchOrdersAsync(Key, It.IsAny<CancellationToken>()))
            .Returns((InstrumentKey _, CancellationToken ct) => events.Reader.ReadAsync(ct).AsTask());
        await using var session = new TradingAccountSession(adapter.Object, TimeSpan.FromMilliseconds(20));
        await session.TrackAsync(Key);
        await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await events.Writer.WriteAsync(new[] { Order(OrderState.Filled) with { FilledQuantity = 1, RemainingQuantity = 0 } });
        await UntilAsync(() => session.Current.Orders.Any(o => o.State == OrderState.Filled));
        snapshot.SetResult(new[] { Order(OrderState.Open) });
        await Task.Delay(60);
        Assert.Equal(OrderState.Filled, session.Current.Orders.Single().State);
        Assert.Equal(1, session.Current.Orders.Single().FilledQuantity);
    }

    /// <summary>A failed display refresh cannot turn an acknowledged order into an error or resend it.</summary>
    [Fact]
    public async Task ReadFailureAfterCreatePreservesTheAcknowledgement()
    {
        var adapter = Adapter();
        adapter.Setup(s => s.CreateLimitOrderAsync(It.IsAny<LimitOrderRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Order());
        await using var session = new TradingAccountSession(adapter.Object);
        await session.TrackAsync(Key);
        adapter.Setup(s => s.FetchOpenOrdersAsync(Key, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
        var order = await session.CreateLimitOrderAsync(new(Key, OrderSide.Buy, 1, 100));
        Assert.Equal("1", order.Reference.ExchangeOrderId);
        adapter.Verify(s => s.CreateLimitOrderAsync(It.IsAny<LimitOrderRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A canceled or failed action is never retried by the session; later manual actions remain available.</summary>
    [Fact]
    public async Task ActionFailureHasNoJournalOrRetryAndDoesNotDisableTheSession()
    {
        var adapter = Adapter();
        adapter.Setup(s => s.CreateMarketOrderAsync(It.IsAny<MarketOrderRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException());
        await using var session = new TradingAccountSession(adapter.Object);
        await session.TrackAsync(Key);
        await Assert.ThrowsAsync<TimeoutException>(() => session.CreateMarketOrderAsync(new(Key, OrderSide.Buy, 1)));
        adapter.Verify(s => s.CreateMarketOrderAsync(It.IsAny<MarketOrderRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(session.Current.Connected);
        session.Suspend();
        Assert.False(session.Current.Connected);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CreateMarketOrderAsync(new(Key, OrderSide.Buy, 1)));
    }

    /// <summary>Closing supervision disposes the adapter without requesting cancellation.</summary>
    [Fact]
    public async Task DisposalStopsReadersAndDoesNotCancelOrders()
    {
        var adapter = Adapter();
        var session = new TradingAccountSession(adapter.Object, TimeSpan.FromMilliseconds(20));
        await session.TrackAsync(Key);
        await session.DisposeAsync();
        await session.DisposeAsync();
        adapter.Verify(s => s.DisposeAsync(), Times.Once);
        adapter.Verify(s => s.CancelOrderAsync(It.IsAny<OrderReference>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Windows of the same profile share one connection; replacing keys retires that connection.</summary>
    [Fact]
    public async Task ManagerSharesOnlyTheProfileAndRetiresReplacedKeys()
    {
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet);
        var store = new Mock<ITradingAccountStore>();
        store.Setup(s => s.Load()).Returns(new TradingAccountsSettings([profile], new Dictionary<string, Guid>()));
        using var keys = new InMemoryCredentialStore();
        using var credentials = new AccountCredentials("fake", "fake");
        keys.Write(profile.Id, credentials);
        var adapters = new List<Mock<ITradingService>>();
        await using var manager = new TradingSessionManager(store.Object, keys, (_, _) =>
        {
            var adapter = Adapter(profile);
            adapters.Add(adapter);
            return adapter.Object;
        });
        var first = await manager.GetAsync(profile.Id);
        Assert.Same(first, await manager.GetAsync(profile.Id));
        keys.Write(profile.Id, credentials);
        var second = await manager.GetAsync(profile.Id);
        Assert.NotSame(first, second);
        Assert.Equal(2, adapters.Count);
        adapters[0].Verify(s => s.DisposeAsync(), Times.Once);
    }

    /// <summary>Market clears the old budget, ignores limit price and passes native quantity through the common contract.</summary>
    [Fact]
    public async Task OrderBookSwitchesTypesAndUnblocksAfterProviderErrors()
    {
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet);
        var store = new Mock<ITradingAccountStore>();
        store.Setup(s => s.Load()).Returns(new TradingAccountsSettings([profile], new Dictionary<string, Guid> { ["binance"] = profile.Id }));
        using var keys = new InMemoryCredentialStore();
        using (var credentials = new AccountCredentials("fake", "fake")) keys.Write(profile.Id, credentials);
        var adapter = Adapter(profile);
        adapter.Setup(s => s.CreateMarketOrderAsync(It.IsAny<MarketOrderRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TradingServiceException(new("Rejected", "Minimum quantity not met.")));
        await using var manager = new TradingSessionManager(store.Object, keys, (_, _) => adapter.Object);
        await using var vm = new OrderBookViewModel("binance", Key.SecurityId, new Mock<IOrderBookService>().Object,
            manager, TradingEnvironment.Testnet);
        await UntilAsync(() => vm.AccountStatus == "Connected");
        vm.PriceText = "100.123";
        vm.AmountText = "10";
        vm.QuantityCurrency = OrderQuantityCurrency.Quote;
        vm.PostOnly = true;
        vm.SelectedOrderType = OrderType.Market;
        Assert.Equal("", vm.AmountText);
        Assert.Equal(OrderQuantityCurrency.Base, vm.QuantityCurrency);
        Assert.False(vm.PostOnly);
        Assert.False(vm.CanSelectQuantityCurrency);
        vm.PriceText = "";
        vm.AmountText = "1.2345";
        Assert.True(vm.CanTrade);
        Assert.Contains("1.234", vm.OrderPreview);
        Assert.DoesNotContain("100.123", vm.OrderPreview);
        await vm.TradeAsync(OrderSide.Sell);
        Assert.True(vm.CanTrade);
        Assert.Contains("Minimum quantity", vm.TradeStatus);
        adapter.Verify(s => s.CreateMarketOrderAsync(It.Is<MarketOrderRequest>(r => r.Side == OrderSide.Sell && r.Quantity == 1.234m),
            It.IsAny<CancellationToken>()), Times.Once);
        vm.SelectedOrderType = OrderType.Limit;
        Assert.False(vm.CanTrade);
        Assert.Equal("", vm.AmountText);
    }
}
