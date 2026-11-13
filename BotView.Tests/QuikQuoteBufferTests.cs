using BotView.Services.Integrations.Quik;

namespace BotView.Tests;

public sealed class QuikQuoteBufferTests
{
    /// <summary>A canceled initial reader cannot poison the subscription or another window's first snapshot.</summary>
    [Fact]
    public async Task EventsSupplyInitialAndSubsequentDepthWithoutARequestResponseRead()
    {
        /* QuikQuoteBuffer is retained but intentionally unused.
        var key = new InstrumentKey("MOEX", "shares", "TQBR", "SBER");
        var buffer = new QuikQuoteBuffer();
        using var canceled = new CancellationTokenSource();
        var first = buffer.ReadAsync(key, TimeSpan.FromSeconds(2), canceled.Token);
        var second = buffer.ReadAsync(key, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var snapshot = new OrderBookSnapshot(new("SBER", "lots", "RUB", 0.01m), [new(100, 5)], [new(101, 6)], DateTimeOffset.UtcNow);
        buffer.Publish(key, snapshot);
        Assert.Same(snapshot, await second);
        var next = snapshot with { Bids = [new(102, 7)] };
        buffer.Publish(key, next);
        Assert.Same(next, await buffer.ReadAsync(key, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        var other = key with { Board = "SPBFUT", Market = "futures" };
        await Assert.ThrowsAsync<TimeoutException>(() => buffer.ReadAsync(other, TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken));
        */
        await Task.CompletedTask;
        Assert.Skip("QuikQuoteBuffer usage is commented out by request.");
    }
}
