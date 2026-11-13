namespace BotView.Services.Integrations.Quik;

/// <summary>Caches event snapshots without invoking the defective GetQuoteLevel2 request in QUIKSharp 2.0.0.</summary>
internal sealed class QuikQuoteBuffer
{
    private readonly object _sync = new();
    private readonly Dictionary<InstrumentKey, OrderBookSnapshot> _latest = [];
    private readonly Dictionary<InstrumentKey, TaskCompletionSource<OrderBookSnapshot>> _pending = [];

    /// <summary>Returns the latest real event or waits for the first one, independently of other readers' cancellation.</summary>
    internal Task<OrderBookSnapshot> ReadAsync(InstrumentKey key, TimeSpan timeout, CancellationToken ct)
    {
        lock (_sync)
        {
            ct.ThrowIfCancellationRequested();
            if (_latest.TryGetValue(key, out var snapshot)) return Task.FromResult(snapshot);
            if (!_pending.TryGetValue(key, out var pending))
            {
                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Add(key, pending);
            }
            return pending.Task.WaitAsync(timeout, ct);
        }
    }

    /// <summary>Publishes only event data, preserving its original receipt timestamp.</summary>
    internal void Publish(InstrumentKey key, OrderBookSnapshot snapshot)
    {
        lock (_sync)
        {
            _latest[key] = snapshot;
            if (_pending.Remove(key, out var pending)) pending.TrySetResult(snapshot);
        }
    }
}
