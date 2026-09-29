using BotView.Models;

namespace BotView.Services;

public static class MorningMovementCalculator
{
    private static readonly TimeZoneInfo NewYorkTimeZone = GetNewYorkTimeZone();

    public static PriceMovement? Calculate(MarketQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);

        var quoteTime = TimeZoneInfo.ConvertTime(quote.AsOf, NewYorkTimeZone);
        if (quote.LastPrice <= 0 || quoteTime.TimeOfDay < new TimeSpan(9, 30, 0) ||
            quoteTime.TimeOfDay >= new TimeSpan(18, 0, 0))
            return null;

        foreach (var bar in quote.Bars)
        {
            var barTime = TimeZoneInfo.ConvertTime(bar.Start, NewYorkTimeZone);
            if (barTime.Date == quoteTime.Date && barTime.TimeOfDay == new TimeSpan(9, 30, 0) &&
                bar.Open > 0 && bar.Start <= quote.AsOf)
                return new PriceMovement(bar.Open, quote.LastPrice, quote.AsOf);
        }

        return null;
    }

    private static TimeZoneInfo GetNewYorkTimeZone()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }

        throw new TimeZoneNotFoundException("New York time zone is unavailable.");
    }
}
