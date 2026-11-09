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
        adapter.Setup(s => s.FetchPositionsAsync(It.IsAny<InstrumentKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PositionSnapshot>());
        adapter.Setup(s => s.FetchMyTradesAsync(It.IsAny<InstrumentKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ExecutionSnapshot>());
        adapter.Setup(s => s.AmountToPrecision(It.IsAny<InstrumentKey>(), It.IsAny<decimal>()))
            .Returns((InstrumentKey _, decimal value) => decimal.Floor(value * 1000) / 1000);
        adapter.Setup(s => s.PriceToPrecision(It.IsAny<InstrumentKey>(), It.IsAny<decimal>()))
            .Returns((InstrumentKey _, decimal value) => decimal.Round(value, 2));
        adapter.Setup(s => s.FetchPositionModeAsync(It.IsAny<InstrumentKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
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

    /// <summary>Leverage is explicit, blocks concurrent actions and restores the form after a timeout.</summary>
    [Fact]
    public async Task FuturesLeverageAppliesOnceAndUnblocksTheForm()
    {
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet, TradingMarket.UsdtFutures);
        var contract = Key with { Market = "futures", SecurityId = "BTC/USDT:USDT" };
        var store = new Mock<ITradingAccountStore>();
        store.Setup(s => s.Load()).Returns(new TradingAccountsSettings([profile], new Dictionary<string, Guid> { ["binance"] = profile.Id }));
        using var keys = new InMemoryCredentialStore();
        using (var credentials = new AccountCredentials("fake", "fake")) keys.Write(profile.Id, credentials);
        var adapter = Adapter(profile);
        adapter.SetupGet(s => s.Account).Returns(new TradingAccountContext(profile.Id.ToString(), "Fake", "binance", TradingEnvironment.Testnet, TradingMarket.UsdtFutures));
        adapter.Setup(s => s.FetchInstrumentAsync(contract, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Instrument(contract, contract.SecurityId, "Bitcoin futures"));
        adapter.Setup(s => s.FetchPositionsAsync(contract, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PositionSnapshot>());
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.Setup(s => s.SetLeverageAsync(contract, 10, It.IsAny<CancellationToken>())).Returns(response.Task);
        await using var manager = new TradingSessionManager(store.Object, keys, (_, _) => adapter.Object);
        await using var vm = new OrderBookViewModel("binance", contract.SecurityId, new Mock<IOrderBookService>().Object,
            manager, TradingEnvironment.Testnet, market: TradingMarket.UsdtFutures);
        await UntilAsync(() => vm.AccountStatus == "Connected");

        Assert.Equal("", vm.LeverageText);
        Assert.False(vm.CanApplyLeverage);
        adapter.Verify(s => s.SetLeverageAsync(It.IsAny<InstrumentKey>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        vm.LeverageText = "0";
        Assert.False(vm.CanApplyLeverage);
        vm.LeverageText = "1.5";
        Assert.False(vm.CanApplyLeverage);
        vm.LeverageText = "10";
        Assert.True(vm.CanApplyLeverage);
        var applying = vm.ApplyLeverageAsync();
        Assert.False(vm.IsTradeEditable);
        Assert.False(vm.CanApplyLeverage);
        Assert.False(vm.CanTrade);
        await vm.ApplyLeverageAsync();
        response.SetException(new TimeoutException("Leverage response timed out."));
        await applying;

        Assert.True(vm.IsTradeEditable);
        Assert.True(vm.CanApplyLeverage);
        Assert.Contains("outcome is unknown", vm.TradeStatus);
        adapter.Verify(s => s.SetLeverageAsync(contract, 10, It.IsAny<CancellationToken>()), Times.Once);
        adapter.Setup(s => s.SetLeverageAsync(contract, 10, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await vm.ApplyLeverageAsync();
        Assert.Contains("10x", vm.TradeStatus);
        Assert.True(vm.IsTradeEditable);
        adapter.Verify(s => s.SetLeverageAsync(contract, 10, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>Margin preview follows selected leverage and native units, using live depth instead of a stale limit price for Market.</summary>
    [Fact]
    public async Task MarginPreviewUsesPreparedInputAndFreshMarketPrices()
    {
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet, TradingMarket.UsdtFutures);
        var contract = Key with { Market = "futures", SecurityId = "BTC/USDT:USDT" };
        var store = new Mock<ITradingAccountStore>();
        store.Setup(s => s.Load()).Returns(new TradingAccountsSettings([profile], new Dictionary<string, Guid> { ["binance"] = profile.Id }));
        using var keys = new InMemoryCredentialStore();
        using (var credentials = new AccountCredentials("fake", "fake")) keys.Write(profile.Id, credentials);
        var adapter = Adapter(profile);
        adapter.Setup(s => s.FetchInstrumentAsync(contract, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Instrument(contract, contract.SecurityId, "Bitcoin futures",
                TradingRules: new(QuantityUnit.Contracts, PriceQuotation.CurrencyPerUnit, ContractSize: .01m)));
        var subscription = new Mock<IOrderBookSubscription>();
        var snapshot = new OrderBookSnapshot(new(contract.SecurityId, "BTC", "USDT", .01m),
            [new(90m, 10m)], [new(110m, 10m)], DateTimeOffset.UtcNow);
        subscription.SetupGet(s => s.Current).Returns(new OrderBookUpdate(snapshot, OrderBookConnection.Live, "Connected"));
        var feed = new Mock<IOrderBookService>();
        feed.Setup(s => s.SubscribeAsync("binance", contract.SecurityId, TradingEnvironment.Testnet, TradingMarket.UsdtFutures, It.IsAny<CancellationToken>()))
            .ReturnsAsync(subscription.Object);
        await using var manager = new TradingSessionManager(store.Object, keys, (_, _) => adapter.Object);
        await using var vm = new OrderBookViewModel("binance", contract.SecurityId, feed.Object,
            manager, TradingEnvironment.Testnet, TradingMarket.UsdtFutures);
        await UntilAsync(() => vm.AccountStatus == "Connected");
        vm.PriceText = "100";
        vm.AmountText = "2";
        Assert.Contains("Select leverage", vm.MarginPreview);
        vm.LeverageText = "10";
        Assert.Contains("≈ 0.2 USDT", vm.MarginPreview);
        vm.LeverageText = "20";
        Assert.Contains("≈ 0.1 USDT", vm.MarginPreview);
        vm.QuantityCurrency = OrderQuantityCurrency.Quote;
        vm.AmountText = "20";
        Assert.Contains("≈ 1 USDT", vm.MarginPreview);
        vm.SelectedOrderType = OrderType.Market;
        vm.AmountText = "2";
        vm.PriceText = "99999";
        Assert.Contains("unavailable", vm.MarginPreview);
        await vm.StartAsync();
        vm.Refresh();
        Assert.Contains("Buy ≈ 0.11 / Sell ≈ 0.09 USDT", vm.MarginPreview);
        subscription.SetupGet(s => s.Current).Returns(new OrderBookUpdate(snapshot with { ReceivedAt = DateTimeOffset.UtcNow.AddSeconds(-30) }, OrderBookConnection.Live, "Connected"));
        vm.Refresh();
        Assert.Contains("unavailable", vm.MarginPreview);
        Assert.True(vm.CanTrade);
        vm.ReduceOnly = true;
        Assert.Contains("not applicable", vm.MarginPreview);
        adapter.Verify(s => s.SetLeverageAsync(It.IsAny<InstrumentKey>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
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

    /// <summary>The form follows the exchange mode: hedge keeps a leg, one-way and an unknown mode do not.</summary>
    [Theory]
    [InlineData("hedge", "Hedge mode", true)]
    [InlineData("oneway", "One-way mode", false)]
    [InlineData("unknown", "did not report", false)]
    public async Task FuturesFormFollowsTheReportedPositionMode(string mode, string expected, bool showSide)
    {
        bool? hedged = mode switch { "hedge" => true, "oneway" => false, _ => null };
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "bybit", TradingEnvironment.Testnet, TradingMarket.UsdtFutures);
        var contract = Key with { Venue = "bybit", Market = "futures", SecurityId = "BTC/USDT:USDT" };
        var store = new Mock<ITradingAccountStore>();
        store.Setup(s => s.Load()).Returns(new TradingAccountsSettings([profile], new Dictionary<string, Guid> { ["bybit"] = profile.Id }));
        using var keys = new InMemoryCredentialStore();
        using (var credentials = new AccountCredentials("fake", "fake")) keys.Write(profile.Id, credentials);
        var adapter = Adapter(profile);
        adapter.SetupGet(s => s.Account).Returns(new TradingAccountContext(profile.Id.ToString(), "Fake", "bybit", TradingEnvironment.Testnet, TradingMarket.UsdtFutures));
        adapter.Setup(s => s.FetchPositionModeAsync(contract, It.IsAny<CancellationToken>())).ReturnsAsync(hedged);
        adapter.Setup(s => s.CreateMarketOrderAsync(It.IsAny<MarketOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderSnapshot(new(contract, "9"), null, OrderSide.Buy, OrderType.Market, OrderState.Open,
                1, 0, 1, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        await using var manager = new TradingSessionManager(store.Object, keys, (_, _) => adapter.Object);
        await using var vm = new OrderBookViewModel("bybit", contract.SecurityId, new Mock<IOrderBookService>().Object,
            manager, TradingEnvironment.Testnet, TradingMarket.UsdtFutures);
        await UntilAsync(() => vm.AccountStatus == "Connected");

        Assert.Equal(showSide, vm.ShowPositionSide);
        Assert.Contains(expected, showSide || hedged == false ? vm.PositionModeText : vm.TradingInputHint);
        vm.SelectedOrderType = OrderType.Market;
        vm.AmountText = "1";
        Assert.Equal(hedged is not null, vm.CanTrade);
        if (hedged is null)
        {
            return;
        }

        vm.PositionTarget = "Short";
        await vm.TradeAsync(OrderSide.Buy);
        adapter.Verify(s => s.CreateMarketOrderAsync(It.Is<MarketOrderRequest>(order =>
            order.PositionSide == (hedged == true ? PositionSide.Short : null) && order.ReduceOnly == false),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
