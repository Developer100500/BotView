namespace BotView.Chart;

/// <summary>Immutable scale divisions and label formatting for one geometry revision.</summary>
public sealed class AxisLayout
{
	private readonly ViewportClippingCoords viewport;
	private readonly BotView.Chart.IndicatorPane.IndicatorViewport indicatorViewport;
	private readonly double mainPaneHeight;
	private readonly TimeSpan timeframe;
	private readonly bool indicatorPaneVisible;
	public double PriceInterval { get; }
	public TimeSpan TimeInterval { get; }
	public double IndicatorValueInterval { get; }
	public IReadOnlyList<AxisTick<double>> PriceTicks { get; }
	public IReadOnlyList<AxisTick<DateTime>> TimeTicks { get; }
	public IReadOnlyList<AxisTick<double>> IndicatorTicks { get; }

	public AxisLayout(ViewportClippingCoords viewport,
		BotView.Chart.IndicatorPane.IndicatorViewport indicatorViewport,
		double mainPaneHeight, TimeSpan timeframe, bool indicatorPaneVisible = true)
	{
		this.viewport = viewport;
		this.indicatorViewport = indicatorViewport;
		this.mainPaneHeight = mainPaneHeight;
		this.timeframe = timeframe;
		this.indicatorPaneVisible = indicatorPaneVisible;
		PriceInterval = CalculateOptimalPriceInterval();
		TimeInterval = CalculateOptimalTimeInterval();
		IndicatorValueInterval = CalculateOptimalIndicatorInterval();
		PriceTicks = NumericTicks(viewport.minPrice, viewport.maxPrice, PriceInterval,
			value => FormatPriceLabel(value, PriceInterval));
		IndicatorTicks = indicatorPaneVisible
			? NumericTicks(indicatorViewport.MinValue, indicatorViewport.MaxValue, IndicatorValueInterval, FormatIndicatorValue)
			: Array.Empty<AxisTick<double>>();
		var times = new List<AxisTick<DateTime>>();
		if (viewport.minTime < viewport.maxTime)
		{
			var first = RoundDownToInterval(viewport.minTime, TimeInterval);
			for (var time = first; time <= viewport.maxTime;)
			{
				times.Add(new(time, FormatTimeLabel(time, TimeInterval)));
				if (DateTime.MaxValue.Ticks - time.Ticks < TimeInterval.Ticks) break;
				time = time.Add(TimeInterval);
			}
		}
		TimeTicks = times.AsReadOnly();
	}

	private static IReadOnlyList<AxisTick<double>> NumericTicks(double min, double max,
		double step, Func<double, string> format)
	{
		var result = new List<AxisTick<double>>();
		if (double.IsFinite(min) && double.IsFinite(max) && max > min && step > 0)
		{
			var first = Math.Floor(min / step) * step;
			for (int i = 0; i < 1000; i++)
			{
				double value = first + i * step;
				if (value > max) break;
				result.Add(new(value, format(value)));
			}
		}
		return result.AsReadOnly();
	}

