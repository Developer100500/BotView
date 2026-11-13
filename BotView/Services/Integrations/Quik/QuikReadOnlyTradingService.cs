using BotView.Configuration;
using System.Threading.Channels;

namespace BotView.Services.Integrations.Quik;

/// <summary>Adapts terminal reads to the existing account session; every mutation is explicitly unsupported.</summary>
public sealed class QuikReadOnlyTradingService : ITradingService
{
    private readonly TradingAccountProfile _profile;
    private readonly IQuikReadClient _client;
    private readonly Dictionary<InstrumentKey, Instrument> _instruments = [];
    private readonly Dictionary<TradingStream, Channel<bool>> _signals = Enum.GetValues<TradingStream>().ToDictionary(s => s,
        _ => Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest }));
    public TradingAccountContext Account { get; }
    public bool CanExecuteTrades => false;

    /// <summary>Creates a lease using the profile's terminal endpoint without requesting credentials.</summary>
    public QuikReadOnlyTradingService(TradingAccountProfile profile)
        : this(profile, CreateClient(profile)) { }

    /// <summary>Validates routing before acquiring a terminal lease.</summary>
    private static IQuikReadClient CreateClient(TradingAccountProfile profile)
    {
        if (profile.Exchange != "quik" || profile.Quik is null || profile.Market is not (TradingMarket.MoexShares or TradingMarket.MoexFutures))
            throw new ArgumentException("A QUIK shares/futures profile is required.");
        return new QuikReadClient(new QuikConnectionOptions
        {
            Host = profile.Quik.Host,
            Port = profile.Quik.Port
        });
    }

    /// <summary>Injects a read-only boundary for deterministic tests and transport isolation.</summary>
    internal QuikReadOnlyTradingService(TradingAccountProfile profile, IQuikReadClient client)
    {
        if (profile.Exchange != "quik" || profile.Quik is null || profile.Market is not (TradingMarket.MoexShares or TradingMarket.MoexFutures))
            throw new ArgumentException("A QUIK shares/futures profile is required.");
        _profile = profile;
        _client = client;
        Account = QuikReadClient.Context(profile);
        _client.AccountChanged += OnAccountChanged;
    }

    /// <summary>Loads instrument metadata once for the session's calculation and display needs.</summary>
    public async Task<Instrument> FetchInstrumentAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        ValidateInstrument(instrument);
        var value = await _client.InstrumentAsync(instrument, ct).ConfigureAwait(false);
        lock (_instruments) _instruments[instrument] = value;
        return value;
    }

    /// <summary>Formats native quantities as whole lots or contracts, without sending a request.</summary>
    public decimal AmountToPrecision(InstrumentKey instrument, decimal quantity) => decimal.Floor(quantity);

    /// <summary>Formats a preview price using known instrument metadata only.</summary>
    public decimal PriceToPrecision(InstrumentKey instrument, decimal price)
    {
        lock (_instruments)
        {
            var step = _instruments[instrument].PriceStep!.Value;
            return decimal.Floor(price / step) * step;
        }
    }

    /// <summary>Returns all open regular and conditional orders belonging to the bound account.</summary>
    public async Task<IReadOnlyList<OrderSnapshot>> FetchOpenOrdersAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        ValidateInstrument(instrument);
        return (await _client.OrdersAsync(_profile, instrument, ct).ConfigureAwait(false)).Where(o => !TradingAccountSession.Terminal(o.State)).ToArray();
    }

    /// <summary>Looks up an order using both its category and exchange identifier.</summary>
    public async Task<OrderSnapshot> FetchOrderAsync(OrderReference order, CancellationToken ct = default)
    {
        ValidateInstrument(order.Instrument);
        return (await _client.OrdersAsync(_profile, order.Instrument, ct).ConfigureAwait(false)).Single(o => o.Reference == order);
    }

    /// <summary>Reads account executions without creating any local trades.</summary>
    public Task<IReadOnlyList<ExecutionSnapshot>> FetchMyTradesAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        ValidateInstrument(instrument);
        return _client.TradesAsync(_profile, instrument, ct);
    }

    /// <summary>Reads balances and collateral from the selected terminal routing tuple.</summary>
    public Task<AccountSnapshot> FetchBalanceAsync(CancellationToken ct = default) => _client.BalanceAsync(_profile, ct);

    /// <summary>Reads native holdings without calculating a synthetic BotView position.</summary>
    public Task<IReadOnlyList<PositionSnapshot>> FetchPositionsAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        ValidateInstrument(instrument);
        return _client.PositionsAsync(_profile, instrument, ct);
    }

    /// <summary>Rejects listing/account market mismatches before exposing terminal data.</summary>
    private void ValidateInstrument(InstrumentKey instrument)
    {
        var board = _profile.Market == TradingMarket.MoexFutures ? "SPBFUT" : "TQBR";
        if (instrument.Venue != "MOEX" || instrument.Board != board || QuikReadClient.Key(board, instrument.SecurityId) != instrument)
            throw new ArgumentException("The listing does not belong to this account market.");
    }

    /// <summary>MOEX positions are net; the adapter never changes an account's position mode.</summary>
    public Task<bool?> FetchPositionModeAsync(InstrumentKey instrument, CancellationToken ct = default) => Task.FromResult<bool?>(false);

    /// <summary>The shared session performs read-only snapshot reconciliation on its polling interval.</summary>
    public bool SupportsStream(TradingStream stream) => true;

    /// <summary>Coalesces callbacks without accumulating work during volatile markets.</summary>
    private void OnAccountChanged(TradingStream stream) => _signals[stream].Writer.TryWrite(true);

    /// <summary>Uses callbacks for fast refreshes and a heartbeat to detect broker disconnects.</summary>
    private async Task WaitForUpdateAsync(TradingStream stream, CancellationToken ct)
    {
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        heartbeat.CancelAfter(TimeSpan.FromSeconds(2));
        try { await _signals[stream].Reader.ReadAsync(heartbeat.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        ct.ThrowIfCancellationRequested();
        if (!await _client.ConnectedAsync(ct).ConfigureAwait(false)) throw new InvalidOperationException("QUIK не подключён к брокеру.");
    }

    /// <summary>Rejects mutation before accessing the terminal transport.</summary>
    public Task<OrderSnapshot> CreateLimitOrderAsync(LimitOrderRequest request, CancellationToken ct = default) => throw ReadOnly();

    /// <summary>Rejects mutation before accessing the terminal transport.</summary>
    public Task<OrderSnapshot> CreateMarketOrderAsync(MarketOrderRequest request, CancellationToken ct = default) => throw ReadOnly();

    /// <summary>Rejects cancellation before accessing the terminal transport.</summary>
    public Task<OrderSnapshot> CancelOrderAsync(OrderReference order, CancellationToken ct = default) => throw ReadOnly();

    /// <summary>Rejects account changes before accessing the terminal transport.</summary>
    public Task SetLeverageAsync(InstrumentKey instrument, int leverage, CancellationToken ct = default) => throw ReadOnly();

    /// <summary>Polling is used instead of a synthetic private order stream.</summary>
    public async Task<IReadOnlyList<OrderSnapshot>> WatchOrdersAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        ValidateInstrument(instrument);
        await WaitForUpdateAsync(TradingStream.Orders, ct).ConfigureAwait(false);
        return await _client.OrdersAsync(_profile, instrument, ct).ConfigureAwait(false);
    }

    /// <summary>Polling is used instead of a synthetic private execution stream.</summary>
    public async Task<IReadOnlyList<ExecutionSnapshot>> WatchMyTradesAsync(InstrumentKey instrument, CancellationToken ct = default)
    {
        await WaitForUpdateAsync(TradingStream.MyTrades, ct).ConfigureAwait(false);
        return await FetchMyTradesAsync(instrument, ct).ConfigureAwait(false);
    }

    /// <summary>Polling is used instead of a synthetic private balance stream.</summary>
    public async Task<AccountSnapshot> WatchBalanceAsync(CancellationToken ct = default)
    {
        await WaitForUpdateAsync(TradingStream.Balance, ct).ConfigureAwait(false);
        return await FetchBalanceAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns an explicit unsupported result, never a successful no-op trade.</summary>
    private static NotSupportedException ReadOnly() => new("QUIK: только чтение. Исполнение новых команд не реализовано.");

    /// <summary>Releases this account reader without stopping other terminal consumers.</summary>
    public ValueTask DisposeAsync()
    {
        _client.AccountChanged -= OnAccountChanged;
        return _client.DisposeAsync();
    }
}
