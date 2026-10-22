using BotView.Services;

namespace BotView.Chart;

/// <summary>Normalizes two candle series at their first shared visible candle.</summary>
internal static class ComparisonSeriesMath
{
    public readonly record struct Anchor(long Timestamp, double PrimaryClose, double ComparisonClose);

    /// <summary>Finds the first shared valid close in primary and comparison between visibleFrom and visibleTo; anchor receives the normalization base.</summary>
    public static bool TryFindAnchor(CandleSeriesSnapshot primary, CandleSeriesSnapshot comparison,
        long visibleFrom, long visibleTo, out Anchor anchor)
    {
        anchor = default;
        using var main = primary.Enumerate(visibleFrom, visibleTo).GetEnumerator();
        using var other = comparison.Enumerate(visibleFrom, visibleTo).GetEnumerator();
        if (!main.MoveNext() || !other.MoveNext()) return false;
        while (true)
        {
            var mainCandle = main.Current.Candle;
            var otherCandle = other.Current.Candle;
            if (mainCandle.timestamp.GetValueOrDefault() == otherCandle.timestamp.GetValueOrDefault())
            {
                if (double.IsFinite(mainCandle.close.GetValueOrDefault()) && mainCandle.close.GetValueOrDefault() > 0 &&
                    double.IsFinite(otherCandle.close.GetValueOrDefault()) && otherCandle.close.GetValueOrDefault() > 0)
                {
                    anchor = new Anchor(mainCandle.timestamp.GetValueOrDefault(), mainCandle.close.GetValueOrDefault(), otherCandle.close.GetValueOrDefault());
                    return true;
                }
                if (!main.MoveNext() || !other.MoveNext()) return false;
            }
            else if (mainCandle.timestamp.GetValueOrDefault() < otherCandle.timestamp.GetValueOrDefault())
            {
                if (!main.MoveNext()) return false;
            }
            else if (!other.MoveNext()) return false;
        }
    }

    /// <summary>Converts price to percentage change from a positive baseClose, returning NaN for invalid input.</summary>
    public static double ToPercent(double price, double baseClose) =>
        double.IsFinite(price) && double.IsFinite(baseClose) && baseClose > 0
            ? (price / baseClose - 1) * 100
            : double.NaN;

    /// <summary>Projects comparisonClose onto the primary price scale using the shared normalization anchor.</summary>
    public static double ToPrimaryPrice(double comparisonClose, Anchor anchor) =>
        double.IsFinite(comparisonClose) && anchor.ComparisonClose > 0
            ? anchor.PrimaryClose * comparisonClose / anchor.ComparisonClose
            : double.NaN;

    /// <summary>Checks whether previousTimestamp and timestamp are consecutive within the expected timeframe.</summary>
    public static bool IsContinuous(long previousTimestamp, long timestamp, TimeSpan timeframe) =>
        previousTimestamp > 0 && timestamp > previousTimestamp &&
        timestamp - previousTimestamp <= timeframe.TotalMilliseconds * 1.5;

    /// <summary>Converts chart time to a UTC Unix timestamp in milliseconds.</summary>
    public static long ToUnixMilliseconds(DateTime time) =>
        new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
