namespace BotView.Services.Integrations.Quik;

/// <summary>Protects a slow terminal snapshot from overwriting newer order callbacks.</summary>
internal sealed class QuikOrderEventBuffer
{
    private readonly object _sync = new();
    private readonly Dictionary<(string? Firm, string Account, string Client, OrderReference Reference), Entry> _events = [];
    private long _version;
    private sealed record Entry(long Version, OrderSnapshot Order);

    internal long Version { get { lock (_sync) return _version; } }

    /// <summary>Records a detached callback using every routing field available in the native record.</summary>
    internal void Add(string? firm, string account, string client, OrderSnapshot order)
    {
        lock (_sync)
        {
            var key = (firm, account, client, order.Reference);
            if (_events.TryGetValue(key, out var prior) && TradingAccountSession.Terminal(prior.Order.State) &&
                !TradingAccountSession.Terminal(order.State)) return;
            _events[key] = new(++_version, order);
            if (_events.Count > 2000)
            {
                foreach (var expired in _events.OrderBy(p => p.Value.Version).Take(500).Select(p => p.Key).ToArray())
                    _events.Remove(expired);
            }
        }
    }

    /// <summary>Merges only callbacks received after the read began, with account and listing isolation.</summary>
    internal IReadOnlyList<OrderSnapshot> Merge(QuikAccountOptions route, InstrumentKey instrument,
        IEnumerable<OrderSnapshot> snapshot, long started)
    {
        var result = snapshot.ToDictionary(o => o.Reference);
        lock (_sync)
        {
            foreach (var pair in _events.Where(p => (p.Value.Version > started || TradingAccountSession.Terminal(p.Value.Order.State)) && p.Key.Account == route.TradeAccount &&
                p.Key.Client == route.ClientCode && (p.Key.Firm is null || p.Key.Firm == route.FirmId) &&
                p.Key.Reference.Instrument == instrument))
                result[pair.Key.Reference] = pair.Value.Order;
        }
        return result.Values.ToArray();
    }
}
