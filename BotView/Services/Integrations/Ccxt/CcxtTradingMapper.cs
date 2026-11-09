using System.Globalization;
using BotView.Models;

namespace BotView.Services.Integrations.Ccxt;

/// <summary>Copies native CCXT responses into detached order-book models.</summary>
internal static class CcxtTradingMapper
{
    /// <summary>Preserves provider status rather than treating an asynchronous acknowledgement as an open order.</summary>
    internal static OrderSnapshot MapOrder(ccxt.Order order, InstrumentKey key)
    {
        var side = order.side == "sell" ? OrderSide.Sell : OrderSide.Buy;
        var type = order.type switch
        {
            "market" => OrderType.Market,
            "stop" or "stop_market" => OrderType.Stop,
            "stop_limit" => OrderType.StopLimit,
            _ => OrderType.Limit
        };
        var state = order.status switch
        {
            "open" => OrderState.Open,
            "closed" => OrderState.Filled,
            "canceled" or "cancelled" => OrderState.Canceled,
            "expired" => OrderState.Expired,
            "rejected" => OrderState.Rejected,
            _ => OrderState.Pending
        };
        var updatedAt = At(order.lastUpdateTimestamp ?? order.lastTradeTimestamp ?? order.timestamp)
            ?? DateTimeOffset.MinValue;

        return new OrderSnapshot(
            new OrderReference(key, order.id ?? ""),
            order.clientOrderId,
            side,
            type,
            state,
            Number(order.amount) ?? 0,
            Number(order.filled),
            Number(order.remaining),
            Number(order.price),
            Number(order.average),
            At(order.timestamp),
            updatedAt,
            StatusReason(order),
            GetPositionSide(order),
            order.reduceOnly);
    }

    /// <summary>Keeps a hedge leg. One-way markers (BOTH, position index 0, net) have no separate leg.</summary>
    private static PositionSide? GetPositionSide(ccxt.Order order)
    {
        if (order.info is null)
        {
            return null;
        }

        var positionSide = Text(order.info, "positionSide");
        if (positionSide is not null)
        {
            if (positionSide.Equals("LONG", StringComparison.OrdinalIgnoreCase))
            {
                return PositionSide.Long;
            }

            if (positionSide.Equals("SHORT", StringComparison.OrdinalIgnoreCase))
            {
                return PositionSide.Short;
            }

            return null;
        }

        var index = Text(order.info, "positionIdx");
        if (index is not null)
        {
            return index switch
            {
                "1" => PositionSide.Long,
                "2" => PositionSide.Short,
                _ => null
            };
        }

        return Text(order.info, "posSide")?.ToLowerInvariant() switch
        {
            "long" => PositionSide.Long,
            "short" => PositionSide.Short,
            _ => null
        };
    }

    /// <summary>Maps an allowlist of provider reason codes; raw rejection text is never shown or persisted.</summary>
    private static string? StatusReason(ccxt.Order order)
    {
        var reason = order.info is null
            ? null
            : Text(order.info, "rejectReason")
                ?? Text(order.info, "cancelType")
                ?? Text(order.info, "expiryReason");

        if (reason is "EC_PostOnlyWillTakeLiquidity" or "CancelByPostOnly")
        {
            return "Post-only would execute immediately.";
        }

        if (reason is "EC_NoEnoughQtyToFill" or "INSUFFICIENT_LIQUIDITY")
        {
            return "Insufficient exchange liquidity.";
        }

        return order.status switch
        {
            "canceled" or "cancelled" => "Canceled by the exchange.",
            "rejected" => "Rejected by the exchange.",
            "expired" => "Expired at the exchange.",
            _ => null
        };
    }

    /// <summary>Maps a fill independently of its order's current state, retaining the actual commission currency.</summary>
    private static ExecutionSnapshot MapExecution(ccxt.Trade trade, InstrumentKey key)
    {
        Money? fee = null;
        if (trade.fee is { } commission
            && commission.currency is not null
            && Number(commission.cost) is decimal cost)
        {
            fee = new Money(cost, commission.currency);
        }

        return new ExecutionSnapshot(
            trade.id ?? "",
            new OrderReference(key, trade.order ?? trade.orderId ?? ""),
            trade.side == "sell" ? OrderSide.Sell : OrderSide.Buy,
            Number(trade.amount) ?? 0,
            Number(trade.price) ?? 0,
            At(trade.timestamp) ?? DateTimeOffset.MinValue,
            fee);
    }

    /// <summary>Copies all currency balances out of the mutable CCXT cache.</summary>
    private static AccountSnapshot MapBalance(ccxt.Balances balance, TradingAccountContext account)
    {
        var balances = balance.balances
            .Select(pair => new AccountBalance(
                pair.Key,
                Number(pair.Value.total),
                Number(pair.Value.free),
                Number(pair.Value.used)))
            .ToArray();

        return new AccountSnapshot(account, balances, null, null, DateTimeOffset.UtcNow);
    }

    /// <summary>Reads optional detached provider metadata.</summary>
    private static string? Text(IDictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var value))
        {
            return null;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Converts an optional exchange timestamp to UTC.</summary>
    internal static DateTimeOffset? At(long? timestamp)
    {
        if (timestamp is not long milliseconds)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    /// <summary>Converts exchange floating-point values without inventing missing amounts.</summary>
    internal static decimal? Number(double? number)
    {
        if (number is not double value || !double.IsFinite(value))
        {
            return null;
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return decimal.Parse(text, CultureInfo.InvariantCulture);
    }

    /// <summary>Copies a fill, preserving its commission and exchange identity.</summary>
    internal static ExecutionSnapshot Execution(ccxt.Trade trade, InstrumentKey key) =>
        MapExecution(trade, key);

    /// <summary>Copies balances and, for futures, the USDT collateral reported by the exchange.</summary>
    internal static AccountSnapshot Balance(ccxt.Balances balance, TradingAccountContext account)
    {
        var snapshot = MapBalance(balance, account);
        if (account.Market == TradingMarket.Spot)
        {
            return snapshot;
        }

        var available = Number(balance.free.GetValueOrDefault("USDT"));
        return snapshot with
        {
            Margins = [new MarginSnapshot("USDT", AvailableFunds: available)]
        };
    }

    /// <summary>Copies contract quantities, margin metadata and the hedge leg.</summary>
    internal static PositionSnapshot Position(ccxt.Position position, InstrumentKey key)
    {
        var pnl = Number(position.unrealizedPnl);
        var margin = Number(position.initialMargin);

        return new PositionSnapshot(
            key,
            position.side == "short" ? PositionSide.Short : PositionSide.Long,
            Math.Abs(Number(position.contracts) ?? 0),
            QuantityUnit.Contracts,
            Number(position.entryPrice),
            pnl is decimal profit ? new Money(profit, "USDT") : null,
            margin is decimal initialMargin ? new Money(initialMargin, "USDT") : null,
            Number(position.leverage),
            position.hedged,
            position.marginMode);
    }
}
