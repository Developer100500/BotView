using BotView.Models;
using BotView.Services;
using BotView.Chart;
using BotView.Chart.IndicatorPane;

namespace BotView.Tests;

public sealed class CandleSeriesTests
{
    private static OHLCV Candle(long timestamp, double close = 1) =>
        new(timestamp, close, close, close, close, 1);

    [Fact]
    public void InitialLoadSortsDeduplicatesAndKeepsLiveInForward()
    {
        var series = new CandleSeries();
        series.LoadInitial(new[] { Candle(30), Candle(10), Candle(20), Candle(20, 2) });

        var snapshot = series.GetSnapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(2, snapshot.ClosedCount);
        Assert.Equal(new long[] { 10, 20, 30 },
            snapshot.Enumerate(long.MinValue, long.MaxValue).Select(x => x.Candle.timestamp));
        Assert.Equal(2, snapshot[1].close);
        Assert.Equal(2, snapshot.BlockReferences.Length);
    }

    [Fact]
    public void LiveTicksReplaceOneSlotAndFullForwardBecomesMainByReference()
    {
        var series = new CandleSeries();
        series.LoadInitial(new[] { Candle(1) });
        var beforeTick = series.GetSnapshot();
        var forward = beforeTick.BlockReferences[^1];

        Assert.Equal(LiveCandleChange.Updated, series.UpdateLive(Candle(1, 2), out _));
        Assert.Equal(1, beforeTick.Live!.Value.close);
        Assert.Same(forward, series.GetSnapshot().BlockReferences[^1]);
        Assert.Equal(2, series.GetSnapshot().Live!.Value.close);

        for (int i = 2; i <= 251; i++)
            Assert.Equal(LiveCandleChange.Closed, series.UpdateLive(Candle(i), out _));

        var snapshot = series.GetSnapshot();
        Assert.Equal(251, snapshot.Count);
        Assert.Equal(250, snapshot.ClosedCount);
        Assert.Equal(2, snapshot.BlockReferences.Length);
        Assert.Same(forward, snapshot.BlockReferences[0]);
        Assert.NotSame(forward, snapshot.BlockReferences[1]);
        Assert.Equal(2, snapshot[0].close);
        Assert.Equal(251, snapshot.Live!.Value.timestamp);
    }

    [Fact]
    public void PrependAddsBlockReferencesWithoutReplacingExistingBlocks()
    {
        var series = new CandleSeries();
        series.LoadInitial(new[] { Candle(501), Candle(502) });
        var main = series.GetSnapshot().BlockReferences[0];

        Assert.Equal(250, series.PrependHistory(Enumerable.Range(251, 250).Select(i => Candle(i))));
        Assert.Equal(125, series.PrependHistory(Enumerable.Range(126, 125).Select(i => Candle(i))));
        Assert.Equal(0, series.PrependHistory(new[] { Candle(251), Candle(502) }));

        var snapshot = series.GetSnapshot();
        Assert.Equal(377, snapshot.Count);
        Assert.Equal(126, snapshot.OldestTimestamp);
        Assert.Equal(502, snapshot.Live!.Value.timestamp);
        Assert.Same(main, snapshot.BlockReferences[^2]);
        Assert.Equal(4, snapshot.BlockReferences.Length);
    }

    [Fact]
    public async Task SnapshotsRemainConsistentDuringConcurrentUpdates()
    {
        var series = new CandleSeries();
        series.LoadInitial(new[] { Candle(1) });
        var original = series.GetSnapshot();
        var producer = Task.Run(() =>
        {
            for (int i = 2; i <= 1000; i++)
                series.UpdateLive(Candle(i), out _);
        }, TestContext.Current.CancellationToken);

        while (!producer.IsCompleted)
        {
            var snapshot = series.GetSnapshot();
            var timestamps = snapshot.Enumerate(long.MinValue, long.MaxValue)
                .Select(x => x.Candle.timestamp).ToArray();
            Assert.Equal(snapshot.Count, timestamps.Length);
            Assert.True(timestamps.SequenceEqual(timestamps.OrderBy(x => x)));
        }
        await producer;
        Assert.Equal(1000, series.GetSnapshot().Count);
        Assert.Equal(1, original.Count);
        Assert.Equal(1, original.Live!.Value.timestamp);
        Assert.Equal(new long[] { 1 },
            original.Enumerate(long.MinValue, long.MaxValue).Select(x => x.Candle.timestamp));
    }

