using System.Collections.ObjectModel;
using BotView.Models;
using BotView.Services;
using BotView.Services.Integrations.Ccxt;

namespace BotView.ViewModels;

public sealed partial class OrderBookViewModel
{
    private TradingSessionManager? _tradingManager;
    private CcxtTradingService? _trading;
    private TradingAccountProfile? _selectedAccount;
    private bool _tradeBusy;
    private bool _bindingAccount;
    private bool _postOnly;
    private int _accountGeneration;
    private int _managerRevision = -1;
    private OrderQuantityCurrency _quantityCurrency;
    private OrderSnapshot? _selectedOrder;
    private SpotTradingState? _lastTradingState;
    private Task _bindTask = Task.CompletedTask;

    public TradingEnvironment Environment { get; private set; }
    public string EnvironmentText => Environment == TradingEnvironment.Testnet ? "Testnet" : "MAINNET · live trading";
    public bool IsFutures => Market == TradingMarket.UsdtFutures;
    public InstrumentKey TradingInstrument => new(Exchange, IsFutures ? "futures" : "spot", "", Symbol);
    private bool _reduceOnly;
    private string _positionTarget = "Auto";
    public string[] PositionTargets { get; } = ["Auto", "Long", "Short"];
    public bool ReduceOnly { get => _reduceOnly; set { if (IsTradeEditable) { _reduceOnly = value; Changed(); } } }
    public string PositionTarget { get => _positionTarget; set { if (IsTradeEditable && PositionTargets.Contains(value)) { _positionTarget = value; Changed(); } } }
    private PositionSide? SelectedPositionSide => PositionTarget == "Long" ? PositionSide.Long : PositionTarget == "Short" ? PositionSide.Short : null;
    public string PositionsText => !IsFutures ? "" : string.Join(" · ", (_trading?.Current.Balance?.Positions ?? [])
        .Where(p => p.Instrument == TradingInstrument)
        .Select(p => $"{p.Side}: {p.Quantity} contracts · {p.Leverage}x · {p.MarginMode} · PnL {p.UnrealizedPnl?.Amount} USDT"));
    public ObservableCollection<TradingAccountProfile> Accounts { get; } = new();
    public IReadOnlyList<OrderSnapshot> Orders { get; private set; } = [];
    public IReadOnlyList<ExecutionSnapshot> Executions { get; private set; } = [];
    public OrderQuantityCurrency[] QuantityCurrencies { get; } = [OrderQuantityCurrency.Base, OrderQuantityCurrency.Quote];
    public RelayCommand CancelSelectedCommand { get; private set; } = null!;
    public RelayCommand CancelAllCommand { get; private set; } = null!;
    public RelayCommand ReconnectCommand { get; private set; } = null!;
    public Func<CancelBatch, Task<bool>>? ConfirmCancelAllAsync { get; set; }
    public bool IsTradeEditable => !_tradeBusy && !_bindingAccount;
    public string AccountStatus => _bindingAccount ? "Connecting account…" : _trading?.Current.Message ?? "Select an account with keys stored in memory.";
    public string AccountIdentity => _trading is null ? "" : _trading.Account.AccountId.StartsWith("key:", StringComparison.Ordinal)
        ? "Account verified by this API key" : _trading.Account.AccountId;
    public string PendingStatus => _lastTradingState is null ? "" : string.Join("\n", _lastTradingState.Pending
        .Where(o => o.Request.Instrument == TradingInstrument)
        .Select(o => $"Unknown {(o.CancelTarget is null ? "placement" : "cancellation")}: {o.Id}. Reconciling; no automatic resend."));
    public string BalanceText => IsFutures ? $"Available margin: {_trading?.Current.Balance?.Margins?.FirstOrDefault(m => m.Currency == "USDT")?.AvailableFunds?.ToString() ?? "?"} USDT" : _trading?.Current.Balance?.Balances is { } balances
        ? string.Join(" · ", balances.Where(b => b.Currency == Symbol.Split('/')[0] || b.Currency == Symbol.Split('/').Last().Split(':')[0])
            .Select(b => $"{b.Currency}: {b.Available?.ToString() ?? "?"} free")) : "Balance unavailable";

