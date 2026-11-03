using BotView.Models;
using BotView.Services.Integrations.Ccxt;

namespace BotView.Tests;

public sealed class CcxtTradingClientTests
{
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
        await using var client = new CcxtTradingClient(native, venue);
        var symbol = futures ? "BTC/USDT:USDT" : "BTC/USDT";
        if (futures)
        {
            if (market) await client.CreateUsdtFuturesMarketOrderAsync(symbol, side, 2, true, PositionSide.Short);
            else await client.CreateUsdtFuturesLimitOrderAsync(symbol, side, 2, 100, true, true, PositionSide.Short);
        }
        else
        {
            if (market) await client.CreateSpotMarketOrderAsync(symbol, side, 2);
            else await client.CreateSpotLimitOrderAsync(symbol, side, 2, 100, true);
        }
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

    /// <summary>Checks native precision formatting and detached responses in the adapter.</summary>
    [Fact]
    public async Task AdapterFormatsTheActualQuantityAndPrice()
    {
        var native = new CapturingExchange();
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet);
        await using var service = new CcxtTradingService(profile, new(native, "binance"));
        var key = new InstrumentKey("binance", "spot", "", "BTC/USDT");
        var order = await service.CreateLimitOrderAsync(new(key, OrderSide.Sell, 1.23456m, 100.123m));
        Assert.Equal(1.2345d, native.Amount);
        Assert.Equal(100.12d, native.Price);
        Assert.Equal("sell", native.Side);
        Assert.Equal("1", order.Reference.ExchangeOrderId);
    }

    /// <summary>Timeouts invoke the transport only once and never become a fabricated exchange rejection.</summary>
    [Fact]
    public async Task AdapterDoesNotRetryUnknownOutcomes()
    {
        var native = new CapturingExchange { Failure = new ccxt.RequestTimeout("signed url must not be displayed") };
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Testnet);
        await using var service = new CcxtTradingService(profile, new(native, "binance"));
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
        await using var client = new CcxtTradingClient(native, "binance");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CreateSpotMarketOrderAsync("BTC/USDT", "buy", 1, stop.Token));
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
        await using var client = CcxtTradingClientFactory.Create(new(Guid.NewGuid(), "Fake", venue, environment, market), credentials);
        var transport = client.Transport;
        var api = (Dictionary<string, object>)((Dictionary<string, object>)transport.urls)["api"];
        var endpoint = venue == "binance" && market == TradingMarket.UsdtFutures ? "fapiPrivate" : "private";
        Assert.Contains(expectedHost, transport.implodeHostname(api[endpoint]));
        Assert.False(transport.verbose);
        Assert.Equal(market == TradingMarket.Spot ? "spot" : venue == "binance" ? "future" : "swap", transport.options["defaultType"]);
    }

    /// <summary>Captures native unified calls; unimplemented operations cannot contact an exchange.</summary>
    internal sealed class CapturingExchange : ccxt.Exchange
    {
        public int Calls;
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
            setMarkets(new List<object>
            {
                new Dictionary<string, object>
                {
                    ["id"] = "BTCUSDT", ["symbol"] = "BTC/USDT", ["base"] = "BTC", ["quote"] = "USDT",
                    ["spot"] = true, ["type"] = "spot",
                    ["precision"] = new Dictionary<string, object> { ["amount"] = .0001, ["price"] = .01 }
                }
            });
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
