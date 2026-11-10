using BotView.Models;
using BotView.Services.Integrations.Ccxt;

namespace BotView.Tests;

public sealed class TradingServiceTests
{
    /// <summary>Leverage reaches the futures route exactly once without order-specific flags.</summary>
    [Theory]
    [InlineData("binance", false)]
    [InlineData("bybit", false)]
    [InlineData("binance", true)]
    [InlineData("bybit", true)]
    public async Task FuturesLeverageUsesOneNativeCall(string venue, bool timeout)
    {
        var native = new CapturingExchange();
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", venue, TradingEnvironment.Testnet, TradingMarket.UsdtFutures);
        await using var service = new FuturesTradingService(profile, native);
        var key = new InstrumentKey(venue, "futures", "", "BTC/USDT:USDT");

        if (timeout)
        {
            native.Failure = new TimeoutException("Response not received.");
            await Assert.ThrowsAsync<TradingServiceException>(() => service.SetLeverageAsync(key, 10));
        }
        else
        {
            await service.SetLeverageAsync(key, 10);
        }

        Assert.Equal(1, native.LeverageCalls);
        Assert.Equal(10, native.Leverage);
        Assert.Equal(key.SecurityId, native.Symbol);
        Assert.DoesNotContain("type", native.Parameters.Keys);
        if (venue == "bybit") Assert.Equal("linear", native.Parameters["category"]);
        Assert.DoesNotContain("positionIdx", native.Parameters.Keys);
        Assert.DoesNotContain("positionSide", native.Parameters.Keys);
        Assert.Equal(0, native.Calls);
    }

