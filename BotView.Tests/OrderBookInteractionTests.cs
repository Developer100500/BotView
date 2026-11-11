using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BotView.ViewModels;
using BotView.Views;
using BotView.Windows;
using Moq;

namespace BotView.Tests;

public sealed class OrderBookInteractionTests
{
    /// <summary>Checks both sides and every kind of empty depth without contacting an exchange.</summary>
    [Theory]
    [InlineData(OrderBookSide.Bid, true, 0)]
    [InlineData(OrderBookSide.Ask, true, 2)]
    [InlineData(OrderBookSide.Bid, false, 0)]
    [InlineData(OrderBookSide.Ask, false, 0)]
    public async Task MouseTradingUsesClickedPriceSideAndDraftOptions(OrderBookSide side, bool hasDepth, int amount)
    {
        await using var fixture = new Fixture(futures: true);
        await fixture.ReadyAsync();
        fixture.Book.MouseTrading = true;
        fixture.Book.PostOnly = true;
        fixture.Book.ReduceOnly = true;
        fixture.Book.PositionTarget = "Short";
        fixture.Book.AmountText = "2";
        var row = new OrderBookRow(101.25m, amount, 0, side, hasDepth, false, 0);

        await fixture.Book.ClickLevelAsync(row);

        var request = Assert.Single(fixture.Sent);
        Assert.Equal(side == OrderBookSide.Bid ? OrderSide.Buy : OrderSide.Sell, request.Side);
        Assert.Equal(101.25m, request.Price);
        Assert.Equal(2, request.Quantity);
        Assert.True(request.PostOnly);
        Assert.True(request.ReduceOnly);
        Assert.Equal(PositionSide.Short, request.PositionSide);
        Assert.True(fixture.Book.MouseTrading);
    }

    /// <summary>Quote Quantity is converted using the clicked bucket, not the old draft price.</summary>
    [Fact]
    public async Task QuoteBudgetUsesClickedGroupedPrice()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        fixture.Book.Multiplier = 10;
        fixture.Book.PriceText = "100";
        fixture.Book.AmountText = "1";
        fixture.Book.QuantityCurrency = OrderQuantityCurrency.Quote;
        fixture.Book.AmountText = "50";
        fixture.Book.MouseTrading = true;

        await fixture.Book.ClickLevelAsync(Row(80.1m, OrderBookSide.Bid));

