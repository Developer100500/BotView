using System.ComponentModel;

namespace BotView.ViewModels;

/// <summary>Independent account/pair display using the application's shared trading session.</summary>
public sealed class AccountOrdersViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly TradingSessionManager _manager;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _token;
    private TradingAccountSession? _session;
    private int _revision = -1;
    private int _generation;
    private bool _binding;
    private bool _busy;
    private bool _disposed;
    private string _status = "Connecting account…";
    private OrderSnapshot? _selectedOrder;

    /// <summary>Fixes the window to one profile, environment, market and instrument without opening a new client.</summary>
    public AccountOrdersViewModel(TradingSessionManager manager, TradingAccountProfile profile, InstrumentKey instrument)
    {
        _manager = manager;
        _token = _stop.Token;
        Profile = profile;
        Instrument = instrument;
        CancelSelectedCommand = new RelayCommand(_ => CancelSelectedAsync(), _ => CanCancel && SelectedOrder is { } order &&
            !TradingAccountSession.Terminal(order.State));
        CancelAllCommand = new RelayCommand(_ => CancelAllAsync(), _ => CanCancel);
        ReconnectCommand = new RelayCommand(_ => BindAsync(), _ => !_disposed && !_busy && !_binding);
    }

    public TradingAccountProfile Profile { get; }
    public InstrumentKey Instrument { get; }
    public string Title => $"Orders · {Instrument.SecurityId} · {Profile.Name} · {Profile.Exchange} · {Profile.Market} · {Network}";
    public string Network => Profile.Exchange == "quik" ? "QUIK · read only" : Profile.Environment == TradingEnvironment.Testnet ? "Testnet" : "MAINNET · live trading";
    public string ContextText => $"{Profile.Name} · {Profile.Exchange} · {Profile.Market} · {Network} · {Instrument.SecurityId}";
    public string AccountIdentity => _session?.Account.AccountId is { } id
        ? id.StartsWith("key:", StringComparison.Ordinal) ? "Account verified by this API key" : id
        : "";
    public string ConnectionStatus => _binding ? "Connecting account…" : _session?.Current.Message ?? _status;
    public string OperationStatus { get; private set; } = "";
    public IReadOnlyList<OrderSnapshot> Orders { get; private set; } = [];
    public IReadOnlyList<ExecutionSnapshot> Executions { get; private set; } = [];
    public RelayCommand CancelSelectedCommand { get; }
    public RelayCommand CancelAllCommand { get; }
    public RelayCommand ReconnectCommand { get; }
    public Func<CancelBatch, Task<bool>>? ConfirmCancelAllAsync { get; set; }
    private bool CanCancel => Profile.Exchange != "quik" && !_disposed && !_busy && !_binding && _session?.Current.Connected == true && _revision == _manager.Revision;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Preserves selection by exchange reference when periodic snapshots replace row objects.</summary>
    public OrderSnapshot? SelectedOrder
    {
        get => _selectedOrder;
        set
        {
            _selectedOrder = value;
            Changed();
        }
    }

    /// <summary>Rebinds only the original profile after key changes; never substitutes another account.</summary>
    public async Task BindAsync()
    {
        if (_disposed) return;

        var generation = ++_generation;
        _revision = _manager.Revision;
        _binding = true;
        _session = null;
        Orders = [];
        Executions = [];
        _selectedOrder = null;
        OperationStatus = "";
        Changed();

        try
        {
            var current = _manager.Settings().Profiles.SingleOrDefault(p => p.Id == Profile.Id);
            if (current is null || current.Exchange != Profile.Exchange || current.Environment != Profile.Environment || current.Market != Profile.Market)
            {
                _status = "The original account profile is unavailable or its connection settings changed.";
                return;
            }

            var session = await _manager.GetAsync(Profile.Id, _token);
            await Task.Run(() => session.TrackAsync(Instrument, _token), _token);

            if (!_disposed && generation == _generation && _revision == _manager.Revision)
            {
                _session = session;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed && generation == _generation) _status = TradingErrorText.FromException(ex);
        }
        finally
        {
            if (!_disposed && generation == _generation)
            {
                _binding = false;
                Refresh();
            }
        }
    }

    /// <summary>Reads shared state on the UI timer, independent of any public order-book window.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        if (_revision != _manager.Revision)
        {
            _ = BindAsync();
            return;
        }

        if (_session is not null)
        {
            var state = _session.Current;
            var selected = _selectedOrder?.Reference;
            var orders = state.Orders.Where(o => o.Reference.Instrument == Instrument)
                .OrderBy(o => TradingAccountSession.Terminal(o.State)).ThenByDescending(o => o.UpdatedAt).ToArray();
            var executions = state.Executions.Where(e => e.Order.Instrument == Instrument).Take(100).ToArray();
            if (!Orders.SequenceEqual(orders))
            {
                Orders = orders;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Orders)));
            }

            if (!Executions.SequenceEqual(executions)) Executions = executions;
            _selectedOrder = Orders.FirstOrDefault(o => o.Reference == selected);
        }

        Changed();
    }

    /// <summary>Cancels one fixed reference and keeps the shared session alive when this view closes.</summary>
    internal async Task CancelSelectedAsync()
    {
        if (!CanCancel || _session is not { } session || SelectedOrder is not { } order || TradingAccountSession.Terminal(order.State)) return;

        var generation = _generation;
        _busy = true;
        Changed();

        try
        {
            var result = await Task.Run(() => session.CancelOrderAsync(order.Reference, _token), _token);
            if (!_disposed && generation == _generation)
                OperationStatus = $"Cancel {order.Reference.ExchangeOrderId}: {result.State} {result.StatusReason}";
        }
        catch (Exception ex)
        {
            if (!_disposed && generation == _generation) OperationStatus = TradingErrorText.FromException(ex);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    /// <summary>Confirms a frozen list with this window's account and cancels exactly those exchange IDs.</summary>
    internal async Task CancelAllAsync()
    {
        if (!CanCancel || _session is not { } session || ConfirmCancelAllAsync is null) return;

        var generation = _generation;
        var revision = _revision;
        _busy = true;
        Changed();

        try
        {
            var batch = await Task.Run(() => session.PrepareCancelAllAsync(Instrument, _token), _token);
            if (_disposed || generation != _generation || revision != _manager.Revision || !await ConfirmCancelAllAsync(batch)) return;
            if (_disposed || generation != _generation || revision != _manager.Revision) return;

            var results = await Task.Run(() => session.CancelBatchAsync(batch, _token), _token);
            if (!_disposed && generation == _generation) OperationStatus = string.Join("; ", results);
        }
        catch (Exception ex)
        {
            if (!_disposed && generation == _generation) OperationStatus = TradingErrorText.FromException(ex);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    /// <summary>Updates bindings and cancellation availability after each detached state change.</summary>
    private void Changed()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        CancelSelectedCommand.RaiseCanExecuteChanged();
        CancelAllCommand.RaiseCanExecuteChanged();
        ReconnectCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Stops this view's work without disposing the shared session or canceling exchange orders.</summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        ++_generation;
        _stop.Cancel();
        _stop.Dispose();
        _session = null;
    }
}
