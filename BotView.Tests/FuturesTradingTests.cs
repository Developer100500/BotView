using System.IO;
using BotView.Models;
using BotView.Services;
using BotView.Services.Integrations.Ccxt;

namespace BotView.Tests;

public sealed class FuturesTradingTests
{
    private static readonly InstrumentKey Contract = new("binance", "futures", "", "BTC/USDT:USDT");

    /// <summary>Verifies: futures Window Renders Contract Units At Minimum Size Without Connecting An Account.</summary>
    [Fact]
    public void FuturesWindowRendersContractUnitsAtMinimumSizeWithoutConnectingAnAccount()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var source = new Moq.Mock<BotView.Services.OrderBooks.IOrderBookService>();
                var window = new BotView.Windows.OrderBookWindow("binance", Contract.SecurityId, source.Object,
                    environment: TradingEnvironment.Testnet, market: TradingMarket.UsdtFutures);
                var viewModel = (BotView.ViewModels.OrderBookViewModel)window.DataContext;
                var content = (System.Windows.FrameworkElement)window.Content;
                content.Measure(new System.Windows.Size(320, 440));
                content.Arrange(new System.Windows.Rect(0, 0, 320, 440));
                content.UpdateLayout();

                Assert.True(viewModel.IsFutures);
                Assert.Contains("contracts", viewModel.QuantityHeader);
                Assert.Contains("USDT", viewModel.QuoteHeader);
                Assert.False(viewModel.CanTrade);
                Assert.True(((System.Windows.FrameworkElement)window.FindName("PriceInput")).ActualWidth > 80);

                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(320, 440, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(content);
                if (Environment.GetEnvironmentVariable("BOTVIEW_FUTURES_PREVIEW") is { Length: > 0 } path)
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output = File.Create(path);
                    encoder.Save(output);
                }

                window.Close();
                window.CleanupTask.GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>Builds a closing intent with explicit native contract quantities.</summary>
    private static PlaceOrderRequest Request(OrderSide side, PositionSide? leg, bool reduce = true) =>
        new(Contract, side, OrderType.Limit, 2, "bv-futures-test", 100, ReduceOnly: reduce, PositionSide: leg);

