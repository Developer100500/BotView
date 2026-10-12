using System.Net.Http;
using BotView.Interfaces;
using BotView.Models;
using BotView.Services;
using BotView.ViewModels;
using ccxt;
using Moq;

namespace BotView.Tests;

public sealed class MarketMoversTests
{
    [Fact]
    public void CalculatorUsesOnlyActiveSpotUsdtMarketsAndValidChanges()
    {
        var markets = new[]
        {
            Spot("BTC/USDT"), Spot("ETH/USDT"), Spot("BAD/USDT"), Spot("MISSING/USDT"),
            Spot("BTC/USDT"),
            Spot("OFF/USDT", active: false),
            Spot("BTC/USDT:USDT", spot: false),
            Spot("ETH/BTC", quote: "BTC")
        };
        var tickers = markets.Select(m => m.symbol).Distinct().ToDictionary(s => s,
            _ => new Ticker { last = 110, open = 100, percentage = 99 });
        tickers["BTC/USDT"] = new Ticker { last = 110, open = 100, percentage = 7 };
        tickers["ETH/USDT"] = new Ticker { close = 90, open = 100 };
        tickers["BAD/USDT"] = new Ticker { last = 1, percentage = double.NaN };
        tickers.Remove("MISSING/USDT");

        var result = MarketMoversCalculator.Calculate(markets, tickers);

        Assert.Equal(2, result.Count);
        Assert.Equal(7, result.Single(m => m.Symbol == "BTC/USDT").ChangePercent);
        Assert.Equal(-10, result.Single(m => m.Symbol == "ETH/USDT").ChangePercent, 8);
    }

    [Theory]
    [InlineData(0, 100, null)]
    [InlineData(100, 0, null)]
    [InlineData(100, 100, double.PositiveInfinity)]
    [InlineData(double.NaN, 100, 10d)]
    public void CalculatorSkipsUnusablePricesOrChanges(double last, double open, double? percentage)
    {
        Assert.Empty(MarketMoversCalculator.Calculate(new[] { Spot("BTC/USDT") },
            new Dictionary<string, Ticker>
            {
                ["BTC/USDT"] = new Ticker { last = last, open = open, percentage = percentage }
            }));
    }

    [Fact]
    public async Task RanksTenInEachDirectionAndExcludesUnchangedCoins()
    {
        var movers = Enumerable.Range(-15, 31)
            .Select(i => new MarketMover($"COIN{i}/USDT", i)).Reverse().ToArray();
        var service = new Mock<IExchangeService>();
        service.Setup(s => s.GetMarketMoversAsync("binance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(movers);
        using var viewModel = new MarketMoversViewModel(service.Object);

        await viewModel.SetExchangeAsync("binance");

        Assert.Equal(Enumerable.Range(-15, 10).Select(i => (double)i), viewModel.Losers.Select(m => m.ChangePercent));
        Assert.Equal(Enumerable.Range(6, 10).Reverse().Select(i => (double)i), viewModel.Gainers.Select(m => m.ChangePercent));
    }

    [Fact]
    public async Task ExchangeChangeCancelsAndIgnoresOldResponse()
    {
        var oldResponse = new TaskCompletionSource<IReadOnlyList<MarketMover>>();
        var service = new Mock<IExchangeService>();
        CancellationToken oldToken = default;
        service.Setup(s => s.GetMarketMoversAsync("binance", It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken ct) => { oldToken = ct; return oldResponse.Task; });
        service.Setup(s => s.GetMarketMoversAsync("bybit", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new MarketMover("NEW/USDT", 5) });
        using var viewModel = new MarketMoversViewModel(service.Object);

        var oldLoad = viewModel.SetExchangeAsync("binance");
        await viewModel.SetExchangeAsync("bybit");
        oldResponse.SetResult(new[] { new MarketMover("OLD/USDT", -30) });
        await oldLoad;

        Assert.True(oldToken.IsCancellationRequested);
        Assert.Empty(viewModel.Losers);
        Assert.Equal("NEW/USDT", Assert.Single(viewModel.Gainers).Symbol);
        Assert.StartsWith("Обновлено", viewModel.StatusText);
    }

    [Fact]
    public async Task FailedRefreshRetainsRankingAndMarksItStale()
    {
        var service = new Mock<IExchangeService>();
        service.SetupSequence(s => s.GetMarketMoversAsync("binance", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new MarketMover("BTC/USDT", 5) })
            .ThrowsAsync(new HttpRequestException("offline"));
        using var viewModel = new MarketMoversViewModel(service.Object);

        await viewModel.SetExchangeAsync("binance");
        await viewModel.RefreshAsync();

        Assert.Single(viewModel.Gainers);
        Assert.Contains("Данные устарели", viewModel.StatusText);
    }

    [Fact]
    public async Task DisposeCancelsRequestAndPreventsLateUpdates()
    {
        var response = new TaskCompletionSource<IReadOnlyList<MarketMover>>();
        var service = new Mock<IExchangeService>();
        CancellationToken token = default;
        service.Setup(s => s.GetMarketMoversAsync("binance", It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken ct) => { token = ct; return response.Task; });
        var viewModel = new MarketMoversViewModel(service.Object);
        var load = viewModel.SetExchangeAsync("binance");

        viewModel.Dispose();
        response.SetResult(new[] { new MarketMover("BTC/USDT", 5) });
        await load;
        await viewModel.RefreshAsync();

        Assert.True(token.IsCancellationRequested);
        Assert.Empty(viewModel.Gainers);
        service.Verify(s => s.GetMarketMoversAsync("binance", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static MarketInterface Spot(string symbol, string quote = "USDT", bool spot = true, bool active = true) =>
        new(new Dictionary<string, object>()) { symbol = symbol, quote = quote, spot = spot, active = active };
}
