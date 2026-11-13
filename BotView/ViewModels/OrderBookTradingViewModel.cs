using System.Collections.ObjectModel;
using BotView.Models;
using BotView.Services;

namespace BotView.ViewModels;

public sealed partial class OrderBookViewModel
{
    private TradingSessionManager? _tradingManager;
    private TradingAccountSession? _trading;
    private TradingAccountProfile? _selectedAccount;
    private bool _tradeBusy;
    private bool _bindingAccount;
    private bool _postOnly;
    private int _accountGeneration;
    private int _managerRevision = -1;
    private OrderQuantityCurrency _quantityCurrency;
    private bool _mouseTrading;
    private Task _bindTask = Task.CompletedTask;

    public TradingEnvironment Environment { get; private set; }
    public string EnvironmentText => IsQuik ? "QUIK · только чтение" : Environment == TradingEnvironment.Testnet ? "Testnet" : "MAINNET · live trading";
    public bool IsQuik => Market is TradingMarket.MoexShares or TradingMarket.MoexFutures;
    public bool IsFutures => Market is TradingMarket.UsdtFutures or TradingMarket.MoexFutures;
    public InstrumentKey TradingInstrument => _listedInstrument?.Key ?? new(Exchange, IsFutures ? "futures" : "spot", "", Symbol);
    private bool _reduceOnly;
    private bool? _hedged;
    private string _positionTarget = "Long";
    public string[] PositionTargets { get; } = ["Long", "Short"];
    public bool ReduceOnly { get => _reduceOnly; set { if (IsTradeEditable) { _reduceOnly = value; Changed(); } } }
    public string PositionTarget { get => _positionTarget; set { if (IsTradeEditable && PositionTargets.Contains(value)) { _positionTarget = value; Changed(); } } }
    /// <summary>Hedge mode chooses a leg. One-way and an unreported mode send Buy or Sell only.</summary>
    public bool ShowPositionSide => IsFutures && _hedged == true;
    public string PositionModeText => !IsFutures || _trading is null ? "" : _hedged switch
    {
        true => "Hedge mode",
        false => "One-way mode",
        _ => "Position mode unavailable"
    };
    private PositionSide? SelectedPositionSide => ShowPositionSide
        ? PositionTarget == "Long" ? PositionSide.Long : PositionSide.Short
        : null;
    public string PositionsText => !IsFutures && !IsQuik ? "" : string.Join(" · ", (_trading?.Current.Balance?.Positions ?? [])
        .Where(p => p.Instrument == TradingInstrument)
        .Select(p => IsQuik ? $"{p.Side}: {p.Quantity} {p.QuantityUnit} · average {p.AveragePrice?.ToString() ?? "?"}"
            : $"{p.Side}: {p.Quantity} contracts · {p.Leverage}x · {p.MarginMode} · PnL {p.UnrealizedPnl?.Amount} USDT"));
    private string _leverageText = "";
    public string[] LeverageOptions { get; } = ["1", "2", "3", "5", "10", "20", "25", "50", "100"];
    public RelayCommand ApplyLeverageCommand { get; private set; } = null!;

    /// <summary>Edits the requested leverage without changing the exchange until Apply is pressed.</summary>
    public string LeverageText
    {
        get => _leverageText;
        set
        {
            if (!IsTradeEditable || value == _leverageText)
            {
                return;
            }

            _leverageText = value;
            Changed();
        }
    }

    public bool CanApplyLeverage => !IsQuik && !_disposed && IsFutures && IsTradeEditable &&
        _trading?.Current.Connected == true && int.TryParse(LeverageText, out var leverage) && leverage >= 1;

    public ObservableCollection<TradingAccountProfile> Accounts { get; } = new();
    private IReadOnlyList<OrderSnapshot> _ownOrders = [];
    public OrderQuantityCurrency[] QuantityCurrencies { get; } = [OrderQuantityCurrency.Base, OrderQuantityCurrency.Quote];
    public RelayCommand ReconnectCommand { get; private set; } = null!;
    public bool CanOpenOrders => !_disposed && SelectedAccount is not null && _tradingManager is not null;
    internal string? ConnectedAccountId => _trading?.Account.AccountId;
    public bool IsTradeEditable => !_tradeBusy && !_bindingAccount;
    public bool CanSelectMouseTrading => !IsQuik && !_disposed && IsLimitOrder && !_tradeBusy;