    [Fact]
    public void RsiMatchesArrayCalculationAcrossChunkBoundary()
    {
        var candles = Enumerable.Range(0, 301)
            .Select(i => Candle(1_700_000_000_000L + i * 60_000L, 100 + Math.Sin(i / 5.0) * 10))
            .ToArray();
        var series = new CandleSeries();
        series.LoadInitial(candles);
        var controller = new ChartController(new ChartModel());
        var fromBlocks = new RSIIndicator();
        var fromArray = new RSIIndicator();

        fromBlocks.Calculate(series.GetSnapshot(), controller);
        fromArray.Calculate(candles, i => candles[i].GetDateTime());

        Assert.Equal(fromArray.Points.Count, fromBlocks.Points.Count);
        for (int i = 0; i < fromArray.Points.Count; i++)
        {
            Assert.Equal(fromArray.Points[i].Time, fromBlocks.Points[i].Time);
            Assert.Equal(fromArray.Points[i].Value, fromBlocks.Points[i].Value, 10);
        }
    }

    [Fact]
    public void VisibleRangeAndIndexesCrossHistoryMainAndForward()
    {
        var series = new CandleSeries();
        series.LoadInitial(Enumerable.Range(251, 301).Select(i => Candle(i)));
        series.PrependHistory(Enumerable.Range(1, 250).Select(i => Candle(i)));
        var snapshot = series.GetSnapshot();

        Assert.Equal(551, snapshot.Count);
        Assert.Equal(1, snapshot[0].timestamp);
        Assert.Equal(250, snapshot[249].timestamp);
        Assert.Equal(251, snapshot[250].timestamp);
        Assert.Equal(551, snapshot[550].timestamp);
        Assert.Equal(new long[] { 249, 250, 251, 252 },
            snapshot.Enumerate(249, 252).Select(x => x.Candle.timestamp));
    }

    [Fact]
    public void CatchUpClosedCandlesFitBeforeExistingLiveCandle()
    {
        var series = new CandleSeries();
        series.LoadInitial(new[] { Candle(100), Candle(400) });
        var before = series.GetSnapshot();

        Assert.Equal(2, series.AppendClosed(new[] { Candle(300), Candle(200), Candle(300) }));
        var after = series.GetSnapshot();
        Assert.Equal(new long[] { 100, 200, 300, 400 },
            after.Enumerate(long.MinValue, long.MaxValue).Select(x => x.Candle.timestamp));
        Assert.Equal(400, after.Live!.Value.timestamp);
        Assert.Equal(2, before.Count);
    }

    [Fact]
    public void TwoHundredFiftiethClosedCandleSealsForwardWithoutCopyingIt()
    {
        var series = new CandleSeries();
        var forward = series.GetSnapshot().BlockReferences[^1];

        Assert.Equal(249, series.AppendClosed(Enumerable.Range(1, 249).Select(i => Candle(i))));
        Assert.Same(forward, series.GetSnapshot().BlockReferences[^1]);
        Assert.Equal(1, series.AppendClosed(new[] { Candle(250) }));

        var snapshot = series.GetSnapshot();
        Assert.Equal(2, snapshot.BlockReferences.Length);
        Assert.Same(forward, snapshot.BlockReferences[0]);
        Assert.NotSame(forward, snapshot.BlockReferences[1]);
        Assert.Equal(250, snapshot.ClosedCount);
        Assert.Null(snapshot.Live);
    }

    [Fact]
    public void PanningLeftRequestsHistoryAndReadsPrependedBlocks()
    {
        const long start = 1_700_000_000_000L;
        var series = new CandleSeries();
        series.LoadInitial(Enumerable.Range(0, 250)
            .Select(i => Candle(start + i * 60_000L)));
        var model = new ChartModel { ChartWidth = 800, ChartHeight = 500 };
        var controller = new ChartController(model);
        controller.SetSeries(series, "1m");
        int requests = 0;
        controller.LeftEdgeApproached += () => requests++;

        controller.Pan(-250 * 60, 0);
        Assert.True(requests > 0);

        series.PrependHistory(Enumerable.Range(1, 250)
            .Select(i => Candle(start - i * 60_000L)));
        controller.OnHistoryExtended();
        Assert.Equal(start - 250 * 60_000L, series.GetSnapshot()[0].timestamp);
        Assert.Equal(series.GetSnapshot()[0].GetDateTime(), controller.GetCandleTime(0));
    }
}