    public TradingAccountProfile? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (_tradeBusy || value?.Id == _selectedAccount?.Id) return;
            _selectedAccount = value;
            _bindTask = BindAccountAsync();
        }
    }

    public bool PostOnly { get => _postOnly; set { if (!IsTradeEditable) return; _postOnly = value; Changed(); } }
    public OrderQuantityCurrency QuantityCurrency
    {
        get => _quantityCurrency;
        set
        {
            if (!IsTradeEditable || value == _quantityCurrency || !Enum.IsDefined(value)) return;
            try
            {
                if (_trading is not null && TryPositive(PriceText, out var price) && TryPositive(AmountText, out var input))
                {
                    var quantity = _trading.ConvertQuantity(TradingInstrument, input, price, _quantityCurrency);
                    _amountText = Format(value == OrderQuantityCurrency.Base ? quantity : _trading.OrderNotional(TradingInstrument, quantity, price));
                }
                else _amountText = "";
            }
            catch (Exception) { _amountText = ""; }
            _quantityCurrency = value;
            Changed();
        }
    }

    public string QuantityHeader => IsFutures && QuantityCurrency == OrderQuantityCurrency.Base ? "Quantity (contracts)" : $"Quantity ({(QuantityCurrency == OrderQuantityCurrency.Base ? Symbol.Split('/')[0] : Symbol.Split('/').Last().Split(':')[0])})";
    public OrderSnapshot? SelectedOrder { get => _selectedOrder; set { _selectedOrder = value; Changed(); } }
    private bool TradingInputValid
    {
        get
        {
            try
            {
                if (_trading is null || !TryPositive(PriceText, out var price) || !TryPositive(AmountText, out var amount)) return false;
                var quantity = _trading.ConvertQuantity(TradingInstrument, amount, price, QuantityCurrency);
                return _trading.ValidateInput(new(TradingInstrument, OrderSide.Buy, OrderType.Limit, quantity, "bvpreview", price, PostOnly: PostOnly, ReduceOnly: ReduceOnly, PositionSide: SelectedPositionSide)).IsValid;
            }
            catch (Exception) { return false; }
        }
    }

    public string OrderPreview
    {
        get
        {
            if (_trading is null || !TryPositive(PriceText, out var price) || !TryPositive(AmountText, out var input)) return "";
            try
            {
                var amount = _trading.ConvertQuantity(TradingInstrument, input, price, QuantityCurrency);
                return $"{Format(amount)} {(IsFutures ? "contracts" : Symbol.Split('/')[0])} · {Format(_trading.OrderNotional(TradingInstrument, amount, price))} {Symbol.Split('/').Last().Split(':')[0]} (excluding commission)";
            }
            catch (Exception) { return "Invalid quantity or precision."; }
        }
    }

    /// <summary>Initializes trade controls without contacting any exchange or starting an order.</summary>
    private void InitializeTrading(TradingSessionManager? manager, TradingEnvironment environment)
    {
        _tradingManager = manager;
        Environment = environment;
        CancelSelectedCommand = new RelayCommand(CancelSelectedAsync, _ => !_tradeBusy && _trading is not null && SelectedOrder is { } order && !CcxtTradingService.Terminal(order.State));
        CancelAllCommand = new RelayCommand(CancelAllAsync, _ => !_tradeBusy && !_bindingAccount && _trading is not null);
        ReconnectCommand = new RelayCommand(_ => BindAccountAsync(), _ => !_tradeBusy && !_bindingAccount && SelectedAccount is not null);
        ReloadAccounts();
    }

    /// <summary>Updates available profiles on the UI timer and rebinds after keys/profile changes.</summary>
    private void ReloadAccounts()
    {
        if (_tradingManager is null) return;
        _managerRevision = _tradingManager.Revision;
        try
        {
            var settings = _tradingManager.Settings();
            var oldId = _selectedAccount?.Id;
            Accounts.Clear();
            foreach (var profile in settings.Profiles.Where(p => p.Exchange == Exchange && p.Environment == Environment && p.Market == Market)) Accounts.Add(profile);
            _selectedAccount = Accounts.FirstOrDefault(p => p.Id == oldId)
                ?? Accounts.FirstOrDefault(p => p.Id == settings.ActiveProfiles.GetValueOrDefault(Exchange));
            _bindTask = BindAccountAsync();
        }
        catch (Exception) { _tradeStatus = "Could not load trading account profiles."; }
    }

    /// <summary>Guards late authentication results and clears order input whenever account identity changes.</summary>
    private async Task BindAccountAsync()
    {
        var generation = ++_accountGeneration;
        var selected = _selectedAccount;
        _trading = null;
        _lastTradingState = null;
        Orders = []; Executions = []; SelectedOrder = null;
        _priceText = ""; _amountText = ""; _tradeStatus = "";
        _bindingAccount = selected is not null;
        Changed();
        if (selected is null || _tradingManager is null) { _bindingAccount = false; return; }
        try
        {
            var service = await _tradingManager.GetAsync(selected.Id, _stop.Token);
            if (_disposed || generation != _accountGeneration) return;
            // Keep the session attached even if the first snapshot fails; its worker continues recovery.
            _trading = service;
            await Task.Run(() => service.TrackAsync(TradingInstrument, _stop.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && generation == _accountGeneration) _tradeStatus = ex is TradingServiceException safe ? safe.Message : "Could not connect the trading account."; }
        finally
        {
            if (!_disposed && generation == _accountGeneration) { _bindingAccount = false; Changed(); }
        }
    }

    /// <summary>Applies the latest detached account state without accumulating Dispatcher callbacks.</summary>
    private void RefreshTrading()
    {
        if (!_tradeBusy && _tradingManager is not null && _managerRevision != _tradingManager.Revision) ReloadAccounts();
        if (_trading is null) return;
        _lastTradingState = _trading.Current;
        var selectedId = SelectedOrder?.Reference;
        var orders = _lastTradingState.Orders.Where(o => o.Reference.Instrument == TradingInstrument)
            .OrderBy(o => CcxtTradingService.Terminal(o.State)).ThenByDescending(o => o.UpdatedAt).ToArray();
        if (!Orders.SequenceEqual(orders)) Orders = orders;
        var fills = _lastTradingState.Executions.Where(f => f.Order.Instrument == TradingInstrument).Take(100).ToArray();
        if (!Executions.SequenceEqual(fills)) Executions = fills;
        _selectedOrder = Orders.FirstOrDefault(o => o.Reference == selectedId);
    }

    /// <summary>Freezes a request before awaiting, preventing edits or another Buy/Sell from changing the dispatched intent.</summary>
    internal async Task TradeAsync(OrderSide side)
    {
        if (!CanTrade || _trading is null) return;

        var service = _trading;
        var generation = _accountGeneration;
        _tradeBusy = true;

        Changed();

        try
        {
            if (!TryPositive(PriceText, out var price) || !TryPositive(AmountText, out var input)) return;

            var quantity = service.ConvertQuantity(TradingInstrument, input, price, QuantityCurrency);
            var request = new PlaceOrderRequest(TradingInstrument, side, OrderType.Limit, quantity,
                "bv" + Guid.NewGuid().ToString("N"), price, PostOnly: PostOnly, ReduceOnly: ReduceOnly, PositionSide: SelectedPositionSide);
            var result = await Task.Run(() => service.PlaceOrderAsync(request, _stop.Token));

            if (!_disposed && generation == _accountGeneration) {
                _tradeStatus = $@"{side}: {result.Status} {result.Order?.State} ·
                    {result.ExchangeOrderId ?? result.ClientOrderId} {result.Order?.StatusReason}" +
                    (result.Error is null ? "" : $" · {result.Error.Message}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) {
            if (!_disposed) {
                _tradeStatus = ex is TradingServiceException safe ?
                    safe.Message :
                    "Order could not be prepared. Check values and precision.";
            }
        }
        finally {
            _tradeBusy = false;
            if (!_disposed) {
                Changed();
            }
        }
    }

    /// <summary>Cancels exactly the selected account order, preserving a possible partial fill.</summary>
    private async Task CancelSelectedAsync(object? parameter)
    {
        if (_tradeBusy || _trading is null || SelectedOrder is not { } order) return;
        var service = _trading;
        _tradeBusy = true; Changed();
        try
        {
            var result = await Task.Run(() => service.CancelOrderAsync(order.Reference, _stop.Token));
            if (!_disposed) _tradeStatus = $"Cancel {order.Reference.ExchangeOrderId}: {result.Status} {result.Error?.Message}";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed) _tradeStatus = "Cancellation failed; check the account status."; }
        finally { _tradeBusy = false; if (!_disposed) Changed(); }
    }

    /// <summary>Confirms a frozen account/pair snapshot, including external orders, before starting batch cancellation.</summary>
    private async Task CancelAllAsync(object? parameter)
    {
        if (_tradeBusy || _trading is null || ConfirmCancelAllAsync is null) return;
        var service = _trading;
        _tradeBusy = true; Changed();
        try
        {
            var batch = await Task.Run(() => service.PrepareCancelAllAsync(TradingInstrument, _stop.Token));
            if (_disposed || !await ConfirmCancelAllAsync(batch)) return;
            var results = await Task.Run(() => service.CancelBatchAsync(batch, _stop.Token));
            if (!_disposed) _tradeStatus = string.Join("; ", results.Select(r => $"{r.Reference.ExchangeOrderId}: {r.Status} {r.Error?.Message}"));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed) _tradeStatus = "Could not complete batch cancellation. Check individual order states."; }
        finally { _tradeBusy = false; if (!_disposed) Changed(); }
    }
}
