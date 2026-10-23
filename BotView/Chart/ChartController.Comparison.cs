using BotView.Services;

namespace BotView.Chart;

public partial class ChartController
{
	/// <summary>Attaches a comparison; series supplies candles and symbol identifies the overlay.</summary>
	public void SetComparison(ICandleSeriesReader series, string symbol)
	{
		ArgumentNullException.ThrowIfNull(series);
		if (state.ComparisonSeries == null)
		{
			state.SavedPriceRange = state.PriceRangeInViewport;
			state.SavedCameraY = state.CameraPosition.y;
		}
		state.ComparisonSeries = series;
		state.ComparisonSymbol = symbol;
		state.ComparisonAutoFit = true;
		UpdateViewportFromCamera();
	}

	/// <summary>Removes the comparison and restores the saved vertical camera and price range.</summary>
	public void ClearComparison()
	{
		if (state.ComparisonSeries == null) return;
		state.ComparisonSeries = null;
		state.ComparisonSymbol = null;
		state.ComparisonAnchor = null;
		state.ComparisonAutoFit = false;
		state.PriceRangeInViewport = state.SavedPriceRange;
		state.CameraPosition = new(state.CameraPosition.x, state.SavedCameraY);
		UpdateViewportFromCamera();
	}

	/// <summary>Rebuilds normalization, geometry and percentage labels after comparison data changes.</summary>
	public void OnComparisonChanged()
	{
		if (state.ComparisonSeries != null) UpdateViewportFromCamera();
	}

	/// <summary>Finds the first valid shared candle in the current visible time range.</summary>
	private void RefreshComparisonAnchor()
	{
		state.ComparisonAnchor = null;
		if (state.ComparisonSeries == null || state.Viewport.minTime >= state.Viewport.maxTime) return;
		if (ComparisonSeriesMath.TryFindAnchor(state.Series.GetSnapshot(), state.ComparisonSeries.GetSnapshot(),
			ComparisonSeriesMath.ToUnixMilliseconds(state.Viewport.minTime),
			ComparisonSeriesMath.ToUnixMilliseconds(state.Viewport.maxTime), out var anchor))
			state.ComparisonAnchor = anchor;
	}

	/// <summary>Fits visible primary candles and normalized comparison closes with vertical padding.</summary>
	private void FitComparisonVertically()
	{
		if (state.ComparisonAnchor is not { } anchor || state.ComparisonSeries == null) return;
		long from = ComparisonSeriesMath.ToUnixMilliseconds(state.Viewport.minTime);
		long to = ComparisonSeriesMath.ToUnixMilliseconds(state.Viewport.maxTime);
		double min = double.PositiveInfinity, max = double.NegativeInfinity;
		foreach (var (_, candle) in state.Series.GetSnapshot().Enumerate(from, to))
		{
			if (double.IsFinite(candle.Low) && candle.Low > 0) min = Math.Min(min, candle.Low);
			if (double.IsFinite(candle.High) && candle.High > 0) max = Math.Max(max, candle.High);
		}
		foreach (var (_, candle) in state.ComparisonSeries.GetSnapshot().Enumerate(anchor.Timestamp, to))
		{
			double projected = ComparisonSeriesMath.ToPrimaryPrice(candle.Close, anchor);
			if (!double.IsFinite(projected) || projected <= 0) continue;
			min = Math.Min(min, projected);
			max = Math.Max(max, projected);
		}
		if (!double.IsFinite(min) || !double.IsFinite(max)) return;
		double padding = Math.Max((max - min) * 0.1, anchor.PrimaryClose * 0.001);
		state.PriceRangeInViewport = Math.Max(1e-8, max - min + 2 * padding);
		state.CameraPosition = new(state.CameraPosition.x, (min + max) / 2 - state.WorldOriginPrice);
		state.Viewport = new(min - padding, max + padding, state.Viewport.minTime, state.Viewport.maxTime);
	}

	/// <summary>Rounds minPrice down to the nearest absolute or percentage tick of the given interval.</summary>
	public double RoundPriceTickDown(double minPrice, double interval) => Axes.RoundPriceTickDown(minPrice, interval);
}
