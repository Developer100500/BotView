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
        if (quantity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        try { return CalculateCore(instrument, quantity, price, includeAccruedInterest); }
        catch (OverflowException) { return null; }
    }

    /// <summary>Evaluates the instrument-specific formula; the public boundary treats overflow as unavailable value.</summary>
    private static Money? CalculateCore(Instrument instrument, decimal quantity, decimal price, bool includeAccruedInterest)
    {
        var currency = instrument.SettlementCurrency ?? instrument.Currency;
        if (string.IsNullOrWhiteSpace(currency))
        {
            return null;
        }

        if (instrument.PriceQuotation is PriceQuotation.CurrencyPerUnit or PriceQuotation.PercentOfFaceValue &&
            !string.IsNullOrWhiteSpace(instrument.Currency) && currency != instrument.Currency)
        {
            // A different settlement currency requires an explicit provider FX conversion.
            return null;
        }

        decimal? value = null;
        switch (instrument.PriceQuotation)
        {
            case PriceQuotation.CurrencyPerUnit:
                var units = Units(instrument, quantity);
                if (units.HasValue) value = price * units.Value;
                break;

            case PriceQuotation.PercentOfFaceValue:
                var securities = Units(instrument, quantity);
                if (instrument.QuantityUnit == QuantityUnit.Contracts || securities is null || instrument.FaceValue is not > 0 ||
                    includeAccruedInterest && instrument.AccruedInterest is null)
                {
                    return null;
                }

                value = securities.Value * (price * instrument.FaceValue.Value / 100m +
                    (includeAccruedInterest ? instrument.AccruedInterest!.Value : 0));
                break;

            case PriceQuotation.Points:
                if (instrument.QuantityUnit != QuantityUnit.Contracts || instrument.PriceStep is not > 0 || instrument.TickValue is not > 0)
                {
                    return null;
                }

                value = quantity * price / instrument.PriceStep.Value * instrument.TickValue.Value;
                break;
        }

        return value.HasValue ? new Money(value.Value, currency) : null;
    }

    /// <summary>Converts provider order quantities only when the underlying multiplier is known.</summary>
    private static decimal? Units(Instrument instrument, decimal quantity) => instrument.QuantityUnit switch
    {
        QuantityUnit.BaseAsset or QuantityUnit.Units => quantity,
        QuantityUnit.Lots when instrument.LotSize > 0 => quantity * instrument.LotSize,
        QuantityUnit.Contracts when instrument.ContractSize is > 0 => quantity * instrument.ContractSize.Value,
        _ => null
    };
}
