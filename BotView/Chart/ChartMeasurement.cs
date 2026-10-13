using System.Globalization;
using BotView.Services;

namespace BotView.Chart;

internal enum MeasurementPhase { Idle, Armed, Measuring, Completed }

/// <summary>Transient, chart-local measurement. Anchors survive prepended history.</summary>
internal sealed class ChartMeasurement
{
    public MeasurementPhase Phase { get; private set; }
    public ChartCoordinates? Start { get; private set; }
    public ChartCoordinates? End { get; private set; }
    internal bool IsActive => Phase is MeasurementPhase.Armed or MeasurementPhase.Measuring;

    /// <summary>Clears any previous ruler and waits for its first anchor.</summary>
    public void Arm() { Clear(); Phase = MeasurementPhase.Armed; }
    /// <summary>Clears both anchors and returns the ruler to its idle state.</summary>
    public void Clear() { Phase = MeasurementPhase.Idle; Start = End = null; }

    /// <summary>Starts, completes or clears the ruler at position; candles provides timestamps for snapping.</summary>
    public void Click(ChartCoordinates position, CandleSeriesSnapshot candles)
    {
        if (Phase == MeasurementPhase.Completed) { Clear(); return; }
        if (Phase == MeasurementPhase.Idle || candles.Count == 0 || !double.IsFinite(position.price)) return;
        var snapped = Snap(position, candles);
        if (Phase == MeasurementPhase.Armed)
        {
            Start = End = snapped;
            Phase = MeasurementPhase.Measuring;
        }
        else { End = snapped; Phase = MeasurementPhase.Completed; }
    }

    /// <summary>Moves the unfinished endpoint to position using the nearest timestamp in candles.</summary>
    public void Move(ChartCoordinates position, CandleSeriesSnapshot candles)
    {
        if (Phase == MeasurementPhase.Measuring && candles.Count > 0 && double.IsFinite(position.price))
            End = Snap(position, candles);
    }

    /// <summary>Preserves the price of position and snaps its time to the nearest bar in candles.</summary>
    private static ChartCoordinates Snap(ChartCoordinates position, CandleSeriesSnapshot candles) =>
        new(candles[MeasurementMath.FindNearest(candles, position.time)].GetDateTime(), position.price);
}

internal readonly record struct MeasurementResult(double PriceDifference, double? Percent, int Bars, TimeSpan Duration)
{
    public bool IsFalling => PriceDifference < 0;
    public string PriceText => MeasurementMath.FormatSigned(PriceDifference) + " (" +
        (Percent is { } percent ? percent.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture) + "%" : "—") + ")";
    public string TimeText => $"{Bars} {(Bars == 1 ? "bar" : "bars")}, {MeasurementMath.FormatDuration(Duration)}";
}

internal static class MeasurementMath
{
    /// <summary>Finds the bar in candles nearest to time, preferring the earlier bar on ties.</summary>
    public static int FindNearest(CandleSeriesSnapshot candles, DateTime time)
    {
        if (candles.Count == 0) throw new ArgumentException("No candles available.", nameof(candles));
        int left = 0, right = candles.Count;
        while (left < right)
        {
            int middle = left + (right - left) / 2;
            if (candles[middle].GetDateTime() < time) left = middle + 1;
            else right = middle;
        }
        if (left == 0) return 0;
        if (left == candles.Count) return left - 1;
        return time - candles[left - 1].GetDateTime() <= candles[left].GetDateTime() - time ? left - 1 : left;
    }

    /// <summary>Computes the change between start and end; candles supplies actual bar counts across gaps.</summary>
    public static MeasurementResult Calculate(ChartCoordinates start, ChartCoordinates end, CandleSeriesSnapshot candles)
    {
        double difference = end.price - start.price;
        double? percent = start.price == 0 ? null : difference / start.price * 100;
        if (percent.HasValue && !double.IsFinite(percent.Value)) percent = null;
        int bars = candles.Count == 0 ? 0 : Math.Abs(FindNearest(candles, end.time) - FindNearest(candles, start.time));
        return new(difference, percent, bars, (end.time - start.time).Duration());
    }

    /// <summary>Formats value as a price difference, prefixing positive numbers with a plus sign.</summary>
    public static string FormatSigned(double value) => (value > 0 ? "+" : "") + value.ToString("G10", CultureInfo.CurrentCulture);

    /// <summary>Formats duration using nonzero days, hours, minutes and seconds.</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        var parts = new List<string>();
        if (duration.Days != 0) parts.Add($"{duration.Days} д");
        if (duration.Hours != 0) parts.Add($"{duration.Hours} ч");
        if (duration.Minutes != 0) parts.Add($"{duration.Minutes} мин");
        if (duration.Seconds != 0) parts.Add($"{duration.Seconds} с");
        return parts.Count == 0 ? "0 с" : string.Join(" ", parts);
    }
}
