using System.Collections.ObjectModel;
using System.ComponentModel;
using BotView.Interfaces;
using BotView.Models;

namespace BotView.ViewModels;

/// <summary>
/// A trading environment option.
/// </summary>
/// <param name="Environment">Testnet or Mainnet (demo vs live trading).</param>
/// <param name="Label">The label to display in the UI.</param>
public sealed record TradingEnvironmentOption(TradingEnvironment Environment, string Label);

/// <summary>
/// Profile + credentials + verification status.
/// </summary>
public sealed class TradingAccountItem : INotifyPropertyChanged
{
    /// <summary> Constructs a new trading account item. </summary>
    /// <param name="Profile">The trading account profile.</param>
    /// <param name="HasCredentials">Whether the account has credentials.</param>
    /// <param name="IsActive">Whether the account is chosen for the exchange.</param>
    public TradingAccountItem(TradingAccountProfile profile, bool hasCredentials)
    {
        Profile = profile;
        HasCredentials = hasCredentials;
        IsActive = profile.Environment == TradingEnvironment.Testnet;
    }
    public TradingAccountProfile Profile { get; private set; }
    public bool HasCredentials { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsChecking { get; private set; }
    public AccountVerificationResult? Result { get; private set; }
    public string DisplayName => Profile.DisplayName;
    public string EnvironmentText => Profile.Environment == TradingEnvironment.Testnet ? "Testnet" : "Mainnet";
    public string Status => IsChecking ? "Verifying…" : !HasCredentials ? "Keys required"
        : Result is null ? "Not verified" : Result.Connected ? "Connected" : "Error";
    public string ActiveText => IsActive ? "Selected for exchange" : "";
    public string PermissionText => $"Trading: {Permission(Result?.Permissions.CanTrade)} · Withdrawals: {Permission(Result?.Permissions.CanWithdraw)}";
    public event PropertyChangedEventHandler? PropertyChanged;
    private static string Permission(bool? value) => value switch { true => "allowed", false => "denied", _ => "unknown" };
    internal void Update(TradingAccountProfile profile, bool hasCredentials, bool reset)
    {
        Profile = profile;
        HasCredentials = hasCredentials;
        if (reset) Result = null;
        Notify();
    }
    internal void SetActive(bool active) { IsActive = active; Notify(); }
    internal void SetChecking(bool checking) { IsChecking = checking; Notify(); }
    internal void SetResult(AccountVerificationResult result) { Result = result; Notify(); }
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

public sealed class TradingAccountsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ITradingAccountStore _store;
    private readonly ICredentialStore _credentials;
    private readonly IAccountConnectionVerifier _verifier;
    private Dictionary<string, Guid> _active = new();
    private CancellationTokenSource? _request;
    private TradingAccountItem? _checkingItem;
    private int _requestVersion;
    private bool _disposed;
    private bool _storageAvailable = true;
    private TradingAccountItem? _selected;
    private string _name = "";
    private string _exchange = "binance";
    private TradingEnvironment _environment = TradingEnvironment.Testnet;
    private string _message = "Add an account profile or select an existing one.";
    private bool _hasCredentialInput; // whether the user has entered any credentials in the input fields
    private bool _hasCompleteCredentials; // all required credentials have been entered

    public TradingAccountsViewModel(
        ITradingAccountStore store,
        ICredentialStore credentials,
        IAccountConnectionVerifier verifier) 
    {
        _store = store;
        _credentials = credentials;
        _verifier = verifier;
        NewCommand = new RelayCommand(BeginNew);
        DeleteCommand = new RelayCommand(_ => DeleteSelected(), _ => CanModifySelected);
        ActivateCommand = new RelayCommand(_ => ActivateSelected(), _ => CanModifySelected);
        VerifyCommand = new RelayCommand(_ => VerifySelectedAsync(),
                _ => !_disposed && _request is null && _selected?.HasCredentials == true);
        CancelCommand = new RelayCommand(_ => CancelVerification(), _ => _request is not null);
        try
        {
            var settings = _store.Load();
            _active = new Dictionary<string, Guid>(settings.ActiveProfiles);

            foreach (var profile in settings.Profiles)
            {
                var item = new TradingAccountItem(profile, _credentials.Contains(profile.Id));
                item.SetActive(_active.GetValueOrDefault(profile.Exchange) == profile.Id);
                Profiles.Add(item);
            }
            SelectedAccount = Profiles.FirstOrDefault();
        }
        catch (Exception)
        {
            _storageAvailable = false;
            _message = "Could not read account profiles. Saving is disabled to avoid overwriting settings.";
        }
    }

    public ObservableCollection<TradingAccountItem> Profiles { get; } = new();
    public ExchangeOption[] Exchanges { get; } = [new("binance", "Binance"), new("bybit", "Bybit")];
    public TradingEnvironmentOption[] Environments { get; } = [
        new(TradingEnvironment.Testnet, "Testnet"),
        new(TradingEnvironment.Production, "Mainnet")
    ];
    public TradingAccountItem? SelectedAccount
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            CancelVerification();
            _selected = value;
            _name = value?.Profile.Name ?? "";
            _exchange = value?.Profile.Exchange ?? "binance";
            _environment = value?.Profile.Environment ?? TradingEnvironment.Testnet;
            ResetEditor();
            _message = value is null ? "New account profile." : value.Result?.Message ?? "Profile selected. Verify the connection manually.";
            Notify();
        }
    }
    /// <summary>The name of the account profile.</summary>
    public string Name { get => _name; set { _name = value; Notify(); } }
    public string Exchange { get => _exchange; set { if (IsNewProfile && Exchanges.Any(e => e.Id == value)) { _exchange = value; Notify(); } } }
    public TradingEnvironment Environment
    {
        get => _environment;
        set { if (IsNewProfile && Enum.IsDefined(value)) { _environment = value; Notify(); } }
    }
    public bool IsNewProfile => _selected is null;
    public bool IsBusy => _request is not null;
    public bool CanSave => !_disposed && _storageAvailable && !IsBusy && !string.IsNullOrWhiteSpace(Name)
        && (IsNewProfile ? _hasCompleteCredentials : !_hasCredentialInput || _hasCompleteCredentials);
    private bool CanModifySelected => !_disposed && _storageAvailable && !IsBusy && _selected is not null;
    public bool CanReplaceCredentials => !_disposed && !IsBusy && _selected is not null;
    public string Message => _message;
    public string CredentialStatus => _selected?.HasCredentials == true
        ? "Keys are stored in memory. Enter both values to replace them."
        : "Enter the API key and secret. You must enter them again after restarting.";
    public string EnvironmentText => Environment == TradingEnvironment.Testnet ? "Testnet — test network" : "MAINNET — live account";
    public string SelectedPermissions => _selected?.PermissionText ?? "Trading: unknown · Withdrawals: unknown";
    public string WithdrawalNotice => _selected?.Result?.Permissions.CanWithdraw == true
        ? "This key has withdrawal permission. Create a separate key without this permission for trading." : "";
    public RelayCommand NewCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ActivateCommand { get; }
    public RelayCommand VerifyCommand { get; }
    public RelayCommand CancelCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? EditorResetRequested;

    public void SetCredentialInputState(bool hasAny, bool complete)
    {
        _hasCredentialInput = hasAny;
        _hasCompleteCredentials = complete;
        Notify();
    }

    public void ReportInvalidCredentials()
    {
        _message = "API key and API secret must not be empty or contain only whitespace.";
        Notify();
    }

    ///<summary> Creates a new account profile and resets the editor.</summary>
    public void BeginNew()
    {
        if (_disposed) return;
        CancelVerification();
        _selected = null;
        _name = "";
        _exchange = "binance";
        _environment = TradingEnvironment.Testnet;
        _message = "New account profile.";
        ResetEditor();
        Notify();
    }

    /// <summary>The view supplies masked input only for this operation;
    /// it is never stored in the view model. Saves the profile to storage.</summary>
    public void Save(AccountCredentials? replacement)
    {
        if (!CanSave || IsNewProfile && replacement is null || _hasCredentialInput && replacement is null) return;

        var profile = _selected is { } selected ? selected.Profile with { Name = Name.Trim() }
            : new TradingAccountProfile(Guid.NewGuid(), Name.Trim(), Exchange, Environment);
        var profiles = Profiles.Select(p => p.Profile.Id == profile.Id ? profile : p.Profile).ToList();

        if (_selected is null) profiles.Add(profile);

        try
        {
            _store.Save(new(profiles, _active));
            if (replacement is not null) _credentials.Write(profile.Id, replacement);
            if (_selected is null)
            {
                var item = new TradingAccountItem(profile, _credentials.Contains(profile.Id));
                Profiles.Add(item);
                SelectedAccount = item;
            }
            else
            {
                _selected.Update(profile, _credentials.Contains(profile.Id), replacement is not null);
                ResetEditor();
            }
            _message = replacement is null ? "Profile saved." : "Profile saved. Keys are available only until the app closes.";
        }
        catch (Exception) { _message = "Could not save the profile. Check access to the settings folder."; }
        Notify();
    }

    public void DeleteSelected()
    {
        if (!CanModifySelected) return;
        var item = _selected!;
        var active = new Dictionary<string, Guid>(_active);
        if (active.GetValueOrDefault(item.Profile.Exchange) == item.Profile.Id) active.Remove(item.Profile.Exchange);
        try
        {
            _store.Save(new(Profiles.Where(p => p != item).Select(p => p.Profile).ToArray(), active));
            _credentials.Delete(item.Profile.Id);
            _active = active;
            Profiles.Remove(item);
            BeginNew();
            _message = "Profile and keys deleted.";
        }
        catch (Exception) { _message = "Could not delete the profile. Settings are unchanged."; }
        Notify();
    }

    /// <summary> Selects the current account for the exchange. </summary>
    public void ActivateSelected()
    {
        if (!CanModifySelected) return;
        var profile = _selected!.Profile;
        var active = new Dictionary<string, Guid>(_active) { [profile.Exchange] = profile.Id };
        try
        {
            _store.Save(new(Profiles.Select(p => p.Profile).ToArray(), active));
            _active = active;
            foreach (var item in Profiles) item.SetActive(active.GetValueOrDefault(item.Profile.Exchange) == item.Profile.Id);
            _message = "Profile selected for the exchange. Trading is not yet enabled.";
        }
        catch (Exception) { _message = "Could not save the selected account."; }
        Notify();
    }

    public async Task VerifySelectedAsync()
    {
        if (_disposed || _request is not null || _selected is null) return;
        
        var item = _selected;
        using var credentials = _credentials.Read(item.Profile.Id);

        if (credentials is null) {
            _message = "Keys are missing from memory. Enter and save them again."; Notify(); return;
        }

        using var request = new CancellationTokenSource();
        var version = ++_requestVersion;
        _request = request;
        _checkingItem = item;
        item.SetChecking(true);
        _message = "Verifying account access…";
        Notify();
        try
        {
            var result = await _verifier.VerifyAsync(item.Profile, credentials, request.Token);
            if (!_disposed && version == _requestVersion && !request.IsCancellationRequested && ReferenceEquals(item, _selected))
            {
                item.SetResult(result);
                _message = result.Message;
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed && version == _requestVersion)
            {
                var result = new AccountVerificationResult(false, new(), "Could not verify the account connection.");
                item.SetResult(result);
                _message = result.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_request, request))
            {
                item.SetChecking(false);
                _request = null;
                _checkingItem = null;
            }
            if (!_disposed) Notify();
        }
    }

    public void CancelVerification()
    {
        ++_requestVersion;
        var request = _request;
        _request = null;
        _checkingItem?.SetChecking(false);
        _checkingItem = null;
        request?.Cancel();
        if (request is not null) _message = "Verification canceled.";
        if (!_disposed) Notify();
    }

    private void ResetEditor()
    {
        _hasCredentialInput = false;
        _hasCompleteCredentials = false;
        EditorResetRequested?.Invoke(); // Clears the API key and secret input fields.
    }

    private void Notify()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        DeleteCommand.RaiseCanExecuteChanged(); ActivateCommand.RaiseCanExecuteChanged();
        VerifyCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelVerification();
        ResetEditor();
    }
}
