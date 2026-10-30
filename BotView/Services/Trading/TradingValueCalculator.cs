using BotView.Models;

namespace BotView.Services.Trading;

/// <summary>Converts quote prices to monetary value, independently of cash required and futures margin.</summary>
public static class TradingValueCalculator
{
    /// <summary>Returns value in settlement currency or null if metadata is missing. Bond dirty value
    /// includes current face value and accrued interest per security; points use monetary tick value.
    /// ProviderSpecific (e.g. inverse/quanto) requires the adapter's valuation implementation.</summary>
    public static Money? Calculate(Instrument instrument, decimal quantity, decimal price, bool includeAccruedInterest = true)
    {
        if (quantity < 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        try { return CalculateCore(instrument, quantity, price, includeAccruedInterest); }
        catch (OverflowException) { return null; }
    }

    /// <summary>Evaluates the instrument-specific formula; the public boundary treats overflow as unavailable value.</summary>
    private static Money? CalculateCore(Instrument instrument, decimal quantity, decimal price, bool includeAccruedInterest)
    {
        var rules = instrument.TradingRules;
        if (rules is null) return null;
        var currency = rules.SettlementCurrency ?? instrument.Currency;
        if (string.IsNullOrWhiteSpace(currency)) return null;
        if (rules.PriceQuotation is PriceQuotation.CurrencyPerUnit or PriceQuotation.PercentOfFaceValue &&
            !string.IsNullOrWhiteSpace(instrument.Currency) && currency != instrument.Currency)
            return null; // A different settlement currency requires an explicit provider FX conversion.
        decimal? value = null;
        switch (rules.PriceQuotation)
        {
            case PriceQuotation.CurrencyPerUnit:
                var units = Units(instrument, rules, quantity);
                if (units.HasValue) value = price * units.Value;
                break;
            case PriceQuotation.PercentOfFaceValue:
                var securities = Units(instrument, rules, quantity);
                if (rules.QuantityUnit == QuantityUnit.Contracts || securities is null || rules.FaceValue is not > 0 ||
                    includeAccruedInterest && rules.AccruedInterest is null) return null;
                value = securities.Value * (price * rules.FaceValue.Value / 100m +
                    (includeAccruedInterest ? rules.AccruedInterest!.Value : 0));
                break;
            case PriceQuotation.Points:
                if (rules.QuantityUnit != QuantityUnit.Contracts || instrument.PriceStep is not > 0 || rules.TickValue is not > 0)
                    return null;
                value = quantity * price / instrument.PriceStep.Value * rules.TickValue.Value;
                break;
        }
        return value.HasValue ? new(value.Value, currency) : null;
    }

    /// <summary>Converts provider order quantities only when the underlying multiplier is known.</summary>
    private static decimal? Units(Instrument instrument, TradingInstrumentRules rules, decimal quantity) => rules.QuantityUnit switch
    {
        QuantityUnit.BaseAsset or QuantityUnit.Units => quantity,
        QuantityUnit.Lots when instrument.LotSize > 0 => quantity * instrument.LotSize,
        QuantityUnit.Contracts when rules.ContractSize is > 0 => quantity * rules.ContractSize.Value,
        _ => null
    };
}