    /// <summary>Keeps the explicit mouse-trading choice across account changes, but never enables it for Market.</summary>
    public bool MouseTrading
    {
        get => _mouseTrading;
        set
        {
            if (!CanSelectMouseTrading || value == _mouseTrading)
            {
                return;
            }

            _mouseTrading = value;
            Changed();
        }
    }
    public string AccountStatus => _bindingAccount ? "Connecting account…" : _trading?.Current.Message ?? "Select an account with keys stored in memory.";
    public string AccountConnectionText => _bindingAccount ? "Account: connecting…"
        : _trading?.Current.Connected == true ? "Account: connected" : "Account: disconnected";
    public string AccountIdentity => _trading is null ? "" : _trading.Account.AccountId.StartsWith("key:", StringComparison.Ordinal)
        ? "Account verified by this API key" : _trading.Account.AccountId;
    public string BalanceText => IsQuik ? string.Join(" · ", (_trading?.Current.Balance?.Balances ?? []).Select(b => $"{b.Currency}: {b.Total} / free {b.Available}")
        .Concat((_trading?.Current.Balance?.Margins ?? []).Select(m => $"{m.Currency}: margin available {m.AvailableFunds}")))
        : IsFutures ? $"Available margin: {_trading?.Current.Balance?.Margins?.FirstOrDefault(m => m.Currency == "USDT")?.AvailableFunds?.ToString() ?? "?"} USDT" : _trading?.Current.Balance?.Balances is { } balances
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
            if (!CanSelectQuantityCurrency || value == _quantityCurrency || !Enum.IsDefined(value)) return;
            try
            {
                if (_trading is not null && TryPositive(PriceText, out var price) && TryPositive(AmountText, out var input))
                {
                    var quantity = _trading.GetOrderInputCalculator(TradingInstrument).ConvertQuantity(input, price, _quantityCurrency);
                    _amountText = Format(value == OrderQuantityCurrency.Base ? quantity : _trading.GetOrderInputCalculator(TradingInstrument).OrderNotional(quantity, price));
                }
                else _amountText = "";
            }
            catch (Exception) { _amountText = ""; }
            _quantityCurrency = value;
            Changed();
        }
    }

    public string QuantityHeader => IsFutures && QuantityCurrency == OrderQuantityCurrency.Base ? "Quantity (contracts)" : $"Quantity ({(QuantityCurrency == OrderQuantityCurrency.Base ? Symbol.Split('/')[0] : Symbol.Split('/').Last().Split(':')[0])})";
    private OrderType _selectedOrderType = OrderType.Limit;
    public OrderType[] OrderTypes { get; } = [OrderType.Limit, OrderType.Market];
    public bool IsLimitOrder => SelectedOrderType == OrderType.Limit;
    public bool CanSelectQuantityCurrency => IsTradeEditable && IsLimitOrder;

    /// <summary>Resets incompatible draft options and disables mouse trading when Market is selected.</summary>
    public OrderType SelectedOrderType
    {
        get => _selectedOrderType;
        set
        {
            if (!IsTradeEditable || !OrderTypes.Contains(value) || value == _selectedOrderType) return;
            _selectedOrderType = value;
            _amountText = "";
            _quantityCurrency = OrderQuantityCurrency.Base;
            _postOnly = false;
            if (!IsLimitOrder) _mouseTrading = false;
            Changed();
        }
    }

    /// <summary>Prepares and validates only required input and exchange precision, without trading policy checks.</summary>
    private (decimal Quantity, decimal Price) PreparedInput()
    {
        if (_trading is null) throw new InvalidOperationException("Select a connected account.");
        if (!TryPositive(AmountText, out var input)) throw new ArgumentException("Enter a quantity greater than zero.");
        decimal price = 1;
        if (IsLimitOrder)
        {
            if (!TryPositive(PriceText, out price)) throw new ArgumentException("Enter a limit price greater than zero.");
            price = _trading.GetOrderInputCalculator(TradingInstrument).PriceToPrecision(price);
            if (price <= 0) throw new ArgumentException("Price rounds to zero at exchange precision.");
        }
        var quantity = _trading.GetOrderInputCalculator(TradingInstrument).ConvertQuantity(input, price,
            IsLimitOrder ? QuantityCurrency : OrderQuantityCurrency.Base);
        if (quantity <= 0) throw new ArgumentException("Quantity rounds to zero at exchange precision.");
        return (quantity, price);
    }

    public string TradingInputHint
    {
        get
        {
            if (_bindingAccount) return "Connecting account…";
            if (IsQuik) return "QUIK: только чтение. Новые команды вызывают заглушки.";
            if (_trading is null) return "Select an account with API keys.";
            if (!_trading.Current.Connected) return "Reconnect the trading account.";
            if (IsFutures && _hedged is null) return "The exchange did not report whether this account is in one-way or hedge mode.";
            if (_tradeBusy) return "Waiting for the exchange response…";
            try { PreparedInput(); return ""; }
            catch (ArgumentException ex) { return ex.Message; }
            catch (Exception) { return "Could not format order quantity or price."; }
        }
    }

    private bool TradingInputValid => TradingInputHint.Length == 0;

    public string OrderPreview
    {
        get
        {
            try
            {
                var input = PreparedInput();
                var unit = IsFutures ? "contracts" : Symbol.Split('/')[0];
                if (!IsLimitOrder) return $"{Format(input.Quantity)} {unit} · Market; execution price is determined by the exchange";
                return $"{Format(input.Quantity)} {unit} @ {Format(input.Price)} · {Format(_trading!.GetOrderInputCalculator(TradingInstrument).OrderNotional(input.Quantity, input.Price))} {Symbol.Split('/').Last().Split(':')[0]} (excluding commission)";
            }
            catch (Exception) { return ""; }
        }
    }

    /// <summary>Shows an opening-margin estimate at the selected leverage without affecting order eligibility.</summary>
    public string MarginPreview
    {
        get
        {
            if (!IsFutures || IsQuik)
            {
                return "";
            }

            if (ReduceOnly)
            {
                return "Initial margin estimate is not applicable to Reduce-only orders.";
            }

            if (!int.TryParse(LeverageText, out var leverage) || leverage < 1)
            {
                return "Select leverage to estimate initial margin.";
            }

            try
            {
                var input = PreparedInput();
                var calculator = _trading!.GetOrderInputCalculator(TradingInstrument);
                var suffix = $" USDT at selected {leverage}x (excluding fees)";

                if (IsLimitOrder)
                {
                    var margin = calculator.EstimateInitialMargin(input.Quantity, input.Price, leverage);
                    return $"Estimated initial margin: ≈ {Format(margin)}{suffix}";
                }

                var book = _update?.Snapshot;

                if (_update?.Connection != OrderBookConnection.Live || book is null ||
                    DateTimeOffset.UtcNow - book.ReceivedAt > TimeSpan.FromSeconds(10) ||
                    book.BestAsk is not > 0 || book.BestBid is not > 0)
                {
                    return "Estimated initial margin: unavailable until live market prices arrive.";
                }

                var buy = calculator.EstimateInitialMargin(input.Quantity, book.BestAsk.Value, leverage);
                var sell = calculator.EstimateInitialMargin(input.Quantity, book.BestBid.Value, leverage);
                return $"Estimated initial margin: Buy ≈ {Format(buy)} / Sell ≈ {Format(sell)}{suffix}; based on best ask/bid, before slippage.";
            }
            catch (Exception)
            {
                return "Enter a valid quantity and price to estimate initial margin.";
            }
        }
    }

    /// <summary>Initializes trade controls without contacting any exchange or starting an order.</summary>
    private void InitializeTrading(TradingSessionManager? manager, TradingEnvironment environment)
    {
        _tradingManager = manager;
        Environment = environment;
        ApplyLeverageCommand = new RelayCommand(_ => ApplyLeverageAsync(), _ => CanApplyLeverage);
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
            foreach (var profile in settings.Profiles.Where(p => p.Exchange == Exchange && p.Environment == Environment && p.Market == Market &&
                (!IsQuik || p.Quik is { } q && ConnectionId == $"quik:{q.Host}:{q.Port}"))) Accounts.Add(profile);
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
        _hedged = null;
        _leverageText = "";
        _ownOrders = [];
        _priceText = ""; _amountText = ""; _tradeStatus = "";
        _bindingAccount = selected is not null;
        Changed();
        if (selected is null || _tradingManager is null) { _bindingAccount = false; return; }
        try
        {
            var service = await _tradingManager.GetAsync(selected.Id, _stop.Token);
            if (_disposed || generation != _accountGeneration) return;
            // Keep display subscriptions separate from the order-book feed.
            _trading = service;
            await Task.Run(() => service.TrackAsync(TradingInstrument, _stop.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && generation == _accountGeneration) _tradeStatus = ex is TradingServiceException safe ? safe.Message : "Could not connect the trading account."; }
        finally
        {
            if (!_disposed && generation == _accountGeneration)
            {
                _hedged = _trading?.Hedged;
                _bindingAccount = false;
                Changed();
            }
        }
    }

    /// <summary>Applies the latest detached account state without accumulating Dispatcher callbacks.</summary>
    private void RefreshTrading()
    {
        if (!_tradeBusy && _tradingManager is not null && _managerRevision != _tradingManager.Revision) ReloadAccounts();
        if (_trading is null) return;
        _hedged = _trading.Hedged;
        _ownOrders = _trading.Current.Orders.Where(o => o.Reference.Instrument == TradingInstrument).ToArray();
    }

    /// <summary>Uses the rendered row's price and side, then submits through the same path as Buy/Sell.</summary>
    public async Task ClickLevelAsync(OrderBookRow row, int clickCount = 1)
    {
        if (_disposed || !IsTradeEditable || clickCount != 1)
        {
            return;
        }

        SelectPrice(row.Price);

        if (!MouseTrading || !IsLimitOrder || row.Side == OrderBookSide.Spread)
        {
            return;
        }

        var latest = _subscription?.Current ?? _update;
        var unavailableReason = MouseTradingUnavailableReason(latest);
        if (unavailableReason is not null)
        {
            _tradeStatus = unavailableReason;
            Changed();
            return;
        }

        if (!CanTrade)
        {
            _tradeStatus = TradingInputHint;
            Changed();
            return;
        }

        await TradeAsync(row.Side == OrderBookSide.Bid ? OrderSide.Buy : OrderSide.Sell);
    }

    /// <summary>Distinguishes a disconnected public subscription from missing or stale depth, independently of account health.</summary>
    private static string? MouseTradingUnavailableReason(OrderBookUpdate? update)
    {
        if (update is null)
        {
            return "Mouse trading: waiting for the first order-book update.";
        }

        if (update.Connection != OrderBookConnection.Live)
        {
            return $"Mouse trading: order-book feed is {update.Connection}. {update.Message}";
        }

        if (update.Snapshot is not { } snapshot)
        {
            return "Mouse trading: the order-book feed has not supplied a depth snapshot yet.";
        }

        var age = DateTimeOffset.UtcNow - snapshot.ReceivedAt;
        if (age > TimeSpan.FromSeconds(10))
        {
            return FormattableString.Invariant($"Mouse trading: no order-book update for {age.TotalSeconds:0.0} s (limit: 10 s). Last received at {snapshot.ReceivedAt.ToLocalTime():HH:mm:ss}.");
        }

        return null;
    }

    /// <summary>Freezes a request before awaiting, preventing edits or another Buy/Sell from changing the request.</summary>
    internal async Task TradeAsync(OrderSide side)
    {
        if (!CanTrade || _trading is null) return;

        var service = _trading;
        var generation = _accountGeneration;
        _tradeBusy = true;

        Changed();

        try
        {
            var input = PreparedInput();
            var limit = IsLimitOrder
                ? new LimitOrderRequest(TradingInstrument, side, input.Quantity, input.Price, PostOnly, ReduceOnly, SelectedPositionSide)
                : null;
            var market = new MarketOrderRequest(TradingInstrument, side, input.Quantity, ReduceOnly, SelectedPositionSide);
            var token = _stop.Token;
            var result = limit is not null
                ? await Task.Run(() => service.CreateLimitOrderAsync(limit, token))
                : await Task.Run(() => service.CreateMarketOrderAsync(market, token));

            if (!_disposed && generation == _accountGeneration)
                _tradeStatus = $"{side}: {result.State} · {result.Reference.ExchangeOrderId} {result.StatusReason}";
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) _tradeStatus = "Response not received; check exchange orders before trying again.";
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _tradeStatus = TradingErrorText.FromException(ex);
            }
        }
        finally
        {
            _tradeBusy = false;
            if (!_disposed)
            {
                Changed();
            }
        }
    }

    /// <summary>Applies leverage once and releases the form after success, rejection or transport failure.</summary>
    internal async Task ApplyLeverageAsync()
    {
        if (!CanApplyLeverage || _trading is null || !int.TryParse(LeverageText, out var leverage))
        {
            return;
        }

        var session = _trading;
        var generation = _accountGeneration;
        _tradeBusy = true;
        Changed();

        try
        {
            await Task.Run(() => session.SetLeverageAsync(TradingInstrument, leverage, _stop.Token));

            if (!_disposed && generation == _accountGeneration)
            {
                _tradeStatus = $"Leverage set to {leverage}x for {Symbol}.";
            }
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && generation == _accountGeneration)
            {
                _tradeStatus = "Leverage response not received; check the exchange setting before trying again.";
            }
        }
        catch (Exception ex)
        {
            if (!_disposed && generation == _accountGeneration)
            {
                _tradeStatus = TradingErrorText.FromException(ex);
            }
        }
        finally
        {
            _tradeBusy = false;

            if (!_disposed)
            {
                Changed();
            }
        }
    }

}
