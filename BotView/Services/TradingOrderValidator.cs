using BotView.Models;

namespace BotView.Services;

/// <summary>Common pure validation. Adapters also check provider precision, rights, sessions,
/// balances/margin and provider-specific rules immediately before dispatch.</summary>
public static class TradingOrderValidator
{
    /// <summary>Checks a request in the instrument's native order units without silently rounding it.</summary>
    /// <param name="request">Order to validate. Quantity uses the instrument's declared unit; limit and trigger prices stay separate.</param>
    /// <param name="instrument">Listing whose venue, lot size, price step, and trading rules are checked.</param>
    /// <param name="capabilities">Order types, durations, and flags this listing and account actually support.</param>
    /// <param name="account">Bound connection. Its venue must match the instrument.</param>
    /// <param name="now">Current time. A GTD order must expire after this instant.</param>
    public static OrderValidationResult Validate(PlaceOrderRequest request, Instrument instrument,
        TradingCapabilities capabilities, TradingAccountContext account, DateTimeOffset now)
    {
        var errors = new List<TradingError>();
        // Adds safe application errors, without provider request bodies or credentials.
        void Error(string code, string message) => errors.Add(new(code, message));

        if (request.Instrument != instrument.Key || !string.Equals(instrument.Key.Venue, account.Venue, StringComparison.OrdinalIgnoreCase))
            Error("InstrumentMismatch", "The instrument does not belong to this request/account venue.");
        if (string.IsNullOrWhiteSpace(request.ClientOrderId)) Error("ClientOrderIdRequired", "A client order ID is required before dispatch.");
        if (!Enum.IsDefined(request.Side)) Error("InvalidSide", "Invalid order side.");
        if (!Enum.IsDefined(request.Type) || !capabilities.OrderTypes.Contains(request.Type)) Error("UnsupportedOrderType", "The order type is not supported.");
        if (!Enum.IsDefined(request.TimeInForce) || !capabilities.TimeInForces.Contains(request.TimeInForce)) Error("UnsupportedTimeInForce", "The order duration is not supported.");
        if (request.PostOnly && (!capabilities.SupportsPostOnly || request.Type is not (OrderType.Limit or OrderType.StopLimit) ||
            request.TimeInForce is OrderTimeInForce.Ioc or OrderTimeInForce.Fok))
            Error("InvalidPostOnly", "Post-only requires a supported limit order and compatible duration.");
        if (request.ReduceOnly && !capabilities.SupportsReduceOnly) Error("UnsupportedReduceOnly", "Reduce-only is not supported.");
        if (request.PositionSide.HasValue && (!Enum.IsDefined(request.PositionSide.Value) || !capabilities.SupportsPositionSide))
            Error("UnsupportedPositionSide", "Explicit position side is not supported.");
        if (request.TimeInForce == OrderTimeInForce.Gtd ? request.ExpiresAt is null || request.ExpiresAt <= now : request.ExpiresAt is not null)
            Error("InvalidExpiration", "An expiration in the future is required only for GTD orders.");

        var rules = instrument.TradingRules;
        if (rules is null) { Error("TradingMetadataRequired", "Trading rules must be loaded before placing an order."); return new(errors); }
        if (!Enum.IsDefined(rules.QuantityUnit) || !Enum.IsDefined(rules.PriceQuotation) ||
            rules.QuantityUnit == QuantityUnit.Lots && instrument.LotSize <= 0)
            Error("InvalidTradingMetadata", "Instrument units and lot size must be valid.");
        if (request.Quantity <= 0) Error("InvalidQuantity", "Quantity must be positive.");
        // MOEX lots/contracts are positive integers; their fixed minimum and step need no per-instrument fields.
        var moexLotsOrContracts = string.Equals(instrument.Key.Venue, "MOEX", StringComparison.OrdinalIgnoreCase)
            && rules.QuantityUnit is QuantityUnit.Lots or QuantityUnit.Contracts;
        if (moexLotsOrContracts)
        {
            if (request.Quantity != decimal.Truncate(request.Quantity))
                Error("WholeQuantityRequired", "MOEX lot and contract quantities must be whole positive numbers.");
        }
        else
        {
            if (rules.QuantityStep is > 0 && request.Quantity % rules.QuantityStep.Value != 0)
                Error("QuantityStep", "Quantity does not match the instrument step.");
            if (rules.MinQuantity.HasValue && request.Quantity < rules.MinQuantity)
                Error("MinQuantity", "Quantity is below the instrument minimum.");
        }
        if (rules.MaxQuantity.HasValue && request.Quantity > rules.MaxQuantity) Error("MaxQuantity", "Quantity exceeds the instrument maximum.");

        var hasLimit = request.Type is OrderType.Limit or OrderType.StopLimit;
        var hasTrigger = request.Type is OrderType.Stop or OrderType.StopLimit;
        if (hasLimit != request.Price.HasValue) Error("LimitPrice", "A limit price is required only for limit and stop-limit orders.");
        if (hasTrigger != request.TriggerPrice.HasValue || hasTrigger != request.TriggerCondition.HasValue ||
            request.TriggerCondition.HasValue && !Enum.IsDefined(request.TriggerCondition.Value))
            Error("TriggerPrice", "Conditional orders require a trigger price and direction.");
        // Checks both prices in quote units, independently of monetary valuation.
        void ValidatePrice(decimal? price)
        {
            if (!price.HasValue) return;
            if (!rules.AllowNonPositivePrices && price <= 0) Error("InvalidPrice", "Price must be positive for this instrument.");
            if (instrument.PriceStep is > 0 && price.Value % instrument.PriceStep.Value != 0)
                Error("PriceStep", "Price does not match the instrument step.");
            if (rules.MinPrice.HasValue && price < rules.MinPrice) Error("MinPrice", "Price is below the permitted minimum.");
            if (rules.MaxPrice.HasValue && price > rules.MaxPrice) Error("MaxPrice", "Price exceeds the permitted maximum.");
        }
        ValidatePrice(request.Price);
        ValidatePrice(request.TriggerPrice);
        if (hasLimit && request.Price.HasValue && request.Quantity > 0 && (rules.MinNotional.HasValue || rules.MaxNotional.HasValue))
        {
            var value = TradingValueCalculator.Calculate(instrument, request.Quantity, request.Price.Value);
            if (value is null) Error("ValuationUnavailable", "Monetary limits cannot be checked without valuation metadata.");
            else
            {
                if (rules.MinNotional.HasValue && Math.Abs(value.Amount) < rules.MinNotional) Error("MinNotional", "Order value is below the minimum.");
                if (rules.MaxNotional.HasValue && Math.Abs(value.Amount) > rules.MaxNotional) Error("MaxNotional", "Order value exceeds the maximum.");
            }
        }
        return new(errors);
    }
}
