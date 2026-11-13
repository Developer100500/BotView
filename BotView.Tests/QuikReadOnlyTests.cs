using System.IO;
using BotView.Configuration;
using BotView.Services.Integrations.Quik;
using BotView.ViewModels;
using Moq;
using NativeState = QuikSharp.DataStructures.State;

namespace BotView.Tests;

public sealed class QuikReadOnlyTests
{
    private static readonly InstrumentKey Future = new("MOEX", "futures", "SPBFUT", "SAME");
    private static readonly InstrumentKey Share = new("MOEX", "shares", "TQBR", "SAME");
    private static readonly TradingAccountProfile Profile = new(Guid.NewGuid(), "QUIK account", "quik",
        TradingEnvironment.Production, TradingMarket.MoexFutures, new("127.0.0.1", 34130, "firm", "client", "account", 0));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The adapter cannot forward any mutation because its injected boundary exposes reads only.</summary>
    [Fact]
    public async Task ReadAdapterRejectsEveryMutationWithoutCallingItsClient()
    {
        var client = new FakeReadClient();
        await using var service = new QuikReadOnlyTradingService(Profile, client);
        Assert.False(service.CanExecuteTrades);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.CreateLimitOrderAsync(new(Future, OrderSide.Buy, 1, 100), Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.CreateMarketOrderAsync(new(Future, OrderSide.Buy, 1), Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.CancelOrderAsync(new(Future, "1"), Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.SetLeverageAsync(Future, 5, Ct));
        Assert.Equal(0, client.Reads);
        var instrument = await service.FetchInstrumentAsync(Future, Ct);
        var balance = await service.FetchBalanceAsync(Ct);
        var positions = await service.FetchPositionsAsync(Future, Ct);
        Assert.Equal(Future, instrument.Key);
        Assert.Equal(service.Account, balance.Account);
        Assert.Equal(2, Assert.Single(positions).Quantity);
    }

    /// <summary>Normal and conditional orders retain distinct identities even with the same native order number.</summary>
    [Fact]
    public void NativeMappingKeepsCategoriesAndAccountDiscriminators()
    {
        var normal = QuikReadClient.MapOrder(new()
        {
            OrderNum = 10, Quantity = 5, Balance = 2, Price = 100,
            Operation = QuikSharp.DataStructures.Operation.Buy,
            Flags = QuikSharp.DataStructures.Transaction.OrderTradeFlags.IsLimit | QuikSharp.DataStructures.Transaction.OrderTradeFlags.Active
        }, Future);
        var stop = QuikReadClient.MapStop(new()
        {
            OrderNum = 10, Quantity = 5, FilledQuantity = 0, Price = 101,
            ConditionPrice = 100, State = NativeState.Active
        }, Future);
        Assert.Equal(3, normal.FilledQuantity);
        Assert.NotEqual(normal.Reference, stop.Reference);
        Assert.Equal(OrderCategory.Conditional, stop.Reference.Category);
        Assert.True(QuikReadClient.Matches(Profile.Quik!, "firm", "account", "client"));
        Assert.False(QuikReadClient.Matches(Profile.Quik!, "other", "account", "client"));
        Assert.False(QuikReadClient.Matches(Profile.Quik!, "firm", "other", "client"));
        Assert.NotEqual(QuikReadClient.Key("SPBFUT", "SAME"), QuikReadClient.Key("TQBR", "SAME"));
    }

    /// <summary>A delayed snapshot cannot reopen a canceled order or import a callback from another account.</summary>
    [Fact]
    public void NewerCallbacksWinOverSlowSnapshots()
    {
        var buffer = new QuikOrderEventBuffer();
        var started = buffer.Version;
        var open = Order(Future, "one", OrderState.Open);
        buffer.Add("firm", "account", "client", open with { State = OrderState.Canceled });
        buffer.Add("firm", "other-account", "client", Order(Future, "other", OrderState.Open));
        buffer.Add("firm", "account", "client", Order(Share, "share", OrderState.Open));
        var result = buffer.Merge(Profile.Quik!, Future, [open], started);
        Assert.Equal(OrderState.Canceled, Assert.Single(result).State);
    }

    /// <summary>Late depth snapshots, disconnects and recovery leave other subscriptions independently alive.</summary>
    [Fact]
    public async Task BookEventsBeatSlowReadsAndRecoverAfterDisconnect()
    {
        var client = new FakeReadClient { BlockBook = true };
        var service = new QuikOrderBookService(() => client, TimeSpan.FromMilliseconds(20));
        await using var subscription = await service.SubscribeAsync("terminal", Instrument(Future), TradingEnvironment.Production,
            TradingMarket.MoexFutures, Ct);
        await client.BookStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        client.PublishBook(Book(Future, 120));
        client.BookResponse.TrySetResult(Book(Future, 100));
        client.BlockBook = false;
        client.Price = 120;
        await UntilAsync(() => subscription.Current.Snapshot?.BestBid == 120);
        client.Connected = false;
        await UntilAsync(() => subscription.Current.Connection == OrderBookConnection.Reconnecting);
        Assert.Equal(120, subscription.Current.Snapshot!.BestBid);
        client.Connected = true;
        client.Price = 130;
        await UntilAsync(() => subscription.Current.Connection == OrderBookConnection.Live && subscription.Current.Snapshot?.BestBid == 130);
        await subscription.DisposeAsync();
        Assert.True(client.Disposed);
        Assert.Equal(OrderBookConnection.Closed, subscription.Current.Connection);
    }

    /// <summary>A quiet stream retains its actual last snapshot without reporting a connection failure.</summary>
    [Fact]
    public async Task WaitingForAnotherQuoteDoesNotClearExistingDepth()
    {
        var client = new FakeReadClient();
        var service = new QuikOrderBookService(() => client, TimeSpan.FromMilliseconds(20));
        await using var subscription = await service.SubscribeAsync("terminal", Instrument(Future), TradingEnvironment.Production,
            TradingMarket.MoexFutures, Ct);
        await UntilAsync(() => subscription.Current.Snapshot is not null);
        var snapshot = subscription.Current.Snapshot;
        client.Quiet = true;
        await Task.Delay(100, Ct);
        Assert.Equal(OrderBookConnection.Live, subscription.Current.Connection);
        Assert.Same(snapshot, subscription.Current.Snapshot);
    }

    /// <summary>Only the final listing consumer may unsubscribe, even when catalog/account leases remain.</summary>
    [Fact]
    public async Task SharedLeasesKeepSubscriptionsUntilTheirLastConsumerLeaves()
    {
        var options = new QuikConnectionOptions { Port = 49321 };
        await using var catalog = QuikConnectionPool.Acquire(options);
        var first = QuikConnectionPool.Acquire(options);
        await using var second = QuikConnectionPool.Acquire(options);
        first.RetainBook(Future);
        second.RetainBook(Future);
        Assert.False(first.ReleaseBook(Future));
        await first.DisposeAsync();
        Assert.True(second.ReleaseBook(Future));
        Assert.False(second.ReleaseBook(Future));
    }

    /// <summary>Callback-triggered reads preserve terminal orders and never interpret cancellation as an execution.</summary>
    [Fact]
    public async Task AccountCallbacksReadTerminalStates()
    {
        var client = new FakeReadClient();
        await using var service = new QuikReadOnlyTradingService(Profile, client);
        var pending = service.WatchOrdersAsync(Future, Ct);
        client.Orders = [Order(Future, "one", OrderState.Canceled)];
        client.PublishAccount(TradingStream.Orders);
        Assert.Equal(OrderState.Canceled, Assert.Single(await pending.WaitAsync(TimeSpan.FromSeconds(3), Ct)).State);
        Assert.Empty(await service.FetchOpenOrdersAsync(Future, Ct));
    }

    /// <summary>QUIK sessions connect without touching credential storage and their legacy trade controls remain disabled.</summary>
    [Fact]
    public async Task QuikSessionsUseNoApiKeysAndKeepLegacyControlsDisabled()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "quik-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TradingAccountStore(Path.Combine(directory, "profiles.json"));
            store.Save(new([Profile], new Dictionary<string, Guid> { ["quik"] = Profile.Id }));
            var credentials = new Mock<ICredentialStore>(MockBehavior.Strict);
            var client = new FakeReadClient();
            await using var manager = new TradingSessionManager(store, credentials.Object,
                (profile, keys) => { Assert.Null(keys); return new QuikReadOnlyTradingService(profile, client); });
            var session = await manager.GetAsync(Profile.Id, Ct);
            Assert.False(session.CanExecuteTrades);
            credentials.Verify(c => c.Read(It.IsAny<Guid>()), Times.Never);
            await using var book = new OrderBookViewModel("quik", "SPBFUT:SAME", new QuikOrderBookService(() => new FakeReadClient()),
                manager, TradingEnvironment.Production, TradingMarket.MoexFutures, Instrument(Future), "quik:127.0.0.1:34130");
            book.SelectedAccount = Profile;
            book.Refresh();
            Assert.False(book.CanTrade);
            Assert.False(book.CanSelectMouseTrading);
            Assert.False(book.CanApplyLeverage);
            using var orders = new AccountOrdersViewModel(manager, Profile, Future);
            await orders.BindAsync();
            Assert.False(orders.CancelAllCommand.CanExecute(null));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>Monetary previews use share lots or futures tick value and reject absent metadata.</summary>
    [Fact]
    public async Task MonetaryCalculationUsesInstrumentUnits()
    {
        await using var service = new QuikReadOnlyTradingService(Profile, new FakeReadClient());
        var shares = Instrument(Share) with { LotSize = 10 };
        var calculator = new OrderInputCalculator(service, shares);
        Assert.Equal(2, calculator.ConvertQuantity(2500, 100, OrderQuantityCurrency.Quote));
        Assert.Equal(2000, calculator.OrderNotional(2, 100));
        var future = Instrument(Future) with { PriceStep = 10, TradingRules = new(QuantityUnit.Contracts, PriceQuotation.Points, TickValue: 20, SettlementCurrency: "RUB") };
        calculator = new(service, future);
        Assert.Equal(3, calculator.ConvertQuantity(650, 100, OrderQuantityCurrency.Quote));
        Assert.Equal(600, calculator.OrderNotional(3, 100));
        calculator = new(service, future with { TradingRules = future.TradingRules! with { TickValue = null } });
        Assert.Throws<ArgumentException>(() => calculator.ConvertQuantity(1000, 100, OrderQuantityCurrency.Quote));
    }

    /// <summary>Market and terminal mismatches are rejected before any native reader is created or called.</summary>
    [Fact]
    public async Task RequestsCannotCrossMarketsOrTerminalEndpoints()
    {
        var client = new FakeReadClient();
        await using var service = new QuikReadOnlyTradingService(Profile, client);
        await Assert.ThrowsAsync<ArgumentException>(() => service.FetchInstrumentAsync(Share, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.FetchOpenOrdersAsync(Share, Ct));
        Assert.Equal(0, client.Reads);
        var depth = new QuikOrderBookService(new QuikConnectionOptions { Port = 49322 });
        await Assert.ThrowsAsync<ArgumentException>(() => depth.SubscribeAsync("quik:other:49322", Instrument(Future),
            TradingEnvironment.Production, TradingMarket.MoexFutures, Ct));
    }

    /// <summary>QUIK profiles save without key storage and invalid endpoint edits invalidate old route selections.</summary>
    [Fact]
    public void ProfileEditorDoesNotSaveStaleRoutesOrRequireApiKeys()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "quik-editor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TradingAccountStore(Path.Combine(directory, "profiles.json"));
            var credentials = new Mock<ICredentialStore>(MockBehavior.Strict);
            var verifier = new Mock<IAccountConnectionVerifier>(MockBehavior.Strict);
            using var editor = new TradingAccountsViewModel(store, credentials.Object, verifier.Object);
            editor.Exchange = "quik";
            editor.Name = "Native read account";
            editor.QuikRoute = new("firm", "client", "account", 0, "TQBR");
            Assert.True(editor.CanSave);
            editor.QuikPortText = "invalid";
            Assert.False(editor.CanSave);
            Assert.Null(editor.QuikRoute);
            editor.QuikPortText = "34130";
            editor.QuikRoute = new("firm", "client", "account", 0, "TQBR");
            editor.Save(null);
            var profile = Assert.Single(store.Load().Profiles);
            Assert.Equal("quik", profile.Exchange);
            Assert.Equal(TradingMarket.MoexShares, profile.Market);
            Assert.NotNull(profile.Quik);
            credentials.VerifyNoOtherCalls();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>Creates deterministic listing metadata with no dependency on the live terminal.</summary>
    private static Instrument Instrument(InstrumentKey key) => new(key, $"{key.Board}:{key.SecurityId}", "Instrument", "RUB", 1, 1,
        TradingRules: new(key == Future ? QuantityUnit.Contracts : QuantityUnit.Lots,
            key == Future ? PriceQuotation.Points : PriceQuotation.CurrencyPerUnit, SettlementCurrency: "RUB"));

    /// <summary>Creates detached depth for event ordering and recovery scenarios.</summary>
    private static OrderBookSnapshot Book(InstrumentKey key, decimal price) => new(
        new($"{key.Board}:{key.SecurityId}", "contracts", "RUB", 1, Instrument(key)), [new(price, 10)], [new(price + 1, 10)], DateTimeOffset.UtcNow);

    /// <summary>Creates account-state samples with explicit exchange state.</summary>
    private static OrderSnapshot Order(InstrumentKey key, string id, OrderState state) => new(new(key, id), null,
        OrderSide.Buy, OrderType.Limit, state, 1, 0, 1, 100, null, null, DateTimeOffset.UtcNow);

    /// <summary>Waits for an asynchronous state transition with a short bounded deadline.</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Expected state transition did not occur.");
            await Task.Delay(10, Ct);
        }
    }

    private sealed class FakeReadClient : IQuikReadClient
    {
        public event Action<OrderBookSnapshot>? BookChanged;
        public event Action<TradingStream>? AccountChanged;
        public bool Connected = true;
        public bool BlockBook;
        public bool Quiet;
        public bool Disposed;
        public int Reads;
        public decimal Price = 100;
        public IReadOnlyList<OrderSnapshot> Orders = [];
        public TaskCompletionSource BookStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<OrderBookSnapshot> BookResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Raises a detached public callback.</summary>
        public void PublishBook(OrderBookSnapshot book) => BookChanged?.Invoke(book);

        /// <summary>Raises a private-data notification without a mutation API.</summary>
        public void PublishAccount(TradingStream stream) => AccountChanged?.Invoke(stream);

        /// <summary>Reports simulated broker health.</summary>
        public Task<bool> ConnectedAsync(CancellationToken ct) => Task.FromResult(Connected);

        /// <summary>Supplies deterministic metadata.</summary>
        public Task<Instrument> InstrumentAsync(InstrumentKey key, CancellationToken ct) { Reads++; return Task.FromResult(Instrument(key)); }

        /// <summary>Supports a slow snapshot racing with a newer callback.</summary>
        public async Task<OrderBookSnapshot> BookAsync(InstrumentKey key, CancellationToken ct)
        {
            if (Quiet) throw new QuikQuoteWaitTimeoutException();
            Reads++;
            BookStarted.TrySetResult();
            return BlockBook ? await BookResponse.Task.WaitAsync(ct) : Book(key, Price);
        }

        /// <summary>Releases only this fake reader's subscription.</summary>
        public Task ReleaseBookAsync(InstrumentKey key) => Task.CompletedTask;

        /// <summary>Provides one explicit terminal routing choice.</summary>
        public Task<IReadOnlyList<QuikRoutingChoice>> RoutesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<QuikRoutingChoice>>([new("firm", "client", "account", 0, "SPBFUT")]);

        /// <summary>Returns the current simulated terminal order table.</summary>
        public Task<IReadOnlyList<OrderSnapshot>> OrdersAsync(TradingAccountProfile profile, InstrumentKey key, CancellationToken ct) => Task.FromResult(Orders);

        /// <summary>Returns an empty execution table rather than fabricated fills.</summary>
        public Task<IReadOnlyList<ExecutionSnapshot>> TradesAsync(TradingAccountProfile profile, InstrumentKey key, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExecutionSnapshot>>([]);

        /// <summary>Returns confirmed simulated account data.</summary>
        public Task<AccountSnapshot> BalanceAsync(TradingAccountProfile profile, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(new AccountSnapshot(QuikReadClient.Context(profile), [], [], [], DateTimeOffset.UtcNow));
        }

        /// <summary>Returns a native position only when explicitly requested by the account reader.</summary>
        public Task<IReadOnlyList<PositionSnapshot>> PositionsAsync(TradingAccountProfile profile, InstrumentKey key, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PositionSnapshot>>([new(key, PositionSide.Long, 2, QuantityUnit.Contracts)]);

        /// <summary>Records reader disposal without any broker operation.</summary>
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
