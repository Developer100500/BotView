namespace BotView.Models;

/// <summary>Identifies a listing, independently of the API used to read it.</summary>
public sealed record InstrumentKey(string Venue, string Market, string Board, string SecurityId);

public enum QuantityUnit { BaseAsset, Units, Lots, Contracts }
public enum PriceQuotation { CurrencyPerUnit, PercentOfFaceValue, Points, ProviderSpecific }

/// <summary>Complete listing description. Symbol is unique within a provider connection; Key identifies
/// the actual listing. Null units, quotations and constraints mean unknown. FaceValue is the current
/// outstanding nominal; AccruedInterest is per security; TickValue is in SettlementCurrency.
/// QuantityStep/MinQuantity are not populated for MOEX lots/contracts: positive integers are implicit.</summary>
public sealed record Instrument(
    InstrumentKey Key,
    string Symbol,
    string Name,
    string? Currency = null,
    decimal LotSize = 1m,
    decimal? PriceStep = null,
    DateOnly? Expiry = null,
    QuantityUnit? QuantityUnit = null,
    PriceQuotation? PriceQuotation = null,
    decimal? QuantityStep = null,
    decimal? MinQuantity = null,
    decimal? MaxQuantity = null,
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    decimal? MinNotional = null,
    decimal? MaxNotional = null,
    decimal? ContractSize = null,
    decimal? FaceValue = null,
    decimal? AccruedInterest = null,
    decimal? TickValue = null,
    string? SettlementCurrency = null,
    bool AllowNonPositivePrices = false
);
