using BotView.Services.Providers;
using System.Collections;
using System.IO;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BotView.Interfaces;
using BotView.Models;
using BotView.Services;
using BotView.ViewModels;
using BotView.Views;

namespace BotView.Tests;

public sealed class OrderBookTests
{
    private static OrderBookSnapshot Book(decimal bid = 100m, decimal ask = 100.03m) => new(
        new("BTC/USDT", "BTC", "USDT", .01m),
        Array.AsReadOnly(new[] { new OrderBookLevel(bid, 2), new(bid - .02m, 3) }),
        Array.AsReadOnly(new[] { new OrderBookLevel(ask, 4), new(ask + .02m, 5) }), DateTimeOffset.UtcNow);

    [Fact]
    public void LadderHasDescendingExactPricesNeutralSpreadAndUnknownDepth()
    {
        var rows = OrderBookLadder.Build(Book(), 1, 100.02m, 13);
        Assert.Equal(13, rows.Count);
        for (var i = 1; i < rows.Count; i++) Assert.Equal(.01m, rows[i - 1].Price - rows[i].Price);
        Assert.Equal(OrderBookSide.Ask, rows.Single(r => r.Price == 100.03m).Side);
        Assert.Equal(OrderBookSide.Bid, rows.Single(r => r.Price == 100m).Side);
        Assert.All(rows.Where(r => r.Price is > 100m and < 100.03m), r => Assert.Equal(OrderBookSide.Spread, r.Side));
        Assert.Equal(0, rows.Single(r => r.Price == 99.99m).Amount);
        Assert.True(rows.Single(r => r.Price == 99.99m).HasDepth);
        Assert.False(rows.Single(r => r.Price == 99.96m).HasDepth);
        Assert.False(rows.Single(r => r.Price == 100.08m).HasDepth);
        Assert.Equal(2, rows.Count(r => r.IsBest));
    }

    [Fact]
    public void QuoteVolumeUsesNotionalValueAndPreservesGroupedCosts()
    {
        var book = new OrderBookSnapshot(new("ETH/USDT", "ETH", "USDT", 1m),
            new[] { new OrderBookLevel(100, 2) }, new[] { new OrderBookLevel(200, 1) }, DateTimeOffset.UtcNow);
        var baseRows = OrderBookLadder.Build(book, 1, 150, 110);
        var quoteRows = OrderBookLadder.Build(book, 1, 150, 110, volumeCurrency: OrderBookVolumeCurrency.Quote);
        Assert.Equal(.5, baseRows.Single(r => r.Price == 200).VolumeFraction);
        Assert.Equal(1, quoteRows.Single(r => r.Price == 200).VolumeFraction);
        Assert.Equal(1, quoteRows.Single(r => r.Price == 100).VolumeFraction);
        var fixedRows = OrderBookLadder.Build(book, 1, 150, 110, 400m, OrderBookVolumeCurrency.Quote);
        Assert.Equal(.5, fixedRows.Single(r => r.Price == 100).VolumeFraction);
        var grouped = OrderBookLadder.Build(Book(), 10, 100m, 10, 1000m, OrderBookVolumeCurrency.Quote);
        var ask = grouped.Single(r => r.Price == 100.1m);
        Assert.Equal((double)((100.03m * 4 + 100.05m * 5) / 1000m), ask.VolumeFraction);
    }

    [Fact]
    public async Task SwitchingVolumeCurrencyRetainsSeparateThresholdsAndUpdatesUnits()
    {
        await using var vm = new OrderBookViewModel("binance", "BTC/USDT", new FakeService(new FakeSubscription(Book())));
        await vm.StartAsync();
        vm.Refresh();
        vm.MaxVolumeText = "8";
        vm.VolumeCurrency = OrderBookVolumeCurrency.Quote;
        Assert.Equal("", vm.MaxVolumeText);
        Assert.Contains("USDT", vm.MaxVolumeHeader);
        vm.MaxVolumeText = "800";
        Assert.Equal(.25, vm.GetRows(13).Single(r => r.Price == 100m).VolumeFraction);
        vm.VolumeCurrency = OrderBookVolumeCurrency.Base;
        Assert.Equal("8", vm.MaxVolumeText);
        Assert.Contains("BTC", vm.MaxVolumeHeader);
        vm.VolumeCurrency = OrderBookVolumeCurrency.Quote;
        Assert.Equal("800", vm.MaxVolumeText);
    }

