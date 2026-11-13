using BotView.Models;

namespace BotView.Services.Trading;

/// <summary>Prepares order input and preview values for one instrument without sending requests.</summary>
public sealed class OrderInputCalculator
{
    private readonly ITradingService _service;
    private readonly Instrument _instrument;

    /// <summary>Uses loaded instrument metadata and the provider's precision rules.</summary>
    public OrderInputCalculator(ITradingService service, Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(instrument);

        _service = service;
        _instrument = instrument;
    }

    /// <summary>Converts a quote budget to native quantity and formats the amount for the provider.</summary>
    public decimal ConvertQuantity(decimal input, decimal price, OrderQuantityCurrency currency)
    {
        var quantity = input;

        if (currency == OrderQuantityCurrency.Quote)
        {
            var one = _instrument.Key.Venue == "MOEX"
                ? TradingValueCalculator.Calculate(_instrument, 1, price)?.Amount
                    ?? throw new ArgumentException("Monetary valuation metadata is unavailable.")
                : price * ContractSize;
            if (one <= 0) throw new ArgumentException("The monetary value must be positive.");
            quantity /= one;
        }

        return _service.AmountToPrecision(_instrument.Key, quantity);
    }

    /// <summary>Formats the actual limit price sent to the provider.</summary>
    public decimal PriceToPrecision(decimal price)
    {
        return _service.PriceToPrecision(_instrument.Key, price);
    }

    /// <summary>Calculates preview notional, without treating it as required margin.</summary>
    public decimal OrderNotional(decimal quantity, decimal price)
    {
        return _instrument.Key.Venue == "MOEX"
            ? TradingValueCalculator.Calculate(_instrument, quantity, price)?.Amount
                ?? throw new ArgumentException("Monetary valuation metadata is unavailable.")
            : quantity * price * ContractSize;
    }

    /// <summary>Estimates initial margin for linear contracts, excluding fees and exchange adjustments.</summary>
    public decimal EstimateInitialMargin(decimal quantity, decimal price, int leverage)
    {
        if (quantity <= 0 || price <= 0 || leverage < 1)
        {
            throw new ArgumentException("Positive quantity, price and leverage are required.");
        }

        return OrderNotional(quantity, price) / leverage;
    }

    private decimal ContractSize => _instrument.TradingRules?.ContractSize ?? 1m;
}
