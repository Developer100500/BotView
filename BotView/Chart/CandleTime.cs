using BotView.Models;
using BotView.Services;

namespace BotView.Chart;

/// <summary>Reads timestamps and legacy fallback times from the same captured series.</summary>
internal static class CandleTime
{
	public static DateTime Get(CandleSeriesSnapshot snapshot, int index, TimeSpan timeframe)
	{
		if (index >= 0 && index < snapshot.Count && snapshot[index].timestamp.GetValueOrDefault() > 0)
			return snapshot[index].GetDateTime();
		return snapshot.Count > 0
			? snapshot[0].GetDateTime().Add(TimeSpan.FromTicks(timeframe.Ticks * index))
			: DateTime.UnixEpoch;
	}
}