    /// <summary>Invalid leverage and spot operations are rejected before any native request.</summary>
    [Fact]
    public async Task LeverageRejectsInvalidInputAndSpot()
    {
        var native = new CapturingExchange();
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet, TradingMarket.UsdtFutures);
        await using var futures = new FuturesTradingService(profile, native);
        var key = new InstrumentKey("binance", "futures", "", "BTC/USDT:USDT");
        await Assert.ThrowsAsync<TradingServiceException>(() => futures.SetLeverageAsync(key, 0));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => futures.SetLeverageAsync(key, 10, canceled.Token));

        await using var spot = new SpotTradingService(profile with { Market = TradingMarket.Spot }, new CapturingExchange());
        await Assert.ThrowsAsync<NotSupportedException>(() => spot.SetLeverageAsync(key with { Market = "spot" }, 10));
        Assert.Equal(0, native.LeverageCalls);
    }

    /// <summary>Checks the actual native CCXT call without sending any HTTP or WebSocket traffic.</summary>
    [Theory]
    [InlineData("binance", false, false, "buy")]
    [InlineData("binance", false, true, "sell")]
    [InlineData("binance", true, false, "sell")]
    [InlineData("binance", true, true, "buy")]
    [InlineData("bybit", false, false, "sell")]
    [InlineData("bybit", false, true, "buy")]
    [InlineData("bybit", true, false, "buy")]
    [InlineData("bybit", true, true, "sell")]
    public async Task OrderTypesAndMarketRoutesHaveExplicitArguments(string venue, bool futures, bool market, string side)
    {
        var native = new CapturingExchange();
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", venue, TradingEnvironment.Testnet,
            futures ? TradingMarket.UsdtFutures : TradingMarket.Spot);
        await using AbstractTradingService service = futures
            ? new FuturesTradingService(profile, native) : new SpotTradingService(profile, native);
        var symbol = futures ? "BTC/USDT:USDT" : "BTC/USDT";
        var key = new InstrumentKey(venue, futures ? "futures" : "spot", "", symbol);
        var direction = side == "sell" ? OrderSide.Sell : OrderSide.Buy;
        OrderSnapshot result;
        if (market)
        {
            result = await service.CreateMarketOrderAsync(new(key, direction, 2,
                ReduceOnly: futures, PositionSide: futures ? PositionSide.Short : null, Hedged: futures ? true : null));
        }
        else
        {
            result = await service.CreateLimitOrderAsync(new(key, direction, 2, 100, PostOnly: true,
                ReduceOnly: futures, PositionSide: futures ? PositionSide.Short : null, Hedged: futures ? true : null));
        }
        Assert.Equal(new OrderReference(key, "1"), result.Reference);
        Assert.Equal(1, native.Calls);
        Assert.Equal(symbol, native.Symbol);
        Assert.Equal(side, native.Side);
        Assert.Equal(market ? "market" : "limit", native.Type);
        Assert.Equal(2d, native.Amount);
        Assert.Equal(market ? null : 100d, native.Price);
        if (market)
        {
            Assert.DoesNotContain("postOnly", native.Parameters.Keys);
            Assert.DoesNotContain("timeInForce", native.Parameters.Keys);
        }
        if (!futures)
        {
            Assert.DoesNotContain("type", native.Parameters.Keys);
            Assert.DoesNotContain("positionSide", native.Parameters.Keys);
            Assert.DoesNotContain("positionIdx", native.Parameters.Keys);
            Assert.DoesNotContain("reduceOnly", native.Parameters.Keys);
        }
        else
        {
            Assert.Equal(true, native.Parameters["reduceOnly"]);
            if (venue == "binance") Assert.Equal("SHORT", native.Parameters["positionSide"]);
            else Assert.Equal(2, native.Parameters["positionIdx"]);
        }
    }

    /// <summary>Hedge and one-way orders use the venue's own fields and the mode the exchange reported.</summary>
    [Theory]
    [InlineData("binance", true, PositionSide.Long, false)]
    [InlineData("binance", true, PositionSide.Short, true)]
    [InlineData("bybit", true, PositionSide.Long, true)]
    [InlineData("bybit", true, PositionSide.Short, false)]
    [InlineData("okx", true, PositionSide.Long, false)]
    [InlineData("okx", true, PositionSide.Short, true)]
    [InlineData("binance", false, null, false)]
    [InlineData("bybit", false, null, true)]
    [InlineData("okx", false, null, false)]
    public async Task FuturesOrdersUseTheReportedPositionMode(string venue, bool hedged, PositionSide? side, bool market)
    {
        var native = new CapturingExchange { ReportedHedged = hedged };
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", venue, TradingEnvironment.Testnet, TradingMarket.UsdtFutures);
        await using var service = new FuturesTradingService(profile, native);
        var key = new InstrumentKey(venue, "futures", "", "BTC/USDT:USDT");
        Task<OrderSnapshot> SendAsync() => market
            ? service.CreateMarketOrderAsync(new(key, OrderSide.Buy, 1, PositionSide: side, Hedged: hedged))
            : service.CreateLimitOrderAsync(new(key, OrderSide.Sell, 1, 100, PositionSide: side, Hedged: hedged));

        await SendAsync();

        Assert.Equal(1, native.Calls);
        Assert.Equal(0, native.PositionModeCalls);
        Assert.Equal(0, native.PositionFetches);
        Assert.DoesNotContain("reduceOnly", native.Parameters.Keys);
        if (venue == "binance") Assert.Equal("future", native.Parameters["type"]);
        if (venue == "bybit") Assert.Equal("linear", native.Parameters["category"]);
        if (venue != "binance") Assert.DoesNotContain("positionSide", native.Parameters.Keys);
        if (venue != "bybit") Assert.DoesNotContain("positionIdx", native.Parameters.Keys);
        if (venue != "okx") Assert.DoesNotContain("posSide", native.Parameters.Keys);
        if (!hedged)
        {
            if (venue == "binance") Assert.Equal("BOTH", native.Parameters["positionSide"]);
            if (venue == "bybit") Assert.Equal(0, native.Parameters["positionIdx"]);
            if (venue == "okx") Assert.Equal("net", native.Parameters["posSide"]);
            return;
        }

        if (venue == "binance") Assert.Equal(side == PositionSide.Long ? "LONG" : "SHORT", native.Parameters["positionSide"]);
        if (venue == "bybit") Assert.Equal(side == PositionSide.Long ? 1 : 2, native.Parameters["positionIdx"]);
        if (venue == "okx") Assert.Equal(side == PositionSide.Long ? "long" : "short", native.Parameters["posSide"]);
    }

    /// <summary>A hedge order needs a leg, and a one-way order must not invent one.</summary>
    [Theory]
    [InlineData(true, null, false)]
    [InlineData(false, PositionSide.Short, false)]
    [InlineData(false, null, true)]
    public async Task FuturesOrdersStopWhenTheModeAndSideDisagree(bool? hedged, PositionSide? side, bool unavailable)
    {
        var native = new CapturingExchange { ReportedHedged = hedged };
        if (unavailable)
        {
            native.has["fetchPositionMode"] = false;
            native.has["fetchPositions"] = false;
        }

        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet, TradingMarket.UsdtFutures);
        await using var service = new FuturesTradingService(profile, native);
        var key = new InstrumentKey("binance", "futures", "", "BTC/USDT:USDT");
        var error = await Assert.ThrowsAsync<TradingServiceException>(() => service.CreateMarketOrderAsync(
            new(key, OrderSide.Buy, 1, PositionSide: side, Hedged: unavailable ? null : hedged)));

        Assert.Equal(0, native.Calls);
        if (unavailable) Assert.Contains("did not report", error.Message);
        else if (hedged == true) Assert.Contains("Select Long or Short", error.Message);
        else Assert.Contains("Buy or Sell only", error.Message);
    }

    /// <summary>Bybit reports the mode on positions because this CCXT version has no position-mode fetch.</summary>
    [Fact]
    public async Task BybitPositionModeComesFromThePositionReport()
    {
        var native = new CapturingExchange();
        native.has["fetchPositionMode"] = false;
        native.has["fetchPositions"] = true;
        native.Positions.Add(new ccxt.Position { symbol = "BTC/USDT:USDT", hedged = false });
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "bybit", TradingEnvironment.Testnet, TradingMarket.UsdtFutures);
        await using var service = new FuturesTradingService(profile, native);
        var key = new InstrumentKey("bybit", "futures", "", "BTC/USDT:USDT");

        var hedged = await service.FetchPositionModeAsync(key);
        await service.CreateMarketOrderAsync(new(key, OrderSide.Sell, 1, ReduceOnly: true, Hedged: hedged));

        Assert.Equal(0, native.PositionModeCalls);
        Assert.Equal(1, native.PositionFetches);
        Assert.Equal(0, native.Parameters["positionIdx"]);
        Assert.Equal(true, native.Parameters["reduceOnly"]);

        native.Positions.Clear();
        native.Positions.Add(new ccxt.Position { symbol = "ETH/USDT:USDT", hedged = true });
        native.Positions.Add(new ccxt.Position { symbol = "BTC/USDT:USDT", hedged = true });
        native.Positions.Add(new ccxt.Position { symbol = "BTC/USDT:USDT", hedged = false });
        var conflictMode = await service.FetchPositionModeAsync(key);
        var conflict = await Assert.ThrowsAsync<TradingServiceException>(() =>
            service.CreateMarketOrderAsync(new(key, OrderSide.Buy, 1, Hedged: conflictMode)));
        Assert.Contains("did not report", conflict.Message);
        Assert.Equal(1, native.Calls);
    }

    /// <summary>An unsupported futures venue does not receive another exchange's position fields.</summary>
    [Fact]
    public async Task UnsupportedFuturesVenueDoesNotBorrowPositionFields()
    {
        var native = new CapturingExchange();
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "kraken", TradingEnvironment.Testnet, TradingMarket.UsdtFutures);
        await using var service = new FuturesTradingService(profile, native);
        var error = await Assert.ThrowsAsync<TradingServiceException>(() =>
            service.CreateMarketOrderAsync(new(new("kraken", "futures", "", "BTC/USDT:USDT"), OrderSide.Buy, 1, Hedged: false)));
        Assert.Contains("not supported", error.Message);
        Assert.Equal(0, native.Calls);
        Assert.Equal(0, native.PositionModeCalls);
    }

    /// <summary>One-way markers stay empty. Only an actual hedge leg is kept, including OKX names.</summary>
    [Theory]
    [InlineData("positionSide", "LONG", "Long")]
    [InlineData("positionSide", "SHORT", "Short")]
    [InlineData("positionSide", "BOTH", "")]
    [InlineData("positionIdx", "1", "Long")]
    [InlineData("positionIdx", "2", "Short")]
    [InlineData("positionIdx", "0", "")]
    [InlineData("posSide", "long", "Long")]
    [InlineData("posSide", "short", "Short")]
    [InlineData("posSide", "net", "")]
    public void OrderPositionSideKeepsOnlyHedgeLegs(string field, string value, string expected)
    {
        var key = new InstrumentKey("binance", "futures", "", "BTC/USDT:USDT");
        var info = new Dictionary<string, object> { [field] = value };
        if (field == "positionSide") info["positionIdx"] = 1;
        var order = new ccxt.Order
        {
            id = "1", side = "buy", type = "limit", status = "open", info = info
        };
        var snapshot = CcxtTradingMapper.MapOrder(order, key);
        Assert.Equal(expected == "" ? null : Enum.Parse<PositionSide>(expected), snapshot.PositionSide);
    }

    /// <summary>Market selection creates a concrete direct service without an intermediate trading client.</summary>
    [Theory]
    [InlineData(TradingMarket.Spot)]
    [InlineData(TradingMarket.UsdtFutures)]
    public async Task FactorySelectsTheServiceByMarket(TradingMarket market)
    {
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet, market);
        await using AbstractTradingService service = TradingServiceFactory.Create(profile);
        Assert.Equal(market, service.Account.Market);
        if (market == TradingMarket.Spot) Assert.IsType<SpotTradingService>(service);
        else Assert.IsType<FuturesTradingService>(service);
    }

    /// <summary>Shared metadata loading retains spot units and the futures subclass's contract valuation.</summary>
    [Theory]
    [InlineData(TradingMarket.Spot)]
    [InlineData(TradingMarket.UsdtFutures)]
    public async Task InstrumentMetadataKeepsTheMarketSpecificUnits(TradingMarket market)
    {
        var native = new CapturingExchange();
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet, market);
        await using AbstractTradingService service = market == TradingMarket.Spot
            ? new SpotTradingService(profile, native) : new FuturesTradingService(profile, native);
        var key = new InstrumentKey("binance", market == TradingMarket.Spot ? "spot" : "futures", "",
            market == TradingMarket.Spot ? "BTC/USDT" : "BTC/USDT:USDT");

        var instrument = await service.FetchInstrumentAsync(key);

        Assert.Equal(key, instrument.Key);
        Assert.Equal(.01m, instrument.PriceStep);
        if (market == TradingMarket.Spot)
        {
            Assert.Equal(QuantityUnit.BaseAsset, instrument.TradingRules!.QuantityUnit);
            Assert.Empty(await service.FetchPositionsAsync(key));
        }
        else
        {
            Assert.Equal(QuantityUnit.Contracts, instrument.TradingRules!.QuantityUnit);
            Assert.Equal(1m, instrument.TradingRules.ContractSize);
            Assert.Equal("USDT", instrument.TradingRules.SettlementCurrency);
        }
    }

    /// <summary>A spot service cannot dispatch an instrument belonging to the futures account route.</summary>
    [Fact]
    public async Task WrongMarketIsRejectedBeforeDispatch()
    {
        var native = new CapturingExchange();
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet);
        await using var service = new SpotTradingService(profile, native);
        var key = new InstrumentKey("binance", "futures", "", "BTC/USDT:USDT");
        await Assert.ThrowsAsync<TradingServiceException>(() => service.CreateMarketOrderAsync(new(key, OrderSide.Buy, 1)));
        Assert.Equal(0, native.Calls);
        Assert.Throws<ArgumentException>(() => new FuturesTradingService(profile, native));
    }

    /// <summary>Checks native precision formatting and detached responses in the adapter.</summary>
    [Fact]
    public async Task ServiceFormatsTheActualQuantityAndPrice()
    {
        var native = new CapturingExchange();
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet);
        await using var service = new SpotTradingService(profile, native);
        var key = new InstrumentKey("binance", "spot", "", "BTC/USDT");
        var order = await service.CreateLimitOrderAsync(new(key, OrderSide.Sell, 1.23456m, 100.123m));
        Assert.Equal(1.2345d, native.Amount);
        Assert.Equal(100.12d, native.Price);
        Assert.Equal("sell", native.Side);
        Assert.Equal("1", order.Reference.ExchangeOrderId);
    }

    /// <summary>Timeouts invoke the transport only once and never become a fabricated exchange rejection.</summary>
    [Fact]
    public async Task ServiceDoesNotRetryUnknownOutcomes()
    {
        var native = new CapturingExchange { Failure = new ccxt.RequestTimeout("signed url must not be displayed") };
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet);
        await using var service = new SpotTradingService(profile, native);
        var key = new InstrumentKey("binance", "spot", "", "BTC/USDT");
        var error = await Assert.ThrowsAsync<TradingServiceException>(() => service.CreateMarketOrderAsync(new(key, OrderSide.Buy, 1)));
        Assert.Equal(1, native.Calls);
        Assert.Contains("outcome is unknown", error.Message);
        Assert.DoesNotContain("signed url", error.Message);
    }

    /// <summary>A token canceled before dispatch never invokes the native create method.</summary>
    [Fact]
    public async Task CanceledRequestDoesNotReachTheExchange()
    {
        var native = new CapturingExchange();
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet);
        await using var service = new SpotTradingService(profile, native);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateMarketOrderAsync(new(new("binance", "spot", "", "BTC/USDT"), OrderSide.Buy, 1), stop.Token));
        Assert.Equal(0, native.Calls);
    }

    /// <summary>Leaves safe exchange rejection reasons visible while dropping signed request text.</summary>
    [Fact]
    public void ExchangeErrorsNeverExposeRequestSecrets()
    {
        var error = new ccxt.InvalidOrder("https://example.invalid/?signature=secret {\"code\":-1,\"msg\":\"Use BOTH in one-way mode\"}");
        Assert.Equal("Use BOTH in one-way mode", TradingErrorText.FromException(error));
        var unsafeError = new ccxt.InvalidOrder("{\"msg\":\"https://example.invalid?apiKey=secret\"}");
        Assert.DoesNotContain("secret", TradingErrorText.FromException(unsafeError));
    }

    /// <summary>Checks routing before any network request, including the private futures API host.</summary>
    [Theory]
    [InlineData("binance", TradingMarket.Spot, TradingEnvironment.Testnet, "testnet.binance.vision")]
    [InlineData("binance", TradingMarket.Spot, TradingEnvironment.Production, "api.binance.com")]
    [InlineData("binance", TradingMarket.UsdtFutures, TradingEnvironment.Testnet, "testnet.binancefuture.com")]
    [InlineData("binance", TradingMarket.UsdtFutures, TradingEnvironment.Production, "fapi.binance.com")]
    [InlineData("bybit", TradingMarket.Spot, TradingEnvironment.Testnet, "api-testnet.bybit.com")]
    [InlineData("bybit", TradingMarket.Spot, TradingEnvironment.Production, "api.bybit.com")]
    [InlineData("bybit", TradingMarket.UsdtFutures, TradingEnvironment.Testnet, "api-testnet.bybit.com")]
    [InlineData("bybit", TradingMarket.UsdtFutures, TradingEnvironment.Production, "api.bybit.com")]
    public async Task FactoryBindsTheMarketAndNetworkBeforeConnecting(string venue, TradingMarket market,
        TradingEnvironment environment, string expectedHost)
    {
        using var credentials = new AccountCredentials("fake", "fake");
        var transport = TradingServiceFactory.CreateExchange(new(Guid.NewGuid(), "Fake", venue, environment, market), credentials);
        var api = (Dictionary<string, object>)((Dictionary<string, object>)transport.urls)["api"];
        var endpoint = venue == "binance" && market == TradingMarket.UsdtFutures ? "fapiPrivate" : "private";
        Assert.Contains(expectedHost, transport.implodeHostname(api[endpoint]));
        Assert.False(transport.verbose);
        Assert.Equal(market == TradingMarket.Spot ? "spot" : venue == "binance" ? "future" : "swap", transport.options["defaultType"]);
        await transport.Close();
    }

    /// <summary>Captures native unified calls; unimplemented operations cannot contact an exchange.</summary>
    internal sealed class CapturingExchange : ccxt.Exchange
    {
        public int Calls;
        public int LeverageCalls;
        public int PositionModeCalls;
        public int PositionFetches;
        public bool? ReportedHedged = true;
        public List<ccxt.Position> Positions = [];
        public int Leverage;
        public string? Symbol;
        public string? Type;
        public string? Side;
        public double Amount;
        public double? Price;
        public Dictionary<string, object> Parameters = new();
        public Exception? Failure;

        /// <summary>Seeds precision locally to avoid network access when formatting order input.</summary>
        public CapturingExchange() : base(new Dictionary<string, object>())
        {
            precisionMode = 4;
            has["fetchPositionMode"] = true;
            setMarkets(new List<object>
            {
                new Dictionary<string, object>
                {
                    ["id"] = "BTCUSDT", ["symbol"] = "BTC/USDT", ["base"] = "BTC", ["quote"] = "USDT",
                    ["spot"] = true, ["type"] = "spot",
                    ["precision"] = new Dictionary<string, object> { ["amount"] = .0001, ["price"] = .01 }
                },
                new Dictionary<string, object>
                {
                    ["id"] = "BTCUSDTFUT", ["symbol"] = "BTC/USDT:USDT", ["base"] = "BTC", ["quote"] = "USDT",
                    ["spot"] = false, ["settle"] = "USDT", ["contract"] = true, ["linear"] = true, ["type"] = "swap", ["contractSize"] = 1,
                    ["precision"] = new Dictionary<string, object> { ["amount"] = .0001, ["price"] = .01 }
                }
            });
        }

        /// <summary>Captures leverage changes without contacting the exchange.</summary>
        public override Task<Dictionary<string, object>> SetLeverage(object leverage, string symbol = null!, object parameters = null!)
        {
            LeverageCalls++;
            Leverage = Convert.ToInt32(leverage);
            Symbol = symbol;
            Parameters = new((Dictionary<string, object>)parameters);

            return Failure is null
                ? Task.FromResult(new Dictionary<string, object>())
                : Task.FromException<Dictionary<string, object>>(Failure);
        }

        /// <summary>Captures the unified position-mode report without contacting the exchange.</summary>
        public override Task<ccxt.PositionModeInfo> FetchPositionMode(string symbol = null!, object parameters = null!)
        {
            PositionModeCalls++;
            return Task.FromResult(new ccxt.PositionModeInfo { hedged = ReportedHedged });
        }

        /// <summary>Captures positions used when a venue exposes the mode only on the position report.</summary>
        public override Task<List<ccxt.Position>> FetchPositions(object symbols = null!, object parameters = null!)
        {
            PositionFetches++;
            return Task.FromResult(Positions);
        }

        /// <summary>Captures the exact payload handed to the library's unified create operation.</summary>
        public override Task<ccxt.Order> CreateOrder(string symbol, string type, string side, double amount,
            double? price = null, object parameters = null!)
        {
            Calls++;
            Symbol = symbol.ToString();
            Type = type.ToString();
            Side = side.ToString();
            Amount = Convert.ToDouble(amount);
            Price = price is null ? null : Convert.ToDouble(price);
            Parameters = new((Dictionary<string, object>)parameters);
            if (Failure is not null) return Task.FromException<ccxt.Order>(Failure);
            return Task.FromResult(new ccxt.Order
            {
                id = "1", symbol = symbol, type = type, side = side,
                amount = amount, price = price, status = "open"
            });
        }
    }
}
