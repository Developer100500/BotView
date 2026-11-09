using System.Text.Json;

namespace BotView.Tests;

public sealed class LeverageRequestTests
{
    /// <summary>Exercises actual CCXT signing and payload construction with HTTP replaced entirely.</summary>
    [Theory]
    [InlineData("binance")]
    [InlineData("bybit")]
    public async Task NativeRequestsSetFuturesLeverageWithoutRoutingFlags(string venue)
    {
        var requests = new List<(Uri Url, string Body)>();
        object Respond(object url, object body)
        {
            requests.Add((new Uri(url.ToString()!), body?.ToString() ?? ""));
            return venue == "binance"
                ? new Dictionary<string, object> { ["leverage"] = 10, ["symbol"] = "BTCUSDT", ["maxNotionalValue"] = "1000000" }
                : new Dictionary<string, object> { ["retCode"] = 0, ["retMsg"] = "OK", ["result"] = new Dictionary<string, object>() };
        }

        var settings = new Dictionary<string, object>
        {
            ["apiKey"] = "fake", ["secret"] = "fake", ["enableRateLimit"] = false,
            ["options"] = new Dictionary<string, object> { ["fetchCurrencies"] = false }
        };
        ccxt.Exchange native = venue == "binance" ? new OfflineBinance(settings, Respond) : new OfflineBybit(settings, Respond);
        native.setMarkets(new List<object>
        {
            new Dictionary<string, object>
            {
                ["id"] = "BTCUSDT", ["symbol"] = "BTC/USDT:USDT", ["base"] = "BTC", ["quote"] = "USDT",
                ["spot"] = false, ["contract"] = true, ["linear"] = true, ["inverse"] = false,
                ["type"] = "swap", ["swap"] = true, ["settle"] = "USDT"
            }
        });
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", venue, TradingEnvironment.Testnet, TradingMarket.UsdtFutures);
        await using var service = new FuturesTradingService(profile, native);

        await service.SetLeverageAsync(new(venue, "futures", "", "BTC/USDT:USDT"), 10);

        var request = Assert.Single(requests);
        Assert.DoesNotContain("type=", request.Body);
        Assert.DoesNotContain("type=", request.Url.Query);
        if (venue == "binance")
        {
            Assert.Equal("/fapi/v1/leverage", request.Url.AbsolutePath);
            Assert.Contains("symbol=BTCUSDT", request.Body + request.Url.Query);
            Assert.Contains("leverage=10", request.Body + request.Url.Query);
        }
        else
        {
            Assert.Equal("/v5/position/set-leverage", request.Url.AbsolutePath);
            using var json = JsonDocument.Parse(request.Body);
            Assert.Equal("linear", json.RootElement.GetProperty("category").GetString());
            Assert.Equal("BTCUSDT", json.RootElement.GetProperty("symbol").GetString());
            Assert.Equal("10", json.RootElement.GetProperty("buyLeverage").GetString());
            Assert.Equal("10", json.RootElement.GetProperty("sellLeverage").GetString());
            Assert.False(json.RootElement.TryGetProperty("type", out _));
        }
    }

    private sealed class OfflineBinance : ccxt.binance
    {
        private readonly Func<object, object, object> _respond;

        /// <summary>Provides fake credentials and intercepts all HTTP requests.</summary>
        public OfflineBinance(Dictionary<string, object> settings, Func<object, object, object> respond) : base(settings)
        {
            _respond = respond;
        }

        /// <summary>Returns an API-shaped response without accessing the network.</summary>
        public override Task<object> fetch(object url, object method = null!, object headers = null!, object body = null!) =>
            Task.FromResult(_respond(url, body));
    }

    private sealed class OfflineBybit : ccxt.bybit
    {
        private readonly Func<object, object, object> _respond;

        /// <summary>Provides fake credentials and intercepts all HTTP requests.</summary>
        public OfflineBybit(Dictionary<string, object> settings, Func<object, object, object> respond) : base(settings)
        {
            _respond = respond;
        }

        /// <summary>Returns an API-shaped response without accessing the network.</summary>
        public override Task<object> fetch(object url, object method = null!, object headers = null!, object body = null!) =>
            Task.FromResult(_respond(url, body));
    }
}