    [Fact]
    public void FixedVolumeScaleRemainsStableAcrossViewportsAndCapsLargeAmounts()
    {
        var book = Book();
        var rows = OrderBookLadder.Build(book, 1, 100.02m, 13, 4m);
        Assert.Equal(.5, rows.Single(r => r.Price == 100m).VolumeFraction);
        Assert.Equal(1, rows.Single(r => r.Price == 100.03m).VolumeFraction);
        Assert.Equal(1, rows.Single(r => r.Price == 100.05m).VolumeFraction);
        var scrolled = OrderBookLadder.Build(book, 1, 99.99m, 7, 4m);
        Assert.Equal(.5, scrolled.Single(r => r.Price == 100m).VolumeFraction);
        var tinyThreshold = OrderBookLadder.Build(book, 1, 100.02m, 13, .0000000000000000000000000001m);
        Assert.All(tinyThreshold.Where(r => r.Amount > 0), r => Assert.Equal(1, r.VolumeFraction));
        Assert.All(rows.Where(r => r.Amount == 0), r => Assert.Equal(0, r.VolumeFraction));
    }

    [Fact]
    public async Task VolumeScaleInputAcceptsDecimalsPreservesLastValidValueAndRestoresAuto()
    {
        await using var vm = new OrderBookViewModel("binance", "BTC/USDT", new FakeService(new FakeSubscription(Book())));
        await vm.StartAsync();
        vm.Refresh();
        Assert.Equal(.4, vm.GetRows(13).Single(r => r.Price == 100m).VolumeFraction);
        vm.MaxVolumeText = "8,0";
        Assert.False(vm.HasVolumeScaleError);
        Assert.Equal(.25, vm.GetRows(13).Single(r => r.Price == 100m).VolumeFraction);
        foreach (var invalid in new[] { "0", "-2", "abc" })
        {
            vm.MaxVolumeText = invalid;
            Assert.True(vm.HasVolumeScaleError);
            Assert.Equal(.25, vm.GetRows(13).Single(r => r.Price == 100m).VolumeFraction);
        }
        vm.MaxVolumeText = " ";
        Assert.False(vm.HasVolumeScaleError);
        Assert.Equal(.4, vm.GetRows(13).Single(r => r.Price == 100m).VolumeFraction);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    public void GroupingRoundsBidsDownAsksUpAndPreservesAmounts(int multiplier)
    {
        var book = Book(100.04m, 100.06m);
        var rows = OrderBookLadder.Build(book, multiplier, 100.05m, 10);
        var bids = rows.Where(r => r.Side == OrderBookSide.Bid).ToArray();
        var asks = rows.Where(r => r.Side == OrderBookSide.Ask).ToArray();
        Assert.Equal(5m, bids.Sum(r => r.Amount));
        Assert.Equal(9m, asks.Sum(r => r.Amount));
        Assert.Equal(book.Bids.Sum(l => l.Price * l.Amount), bids.Sum(r => r.Cost));
        Assert.Equal(book.Asks.Sum(l => l.Price * l.Amount), asks.Sum(r => r.Cost));
        Assert.True(bids.Max(r => r.Price) <= book.BestBid);
        Assert.True(asks.Min(r => r.Price) >= book.BestAsk);
        Assert.All(rows, r => Assert.Equal(0m, r.Price % (.01m * multiplier)));
    }

    [Theory]
    [InlineData(.00000001, 4, "0.00000001")]
    [InlineData(2, 2, "0.01")]
    [InlineData(0, 2, "1")]
    [InlineData(-2, 2, "100")]
    public void TickSizeRespectsCcxtPrecisionMode(double precision, int mode, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ProOrderBookClient.GetTickSize(precision, mode));

    [Fact]
    public void UnsupportedPrecisionIsExplicitRatherThanInventingATick()
    {
        Assert.Throws<NotSupportedException>(() => ProOrderBookClient.GetTickSize(null, 4));
        Assert.Throws<NotSupportedException>(() => ProOrderBookClient.GetTickSize(5, 3));
        Assert.Throws<NotSupportedException>(() => ProOrderBookClient.GetTickSize(0, 4));
    }

    [Fact]
    public void SnapshotCopiesSortsAndFiltersMutableCcxtLevels()
    {
        var input = new ArrayList { new object[] { 99d, 2d }, new object[] { 100d, 3d },
            new object[] { double.NaN, 1d }, new object[] { 100d, 0d } };
        var copy = ProOrderBookClient.CopyLevels(input, true);
        ((object[])input[1]!)[1] = 900d;
        input.Clear();
        Assert.Equal(new[] { 100m, 99m }, copy.Select(l => l.Price));
        Assert.Equal(3m, copy[0].Amount);
        Assert.Throws<NotSupportedException>(() => ((IList<OrderBookLevel>)copy).Add(new(1, 1)));
    }

    [Fact]
    public void RealProOrderBookSideCanBeCopiedWithoutSharingItsCache()
    {
        var side = new ccxt.pro.Asks(new List<object>(), 100);
        side.store(100.01, 2.0);
        side.store(100.03, 4.0);
        var snapshot = ProOrderBookClient.CopyLevels(side.Copy(), false);
        side.store(100.01, 900.0);
        Assert.Equal(new[] { 100.01m, 100.03m }, snapshot.Select(l => l.Price));
        Assert.Equal(2m, snapshot[0].Amount);
    }

    [Fact]
    public void ProSettingsUseTheListTypeRequiredByCcxt()
    {
        var client = new ccxt.pro.binance(ProOrderBookClient.CreateSettings());
        var markets = (Dictionary<string, object>)client.options["fetchMarkets"];
        Assert.Equal("spot", Assert.Single(client.safeList(markets, "types")));
        Assert.Equal("spot", client.options["defaultType"]);
    }

    [Fact]
    public async Task SubscriptionRunsInBackgroundAndDisposesDuringPendingWatch()
    {
        var fake = new FakeClient();
        var service = new OrderBookService(_ => fake);
        var subscription = await service.SubscribeAsync("binance", "BTC/USDT", TestContext.Current.CancellationToken);
        await fake.Watching.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fake.LoadedOnThreadPool);
        await subscription.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fake.Disposed);
        Assert.Equal(OrderBookConnection.Closed, subscription.Current.Connection);
        await subscription.DisposeAsync();
    }

    [Fact]
    public async Task ReconnectRetainsSnapshotAndClosesOldClient()
    {
        var first = new FakeClient();
        var second = new FakeClient();
        var delayed = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;
        var service = new OrderBookService(_ => Interlocked.Increment(ref created) == 1 ? first : second,
            async (duration, ct) => { delayed.TrySetResult(duration); await continueRetry.Task.WaitAsync(ct); });
        await using var subscription = await service.SubscribeAsync("binance", "BTC/USDT", TestContext.Current.CancellationToken);
        var book = Book();
        await first.Feed.Writer.WriteAsync(book, TestContext.Current.CancellationToken);
        await Until(() => subscription.Current.Connection == OrderBookConnection.Live);
        first.Feed.Writer.Complete(new IOException("Connection lost"));
        Assert.Equal(TimeSpan.FromSeconds(1), await delayed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(book, subscription.Current.Snapshot);
        Assert.Equal(OrderBookConnection.Reconnecting, subscription.Current.Connection);
        Assert.True(first.Disposed);
        continueRetry.SetResult();
        var next = Book(101, 101.03m);
        await second.Feed.Writer.WriteAsync(next, TestContext.Current.CancellationToken);
        await Until(() => ReferenceEquals(subscription.Current.Snapshot, next));
        Assert.Equal(OrderBookConnection.Live, subscription.Current.Connection);
    }

    [Fact]
    public async Task UnsupportedPairDoesNotRetry()
    {
        var client = new FakeClient { LoadError = new NotSupportedException("Pair unavailable") };
        var creates = 0;
        var service = new OrderBookService(_ => { Interlocked.Increment(ref creates); return client; });
        await using var subscription = await service.SubscribeAsync("binance", "BTC/USDT", TestContext.Current.CancellationToken);
        await Until(() => client.Disposed);
        Assert.Equal(1, creates);
        Assert.Equal(OrderBookConnection.Error, subscription.Current.Connection);
        Assert.Contains("unavailable", subscription.Current.Message);
    }

    [Fact]
    public async Task UiCoalescesSnapshotsAndManualScrollDoesNotFollowPrice()
    {
        var subscription = new FakeSubscription(Book());
        await using var vm = new OrderBookViewModel("binance", "BTC/USDT", new FakeService(subscription));
        await vm.StartAsync();
        vm.Refresh();
        var initial = vm.GetRows(11);
        vm.Scroll(3);
        var scrolled = vm.GetRows(11);
        Assert.False(vm.IsFollowing);
        Assert.Equal(.03m, scrolled[0].Price - initial[0].Price);
        subscription.Current = new(Book(105, 105.03m), OrderBookConnection.Live, "Connected");
        Assert.Equal(initial.Count, vm.GetRows(11).Count);
        vm.Refresh();
        Assert.Equal(scrolled[0].Price, vm.GetRows(11)[0].Price);
        vm.CenterCommand.Execute(null);
        Assert.True(vm.IsFollowing);
        Assert.True(vm.GetRows(11)[0].Price > scrolled[0].Price);
        Assert.Equal("binance", vm.Exchange);
    }

    [Theory]
    [InlineData("", "1", false)]
    [InlineData("100", "0", false)]
    [InlineData("100", "-1", false)]
    [InlineData("NaN", "1", false)]
    [InlineData("100.01", "0,25", true)]
    public async Task TradeStubsValidateInputAndReportSelectedSide(string price, string amount, bool valid)
    {
        await using var vm = new OrderBookViewModel("binance", "BTC/USDT", new FakeService(new FakeSubscription(Book())));
        vm.PriceText = price;
        vm.AmountText = amount;
        Assert.Equal(valid, vm.BuyCommand.CanExecute(null));
        Assert.Equal(valid, vm.SellCommand.CanExecute(null));
        if (valid)
        {
            vm.SellCommand.Execute(null);
            Assert.Contains("Trading is not yet enabled", vm.TradeStatus);
            Assert.Contains("Sell", vm.TradeStatus);
            Assert.Contains(price, vm.TradeStatus);
        }
    }

    [Fact]
    public async Task ClosingBeforeDelayedSubscriptionArrivesDisposesItAndIgnoresUpdates()
    {
        var pending = new TaskCompletionSource<IOrderBookSubscription>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Moq.Mock<IOrderBookService>();
        service.Setup(s => s.SubscribeAsync("binance", "BTC/USDT", Moq.It.IsAny<CancellationToken>())).Returns(pending.Task);
        var vm = new OrderBookViewModel("binance", "BTC/USDT", service.Object);
        var start = vm.StartAsync();
        var closing = vm.DisposeAsync().AsTask();
        var subscription = new FakeSubscription(Book());
        pending.SetResult(subscription);
        await start;
        await closing;
        vm.Refresh();
        Assert.True(subscription.Disposed);
        Assert.Empty(vm.GetRows(10));
    }

    [Fact]
    public void WindowRendersAtMinimumSizeWithColorsAndPriceSelection()
    {
        OnSta(() =>
        {
            var window = new OrderBookWindow("binance", "BTC/USDT", new FakeService(new FakeSubscription(Book())));
            var vm = (OrderBookViewModel)window.DataContext;
            vm.StartAsync().GetAwaiter().GetResult();
            vm.Refresh();
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(320, 440));
            content.Arrange(new Rect(0, 0, 320, 440));
            content.UpdateLayout();
            var scaleInput = (TextBox)window.FindName("MaxVolumeInput");
            scaleInput.Text = "8";
            Assert.Equal("8", vm.MaxVolumeText);
            Assert.Equal(.25, vm.GetRows(13).Single(r => r.Price == 100m).VolumeFraction);
            var currencySelector = (ComboBox)window.FindName("VolumeCurrencySelector");
            currencySelector.SelectedValue = OrderBookVolumeCurrency.Quote;
            Assert.Equal(OrderBookVolumeCurrency.Quote, vm.VolumeCurrency);
            Assert.Equal("", scaleInput.Text);
            currencySelector.SelectedValue = OrderBookVolumeCurrency.Base;
            Assert.Equal("8", scaleInput.Text);
            content.UpdateLayout();
            var ladder = (OrderBookLadderControl)window.FindName("Ladder");
            Assert.True(ladder.ActualHeight >= 4 * OrderBookLadderControl.RowHeight);
            var rows = vm.GetRows((int)(ladder.ActualHeight / OrderBookLadderControl.RowHeight));
            Assert.Contains(rows, r => r.Side == OrderBookSide.Spread);
            Assert.Contains(rows, r => r.Side == OrderBookSide.Ask);
            Assert.Contains(rows, r => r.Side == OrderBookSide.Bid);
            var bitmap = new RenderTargetBitmap(320, 440, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var pixels = new byte[320 * 440 * 4];
            bitmap.CopyPixels(pixels, 320 * 4, 0);
            Assert.True(HasColor(pixels, 0xFC, 0xE4, 0xE4));
            Assert.True(HasColor(pixels, 0xE1, 0xF4, 0xE6));
            Assert.True(HasColor(pixels, 0x11, 0x18, 0x27));
            vm.SelectPrice(100.03m);
            Assert.Equal("100.03", ((TextBox)window.FindName("PriceInput")).Text);
            if (Environment.GetEnvironmentVariable("BOTVIEW_ORDERBOOK_PREVIEW") is { Length: > 0 } path)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(path);
                encoder.Save(output);
            }
            window.Close();
            window.CleanupTask.GetAwaiter().GetResult();
        });
    }

    [Theory]
    [InlineData("binance")]
    [InlineData("bybit")]
    public async Task LiveProFeedWorksAlongsideExistingRestCandles(string exchange)
    {
        if (Environment.GetEnvironmentVariable("BOTVIEW_LIVE_ORDERBOOK") != "1") Assert.Skip("Set BOTVIEW_LIVE_ORDERBOOK=1 to check public exchange endpoints.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var service = new OrderBookService();
        await using var subscription = await service.SubscribeAsync(exchange, "BTC/USDT", timeout.Token);
        await using var candleService = new ExchangeService(CcxtMarketDataProvider.CreateDefaults(), maxRetryAttempts: 1);
        var candles = candleService.GetCandlesAsync(exchange, "BTC/USDT", "1m", null, 5, timeout.Token);
        var lastStatus = "Connecting…";
        try
        {
            while (subscription.Current.Connection != OrderBookConnection.Live)
            {
                lastStatus = subscription.Current.Message;
                Assert.NotEqual(OrderBookConnection.Error, subscription.Current.Connection);
                await Task.Delay(50, timeout.Token);
            }
            Assert.NotEmpty(subscription.Current.Snapshot!.Bids);
            Assert.NotEmpty(subscription.Current.Snapshot.Asks);
            Assert.NotEmpty(await candles);
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException($"{exchange}: {lastStatus}", ex);
        }
    }

    private static bool HasColor(byte[] pixels, byte red, byte green, byte blue)
    {
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] == blue && pixels[i + 1] == green && pixels[i + 2] == red && pixels[i + 3] == 255) return true;
        return false;
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class FakeClient : IOrderBookClient
    {
        public readonly Channel<OrderBookSnapshot> Feed = Channel.CreateUnbounded<OrderBookSnapshot>();
        public readonly TaskCompletionSource Watching = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Disposed;
        public bool LoadedOnThreadPool;
        public Exception? LoadError;
        public Task<OrderBookMarket> LoadMarketAsync(string symbol, CancellationToken ct)
        {
            LoadedOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            if (LoadError is not null) throw LoadError;
            return Task.FromResult(Book().Market);
        }
        public async Task<OrderBookSnapshot> WatchAsync(OrderBookMarket market, CancellationToken ct)
        {
            Watching.TrySetResult();
            return await Feed.Reader.ReadAsync(ct);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeSubscription(OrderBookSnapshot snapshot) : IOrderBookSubscription
    {
        public OrderBookUpdate Current { get; set; } = new(snapshot, OrderBookConnection.Live, "Connected");
        public bool Disposed;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeService(IOrderBookSubscription subscription) : IOrderBookService
    {
        public Task<IOrderBookSubscription> SubscribeAsync(string exchange, string symbol, CancellationToken ct = default) => Task.FromResult(subscription);
    }
}
