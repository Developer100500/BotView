using System.Globalization;
using BotView.Models;

namespace BotView.Services.Integrations.Ccxt;

/// <summary>Copies native CCXT responses into detached order-book models.</summary>
internal static class CcxtTradingMapper
{
    /// <summary>Preserves provider status rather than treating an asynchronous acknowledgement as an open order.</summary>
    internal static OrderSnapshot MapOrder(ccxt.Order order, InstrumentKey key) => new(
        new(key, order.id ?? ""), order.clientOrderId, order.side == "sell" ? OrderSide.Sell : OrderSide.Buy,
        order.type switch { "market" => OrderType.Market, "stop" or "stop_market" => OrderType.Stop,
            "stop_limit" => OrderType.StopLimit, _ => OrderType.Limit }, order.status switch { "open" => OrderState.Open, "closed" => OrderState.Filled,
            "canceled" or "cancelled" => OrderState.Canceled, "expired" => OrderState.Expired, "rejected" => OrderState.Rejected, _ => OrderState.Pending },
        Number(order.amount) ?? 0, Number(order.filled), Number(order.remaining),
        Number(order.price), Number(order.average), At(order.timestamp),
        At(order.lastUpdateTimestamp ?? order.lastTradeTimestamp ?? order.timestamp) ?? DateTimeOffset.MinValue, StatusReason(order),
        GetPositionSide(order), order.reduceOnly);

    /// <summary>Retains the hedge leg for exchange orders created by this or another application.</summary>
    private static PositionSide? GetPositionSide(ccxt.Order order)
    {
        if (order.info is null)
        {
            return null;
        }

        return Text(order.info, "positionSide") switch
        {
            "LONG" => PositionSide.Long,
            "SHORT" => PositionSide.Short,
            _ => Text(order.info, "positionIdx") switch
            {
                "1" => PositionSide.Long,
                "2" => PositionSide.Short,
                _ => null
            }
        };
    }

    /// <summary>Maps an allowlist of provider reason codes; raw rejection text is never shown or persisted.</summary>
    private static string? StatusReason(ccxt.Order order)
    {
        var reason = order.info is null ? null : Text(order.info, "rejectReason") ?? Text(order.info, "cancelType") ?? Text(order.info, "expiryReason");
        if (reason is "EC_PostOnlyWillTakeLiquidity" or "CancelByPostOnly") return "Post-only would execute immediately.";
        if (reason is "EC_NoEnoughQtyToFill" or "INSUFFICIENT_LIQUIDITY") return "Insufficient exchange liquidity.";
        return order.status switch { "canceled" or "cancelled" => "Canceled by the exchange.", "rejected" => "Rejected by the exchange.",
            "expired" => "Expired at the exchange.", _ => null };
    }

    /// <summary>Maps a fill independently of its order's current state, retaining the actual commission currency.</summary>
    private static ExecutionSnapshot MapExecution(ccxt.Trade trade, InstrumentKey key) => new(trade.id ?? "",
        new(key, trade.order ?? trade.orderId ?? ""), trade.side == "sell" ? OrderSide.Sell : OrderSide.Buy,
        Number(trade.amount) ?? 0, Number(trade.price) ?? 0,
        At(trade.timestamp) ?? DateTimeOffset.MinValue,
        trade.fee is { } fee && fee.currency is not null && Number(fee.cost) is { } cost ? new(cost, fee.currency) : null);

    /// <summary>Copies all currency balances out of the mutable CCXT cache.</summary>
    private static AccountSnapshot MapBalance(ccxt.Balances balance, TradingAccountContext account) => new(account,
        balance.balances.Select(p => new AccountBalance(p.Key, Number(p.Value.total),
            Number(p.Value.free), Number(p.Value.used))).ToArray(), null, null, DateTimeOffset.UtcNow);

    /// <summary>Reads optional detached provider metadata.</summary>
    private static string? Text(IDictionary<string, object> data, string key) => data.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;


    /// <summary>Converts an optional exchange timestamp to UTC.</summary>
    internal static DateTimeOffset? At(long? timestamp) => timestamp.HasValue
        ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value) : null;

    /// <summary>Converts exchange floating-point values without inventing missing amounts.</summary>
    internal static decimal? Number(double? number) => number is double value && double.IsFinite(value)
        ? decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : null;

    /// <summary>Copies a fill, preserving its commission and exchange identity.</summary>
    internal static ExecutionSnapshot Execution(ccxt.Trade trade, InstrumentKey key) => MapExecution(trade, key);

    /// <summary>Copies balances and USDT collateral reported by the exchange.</summary>
    internal static AccountSnapshot Balance(ccxt.Balances balance, TradingAccountContext account)
    {
        var snapshot = MapBalance(balance, account);
        return account.Market == TradingMarket.Spot ? snapshot : snapshot with
        {
            Margins = [new("USDT", AvailableFunds: Number(balance.free.GetValueOrDefault("USDT")))]
        };
    }

    /// <summary>Copies contract quantities, margin metadata and the hedge leg.</summary>
    internal static PositionSnapshot Position(ccxt.Position position, InstrumentKey key) => new(
        key, position.side == "short" ? PositionSide.Short : PositionSide.Long,
        Math.Abs(Number(position.contracts) ?? 0), QuantityUnit.Contracts,
        Number(position.entryPrice), Number(position.unrealizedPnl) is decimal pnl ? new(pnl, "USDT") : null,
        Number(position.initialMargin) is decimal margin ? new(margin, "USDT") : null,
        Number(position.leverage), position.hedged, position.marginMode);
}
