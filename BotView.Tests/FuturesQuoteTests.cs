using System.Net;
using System.Net.Http;
using BotView.Interfaces;
using BotView.Models;
using BotView.Services;
using BotView.ViewModels;

namespace BotView.Tests;

public class FuturesQuoteTests
{
    [Fact]
    public async Task YahooProvider_MapsCurrentPriceAndIntradayOpens()
    {
        var atNineThirty = new DateTimeOffset(2026, 9, 29, 9, 30, 0, TimeSpan.FromHours(-4));
        var atTen = atNineThirty.AddMinutes(30);
        var json = $$$"""
            {"chart":{"result":[{"meta":{"regularMarketPrice":5012.5,"previousClose":4995.0,"regularMarketTime":{{{atTen.ToUnixTimeSeconds()}}}},
            "timestamp":[{{{atNineThirty.ToUnixTimeSeconds()}}},{{{atTen.ToUnixTimeSeconds()}}}],
            "indicators":{"quote":[{"open":[5000.0,null]}]}}],"error":null}}
            """;
        var handler = new StubHandler(json);
        using var client = new HttpClient(handler);
        var provider = new YahooFinanceQuoteProvider(client);

        var quote = await provider.GetQuoteAsync("ES=F");

        Assert.Equal("https://query1.finance.yahoo.com/v8/finance/chart/ES%3DF?range=1d&interval=5m", handler.RequestUri);
        Assert.Equal(5012.5m, quote.LastPrice);
        Assert.Equal(4995m, quote.PreviousClose);
        Assert.Equal(atTen, quote.AsOf);
        Assert.Single(quote.Bars);
        Assert.Equal(5000m, quote.Bars[0].Open);
        Assert.Equal(12.5m, MorningMovementCalculator.Calculate(quote)!.ChangePoints);
        Assert.Equal(0.25m, MorningMovementCalculator.Calculate(quote)!.ChangePercent);
    }

    [Theory]
    [InlineData(8, 55)]
    [InlineData(18, 0)]
    public void MorningMovement_RequiresCurrentDayAndMorningSession(int hour, int minute)
    {
        var morning = new DateTimeOffset(2026, 9, 29, 9, 30, 0, TimeSpan.FromHours(-4));
        var quote = new MarketQuote("ES=F", 5020m,
            new DateTimeOffset(2026, 9, 29, hour, minute, 0, TimeSpan.FromHours(-4)),
            [new QuoteBar(morning, 5000m)], "test");

        Assert.Null(MorningMovementCalculator.Calculate(quote));
    }

    [Fact]
    public void MorningMovement_DoesNotCompareWithPreviousDayOpen()
    {
        var yesterday = new DateTimeOffset(2026, 9, 28, 9, 30, 0, TimeSpan.FromHours(-4));
        var today = yesterday.AddDays(1).AddHours(2);
        var quote = new MarketQuote("ES=F", 5020m, today,
            [new QuoteBar(yesterday, 5000m)], "test");

        Assert.Null(MorningMovementCalculator.Calculate(quote));
    }

    [Fact]
    public void MorningMovement_UsesNewYorkClockAfterDaylightSavingChange()
    {
        var morning = new DateTimeOffset(2026, 11, 3, 9, 30, 0, TimeSpan.FromHours(-5));
        var quote = new MarketQuote("ES=F", 4990m, morning.AddMinutes(20),
            [new QuoteBar(morning, 5000m)], "test");

        Assert.Equal(-10m, MorningMovementCalculator.Calculate(quote)!.ChangePoints);
    }

    [Fact]
    public async Task Widget_UsesPreviousCloseUntilNineThirtyAndLabelsIt()
    {
        var asOf = new DateTimeOffset(2026, 9, 29, 9, 12, 0, TimeSpan.FromHours(-4));
        var quote = new MarketQuote("ES=F", 7756.50m, asOf, [], "Yahoo Finance",
            PreviousClose: 7746.75m);
        using var widget = new FuturesQuoteViewModel(new StaticQuoteProvider(quote));

        await widget.RefreshAsync();

        Assert.Equal("+9,75 п. (+0,13%)", widget.ChangeText);
        Assert.StartsWith("От предыдущего закрытия", widget.StatusText);
    }

    [Fact]
    public async Task Widget_SwitchesToNineThirtyOpenWhenBarAppears()
    {
        var open = new DateTimeOffset(2026, 9, 29, 9, 30, 0, TimeSpan.FromHours(-4));
        var quote = new MarketQuote("ES=F", 7756.50m, open.AddMinutes(10),
            [new QuoteBar(open, 7750m)], "Yahoo Finance", PreviousClose: 7746.75m);
        using var widget = new FuturesQuoteViewModel(new StaticQuoteProvider(quote));

        await widget.RefreshAsync();

        Assert.Equal("+6,50 п. (+0,08%)", widget.ChangeText);
        Assert.StartsWith("От 09:30 NY", widget.StatusText);
    }

    private sealed class StaticQuoteProvider(MarketQuote quote) : IQuoteProvider
    {
        public Task<MarketQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(quote);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
        }
    }
}