        var request = Assert.Single(fixture.Sent);
        Assert.Equal(80.1m, request.Price);
        Assert.Equal(.624m, request.Quantity);
        Assert.Equal("50", fixture.Book.AmountText);
    }

    /// <summary>Neutral spread and the ordinary click mode select a price without placing an order.</summary>
    [Fact]
    public async Task SpreadAndDisabledModeOnlySelectPrice()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        Assert.False(fixture.Book.MouseTrading);
        await fixture.Book.ClickLevelAsync(Row(95, OrderBookSide.Bid));
        Assert.Equal("95", fixture.Book.PriceText);
        fixture.Book.MouseTrading = true;
        await fixture.Book.ClickLevelAsync(Row(100, OrderBookSide.Spread));
        Assert.Equal("100", fixture.Book.PriceText);
        Assert.Empty(fixture.Sent);
    }

    /// <summary>A delayed response, a double click and draft edits cannot duplicate or mutate a sent request.</summary>
    [Fact]
    public async Task InFlightAndDoubleClicksDoNotSendAgain()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        var response = new TaskCompletionSource<OrderSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Adapter.Setup(s => s.CreateLimitOrderAsync(It.IsAny<LimitOrderRequest>(), It.IsAny<CancellationToken>()))
            .Returns((LimitOrderRequest request, CancellationToken _) =>
            {
                fixture.Sent.Add(request);
                return response.Task;
            });
        fixture.Book.MouseTrading = true;
        var sending = fixture.Book.ClickLevelAsync(Row(90, OrderBookSide.Bid));
        await fixture.Book.ClickLevelAsync(Row(110, OrderBookSide.Ask));
        Assert.Equal("90", fixture.Book.PriceText);
        fixture.Book.PriceText = "999";
        fixture.Book.AmountText = "999";
        await UntilAsync(() => fixture.Sent.Count == 1);
        response.SetResult(fixture.Order());
        await sending;
        await fixture.Book.ClickLevelAsync(Row(110, OrderBookSide.Ask), clickCount: 2);
        Assert.Single(fixture.Sent);
        Assert.Equal(90, fixture.Sent[0].Price);
        Assert.Equal(1, fixture.Sent[0].Quantity);
    }

    /// <summary>Missing fresh public prices, an invalid draft or retired keys must never initiate an order.</summary>
    [Theory]
    [InlineData("stale")]
    [InlineData("reconnecting")]
    [InlineData("quantity")]
    [InlineData("account")]
    public async Task UnavailableInputsBlockMouseOrdersWithoutDisarming(string failure)
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        fixture.Book.MouseTrading = true;
        if (failure == "stale") fixture.Update = fixture.Update with { Snapshot = fixture.Update.Snapshot! with { ReceivedAt = DateTimeOffset.UtcNow.AddSeconds(-30) } };
        if (failure == "reconnecting") fixture.Update = fixture.Update with { Connection = OrderBookConnection.Reconnecting };
        if (failure == "quantity") fixture.Book.AmountText = "0";
        if (failure == "account") fixture.Keys.Delete(fixture.Profile.Id);

        await fixture.Book.ClickLevelAsync(Row(90, OrderBookSide.Bid));

        Assert.Empty(fixture.Sent);
        Assert.NotEmpty(fixture.Book.TradeStatus);
        Assert.True(fixture.Book.MouseTrading);
    }

    /// <summary>Market cannot arm mouse trading; account changes and reconnects retain the explicit choice.</summary>
    [Fact]
    public async Task ModePersistsAcrossAccountChangesButNotMarket()
    {
        await using var fixture = new Fixture();
        await fixture.ReadyAsync();
        fixture.Book.MouseTrading = true;
        fixture.Book.SelectedAccount = null;
        Assert.True(fixture.Book.MouseTrading);
        fixture.Book.SelectedAccount = fixture.Profile;
        await fixture.ReadyAsync();
        Assert.True(fixture.Book.MouseTrading);
        using (var keys = new AccountCredentials("replacement", "fake")) fixture.Keys.Write(fixture.Profile.Id, keys);
        fixture.Book.Refresh();
        await fixture.ReadyAsync();
        Assert.True(fixture.Book.MouseTrading);
        fixture.Book.SelectedOrderType = OrderType.Market;
        Assert.False(fixture.Book.MouseTrading);
        Assert.False(fixture.Book.CanSelectMouseTrading);
        fixture.Book.MouseTrading = true;
        Assert.False(fixture.Book.MouseTrading);
        fixture.Book.SelectedOrderType = OrderType.Limit;
        Assert.False(fixture.Book.MouseTrading);
    }

    /// <summary>An order window keeps shared state and cancellation after its originating book closes.</summary>
    [Fact]
    public async Task OrdersRemainIndependentAndCancelAFrozenBatch()
    {
        await using var fixture = new Fixture();
        var open = new List<OrderSnapshot> { fixture.Order() };
        fixture.Adapter.Setup(s => s.FetchOpenOrdersAsync(fixture.Key, It.IsAny<CancellationToken>())).ReturnsAsync(() => open.ToArray());
        fixture.Adapter.Setup(s => s.FetchMyTradesAsync(fixture.Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ExecutionSnapshot("fill", fixture.Order().Reference, OrderSide.Buy, .25m, 100, DateTimeOffset.UtcNow, new(.01m, "USDT")) });
        await fixture.ReadyAsync();
        fixture.Adapter.Setup(s => s.CancelOrderAsync(It.IsAny<OrderReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrderReference reference, CancellationToken _) => fixture.Order() with { Reference = reference, State = OrderState.Canceled });
        using var orders = new AccountOrdersViewModel(fixture.Manager, fixture.Profile, fixture.Key);
        await orders.BindAsync();
        await fixture.Book.DisposeAsync();
        await UntilAsync(() =>
        {
            orders.Refresh();
            return orders.Orders.Count == 1 && orders.Executions.Count == 1;
        });
        orders.SelectedOrder = orders.Orders[0];
        Assert.True(orders.CancelSelectedCommand.CanExecute(null));
        Assert.Equal(fixture.Profile.Id, orders.Profile.Id);
        orders.ConfirmCancelAllAsync = batch =>
        {
            Assert.Single(batch.Orders);
            Assert.Equal(fixture.Profile.Environment, batch.Account.Environment);
            open.Add(fixture.Order() with { Reference = new(fixture.Key, "new") });
            return Task.FromResult(true);
        };

        await orders.CancelAllAsync();

        fixture.Adapter.Verify(s => s.CancelOrderAsync(It.Is<OrderReference>(r => r.ExchangeOrderId == "1"), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Adapter.Verify(s => s.CancelOrderAsync(It.Is<OrderReference>(r => r.ExchangeOrderId == "new"), It.IsAny<CancellationToken>()), Times.Never);
        orders.Dispose();
        fixture.Adapter.Verify(s => s.DisposeAsync(), Times.Never);
    }

    /// <summary>Replacing keys reuses the pinned profile; removing it clears rows and disables cancellation.</summary>
    [Fact]
    public async Task OrdersRebindOriginalProfileAndRejectDeletedProfile()
    {
        await using var fixture = new Fixture();
        using var orders = new AccountOrdersViewModel(fixture.Manager, fixture.Profile, fixture.Key);
        await orders.BindAsync();
        Assert.True(orders.CancelAllCommand.CanExecute(null));
        using (var keys = new AccountCredentials("replacement", "fake")) fixture.Keys.Write(fixture.Profile.Id, keys);
        orders.Refresh();
        await UntilAsync(() => { orders.Refresh(); return orders.CancelAllCommand.CanExecute(null); });
        Assert.Equal(2, fixture.Connections);
        fixture.Store.Save(new([], new Dictionary<string, Guid>()));
        orders.Refresh();
        Assert.Empty(orders.Orders);
        Assert.False(orders.CancelAllCommand.CanExecute(null));
        Assert.Contains("original account", orders.ConnectionStatus);
    }

    /// <summary>Exercises actual ladder hit testing in all columns and renders the compact minimum-size layout.</summary>
    [Fact]
    public void CompactWindowHasClickableColumnsAndBoundedSelectors()
    {
        OnSta(() =>
        {
            var subscription = new Mock<IOrderBookSubscription>();
            subscription.SetupGet(s => s.Current).Returns(new OrderBookUpdate(Book(), OrderBookConnection.Live, "Connected"));
            var feed = new Mock<IOrderBookService>();
            feed.Setup(s => s.SubscribeAsync("binance", "BTC/USDT", It.IsAny<CancellationToken>())).ReturnsAsync(subscription.Object);
            var window = new OrderBookWindow("binance", "BTC/USDT", feed.Object);
            var vm = (OrderBookViewModel)window.DataContext;
            vm.StartAsync().GetAwaiter().GetResult();
            vm.Refresh();
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(320, 440));
            content.Arrange(new Rect(0, 0, 320, 440));
            content.UpdateLayout();
            var ladder = (OrderBookLadderControl)window.FindName("Ladder");
            var actions = (FrameworkElement)window.FindName("TradeActions");
            Assert.True(actions.TransformToAncestor(content).Transform(new Point(0, actions.ActualHeight)).Y <= 440);
            var bitmap = new RenderTargetBitmap(320, 440, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            Assert.True(ladder.ActualHeight >= 4 * OrderBookLadderControl.RowHeight);
            var price = vm.GetRows((int)(ladder.ActualHeight / OrderBookLadderControl.RowHeight))[0].Price;
            foreach (var column in new[] { .17, .51, .84 })
            {
                vm.PriceText = "";
                ladder.ClickAtAsync(new Point(ladder.ActualWidth * column, 5), 1).GetAwaiter().GetResult();
                Assert.Equal(price.ToString(System.Globalization.CultureInfo.InvariantCulture), vm.PriceText);
            }

            Assert.True(((ComboBox)window.FindName("VolumeCurrencySelector")).Width <= 140);
            Assert.True(((ComboBox)window.FindName("AccountSelector")).ActualWidth <= 240);
            Assert.Null(FindVisual<DataGrid>(content));
            ((Expander)window.FindName("DepthSettings")).IsExpanded = true;
            content.UpdateLayout();
            Assert.True(ladder.ActualHeight >= 4 * OrderBookLadderControl.RowHeight);
            Assert.True(actions.TransformToAncestor(content).Transform(new Point(0, actions.ActualHeight)).Y <= 440);
            ((Expander)window.FindName("DepthSettings")).IsExpanded = false;
            content.UpdateLayout();
            if (Environment.GetEnvironmentVariable("BOTVIEW_COMPACT_PREVIEW") is { Length: > 0 } path)
            {
                using var output = File.Create(path);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(output);
            }

            window.Close();
            window.CleanupTask.GetAwaiter().GetResult();
        });
    }

    /// <summary>Checks registry identity, main-window ownership, standalone sizing and coordinated closure.</summary>
    [Fact]
    public void RegistryReusesOnlyMatchingAccountAndInstrument()
    {
        OnSta(() =>
        {
            var fixture = new Fixture();
            var owner = new Window { Width = 1, Height = 1, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
            owner.Show();
            var registry = new AccountOrdersWindowRegistry(owner, fixture.Manager);
            var first = registry.Show(fixture.Profile, fixture.Key);
            Assert.Same(first, registry.Show(fixture.Profile, fixture.Key));
            var otherPair = registry.Show(fixture.Profile, fixture.Key with { SecurityId = "ETH/USDT" });
            var otherAccount = registry.Show(fixture.Profile with { Id = Guid.NewGuid() }, fixture.Key);
            Assert.NotSame(first, otherPair);
            Assert.NotSame(first, otherAccount);
            Assert.Same(owner, first.Owner);
            var content = (FrameworkElement)first.Content;
            ((Grid)content).Background = first.Background;
            content.Measure(new Size(700, 320));
            content.Arrange(new Rect(0, 0, 700, 320));
            content.UpdateLayout();
            Assert.True(((DataGrid)first.FindName("OrdersTable")).ActualHeight > 100);
            if (Environment.GetEnvironmentVariable("BOTVIEW_ORDERS_PREVIEW") is { Length: > 0 } path)
            {
                var bitmap = new RenderTargetBitmap(700, 320, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                using var output = File.Create(path);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(output);
            }
            Assert.True(first.IsVisible);
            registry.CloseAll();
            Assert.False(first.IsVisible);
            Assert.False(otherPair.IsVisible);
            Assert.False(otherAccount.IsVisible);
            owner.Close();
            fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });
    }

    /// <summary>Builds a stable row whose identity is independent of subsequent public feed updates.</summary>
    private static OrderBookRow Row(decimal price, OrderBookSide side) => new(price, 0, 0, side, true, false, 0);

    /// <summary>Provides a public snapshot with both sides and a visible neutral spread.</summary>
    private static OrderBookSnapshot Book() => new(new("BTC/USDT", "BTC", "USDT", .01m),
        [new(100m, 2)], [new(100.03m, 2)], DateTimeOffset.UtcNow);

    /// <summary>Waits on observable state rather than relying on worker scheduling.</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }

    /// <summary>Finds controls in the real visual tree for layout assertions.</summary>
    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is { } nested) return nested;
        }

        return null;
    }

    /// <summary>Runs WPF work on an isolated STA thread and propagates assertion failures.</summary>
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ProfileStore : ITradingAccountStore, ITradingProfileNotifications
    {
        public TradingAccountsSettings Settings = new([], new Dictionary<string, Guid>());
        public event Action? Changed;

        /// <summary>Returns only fake profile metadata.</summary>
        public TradingAccountsSettings Load() => Settings;

        /// <summary>Notifies the actual session manager about profile removal.</summary>
        public void Save(TradingAccountsSettings settings)
        {
            Settings = settings;
            Changed?.Invoke();
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public TradingAccountProfile Profile { get; }
        public InstrumentKey Key { get; }
        public ProfileStore Store { get; } = new();
        public InMemoryCredentialStore Keys { get; } = new();
        public Mock<ITradingService> Adapter { get; } = new();
        public List<LimitOrderRequest> Sent { get; } = [];
        public TradingSessionManager Manager { get; }
        public OrderBookViewModel Book { get; }
        public OrderBookUpdate Update;
        public int Connections;

        /// <summary>Injects an entirely offline account, precision rules and public feed into production view models.</summary>
        public Fixture(bool futures = false)
        {
            Profile = new(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet,
                futures ? TradingMarket.UsdtFutures : TradingMarket.Spot);
            Key = new("binance", futures ? "futures" : "spot", "", futures ? "BTC/USDT:USDT" : "BTC/USDT");
            Store.Settings = new([Profile], new Dictionary<string, Guid> { ["binance"] = Profile.Id });
            using (var credentials = new AccountCredentials("fake", "fake")) Keys.Write(Profile.Id, credentials);
            var account = new TradingAccountContext(Profile.Id.ToString(), "Fake", "binance", Profile.Environment, Profile.Market);
            Adapter.SetupGet(s => s.Account).Returns(account);
            Adapter.Setup(s => s.FetchInstrumentAsync(It.IsAny<InstrumentKey>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((InstrumentKey key, CancellationToken _) => new Instrument(key, key.SecurityId, "Fake",
                    TradingRules: new(futures ? QuantityUnit.Contracts : QuantityUnit.BaseAsset, PriceQuotation.CurrencyPerUnit, ContractSize: 1)));
            Adapter.Setup(s => s.FetchBalanceAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AccountSnapshot(account, [new("USDT", 1000, 1000, 0)], [], [], DateTimeOffset.UtcNow));
            Adapter.Setup(s => s.FetchOpenOrdersAsync(It.IsAny<InstrumentKey>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<OrderSnapshot>());
            Adapter.Setup(s => s.FetchMyTradesAsync(It.IsAny<InstrumentKey>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ExecutionSnapshot>());
            Adapter.Setup(s => s.FetchPositionsAsync(It.IsAny<InstrumentKey>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<PositionSnapshot>());
            Adapter.Setup(s => s.FetchPositionModeAsync(It.IsAny<InstrumentKey>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Adapter.Setup(s => s.AmountToPrecision(It.IsAny<InstrumentKey>(), It.IsAny<decimal>())).Returns((InstrumentKey _, decimal v) => decimal.Floor(v * 1000) / 1000);
            Adapter.Setup(s => s.PriceToPrecision(It.IsAny<InstrumentKey>(), It.IsAny<decimal>())).Returns((InstrumentKey _, decimal v) => v);
            Adapter.Setup(s => s.CreateLimitOrderAsync(It.IsAny<LimitOrderRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((LimitOrderRequest request, CancellationToken _) =>
                {
                    Sent.Add(request);
                    return Order();
                });
            Adapter.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);
            Manager = new(Store, Keys, (_, _) => { Interlocked.Increment(ref Connections); return Adapter.Object; });
            Update = new(OrderBookInteractionTests.Book(), OrderBookConnection.Live, "Connected");
            var subscription = new Mock<IOrderBookSubscription>();
            subscription.SetupGet(s => s.Current).Returns(() => Update);
            var feed = new Mock<IOrderBookService>();
            feed.Setup(s => s.SubscribeAsync("binance", Key.SecurityId, TradingEnvironment.Testnet, It.IsAny<CancellationToken>())).ReturnsAsync(subscription.Object);
            feed.Setup(s => s.SubscribeAsync("binance", Key.SecurityId, TradingEnvironment.Testnet, TradingMarket.UsdtFutures, It.IsAny<CancellationToken>())).ReturnsAsync(subscription.Object);
            Book = new("binance", Key.SecurityId, feed.Object, Manager, TradingEnvironment.Testnet, Profile.Market);
        }

        /// <summary>Returns a stable exchange order without introducing a real trading request.</summary>
        public OrderSnapshot Order() => new(new(Key, "1"), null, OrderSide.Buy, OrderType.Limit, OrderState.Open,
            1, 0, 1, 100, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        /// <summary>Waits for authentication and public depth, then sets a valid native-unit draft.</summary>
        public async Task ReadyAsync()
        {
            await UntilAsync(() => Book.AccountStatus == "Connected" && Book.IsTradeEditable);
            await Book.StartAsync();
            Book.Refresh();
            Book.PriceText = "100";
            Book.AmountText = "1";
        }

        /// <summary>Ends only fake workers and clears fake in-memory credentials.</summary>
        public async ValueTask DisposeAsync()
        {
            await Book.DisposeAsync();
            await Manager.DisposeAsync();
            Keys.Dispose();
        }
    }
}
