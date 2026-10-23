using BotView.Chart;
using BotView.Configuration;
using BotView.Database;
using BotView.Interfaces;
using BotView.Services;
using BotView.Services.Providers;
using BotView.ViewModels;
using Moq;

namespace BotView.Tests;

public sealed class MarketCatalogTests
{
    [Fact]
    public void ExchangeListsAndFactoriesUseTheSameRegistry()
    {
        var exchangeService = new ExchangeService(CcxtMarketDataProvider.CreateDefaults());
        using var viewModel = new MainWindowViewModel(
            new DatabaseService(), new DataProvider(), exchangeService,
            new Mock<IMarketDataService>().Object, new MetricsController(exchangeService));

        Assert.Equal(MarketCatalog.Exchanges.Select(exchange => exchange.Id),
            exchangeService.GetExchanges().Select(d => d.Id));
        Assert.Equal(MarketCatalog.Exchanges.Select(exchange => exchange.Id),
            exchangeService.GetSupportedExchanges());
        Assert.Equal(MarketCatalog.Exchanges.Select(exchange => exchange.Id),
            viewModel.Exchanges.Select(exchange => exchange.Id));
        Assert.Equal(MarketCatalog.Exchanges.Select(exchange => exchange.DisplayName),
            ExchangeConfig.AvailableExchanges);

        foreach (var exchange in MarketCatalog.Exchanges)
        {
            Assert.Contains(exchangeService.GetExchanges(), d => d.Id.Equals(exchange.Id.ToUpperInvariant(), StringComparison.OrdinalIgnoreCase));
            Assert.Equal(exchange.DisplayName, exchangeService.GetExchanges().Single(d => d.Id == exchange.Id).DisplayName);
            Assert.Equal(exchange.Id, CcxtMarketDataProvider.CreateClient(exchange.Id).GetType().Name);
            Assert.Equal(exchange.SupportedTimeframes, ExchangeConfig.GetSupportedTimeframes(exchange.Id));
        }
    }

    [Fact]
    public void TimeframeDurationsAndDemoCountsUseTheSameRegistry()
    {
        var exchangeService = new ExchangeService(CcxtMarketDataProvider.CreateDefaults());
        var controller = new ChartController(new ChartState());
        var dataProvider = new DataProvider();

        Assert.Equal(MarketCatalog.TimeframeIds, exchangeService.GetSupportedTimeframes());
        Assert.Equal(MarketCatalog.TimeframeIds, ExchangeConfig.SupportedTimeframes);

        foreach (var timeframe in MarketCatalog.Timeframes)
        {
            Assert.Equal(timeframe.Duration, controller.ParseTimeframe(timeframe.Id));
            Assert.Equal(timeframe.DemoCandleCount,
                dataProvider.LoadDemoData(timeframe.Id).Length);
        }
    }
}