	public static string FormatIndicatorValue(double value) => Math.Abs(value) >= 100
		? value.ToString("F0") : Math.Abs(value) >= 10 ? value.ToString("F1") : value.ToString("F2");
	/// <summary>Вычисляет оптимальный интервал для меток шкалы индикатора</summary>
	public double CalculateOptimalIndicatorInterval()
	{
		if (!indicatorPaneVisible) return 0;
		var viewport = indicatorViewport;
		double valueRange = viewport.MaxValue - viewport.MinValue;
		if (valueRange <= 0 || !double.IsFinite(valueRange))
			return 1;

		double rawInterval = valueRange / 5; // Target ~5 ticks
		double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(rawInterval, 0.001))));
		double normalizedInterval = rawInterval / magnitude;
		if (normalizedInterval <= 1) return magnitude;
		if (normalizedInterval <= 2) return 2 * magnitude;
		if (normalizedInterval <= 5) return 5 * magnitude;
		return 10 * magnitude;
	}

	/// <summary>
	/// Вычисляет оптимальный интервал для меток времени
	/// </summary>
	public TimeSpan CalculateOptimalTimeInterval()
	{
		TimeSpan viewportRange = viewport.maxTime - viewport.minTime;
		double totalSeconds = viewportRange.TotalSeconds;

		// Целевое количество меток на экране (примерно 5-10)
		int targetTickCount = 10;
		double secondsPerTick = totalSeconds / targetTickCount;

		// Выбираем подходящий интервал
		if (secondsPerTick <= 60) // Меньше минуты
			return TimeSpan.FromSeconds(Math.Max(1, Math.Round(secondsPerTick / 10) * 10));
		else if (secondsPerTick <= 3600) // Меньше часа
			return TimeSpan.FromMinutes(Math.Max(1, Math.Round(secondsPerTick / 60 / 5) * 5));
		else if (secondsPerTick <= 86400) // Меньше дня
			return TimeSpan.FromHours(Math.Max(1, Math.Round(secondsPerTick / 3600)));
		else if (secondsPerTick <= 604800) // Меньше недели
			return TimeSpan.FromDays(Math.Max(1, Math.Round(secondsPerTick / 86400)));
		else // Больше недели
			return TimeSpan.FromDays(Math.Max(7, Math.Round(secondsPerTick / 86400 / 7) * 7));
	}

	/// <summary>
	/// Вычисляет оптимальный интервал для меток цены
	/// </summary>
	public double CalculateOptimalPriceInterval()
	{
		double priceRange = viewport.maxPrice - viewport.minPrice;
		if (priceRange <= 0 || !double.IsFinite(priceRange))
			return 1;

		int targetTickCount = GetTargetPriceSectionCount();
		double rawInterval = priceRange / targetTickCount;

		return RoundUpToNicePriceStep(rawInterval);
	}

	/// <summary> Возвращает целевое число ценовых секций по высоте main pane </summary>
	private int GetTargetPriceSectionCount()
	{
		const double pixelsPerSection = 55;
		const int minSections = 4;
		const int maxSections = 10;

		if (mainPaneHeight <= 0)
			return maxSections;

		int sections = (int)Math.Round(mainPaneHeight / pixelsPerSection);
		return Math.Clamp(sections, minSections, maxSections);
	}

	/// <summary> Округляет шаг цены вверх до ряда 1 / 2 / 2.5 / 5 / 10 × 10ⁿ </summary>
	internal static double RoundUpToNicePriceStep(double rawStep)
	{
		if (rawStep <= 0 || !double.IsFinite(rawStep))
			return 1;

		double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
		double normalized = rawStep / magnitude;

		double niceNormalized = normalized switch
		{
			<= 1.0 => 1.0,
			<= 2.0 => 2.0,
			<= 2.5 => 2.5,
			<= 5.0 => 5.0,
			_ => 10.0
		};

		return niceNormalized * magnitude;
	}

	/// <summary> Определяет число знаков после запятой по шагу цены </summary>
	private static int GetDecimalPlacesForPriceStep(double priceInterval)
	{
		if (priceInterval <= 0 || !double.IsFinite(priceInterval))
			return 4;

		double magnitude = Math.Pow(10, Math.Floor(Math.Log10(priceInterval)));
		double normalized = priceInterval / magnitude;
		int baseDecimals = (int)Math.Max(0, -Math.Floor(Math.Log10(magnitude)));

		if (Math.Abs(normalized - 2.5) < 0.01)
			return baseDecimals + 1;

		return baseDecimals;
	}

	/// <summary>
	/// Округляет время вниз до ближайшего интервала
	/// </summary>
	public DateTime RoundDownToInterval(DateTime time, TimeSpan interval)
	{
		long ticks = time.Ticks;
		long intervalTicks = interval.Ticks;
		long roundedTicks = (ticks / intervalTicks) * intervalTicks;
		return new DateTime(roundedTicks, time.Kind);
	}

	/// <summary>
	/// Форматирует подпись времени в зависимости от интервала
	/// </summary>
	public string FormatTimeLabel(DateTime time, TimeSpan interval)
	{
		if (interval.TotalSeconds < 60)
			return time.ToString("HH:mm:ss");
		else if (interval.TotalMinutes < 60)
			return time.ToString("HH:mm");
		else if (interval.TotalHours < 24)
			return time.ToString("HH:mm");
		else if (interval.TotalDays < 7)
			return time.ToString("dd.MM");
		else
			return time.ToString("dd.MM.yy");
	}

	/// <summary> Форматирует подпись цены с учётом шага шкалы </summary>
	public string FormatPriceLabel(double price, double? priceInterval = null)
	{
		double interval = priceInterval ?? CalculateOptimalPriceInterval();
		int decimals = GetDecimalPlacesForPriceStep(interval);
		return price.ToString($"F{decimals}");
	}

	/// <summary>Минимальное изменение цены, различимое в подписи текущей ценовой шкалы.</summary>
	public double GetMinimumDisplayedPriceStep()
	{
		int decimals = GetDecimalPlacesForPriceStep(PriceInterval);
		return Math.Pow(10, -decimals);
	}

	/// <summary>
	/// Formats crosshair time label: always date; time included when timeframe is shorter than 1 day.
	/// </summary>
	public string FormatCrosshairTimeLabel(DateTime time)
	{
		TimeSpan tf = timeframe;
		if (tf < TimeSpan.FromDays(1))
			return time.ToString("dd.MM.yyyy HH:mm");
		return time.ToString("dd.MM.yyyy");
	}
}

public readonly record struct AxisTick<T>(T Value, string Label);
