using System.Globalization;
using BotView.Models;

namespace BotView.Services.Trading;

/// <summary>Small exchange boundary used by the account service and deterministic tests.</summary>
internal interface ISpotTradingClient : IAsyncDisposable
{
    Task<(string Identity, bool? CanTrade)> ConnectAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> LoadTradingPairsAsync(string spotSymbol, CancellationToken ct = default);
    Task<SpotMarket> MarketAsync(InstrumentKey key, CancellationToken ct);
    decimal NormalizeAmount(string symbol, decimal amount);
    decimal NormalizePrice(string symbol, decimal price);
    Task<OrderSnapshot> PlaceAsync(PlaceOrderRequest request, CancellationToken ct);
    Task<OrderSnapshot?> CancelAsync(OrderReference order, CancellationToken ct);
    Task<OrderSnapshot?> OrderAsync(OrderReference order, CancellationToken ct);
    Task<OrderSnapshot?> FindAsync(InstrumentKey key, string clientId, CancellationToken ct);
    Task<TradingPage<OrderSnapshot>> OrdersAsync(TradingQuery query, bool open, CancellationToken ct);
    Task<TradingPage<ExecutionSnapshot>> ExecutionsAsync(TradingQuery query, CancellationToken ct);
    Task<AccountSnapshot> BalanceAsync(TradingAccountContext account, CancellationToken ct);
    Task<IReadOnlyList<PositionSnapshot>> PositionsAsync(InstrumentKey? key, CancellationToken ct) => throw new NotSupportedException();
    bool IsFutures => false;
    Task<IReadOnlyList<TradingError>> ValidateFuturesAsync(PlaceOrderRequest request, SpotMarket market, AccountSnapshot balance, CancellationToken ct) => throw new NotSupportedException();
    Task<IReadOnlyList<TradingError>> ValidateMarketAsync(PlaceOrderRequest request, CancellationToken ct) => Task.FromResult<IReadOnlyList<TradingError>>([]);
    bool SupportsStream(string kind);
    Task<IReadOnlyList<TradingUpdate>> WatchAsync(string kind, InstrumentKey key, TradingAccountContext account, CancellationToken ct);
}

internal sealed record SpotMarket(Instrument Instrument, string Base, string Quote, decimal? FeeRate, string? FeeSide = null);

/// <summary>Decimal arithmetic and order-entry conversion shared by the UI and validation.</summary>
internal static class SpotOrderMath
{
    /// <summary>Converts a quote budget into base units, rounding down through the exchange precision function.</summary>
    internal static decimal Quantity(decimal input, decimal price, OrderQuantityCurrency unit, Func<decimal, decimal> normalize)
    {
        if (input <= 0 || price <= 0) throw new ArgumentOutOfRangeException(nameof(input));
        return unit == OrderQuantityCurrency.Base ? input : normalize(input / price);
    }

    /// <summary>Parses detached exchange numbers without treating missing values as zero.</summary>
    internal static decimal? Number(double? number) => number.HasValue && double.IsFinite(number.Value)
        ? decimal.Parse(number.Value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : null;
}
