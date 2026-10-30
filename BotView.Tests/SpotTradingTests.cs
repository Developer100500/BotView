using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;
using BotView.Models;
using BotView.Services;
using BotView.Services.Integrations.Ccxt;
using BotView.ViewModels;

namespace BotView.Tests;

public sealed class SpotTradingTests
{
    private static readonly InstrumentKey Key = new("binance", "spot", "", "ETH/USDT");

    /// <summary>Creates a unique client intent using native base-asset units.</summary>
    private static PlaceOrderRequest Request(decimal quantity = 1, decimal price = 100, bool postOnly = false) =>
        new(Key, OrderSide.Buy, OrderType.Limit, quantity, "bv" + Guid.NewGuid().ToString("N"), price, PostOnly: postOnly);

    /// <summary>Builds a detached exchange order for event and race tests.</summary>
    private static OrderSnapshot Order(string id = "1", OrderState state = OrderState.Open, decimal filled = 0,
        string? clientId = null, int seconds = 0) => new(new(Key, id), clientId, OrderSide.Buy, OrderType.Limit,
        state, 1, filled, 1 - filled, 100, filled > 0 ? 100 : null, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddSeconds(seconds));

    [Fact]
    public void QuoteConversionRoundsDownWithoutExceedingBudget()
    {
        var quantity = SpotOrderMath.Quantity(100m, 3m, OrderQuantityCurrency.Quote, value => decimal.Floor(value * 1000) / 1000);
        Assert.Equal(33.333m, quantity);
        Assert.True(quantity * 3 <= 100m);
        Assert.Equal(1.2345m, SpotOrderMath.Quantity(1.2345m, 3, OrderQuantityCurrency.Base, _ => throw new Exception()));
    }

    [Fact]
    public async Task CcxtPrecisionSupportsSignificantDigitsAndRejectsLossAtDoubleBoundary()
    {
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet);
        using var keys = new AccountCredentials("fake", "fake");
        await using var client = new CcxtSpotTradingClient(profile, keys);
        client.Transport.precisionMode = 3;
        client.Transport.setMarkets(new List<object> { new Dictionary<string, object>
        {
            ["id"] = "ETHUSDT", ["symbol"] = "ETH/USDT", ["base"] = "ETH", ["quote"] = "USDT", ["spot"] = true, ["type"] = "spot",
            ["precision"] = new Dictionary<string, object> { ["amount"] = 3, ["price"] = 5 }
        } });
        Assert.Equal(1.23m, client.NormalizeAmount("ETH/USDT", 1.234m));
        Assert.Equal(.00123m, client.NormalizeAmount("ETH/USDT", .001234m));
        client.Transport.precisionMode = 4;
        client.Transport.setMarkets(new List<object> { new Dictionary<string, object>
        {
            ["id"] = "ETHUSDT", ["symbol"] = "ETH/USDT", ["base"] = "ETH", ["quote"] = "USDT", ["spot"] = true, ["type"] = "spot",
            ["precision"] = new Dictionary<string, object> { ["amount"] = .001, ["price"] = .01 }
        } });
        Assert.Throws<ArgumentException>(() => client.WireNumber("ETH/USDT", 1000000000000000.01m, false));
        Assert.Equal(100.01, client.WireNumber("ETH/USDT", 100.01m, false));
        Assert.Contains("testnet", System.Text.Json.JsonSerializer.Serialize(client.Transport.urls), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("binance", TradingEnvironment.Testnet, "testnet.binance.vision")]
    [InlineData("binance", TradingEnvironment.Production, "api.binance.com")]
    [InlineData("bybit", TradingEnvironment.Testnet, "api-testnet.bybit.com")]
    [InlineData("bybit", TradingEnvironment.Production, "api.bybit.com")]
    public async Task ProClientPrivateRequestsAreBoundToTheSelectedNetwork(string exchange, TradingEnvironment environment, string host)
    {
        using var keys = new AccountCredentials("fake", "fake");
        await using var client = new CcxtSpotTradingClient(new(Guid.NewGuid(), "Fake", exchange, environment), keys);
        var api = (Dictionary<string, object>)((Dictionary<string, object>)client.Transport.urls)["api"];
        Assert.Contains(host, client.Transport.implodeHostname(api["private"]));
        Assert.False(client.Transport.verbose);
    }

