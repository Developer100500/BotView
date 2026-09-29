using BotView.Chart;
using BotView.Configuration;
using BotView.Database;
using BotView.Interfaces;
using BotView.Services;
using BotView.ViewModels;
using Moq;

namespace BotView.Tests;

public sealed class MarketCatalogTests
{
    [Fact]
    public void ExchangeListsAndFactoriesUseTheSameRegistry()
    {
        var exchangeService = new ExchangeService();
        using var viewModel = new MainWindowViewModel(
            new DatabaseService(), new DataProvider(), exchangeService,
            new Mock<IMarketDataService>().Object, new MetricsController(exchangeService));

        Assert.Equal(MarketCatalog.Exchanges.Select(exchange => exchange.Id),
            ExchangeFactory.GetSupportedExchanges());
        Assert.Equal(MarketCatalog.Exchanges.Select(exchange => exchange.Id),
            exchangeService.GetSupportedExchanges());
        Assert.Equal(MarketCatalog.Exchanges.Select(exchange => exchange.Id),
            viewModel.Exchanges.Select(exchange => exchange.Id));
        Assert.Equal(MarketCatalog.Exchanges.Select(exchange => exchange.DisplayName),
            ExchangeConfig.AvailableExchanges);

        foreach (var exchange in MarketCatalog.Exchanges)
        {
            Assert.True(ExchangeFactory.IsExchangeSupported(exchange.Id.ToUpperInvariant()));
            Assert.Equal(exchange.DisplayName, ExchangeFactory.GetExchangeDisplayName(exchange.Id));
            Assert.Equal(exchange.Id, ExchangeFactory.CreateExchange(exchange.Id).GetType().Name);
            Assert.Equal(exchange.SupportedTimeframes, ExchangeConfig.GetSupportedTimeframes(exchange.Id));
        }
    }

    [Fact]
    public void TimeframeDurationsAndDemoCountsUseTheSameRegistry()
    {
        var exchangeService = new ExchangeService();
        var controller = new ChartController(new ChartModel());
        var dataProvider = new DataProvider();

        Assert.Equal(MarketCatalog.TimeframeIds, exchangeService.GetSupportedTimeframes());
        Assert.Equal(MarketCatalog.TimeframeIds, ExchangeConfig.SupportedTimeframes);

        foreach (var timeframe in MarketCatalog.Timeframes)
        {
            Assert.Equal(timeframe.Duration, controller.ParseTimeframe(timeframe.Id));
            Assert.Equal(timeframe.DemoCandleCount,
                dataProvider.LoadDemoData(timeframe.Id).candles.Length);
        }
    }
}
