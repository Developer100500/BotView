namespace BotView.Models;

public enum QuantityUnit { BaseAsset, Units, Lots, Contracts }
public enum PriceQuotation { CurrencyPerUnit, PercentOfFaceValue, Points, ProviderSpecific }

/// <summary>Quantity uses QuantityUnit. Null constraints mean unknown. FaceValue is current outstanding
/// nominal; AccruedInterest is per security; TickValue is in SettlementCurrency.
/// QuantityStep/MinQuantity are not populated for MOEX lots/contracts: positive integers are implicit.</summary>
public sealed record TradingInstrumentRules(
    QuantityUnit QuantityUnit, PriceQuotation PriceQuotation,
    decimal? QuantityStep = null, decimal? MinQuantity = null, decimal? MaxQuantity = null,
    decimal? MinPrice = null, decimal? MaxPrice = null,
    decimal? MinNotional = null, decimal? MaxNotional = null,
    decimal? ContractSize = null, decimal? FaceValue = null, decimal? AccruedInterest = null,
    decimal? TickValue = null, string? SettlementCurrency = null, bool AllowNonPositivePrices = false);