    [Fact]
    public async Task BybitAdapterPreservesEnvelopeCursorsExternalConditionalRoutingAndActualFees()
    {
        using var keys = new AccountCredentials("fake", "fake");
        await using var client = new CcxtSpotTradingClient(new(Guid.NewGuid(), "Fake", "bybit", TradingEnvironment.Testnet), keys);
        client.Transport.setMarkets(new List<object> { new Dictionary<string, object>
        {
            ["id"] = "ETHUSDT", ["symbol"] = "ETH/USDT", ["base"] = "ETH", ["quote"] = "USDT", ["spot"] = true, ["type"] = "spot",
            ["precision"] = new Dictionary<string, object> { ["amount"] = .001, ["price"] = .01 }
        } });
        var key = Key with { Venue = "bybit" };
        var orderPage = client.ParseBybitOrders(new Dictionary<string, object> { ["result"] = new Dictionary<string, object>
        {
            ["nextPageCursor"] = "opaque-order-cursor", ["list"] = new List<object> { new Dictionary<string, object>
            {
                ["orderId"] = "external-stop", ["orderLinkId"] = "outside", ["symbol"] = "ETHUSDT", ["orderType"] = "Limit",
                ["side"] = "Buy", ["qty"] = "1", ["price"] = "100", ["cumExecQty"] = "0.4", ["leavesQty"] = "0.6",
                ["orderStatus"] = "PartiallyFilled", ["createdTime"] = "1700000000000", ["updatedTime"] = "1700000001000", ["stopOrderType"] = "Stop"
            } }
        } }, key);
        Assert.Equal("opaque-order-cursor", orderPage.NextCursor);
        Assert.Equal(.4m, orderPage.Items.Single().FilledQuantity);
        Assert.Equal(.6m, orderPage.Items.Single().RemainingQuantity);
        Assert.Equal("StopOrder", client.BybitCancelParameters(orderPage.Items.Single().Reference)["orderFilter"]);
        var fills = client.ParseBybitExecutions(new Dictionary<string, object> { ["result"] = new Dictionary<string, object>
        {
            ["nextPageCursor"] = "opaque-fill-cursor", ["list"] = new List<object> { new Dictionary<string, object>
            {
                ["execId"] = "fill-1", ["orderId"] = "external-stop", ["symbol"] = "ETHUSDT", ["side"] = "Buy",
                ["execQty"] = "0.4", ["execPrice"] = "100", ["execFee"] = "0.0004", ["feeCurrency"] = "ETH",
                ["execTime"] = "1700000001000", ["isMaker"] = true
            } }
        } }, key);
        Assert.Equal("opaque-fill-cursor", fills.NextCursor);
        Assert.Equal("fill-1", fills.Items.Single().ExecutionId);
        Assert.Equal(new Money(.0004m, "ETH"), fills.Items.Single().Fee);
    }

    [Theory]
    [InlineData("binance", false)]
    [InlineData("binance", true)]
    [InlineData("bybit", false)]
    [InlineData("bybit", true)]
    public void LimitAndPostOnlyMapWithoutFallback(string exchange, bool maker)
    {
        var request = Request(postOnly: maker);
        var parameters = CcxtSpotTradingClient.OrderParameters(exchange, request);
        Assert.Equal(request.ClientOrderId, parameters["clientOrderId"]);
        if (exchange == "binance")
        {
            Assert.Equal(maker, parameters.ContainsKey("postOnly"));
            Assert.Equal(!maker, parameters.ContainsKey("timeInForce"));
        }
        else
        {
            Assert.Equal(maker ? "PostOnly" : "GTC", parameters["timeInForce"]);
            Assert.Equal(0, parameters["isLeverage"]);
            Assert.Equal("spot", parameters["category"]);
        }
    }

