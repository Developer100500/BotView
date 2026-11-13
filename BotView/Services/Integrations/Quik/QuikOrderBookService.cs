using BotView.Configuration;

namespace BotView.Services.Integrations.Quik;

/// <summary>Reads full MOEX listings using a shared terminal connection.</summary>
public sealed class QuikOrderBookService : IOrderBookService
{
    private readonly Func<IQuikReadClient> _factory;
    private readonly TimeSpan _pollInterval;
    private readonly string? _expectedConnection;

    /// <summary>Uses the configured terminal by default; an explicit endpoint supports multiple connections.</summary>
    public QuikOrderBookService(QuikConnectionOptions? options = null)
    {
        options ??= QuikConnectionOptions.Load();
        options.Validate();
        _factory = () => new QuikReadClient(options);
        _pollInterval = TimeSpan.FromSeconds(2);
        _expectedConnection = $"quik:{options.Host}:{options.Port}";
    }

    /// <summary>Injects a read-only fake without requiring a running terminal.</summary>
    internal QuikOrderBookService(Func<IQuikReadClient> factory, TimeSpan? pollInterval = null)
    {
        _factory = factory;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Rejects ambiguous symbol-only calls that omit the trading class.</summary>
    public Task<IOrderBookSubscription> SubscribeAsync(string exchange, string symbol, CancellationToken ct = default) =>
        throw new NotSupportedException("QUIK subscriptions require the full InstrumentKey.");

    /// <summary>Starts asynchronous reading without blocking window creation.</summary>
    public Task<IOrderBookSubscription> SubscribeAsync(string connection, Instrument instrument, TradingEnvironment environment,
        TradingMarket market, CancellationToken ct = default)
    {
        if (instrument.Key.Venue != "MOEX" || instrument.Key.Board is not ("TQBR" or "SPBFUT"))
            throw new NotSupportedException("Стакан QUIK поддерживает TQBR и SPBFUT.");
        if (_expectedConnection is not null && connection != _expectedConnection)
            throw new ArgumentException("The depth request belongs to a different terminal connection.");
        if (market != (instrument.Key.Board == "SPBFUT" ? TradingMarket.MoexFutures : TradingMarket.MoexShares))
            throw new ArgumentException("The depth market does not match its listing.");
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IOrderBookSubscription>(new Subscription(_factory(), instrument.Key, _pollInterval, ct));
    }

    private sealed class Subscription : IOrderBookSubscription
    {
        private readonly IQuikReadClient _client;
        private readonly InstrumentKey _key;
        private readonly CancellationTokenSource _stop;
        private readonly Task _worker;
        private readonly object _sync = new();
        private OrderBookUpdate _current = new(null, OrderBookConnection.Connecting, "QUIK: подключение…");
        private long _version;
        private bool _connected;
        private int _disposed;
        private readonly TimeSpan _pollInterval;
        public OrderBookUpdate Current { get { lock (_sync) return _current; } }

        /// <summary>Owns callback registration and a bounded heartbeat, independent of the UI timer.</summary>
        internal Subscription(IQuikReadClient client, InstrumentKey key, TimeSpan pollInterval, CancellationToken ct)
        {
            _client = client;
            _key = key;
            _pollInterval = pollInterval;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _client.BookChanged += OnBook;
            _worker = Task.Run(ReadAsync);
        }

        /// <summary>Publishes detached live events without permitting a slower snapshot to replace them.</summary>
        private void OnBook(OrderBookSnapshot snapshot)
        {
            if (snapshot.Market.Instrument?.Key != _key) return;
            lock (_sync)
            {
                if (!_connected || _stop.IsCancellationRequested) return;
                _version++;
                _current = new(snapshot, OrderBookConnection.Live, "QUIK · только чтение");
            }
        }

        /// <summary>Checks connection/subscription health every two seconds; depth itself arrives exclusively through OnQuote.</summary>
        private async Task ReadAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    if (!await _client.ConnectedAsync(_stop.Token).ConfigureAwait(false))
                        throw new InvalidOperationException("QUIK не подключён к брокеру.");
                    long version;
                    lock (_sync) { _connected = true; version = _version; }
                    var snapshot = await _client.BookAsync(_key, _stop.Token).ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (version == _version) _current = new(snapshot, OrderBookConnection.Live, "QUIK · только чтение");
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (QuikQuoteWaitTimeoutException)
                {
                    lock (_sync)
                    {
                        if (_current.Snapshot is null)
                            _current = new(null, OrderBookConnection.Connecting, "QUIK: ожидание первого события OnQuote…");
                    }
                }
                catch (Exception ex)
                {
                    lock (_sync)
                    {
                        _connected = false;
                        _current = new(_current.Snapshot, OrderBookConnection.Reconnecting, $"QUIK: {ex.Message}");
                    }
                }
                try { await Task.Delay(_pollInterval, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>Ends callbacks and reading before releasing the shared connection lease.</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _client.BookChanged -= OnBook;
            await _stop.CancelAsync();
            await _worker.ConfigureAwait(false);
            await _client.ReleaseBookAsync(_key).ConfigureAwait(false);
            await _client.DisposeAsync().ConfigureAwait(false);
            lock (_sync) _current = new(_current.Snapshot, OrderBookConnection.Closed, "QUIK: закрыто");
            _stop.Dispose();
        }
    }
}
