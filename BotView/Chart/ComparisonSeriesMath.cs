using BotView.Services;

namespace BotView.Chart;

/// <summary>Normalizes two candle series at their first shared visible candle.</summary>
internal static class ComparisonSeriesMath
{
    public readonly record struct Anchor(long Timestamp, double PrimaryClose, double ComparisonClose);

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
            if (mainCandle.timestamp == otherCandle.timestamp)
            {
                if (double.IsFinite(mainCandle.close) && mainCandle.close > 0 &&
                    double.IsFinite(otherCandle.close) && otherCandle.close > 0)
                {
                    anchor = new Anchor(mainCandle.timestamp, mainCandle.close, otherCandle.close);
                    return true;
                }
                if (!main.MoveNext() || !other.MoveNext()) return false;
            }
            else if (mainCandle.timestamp < otherCandle.timestamp)
            {
                if (!main.MoveNext()) return false;
            }
            else if (!other.MoveNext()) return false;
        }
    }

    public static double ToPercent(double price, double baseClose) =>
        double.IsFinite(price) && double.IsFinite(baseClose) && baseClose > 0
            ? (price / baseClose - 1) * 100
            : double.NaN;

    public static double ToPrimaryPrice(double comparisonClose, Anchor anchor) =>
        double.IsFinite(comparisonClose) && anchor.ComparisonClose > 0
            ? anchor.PrimaryClose * comparisonClose / anchor.ComparisonClose
            : double.NaN;

    public static bool IsContinuous(long previousTimestamp, long timestamp, TimeSpan timeframe) =>
        previousTimestamp > 0 && timestamp > previousTimestamp &&
        timestamp - previousTimestamp <= timeframe.TotalMilliseconds * 1.5;

    public static long ToUnixMilliseconds(DateTime time) =>
        new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