    [Fact]
    public void ReducerPreservesPartialFillsAndTerminalStateAgainstOldEvents()
    {
        var canceled = Order(state: OrderState.Canceled, filled: .4m, seconds: 2);
        var oldOpen = Order(filled: .1m, seconds: -10);
        var reduced = CcxtTradingService.Reduce(canceled, oldOpen);
        Assert.Equal(OrderState.Canceled, reduced.State);
        Assert.Equal(.4m, reduced.FilledQuantity);
        Assert.Equal(.6m, reduced.RemainingQuantity);
        var race = CcxtTradingService.Reduce(reduced, Order(state: OrderState.Filled, filled: 1, seconds: 3));
        Assert.Equal(OrderState.Filled, race.State);
        Assert.Equal(0, race.RemainingQuantity);
        Assert.Equal(OrderState.Filled, CcxtTradingService.Reduce(race, canceled).State);
    }

    [Theory]
    [InlineData(.0001, 100, "QuantityStep")]
    [InlineData(.001, 1, "MinNotional")]
    [InlineData(1, 100.001, "PriceStep")]
    [InlineData(101, 100, "MaxQuantity")]
    public async Task ValidatesLimitsAndPrecisionBeforeAnySend(double quantity, double price, string error)
    {
        await using var harness = await Harness.CreateAsync();
        var result = await harness.Service.PlaceOrderAsync(Request((decimal)quantity, (decimal)price));
        Assert.Equal(PlaceOrderStatus.Rejected, result.Status);
        Assert.Equal(error, result.Error!.Code);
        Assert.Equal(0, harness.Client.Sends);
    }

