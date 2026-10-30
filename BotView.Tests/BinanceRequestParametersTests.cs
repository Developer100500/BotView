using BotView.Models;
using BotView.Services;
using BotView.Services.Integrations.Ccxt;

namespace BotView.Tests;

public sealed class BinanceRequestParametersTests
{
    /// <summary>Exercises real CCXT request construction while replacing all HTTP with an in-memory response.</summary>
    [Fact]
    public async Task SpotReconciliationNeverSendsTypeOrSubTypeToBinance()
    {
        var transport = new CapturingBinance();
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Fake", "binance", TradingEnvironment.Production);
        using var credentials = new AccountCredentials("fake", "fake");
        await using var client = new CcxtSpotTradingClient(profile, credentials, transport);
        var instrument = new InstrumentKey("binance", "spot", "", "BTC/USDT");

        await client.OrdersAsync(new(instrument), true, CancellationToken.None);
        await client.ExecutionsAsync(new(instrument, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow), CancellationToken.None);
        await client.BalanceAsync(new(profile.Id.ToString(), "owner", "binance", profile.Environment), CancellationToken.None);
        await client.FindAsync(instrument, "bv-original", CancellationToken.None);

        Assert.Contains(transport.Requests, uri => uri.AbsolutePath == "/api/v3/openOrders");
        Assert.Contains(transport.Requests, uri => uri.AbsolutePath == "/api/v3/myTrades");
        Assert.Contains(transport.Requests, uri => uri.AbsolutePath == "/api/v3/account");
        Assert.Contains(transport.Requests, uri => uri.AbsolutePath == "/api/v3/order");
        Assert.All(transport.Requests, uri =>
        {
            Assert.DoesNotContain("subType=", uri.Query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("type=", uri.Query, StringComparison.OrdinalIgnoreCase);
        });
    }

    private sealed class CapturingBinance : ccxt.binance
    {
        public List<Uri> Requests { get; } = new();

        /// <summary>Seeds public markets locally; no method in this fixture can access the network.</summary>
        public CapturingBinance() : base(new Dictionary<string, object>
        {
            ["apiKey"] = "fake",
            ["secret"] = "fake",
            ["enableRateLimit"] = false,
            ["options"] = new Dictionary<string, object> { ["fetchCurrencies"] = false }
        })
        {
            setMarkets(new List<object>
            {
                new Dictionary<string, object>
                {
                    ["id"] = "BTCUSDT", ["symbol"] = "BTC/USDT",
                    ["base"] = "BTC", ["quote"] = "USDT", ["spot"] = true, ["type"] = "spot",
                    ["precision"] = new Dictionary<string, object> { ["price"] = .01, ["amount"] = .00001 }
                }
            });
        }

        /// <summary>Captures final signed URLs and supplies API-shaped data without sending an HTTP request.</summary>
        public override Task<object> fetch(object url, object method = null!, object headers = null!, object body = null!)
        {
            var uri = new Uri(url.ToString()!);
            Requests.Add(uri);
            object response = uri.AbsolutePath switch
            {
                "/api/v3/openOrders" or "/api/v3/myTrades" => new List<object>(),
                "/api/v3/account" => new Dictionary<string, object> { ["balances"] = new List<object>() },
                "/api/v3/order" => new Dictionary<string, object>
                {
                    ["symbol"] = "BTCUSDT", ["orderId"] = "1", ["clientOrderId"] = "bv-original",
                    ["status"] = "NEW", ["type"] = "LIMIT", ["side"] = "BUY",
                    ["price"] = "77000", ["origQty"] = "0.0001", ["executedQty"] = "0"
                },
                _ => throw new InvalidOperationException("Unexpected endpoint in the offline request fixture.")
            };

            return Task.FromResult(response);
        }
    }
}