    /// <summary>Verifies: futures Clients Use Derivative Endpoints Before Any Request.</summary>
    [Theory]
    [InlineData("binance", TradingEnvironment.Testnet)]
    [InlineData("binance", TradingEnvironment.Production)]
    [InlineData("bybit", TradingEnvironment.Testnet)]
    [InlineData("bybit", TradingEnvironment.Production)]
    public async Task FuturesClientsUseDerivativeEndpointsBeforeAnyRequest(string exchange, TradingEnvironment environment)
    {
        using var keys = new AccountCredentials("fake", "fake");
        await using var client = new CcxtSpotTradingClient(new(Guid.NewGuid(), "Futures", exchange, environment, TradingMarket.UsdtFutures), keys);
        Assert.True(client.IsFutures);
        var api = System.Text.Json.JsonSerializer.Serialize(((Dictionary<string, object>)client.Transport.urls)["api"]);
        Assert.Contains(exchange == "binance" ? "fapi" : "bybit", api, StringComparison.OrdinalIgnoreCase);
        if (environment == TradingEnvironment.Testnet)
        {
            Assert.Contains("testnet", api, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Verifies: closing Short Maps To Correct Leg Without Unsupported Binance Hedge Flag.</summary>
    [Theory]
    [InlineData("binance", false)]
    [InlineData("binance", true)]
    [InlineData("bybit", false)]
    [InlineData("bybit", true)]
    public void ClosingShortMapsToCorrectLegWithoutUnsupportedBinanceHedgeFlag(string exchange, bool hedged)
    {
        var request = Request(OrderSide.Buy, hedged ? PositionSide.Short : null) with { PostOnly = true };
        var parameters = CcxtSpotTradingClient.FuturesOrderParameters(exchange, request, hedged);
        Assert.Equal(exchange == "binance" ? "GTX" : "PostOnly", parameters["timeInForce"]);
        if (exchange == "binance")
        {
            Assert.Equal(hedged ? "SHORT" : "BOTH", parameters["positionSide"]);
            Assert.Equal(!hedged, parameters.ContainsKey("reduceOnly"));
        }
        else
        {
            Assert.Equal("linear", parameters["category"]);
            Assert.Equal(hedged ? 2 : 0, parameters["positionIdx"]);
            Assert.Equal(true, parameters["reduceOnly"]);
        }
    }

    /// <summary>Verifies: hedge Closing Cannot Open The Opposite Leg Or Exceed Position.</summary>
    [Fact]
    public void HedgeClosingCannotOpenTheOppositeLegOrExceedPosition()
    {
        var positions = new[] { new PositionSnapshot(Contract, PositionSide.Long, 3, QuantityUnit.Contracts, Leverage: 5, Hedged: true) };
        Assert.Empty(CcxtSpotTradingClient.ValidatePositionIntent(Request(OrderSide.Sell, PositionSide.Long), positions));
        Assert.NotEmpty(CcxtSpotTradingClient.ValidatePositionIntent(Request(OrderSide.Buy, PositionSide.Long), positions));
        Assert.NotEmpty(CcxtSpotTradingClient.ValidatePositionIntent(Request(OrderSide.Sell, PositionSide.Long) with { Quantity = 4 }, positions));
        Assert.NotEmpty(CcxtSpotTradingClient.ValidatePositionIntent(Request(OrderSide.Sell, null), positions));
    }

    /// <summary>Verifies: one Way Requires Auto And Unknown Mode Never Defaults To An Opening Order.</summary>
    [Fact]
    public void OneWayRequiresAutoAndUnknownModeNeverDefaultsToAnOpeningOrder()
    {
        var positions = new[] { new PositionSnapshot(Contract, PositionSide.Short, 3, QuantityUnit.Contracts, Leverage: 5, Hedged: false) };
        Assert.Empty(CcxtSpotTradingClient.ValidatePositionIntent(Request(OrderSide.Buy, null), positions));
        Assert.NotEmpty(CcxtSpotTradingClient.ValidatePositionIntent(Request(OrderSide.Buy, PositionSide.Short), positions));
        Assert.NotEmpty(CcxtSpotTradingClient.ValidatePositionIntent(Request(OrderSide.Sell, null), positions));
        Assert.NotEmpty(CcxtSpotTradingClient.ValidatePositionIntent(Request(OrderSide.Buy, null), []));
    }

    /// <summary>Verifies: margin Uses Contract Size Actual Leverage Higher Price And Both Commissions.</summary>
    [Fact]
    public void MarginUsesContractSizeActualLeverageHigherPriceAndBothCommissions()
    {
        // Ten contracts of 0.01 BTC at 110 USDT, leverage 5: 2.2 initial margin + 0.022 fees.
        Assert.Equal(2.222m, CcxtSpotTradingClient.RequiredMargin(10, .01m, 100, 110, 5, .001m));
        Assert.Equal(3.222m, CcxtSpotTradingClient.RequiredMargin(10, .01m, 100, 110, 5, .001m, OrderSide.Sell));
        Assert.Throws<ArgumentOutOfRangeException>(() => CcxtSpotTradingClient.RequiredMargin(1, 1, 100, 100, 0, .001m));
    }

    /// <summary>Verifies: session Uses Derivative Margin Rather Than Spot Coin Balance.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SessionUsesDerivativeMarginRatherThanSpotCoinBalance(bool sufficient)
    {
        var client = new DerivativeClient { Available = sufficient ? 100 : 0 };
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Futures", "binance", TradingEnvironment.Testnet, TradingMarket.UsdtFutures);
        var journal = new TradingJournal(Path.Combine(AppContext.BaseDirectory, "futures-" + Guid.NewGuid().ToString("N"), "journal.db"));
        await using var session = new CcxtTradingService(profile, client, journal, "owner", true);
        await session.TrackAsync(Contract);
        var request = Request(OrderSide.Sell, null, false);
        var result = await session.PlaceOrderAsync(request);
        Assert.Equal(sufficient ? PlaceOrderStatus.Accepted : PlaceOrderStatus.Rejected, result.Status);
        Assert.Equal(sufficient ? 1 : 0, client.Sends);
        Assert.Equal(TradingMarket.UsdtFutures, session.Account.Market);
        Assert.True((await session.GetCapabilitiesAsync(Contract)).SupportsPositions);
        Assert.NotEmpty(await session.GetPositionsAsync(Contract));
        Assert.Equal(2, session.ConvertQuantity(Contract, 200, 100, OrderQuantityCurrency.Quote));
        Assert.Empty(journal.Orders("binance:Testnet:owner"));
    }

    private sealed class DerivativeClient : ISpotTradingClient
    {
        public bool IsFutures => true;
        public decimal Available;
        public int Sends;
        private OrderSnapshot? _order;

        /// <summary>Provides a deterministic futures identity.</summary>
        public Task<(string Identity, bool? CanTrade)> ConnectAsync(CancellationToken ct) => Task.FromResult<(string, bool?)>(("owner", true));

        /// <summary>This fixture does not load an exchange catalog.</summary>
        public Task<IReadOnlyList<string>> LoadTradingPairsAsync(string spotSymbol, CancellationToken ct = default) => throw new NotSupportedException();

        /// <summary>Supplies native contracts and settlement units for the shared validator.</summary>
        public Task<SpotMarket> MarketAsync(InstrumentKey key, CancellationToken ct) => Task.FromResult(new SpotMarket(
            new(key, key.SecurityId, "Future", "USDT", PriceStep: 1,
                TradingRules: new(QuantityUnit.Contracts, PriceQuotation.CurrencyPerUnit, QuantityStep: 1,
                    ContractSize: 1, SettlementCurrency: "USDT")), "BTC", "USDT", .001m));

        /// <summary>Uses whole contracts in this fixture.</summary>
        public decimal NormalizeAmount(string symbol, decimal amount) => decimal.Floor(amount);

        /// <summary>Uses whole price ticks in this fixture.</summary>
        public decimal NormalizePrice(string symbol, decimal price) => decimal.Round(price);

        /// <summary>Records an acknowledged derivative order without network access.</summary>
        public Task<OrderSnapshot> PlaceAsync(PlaceOrderRequest request, CancellationToken ct)
        {
            Sends++;
            _order = new(new(request.Instrument, "future-1"), request.ClientOrderId, request.Side, request.Type,
                OrderState.Open, request.Quantity, 0, request.Quantity, request.Price, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            return Task.FromResult(_order);
        }

        /// <summary>Returns the fixture's observed order.</summary>
        public Task<OrderSnapshot?> OrderAsync(OrderReference order, CancellationToken ct) => Task.FromResult(_order);

        /// <summary>Provides a no-op cancellation for session teardown checks.</summary>
        public Task<OrderSnapshot?> CancelAsync(OrderReference order, CancellationToken ct) => Task.FromResult(_order);

        /// <summary>Resolves only the fixture's original client ID.</summary>
        public Task<OrderSnapshot?> FindAsync(InstrumentKey key, string clientId, CancellationToken ct) => Task.FromResult(_order?.ClientOrderId == clientId ? _order : null);

        /// <summary>Supplies a bounded order snapshot.</summary>
        public Task<TradingPage<OrderSnapshot>> OrdersAsync(TradingQuery query, bool open, CancellationToken ct) => Task.FromResult(new TradingPage<OrderSnapshot>(_order is null ? [] : [_order]));

        /// <summary>No executions occur in this fixture.</summary>
        public Task<TradingPage<ExecutionSnapshot>> ExecutionsAsync(TradingQuery query, CancellationToken ct) => Task.FromResult(new TradingPage<ExecutionSnapshot>([]));

        /// <summary>Provides margin despite zero spot cash, exposing accidental reuse of spot validation.</summary>
        public Task<AccountSnapshot> BalanceAsync(TradingAccountContext account, CancellationToken ct) => Task.FromResult(new AccountSnapshot(
            account, [new("BTC", 0, 0, 0), new("USDT", 0, 0, 0)], null,
            [new("USDT", AvailableFunds: Available)], DateTimeOffset.UtcNow));

        /// <summary>Reports one-way mode and leverage without changing either setting.</summary>
        public Task<IReadOnlyList<PositionSnapshot>> PositionsAsync(InstrumentKey? key, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PositionSnapshot>>([new(Contract, PositionSide.Long, 0, QuantityUnit.Contracts, Leverage: 5, Hedged: false)]);

        /// <summary>Checks collateral through the same margin calculation used by the real adapter.</summary>
        public Task<IReadOnlyList<TradingError>> ValidateFuturesAsync(PlaceOrderRequest request, SpotMarket market, AccountSnapshot balance, CancellationToken ct)
        {
            var required = CcxtSpotTradingClient.RequiredMargin(request.Quantity, 1, request.Price!.Value, 100, 5, .001m, request.Side);
            return Task.FromResult<IReadOnlyList<TradingError>>(Available >= required ? [] : [new("InsufficientMargin", "Insufficient margin")]);
        }

        /// <summary>Exercises REST-only operation without private subscriptions.</summary>
        public bool SupportsStream(string kind) => false;

        /// <summary>Fails if the session incorrectly starts a disabled stream.</summary>
        public Task<IReadOnlyList<TradingUpdate>> WatchAsync(string kind, InstrumentKey key, TradingAccountContext account, CancellationToken ct) => throw new NotSupportedException();

        /// <summary>Disposal never invokes exchange cancellation.</summary>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Verifies: legacy Profile Defaults To Spot And New Market Survives Persistence.</summary>
    [Fact]
    public void LegacyProfileDefaultsToSpotAndNewMarketSurvivesPersistence()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<TradingAccountProfile>(
            "{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Name\":\"Legacy\",\"Exchange\":\"binance\",\"Environment\":0}")!;
        Assert.Equal(TradingMarket.Spot, legacy.Market);
        var profile = legacy with { Market = TradingMarket.UsdtFutures };
        var path = Path.Combine(AppContext.BaseDirectory, "futures-" + Guid.NewGuid().ToString("N"), "profiles.json");
        var store = new TradingAccountStore(path);
        store.Save(new([profile], new Dictionary<string, Guid> { ["binance"] = profile.Id }));
        Assert.Equal(TradingMarket.UsdtFutures, store.Load().Profiles.Single().Market);
    }
}
