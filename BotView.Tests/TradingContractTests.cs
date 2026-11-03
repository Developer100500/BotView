using BotView.Models;
using BotView.Services;
using BotView.Services.Integrations.Ccxt;

namespace BotView.Tests;

public sealed class TradingContractTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    /// <summary>Creates deterministic listing metadata for each price/quantity convention.</summary>
    private static Instrument Instrument(QuantityUnit quantityUnit, PriceQuotation quotation) => new(
        new("MOEX", "test", "BOARD", "SEC"), "BOARD:SEC", "Test instrument", "RUB", LotSize: 10m, PriceStep: .01m,
        TradingRules: new(quantityUnit, quotation,
            FaceValue: 1000m, AccruedInterest: 25m, TickValue: 5m, ContractSize: .01m));

    /// <summary>Checks money formulas and prevents futures notional from being treated as margin.</summary>
    [Fact]
    public void MonetaryValuesRespectLotsNominalAccruedInterestAndTickValue()
    {
        var crypto = Instrument(QuantityUnit.BaseAsset, PriceQuotation.CurrencyPerUnit) with { Currency = "USDT" };
        Assert.Equal(new Money(1000m, "USDT"), TradingValueCalculator.Calculate(crypto, .5m, 2000m));
        var shares = Instrument(QuantityUnit.Lots, PriceQuotation.CurrencyPerUnit);
        Assert.Equal(new Money(5000m, "RUB"), TradingValueCalculator.Calculate(shares, 2m, 250m));
        var bond = Instrument(QuantityUnit.Lots, PriceQuotation.PercentOfFaceValue) with { LotSize = 1m };
        Assert.Equal(new Money(2020m, "RUB"), TradingValueCalculator.Calculate(bond, 2m, 98.5m));
        Assert.Equal(new Money(1970m, "RUB"), TradingValueCalculator.Calculate(bond, 2m, 98.5m, includeAccruedInterest: false));
        var future = Instrument(QuantityUnit.Contracts, PriceQuotation.Points) with { PriceStep = 10m };
        Assert.Equal(new Money(15000m, "RUB"), TradingValueCalculator.Calculate(future, 3m, 10000m));
        var linearContract = Instrument(QuantityUnit.Contracts, PriceQuotation.CurrencyPerUnit);
        Assert.Equal(new Money(60m, "RUB"), TradingValueCalculator.Calculate(linearContract, 3m, 2000m));
    }

    /// <summary>Unknown valuation metadata and unsupported nonlinear formulas never produce invented money.</summary>
    [Fact]
    public void MissingOrUnsupportedValuationIsExplicitlyUnknown()
    {
        var bond = Instrument(QuantityUnit.Lots, PriceQuotation.PercentOfFaceValue);
        bond = bond with { TradingRules = bond.TradingRules! with { AccruedInterest = null } };
        Assert.Null(TradingValueCalculator.Calculate(bond, 1, 98));
        Assert.NotNull(TradingValueCalculator.Calculate(bond, 1, 98, includeAccruedInterest: false));
        var future = Instrument(QuantityUnit.Contracts, PriceQuotation.Points);
        Assert.Null(TradingValueCalculator.Calculate(future with { TradingRules = future.TradingRules! with { TickValue = null } }, 1, 100));
        Assert.Null(TradingValueCalculator.Calculate(future with { Currency = null }, 1, 100));
        Assert.Null(TradingValueCalculator.Calculate(Instrument(QuantityUnit.Contracts, PriceQuotation.ProviderSpecific), 1, 100));
        Assert.Null(TradingValueCalculator.Calculate(Instrument(QuantityUnit.BaseAsset, PriceQuotation.CurrencyPerUnit), decimal.MaxValue, 2));
        var foreignShares = Instrument(QuantityUnit.Lots, PriceQuotation.CurrencyPerUnit) with { Currency = "USD" };
        foreignShares = foreignShares with { TradingRules = foreignShares.TradingRules! with { SettlementCurrency = "RUB" } };
        Assert.Null(TradingValueCalculator.Calculate(foreignShares, 1, 100));
    }

    /// <summary>Ensures an amortized bond uses current nominal instead of assuming the original issue nominal.</summary>
    [Fact]
    public void BondValuationUsesCurrentOutstandingNominal()
    {
        var bond = Instrument(QuantityUnit.Lots, PriceQuotation.PercentOfFaceValue) with { LotSize = 1m };
        bond = bond with { TradingRules = bond.TradingRules! with { FaceValue = 600m } };
        Assert.Equal(new Money(1232m, "RUB"), TradingValueCalculator.Calculate(bond, 2m, 98.5m));
    }

    /// <summary>Order-book monetary bars use bond cash value and display missing data as unknown.</summary>
    [Fact]
    public void OrderBookValuesBondLevelsBeforeGroupingAndDoesNotGuessMissingNominal()
    {
        var bond = Instrument(QuantityUnit.Lots, PriceQuotation.PercentOfFaceValue) with { LotSize = 1m };
        var market = new OrderBookMarket("BOND", "lots", "RUB", .01m, bond);
        var book = new OrderBookSnapshot(market, new[] { new OrderBookLevel(98.50m, 2m) },
            new[] { new OrderBookLevel(98.52m, 1m) }, Now);
        var row = OrderBookLadder.Build(book, 1, 98.51m, 7, 4040m, OrderBookVolumeCurrency.Quote).Single(r => r.Price == 98.5m);
        Assert.Equal(2020m, row.Cost);
        Assert.Equal(.5, row.VolumeFraction);
        var missing = bond with { TradingRules = bond.TradingRules! with { FaceValue = null } };
        var unknown = book with { Market = market with { Instrument = missing } };
        row = OrderBookLadder.Build(unknown, 1, 98.51m, 7, volumeCurrency: OrderBookVolumeCurrency.Quote).Single(r => r.Price == 98.5m);
        Assert.Null(row.Cost);
        Assert.Equal(2m, row.Amount);
        Assert.Equal(0, row.VolumeFraction);
    }

    /// <summary>Maps real CCXT metadata without losing fractional quantity precision or cost limits.</summary>
    [Fact]
    public void CcxtSpotMetadataPreservesUnitsAndLimits()
    {
        var market = new ccxt.MarketInterface
        {
            symbol = "ETH/USDT", quote = "USDT", precision = new ccxt.Precision { amount = .001, price = .01 },
            limits = new ccxt.Limits { amount = new ccxt.MinMax { min = .01, max = 10 }, cost = new ccxt.MinMax { min = 5 } }
        };
        var instrument = CcxtMarketDataProvider.MapInstrument("binance", market, 4);
        Assert.Equal(.01m, instrument.PriceStep);
        Assert.Equal(.001m, instrument.TradingRules!.QuantityStep);
        Assert.Equal(5m, instrument.TradingRules.MinNotional);
        Assert.Equal(QuantityUnit.BaseAsset, instrument.TradingRules.QuantityUnit);
        Assert.Null(CcxtMarketDataProvider.MapInstrument("binance", market, 3).TradingRules!.QuantityStep);
    }
}
