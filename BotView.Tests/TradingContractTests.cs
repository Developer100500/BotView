using BotView.Models;
using BotView.Services;
using BotView.Services.Providers;

namespace BotView.Tests;

public sealed class TradingContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly TradingCapabilities Capabilities = new(
        new HashSet<OrderType>(Enum.GetValues<OrderType>()), new HashSet<OrderTimeInForce>(Enum.GetValues<OrderTimeInForce>()));

    /// <summary>Creates deterministic listing metadata for each price/quantity convention.</summary>
    private static Instrument Instrument(QuantityUnit quantityUnit, PriceQuotation quotation) => new(
        new("MOEX", "test", "BOARD", "SEC"), "BOARD:SEC", "Test instrument", "RUB", LotSize: 10m, PriceStep: .01m,
        TradingRules: new(quantityUnit, quotation,
            FaceValue: 1000m, AccruedInterest: 25m, TickValue: 5m, ContractSize: .01m));

    /// <summary>Creates a valid order and binds it to the same venue/account.</summary>
    private static OrderValidationResult Validate(PlaceOrderRequest request, Instrument instrument, TradingCapabilities? capabilities = null) =>
        TradingOrderValidator.Validate(request, instrument, capabilities ?? Capabilities,
            new("connection", "account", instrument.Key.Venue, TradingEnvironment.Testnet), Now);

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

    /// <summary>Rejects fractional MOEX lots/contracts but preserves fractional base-asset quantities.</summary>
    [Theory]
    [InlineData(QuantityUnit.Lots)]
    [InlineData(QuantityUnit.Contracts)]
    public void OrderQuantityUsesDeclaredUnitsAndNeverRounds(QuantityUnit unit)
    {
        var instrument = Instrument(unit, PriceQuotation.CurrencyPerUnit);
        var order = new PlaceOrderRequest(instrument.Key, OrderSide.Buy, OrderType.Limit, 1.5m, "client-1", Price: 100m);
        Assert.Null(instrument.TradingRules!.QuantityStep);
        Assert.Null(instrument.TradingRules.MinQuantity);
        Assert.Contains(Validate(order, instrument).Errors, e => e.Code == "WholeQuantityRequired");
        foreach (var invalid in new[] { -1m, 0m, .5m, 1.5m })
            Assert.False(Validate(order with { Quantity = invalid }, instrument).IsValid);
        Assert.True(Validate(order with { Quantity = 1m }, instrument).IsValid);
        Assert.True(Validate(order with { Quantity = 2m }, instrument).IsValid);
        Assert.Equal(1.5m, order.Quantity);
        var crypto = instrument with
        {
            Key = new("binance", "spot", "", "ETH/USDT"),
            TradingRules = instrument.TradingRules! with { QuantityUnit = QuantityUnit.BaseAsset, QuantityStep = .001m, MinQuantity = .01m }
        };
        var cryptoOrder = order with { Instrument = crypto.Key };
        Assert.True(Validate(cryptoOrder, crypto).IsValid);
        Assert.Contains(Validate(cryptoOrder with { Quantity = 1.0001m }, crypto).Errors, e => e.Code == "QuantityStep");
        Assert.Contains(Validate(cryptoOrder with { Quantity = .001m }, crypto).Errors, e => e.Code == "MinQuantity");
        var fractionalContracts = crypto with { TradingRules = crypto.TradingRules! with { QuantityUnit = QuantityUnit.Contracts, QuantityStep = .1m } };
        Assert.True(Validate(cryptoOrder, fractionalContracts).IsValid);
    }

    /// <summary>Validates market, stop and stop-limit field combinations separately.</summary>
    [Fact]
    public void MarketAndConditionalOrdersRequireDifferentPrices()
    {
        var instrument = Instrument(QuantityUnit.Lots, PriceQuotation.CurrencyPerUnit);
        var market = new PlaceOrderRequest(instrument.Key, OrderSide.Buy, OrderType.Market, 1m, "market-1");
        Assert.True(Validate(market, instrument).IsValid);
        Assert.Contains(Validate(market with { Price = 100m }, instrument).Errors, e => e.Code == "LimitPrice");
        var stop = market with { Type = OrderType.Stop, TriggerPrice = 100m, TriggerCondition = TriggerCondition.AtOrAbove };
        Assert.True(Validate(stop, instrument).IsValid);
        Assert.Contains(Validate(stop with { TriggerCondition = null }, instrument).Errors, e => e.Code == "TriggerPrice");
        var stopLimit = stop with { Type = OrderType.StopLimit, Price = 100.01m };
        Assert.True(Validate(stopLimit, instrument).IsValid);
        Assert.Contains(Validate(stopLimit with { TriggerPrice = 100.001m }, instrument).Errors, e => e.Code == "PriceStep");
    }

    /// <summary>Checks instrument/account identity and unsupported provider options.</summary>
    [Fact]
    public void CapabilitiesAndListingIdentityAreValidated()
    {
        var instrument = Instrument(QuantityUnit.Lots, PriceQuotation.CurrencyPerUnit);
        var order = new PlaceOrderRequest(instrument.Key, OrderSide.Buy, OrderType.Limit, 1m, "client", Price: 100m);
        Assert.True(Validate(order, instrument).IsValid);
        Assert.Contains(Validate(order with { Instrument = instrument.Key with { Board = "OTHER" } }, instrument).Errors, e => e.Code == "InstrumentMismatch");
        Assert.Contains(Validate(order with { ClientOrderId = "" }, instrument).Errors, e => e.Code == "ClientOrderIdRequired");
        Assert.Contains(Validate(order with { PostOnly = true }, instrument).Errors, e => e.Code == "InvalidPostOnly");
        Assert.Contains(Validate(order with { ReduceOnly = true }, instrument).Errors, e => e.Code == "UnsupportedReduceOnly");
        Assert.Contains(Validate(order with { PositionSide = PositionSide.Short }, instrument).Errors, e => e.Code == "UnsupportedPositionSide");
        var supported = Capabilities with { SupportsPostOnly = true };
        Assert.True(Validate(order with { PostOnly = true }, instrument, supported).IsValid);
        Assert.Contains(Validate(order with { PostOnly = true, TimeInForce = OrderTimeInForce.Ioc }, instrument, supported).Errors, e => e.Code == "InvalidPostOnly");
        Assert.Contains(Validate(order with { TimeInForce = OrderTimeInForce.Gtd }, instrument).Errors, e => e.Code == "InvalidExpiration");
        Assert.True(Validate(order with { TimeInForce = OrderTimeInForce.Gtd, ExpiresAt = Now.AddDays(1) }, instrument).IsValid);
    }

    /// <summary>Notional checks use monetary lot conversion, and missing required data blocks validation.</summary>
    [Fact]
    public void MonetaryLimitsAreNotComparedToRawQuantityTimesPrice()
    {
        var shares = Instrument(QuantityUnit.Lots, PriceQuotation.CurrencyPerUnit);
        shares = shares with { TradingRules = shares.TradingRules! with { MinNotional = 500m } };
        var order = new PlaceOrderRequest(shares.Key, OrderSide.Buy, OrderType.Limit, 1m, "client", Price: 100m);
        Assert.True(Validate(order, shares).IsValid); // 1 lot * 10 shares * 100 = 1000.
        Assert.Contains(Validate(order with { Price = 10m }, shares).Errors, e => e.Code == "MinNotional");
        var bond = shares with { TradingRules = shares.TradingRules! with { PriceQuotation = PriceQuotation.PercentOfFaceValue, FaceValue = null } };
        Assert.Contains(Validate(order, bond).Errors, e => e.Code == "ValuationUnavailable");
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