    [Fact]
    public async Task IncludesCommissionAndRejectsUnknownFunds()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Client.Free = 100m;
        harness.Service.Merge(Order("external"));
        // Force a fresh read through a new session, avoiding a cached preview balance.
        await harness.Service.DisposeAsync();
        await using var second = await Harness.CreateAsync(client: new FakeClient { Free = 100m });
        var result = await second.Service.PlaceOrderAsync(Request());
        Assert.Equal("InsufficientFunds", result.Error!.Code);
        await using var unknown = await Harness.CreateAsync(client: new FakeClient { Free = null });
        Assert.Equal("BalanceUnavailable", (await unknown.Service.PlaceOrderAsync(Request())).Error!.Code);
    }

    [Fact]
    public async Task RejectsDeniedPermissionsButAllowsUnknownPermissionToReachExchange()
    {
        await using var denied = await Harness.CreateAsync(permission: false);
        Assert.Equal("TradingDenied", (await denied.Service.PlaceOrderAsync(Request())).Error!.Code);
        await using var unknown = await Harness.CreateAsync(permission: null);
        Assert.Equal(PlaceOrderStatus.Accepted, (await unknown.Service.PlaceOrderAsync(Request())).Status);
    }

    [Fact]
    public async Task LostPlacementResponseIsJournaledAndNeverResent()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Client.LoseResponse = true;
        var request = Request();
        var result = await harness.Service.PlaceOrderAsync(request);
        Assert.Equal(PlaceOrderStatus.Unknown, result.Status);
        Assert.Single(harness.Service.Current.Pending);
        Assert.Equal(TradingOperationPhase.Unknown, harness.Journal.Operations(harness.AccountKey).Single().Phase);
        var blocked = await harness.Service.PlaceOrderAsync(Request());
        Assert.Equal(PlaceOrderStatus.Rejected, blocked.Status);
        Assert.Equal(1, harness.Client.Sends);
        var duplicate = await harness.Service.PlaceOrderAsync(request);
        Assert.Equal("DuplicateIntent", duplicate.Error!.Code);
        harness.Client.RevealLostOrder = true;
        await harness.Service.TrackAsync(Key);
        Assert.Empty(harness.Service.Current.Pending);
        Assert.Equal(1, harness.Client.Sends);
    }

    [Fact]
    public async Task RestartRestoresDispatchingAsUnknownAndReconcilesWithoutSending()
    {
        await using var harness = await Harness.CreateAsync();
        var request = Request();
        harness.Journal.Save(new(request.ClientOrderId, harness.AccountKey, request, TradingOperationPhase.Dispatching, DateTimeOffset.UtcNow));
        await harness.Service.DisposeAsync();
        var client = new FakeClient { LostOrder = Order(clientId: request.ClientOrderId), RevealLostOrder = true };
        await using var restored = new CcxtTradingService(harness.Profile, client, harness.Journal, "owner", true);
        await restored.TrackAsync(Key);
        Assert.Empty(restored.Current.Pending);
        Assert.Equal(0, client.Sends);
        Assert.Contains(restored.Current.Orders, o => o.ClientOrderId == request.ClientOrderId);
    }

    [Fact]
    public async Task PreparedIntentIsNeverReplayedAndAnotherOwnerCannotSeeIt()
    {
        await using var harness = await Harness.CreateAsync();
        var request = Request();
        harness.Journal.Save(new(request.ClientOrderId, harness.AccountKey, request, TradingOperationPhase.Prepared, DateTimeOffset.UtcNow));
        await using var other = new CcxtTradingService(harness.Profile, new FakeClient(), harness.Journal, "another-owner", true);
        await other.TrackAsync(Key);
        Assert.Empty(other.Current.Pending);
        Assert.Empty(other.Current.Orders);
        Assert.Empty(harness.Journal.Operations("binance:Production:owner"));
    }

    [Fact]
    public async Task ConcurrentWindowsSerializeAndObserveUpdatedFreeBalance()
    {
        await using var harness = await Harness.CreateAsync(client: new FakeClient { Free = 150m });
        var results = await Task.WhenAll(harness.Service.PlaceOrderAsync(Request()), harness.Service.PlaceOrderAsync(Request()));
        Assert.Single(results.Where(r => r.Status == PlaceOrderStatus.Accepted));
        Assert.Single(results.Where(r => r.Status == PlaceOrderStatus.Rejected));
        Assert.Equal(1, harness.Client.Sends);
    }

    [Fact]
    public async Task CancelAllCapturesAllPagesAndDoesNotIncludeNewOrders()
    {
        var client = new FakeClient { PageOpenOrders = true };
        client.Live["external-1"] = Order("external-1");
        client.Live["external-2"] = Order("external-2");
        await using var harness = await Harness.CreateAsync(client: client);
        var batch = await harness.Service.PrepareCancelAllAsync(Key);
        Assert.Equal(2, batch.Orders.Count);
        client.Live["new-after-confirmation"] = Order("new-after-confirmation");
        client.FailCancelId = "external-2";
        var results = await harness.Service.CancelBatchAsync(batch);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Status == CancelOrderStatus.Canceled);
        Assert.Contains(results, r => r.Status == CancelOrderStatus.Unknown);
        Assert.DoesNotContain("new-after-confirmation", client.Canceled);
    }

    [Fact]
    public async Task CancelFillRaceReportsAlreadyClosedWithExecutionsRetained()
    {
        var client = new FakeClient { FillOnCancel = true };
        client.Live["1"] = Order(filled: .4m);
        await using var harness = await Harness.CreateAsync(client: client);
        var result = await harness.Service.CancelOrderAsync(new(Key, "1"));
        Assert.Equal(CancelOrderStatus.AlreadyClosed, result.Status);
        Assert.Equal(1, result.Order!.FilledQuantity);
        Assert.Equal(OrderState.Filled, harness.Service.Current.Orders.Single().State);
    }

    [Fact]
    public async Task StreamDuplicatesAreDeduplicatedAndBalanceGapBlocksUntilResync()
    {
        var client = new FakeClient { Streaming = true };
        await using var harness = await Harness.CreateAsync(client: client);
        var fill = new ExecutionSnapshot("fill-1", new(Key, "1"), OrderSide.Buy, .4m, 100, DateTimeOffset.UtcNow, new(.04m, "USDT"));
        await client.Stream.Writer.WriteAsync(new ExecutionReceived(fill));
        await client.Stream.Writer.WriteAsync(new ExecutionReceived(fill));
        await UntilAsync(() => harness.Service.Current.Executions.Count == 1);
        client.FailReads = true;
        client.Stream.Writer.TryComplete(new IOException("Simulated loss"));
        await UntilAsync(() => !harness.Service.Current.Ready);
        Assert.Equal(PlaceOrderStatus.Rejected, (await harness.Service.PlaceOrderAsync(Request())).Status);
        client.FailReads = false;
        await harness.Service.TrackAsync(Key);
        Assert.True(harness.Service.Current.Ready);
        Assert.Single(harness.Journal.Executions(harness.AccountKey));
    }

    [Fact]
    public async Task ClosingSessionCancelsWaitsButDoesNotCancelExchangeOrders()
    {
        await using var harness = await Harness.CreateAsync(client: new FakeClient { Streaming = true });
        await harness.Service.PlaceOrderAsync(Request());
        await harness.Service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(harness.Client.Canceled);
        Assert.True(harness.Client.Disposed);
        Assert.False(harness.Service.Current.Ready);
    }

    [Fact]
    public async Task ManagersShareOwnerSessionsAndInvalidateThemImmediatelyOnKeyReplacement()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BotView-trading-" + Guid.NewGuid().ToString("N"));
        var store = new TradingAccountStore(Path.Combine(directory, "profiles.json"));
        using var credentials = new InMemoryCredentialStore();
        var first = new TradingAccountProfile(Guid.NewGuid(), "First", "binance", TradingEnvironment.Testnet);
        var second = first with { Id = Guid.NewGuid(), Name = "Second" };
        store.Save(new([first, second], new Dictionary<string, Guid> { ["binance"] = first.Id }));
        using (var keys = new AccountCredentials("fake-key", "fake-secret")) { credentials.Write(first.Id, keys); credentials.Write(second.Id, keys); }
        var created = new List<FakeClient>();
        await using var manager = new TradingSessionManager(store, credentials, (_, _) => { var c = new FakeClient(); created.Add(c); return c; }, new(() => new TradingJournal(Path.Combine(directory, "journal.db"))));
        var a = await manager.GetAsync(first.Id);
        var b = await manager.GetAsync(second.Id);
        Assert.Same(a, b);
        await a.TrackAsync(Key);
        using (var changed = new AccountCredentials("new-key", "new-secret")) credentials.Write(first.Id, changed);
        Assert.False(a.Current.Ready);
        var replacement = await manager.GetAsync(first.Id);
        Assert.NotSame(a, replacement);
        Assert.Empty(created.SelectMany(c => c.Canceled));
    }

    [Fact]
    public async Task OrderBookConvertsUnitsFreezesIntentAndLeavesSessionAliveAfterClose()
    {
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet);
        var settings = new TradingAccountsSettings([profile], new Dictionary<string, Guid> { ["binance"] = profile.Id });
        var store = new Moq.Mock<ITradingAccountStore>();
        store.Setup(s => s.Load()).Returns(settings);
        using var credentials = new InMemoryCredentialStore();
        using (var keys = new AccountCredentials("fake", "fake")) credentials.Write(profile.Id, keys);
        var client = new FakeClient { PlacementHold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var manager = new TradingSessionManager(store.Object, credentials, (_, _) => client,
            new(() => new TradingJournal(Path.Combine(Path.GetTempPath(), "BotView-trading-" + Guid.NewGuid().ToString("N"), "journal.db"))));
        var depth = new FakeDepthService();
        await using var vm = new OrderBookViewModel("binance", "ETH/USDT", depth, manager, TradingEnvironment.Testnet);
        await vm.StartAsync();
        await UntilAsync(() => vm.AccountStatus.StartsWith("Connected", StringComparison.Ordinal));
        vm.Refresh();
        Assert.Equal(TradingEnvironment.Testnet, depth.RequestedEnvironment);
        vm.QuantityCurrency = OrderQuantityCurrency.Quote;
        vm.PriceText = "3"; vm.AmountText = "100";
        Assert.Contains("33.333 ETH", vm.OrderPreview);
        vm.QuantityCurrency = OrderQuantityCurrency.Base;
        Assert.Equal("33.333", vm.AmountText);
        vm.AmountText = "0.0001";
        Assert.False(vm.CanTrade);
        vm.AmountText = "1"; vm.PriceText = "100";
        Assert.True(vm.CanTrade);
        var sending = vm.TradeAsync(OrderSide.Buy);
        await UntilAsync(() => client.Sends == 1);
        Assert.False(vm.SellCommand.CanExecute(null));
        vm.PriceText = "101";
        await vm.TradeAsync(OrderSide.Sell);
        client.PlacementHold.SetResult();
        await sending;
        Assert.Equal(1, client.Sends);
        Assert.Equal(100, client.LastRequest!.Price);
        Assert.Equal(OrderSide.Buy, client.LastRequest.Side);
        await vm.DisposeAsync();
        var session = await manager.GetAsync(profile.Id);
        Assert.NotEmpty(session.Current.Orders);
        Assert.Empty(client.Canceled);
        Assert.False(client.Disposed);
    }

    /// <summary>Waits for a deterministic fake event with a bounded failure timeout.</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required CcxtTradingService Service { get; init; }
        public required FakeClient Client { get; init; }
        public required TradingJournal Journal { get; init; }
        public required TradingAccountProfile Profile { get; init; }
        public string AccountKey => "binance:Testnet:owner";

        /// <summary>Starts a fully synchronized session against a private temporary journal.</summary>
        internal static async Task<Harness> CreateAsync(FakeClient? client = null, bool? permission = true)
        {
            client ??= new();
            var journal = new TradingJournal(Path.Combine(Path.GetTempPath(), "BotView-trading-" + Guid.NewGuid().ToString("N"), "journal.db"));
            var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet);
            var service = new CcxtTradingService(profile, client, journal, "owner", permission);
            await service.TrackAsync(Key);
            return new() { Service = service, Client = client, Journal = journal, Profile = profile };
        }

        /// <summary>Releases fake background subscriptions without touching exchange orders.</summary>
        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }

    private sealed class FakeClient : ISpotTradingClient
    {
        public decimal? Free = 10000;
        public int Sends;
        public bool LoseResponse, RevealLostOrder, Streaming, FailReads, PageOpenOrders, FillOnCancel, Disposed;
        public string? FailCancelId;
        public OrderSnapshot? LostOrder;
        public PlaceOrderRequest? LastRequest;
        public TaskCompletionSource? PlacementHold;
        public ConcurrentDictionary<string, OrderSnapshot> Live { get; } = new();
        public ConcurrentBag<string> Canceled { get; } = new();
        public Channel<TradingUpdate> Stream { get; } = Channel.CreateUnbounded<TradingUpdate>();

        /// <summary>Supplies a stable fake account owner without network access.</summary>
        public Task<(string Identity, bool? CanTrade)> ConnectAsync(CancellationToken ct) => Task.FromResult<(string, bool?)>(("owner", true));

        /// <summary>This fixture does not load an exchange catalog.</summary>
        public Task<IReadOnlyList<string>> LoadTradingPairsAsync(string spotSymbol, CancellationToken ct = default)
                => throw new NotSupportedException();

        /// <summary>Provides real trading constraints and a conservative input-asset fee.</summary>
        public Task<SpotMarket> MarketAsync(InstrumentKey key, CancellationToken ct) => Task.FromResult(new SpotMarket(
            new(key, key.SecurityId, "Ether", "USDT", PriceStep: .01m, TradingRules: new(QuantityUnit.BaseAsset, PriceQuotation.CurrencyPerUnit,
                QuantityStep: .001m, MinQuantity: .001m, MaxQuantity: 100, MinNotional: 1)), "ETH", "USDT", .001m));

        /// <summary>Implements a deterministic quantity tick.</summary>
        public decimal NormalizeAmount(string symbol, decimal amount) => decimal.Floor(amount * 1000) / 1000;

        /// <summary>Implements a deterministic price tick.</summary>
        public decimal NormalizePrice(string symbol, decimal price) => decimal.Round(price, 2);

        /// <summary>Simulates acceptance before transport loss, including a reduced free balance.</summary>
        public async Task<OrderSnapshot> PlaceAsync(PlaceOrderRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Sends);
            LastRequest = request;
            var order = Order(Sends.ToString(), clientId: request.ClientOrderId) with { Quantity = request.Quantity, Price = request.Price };
            if (PlacementHold is not null) await PlacementHold.Task.WaitAsync(ct);
            Free -= request.Quantity * request.Price * 1.001m;
            if (LoseResponse) { LostOrder = order; throw new ccxt.RequestTimeout("Simulated timeout"); }
            Live[order.Reference.ExchangeOrderId] = order;
            return order;
        }

        /// <summary>Simulates normal cancellation, a lost cancellation response, or a concurrent complete fill.</summary>
        public Task<OrderSnapshot?> CancelAsync(OrderReference order, CancellationToken ct)
        {
            Canceled.Add(order.ExchangeOrderId);
            if (order.ExchangeOrderId == FailCancelId) throw new ccxt.RequestTimeout("Simulated timeout");
            var previous = Live[order.ExchangeOrderId];
            var changed = previous with { State = FillOnCancel ? OrderState.Filled : OrderState.Canceled,
                FilledQuantity = FillOnCancel ? 1 : previous.FilledQuantity, RemainingQuantity = FillOnCancel ? 0 : previous.RemainingQuantity,
                UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1) };
            Live[order.ExchangeOrderId] = changed;
            return Task.FromResult<OrderSnapshot?>(changed);
        }

        /// <summary>Returns authoritative fake order state, preserving explicit absence.</summary>
        public Task<OrderSnapshot?> OrderAsync(OrderReference order, CancellationToken ct) => Task.FromResult(Live.GetValueOrDefault(order.ExchangeOrderId));

        /// <summary>Reveals a previously accepted order only when the test permits recovery.</summary>
        public Task<OrderSnapshot?> FindAsync(InstrumentKey key, string clientId, CancellationToken ct) => Task.FromResult(
            RevealLostOrder && LostOrder?.ClientOrderId == clientId ? LostOrder : Live.Values.FirstOrDefault(o => o.ClientOrderId == clientId));

        /// <summary>Exercises both full and paged open-order reconciliation.</summary>
        public Task<TradingPage<OrderSnapshot>> OrdersAsync(TradingQuery query, bool open, CancellationToken ct)
        {
            if (FailReads) throw new IOException("Simulated connection loss");
            var all = Live.Values.Where(o => !open || !CcxtTradingService.Terminal(o.State)).OrderBy(o => o.Reference.ExchangeOrderId).ToArray();
            if (!PageOpenOrders) return Task.FromResult(new TradingPage<OrderSnapshot>(all));
            var offset = query.Cursor is null ? 0 : int.Parse(query.Cursor);
            return Task.FromResult(new TradingPage<OrderSnapshot>(all.Skip(offset).Take(1).ToArray(), offset + 1 < all.Length ? (offset + 1).ToString() : null));
        }

        /// <summary>Streams carry fills in tests; REST remains available for reconciliation.</summary>
        public Task<TradingPage<ExecutionSnapshot>> ExecutionsAsync(TradingQuery query, CancellationToken ct) => Task.FromResult(new TradingPage<ExecutionSnapshot>([]));

        /// <summary>Reports mutable fake free funds so serialized submissions compete for the same balance.</summary>
        public Task<AccountSnapshot> BalanceAsync(TradingAccountContext account, CancellationToken ct) => Task.FromResult(new AccountSnapshot(account,
            [new("USDT", Free, Free, 0), new("ETH", 100, 100, 0)], null, null, DateTimeOffset.UtcNow));

        /// <summary>Only execution streaming is enabled to keep event tests deterministic.</summary>
        public bool SupportsStream(string kind) => Streaming && kind == "watchMyTrades";

        /// <summary>Waits until a test supplies a private event or the session closes.</summary>
        public async Task<IReadOnlyList<TradingUpdate>> WatchAsync(string kind, InstrumentKey key, TradingAccountContext account, CancellationToken ct) => [await Stream.Reader.ReadAsync(ct)];

        /// <summary>Records disposal independently of remote cancellation.</summary>
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeDepthService : IOrderBookService
    {
        public TradingEnvironment RequestedEnvironment;

        /// <summary>Records network routing without starting a public WebSocket.</summary>
        public Task<IOrderBookSubscription> SubscribeAsync(string exchange, string symbol, CancellationToken ct = default) => SubscribeAsync(exchange, symbol, TradingEnvironment.Production, ct);

        /// <summary>Supplies one detached network-specific book to the order-entry view model.</summary>
        public Task<IOrderBookSubscription> SubscribeAsync(string exchange, string symbol, TradingEnvironment environment, CancellationToken ct = default)
        {
            RequestedEnvironment = environment;
            return Task.FromResult<IOrderBookSubscription>(new Depth());
        }

        private sealed class Depth : IOrderBookSubscription
        {
            public OrderBookUpdate Current { get; } = new(new(new("ETH/USDT", "ETH", "USDT", .01m),
                [new(100, 1)], [new(100.02m, 1)], DateTimeOffset.UtcNow), OrderBookConnection.Live, "Connected");

            /// <summary>Detaches only public depth; account state belongs to its session manager.</summary>
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
