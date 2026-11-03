using BotView.Models;
using Moq;

namespace BotView.Tests;

public sealed class OrderInputCalculatorTests
{
    /// <summary>Quote budgets respect native contract size and provider rounding for both supported markets.</summary>
    [Theory]
    [InlineData("spot", 1, 0.333, 99.9)]
    [InlineData("futures", 0.01, 33.333, 99.999)]
    public void QuoteBudgetUsesFormattedQuantity(string market, decimal contractSize, decimal expectedQuantity, decimal expectedNotional)
    {
        var key = new InstrumentKey("binance", market, "", "BTC/USDT");
        var service = new Mock<ITradingService>(MockBehavior.Strict);
        service.Setup(s => s.AmountToPrecision(key, It.IsAny<decimal>()))
            .Returns((InstrumentKey _, decimal value) => decimal.Floor(value * 1000) / 1000);
        var instrument = new Instrument(key, "BTC/USDT", "Bitcoin", "USDT",
            TradingRules: new(QuantityUnit.Contracts, PriceQuotation.CurrencyPerUnit, ContractSize: contractSize));
        var calculator = new OrderInputCalculator(service.Object, instrument);

        var quantity = calculator.ConvertQuantity(100m, 300m, OrderQuantityCurrency.Quote);

        Assert.Equal(expectedQuantity, quantity);
        Assert.Equal(expectedNotional, calculator.OrderNotional(quantity, 300m));
    }

    /// <summary>Market quantities need no price, and spot metadata without contract size defaults to one.</summary>
    [Fact]
    public void BaseQuantityAndPriceUseProviderPrecision()
    {
        var key = new InstrumentKey("bybit", "spot", "", "ETH/USDT");
        var service = new Mock<ITradingService>(MockBehavior.Strict);
        service.Setup(s => s.AmountToPrecision(key, 2.1234m)).Returns(2.123m);
        service.Setup(s => s.PriceToPrecision(key, 100.129m)).Returns(100.12m);
        var calculator = new OrderInputCalculator(service.Object, new Instrument(key, "ETH/USDT", "Ethereum"));

        Assert.Equal(2.123m, calculator.ConvertQuantity(2.1234m, 0m, OrderQuantityCurrency.Base));
        Assert.Equal(100.12m, calculator.PriceToPrecision(100.129m));
        Assert.Equal(200m, calculator.OrderNotional(2m, 100m));
    }
}
