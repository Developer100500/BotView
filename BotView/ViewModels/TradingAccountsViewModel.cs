using System.Collections.ObjectModel;
using System.ComponentModel;
using BotView.Interfaces;
using BotView.Models;

namespace BotView.ViewModels;

public sealed record TradingEnvironmentOption(TradingEnvironment Environment, string Label);

public sealed class TradingAccountItem : INotifyPropertyChanged
{
    public TradingAccountItem(TradingAccountProfile profile, bool hasCredentials) { Profile = profile; HasCredentials = hasCredentials; }
    public TradingAccountProfile Profile { get; private set; }
    public bool HasCredentials { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsChecking { get; private set; }
    public AccountVerificationResult? Result { get; private set; }
    public string DisplayName => Profile.DisplayName;
    public string EnvironmentText => Profile.Environment == TradingEnvironment.Testnet ? "Testnet" : "Основная сеть";
    public string Status => IsChecking ? "Проверка…" : !HasCredentials ? "Нужны ключи"
        : Result is null ? "Не проверено" : Result.Connected ? "Подключено" : "Ошибка";
    public string ActiveText => IsActive ? "Выбран для биржи" : "";
    public string PermissionText => $"Торговля: {Permission(Result?.Permissions.CanTrade)} · Вывод: {Permission(Result?.Permissions.CanWithdraw)}";
    public event PropertyChangedEventHandler? PropertyChanged;
    private static string Permission(bool? value) => value switch { true => "разрешено", false => "запрещено", _ => "неизвестно" };
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
    private string _message = "Добавьте профиль счета или выберите существующий.";
    private bool _hasCredentialInput;
    private bool _hasCompleteCredentials;

    public TradingAccountsViewModel(ITradingAccountStore store, ICredentialStore credentials, IAccountConnectionVerifier verifier)
    {
        _store = store;
        _credentials = credentials;
        _verifier = verifier;
        NewCommand = new RelayCommand(BeginNew);
        DeleteCommand = new RelayCommand(_ => DeleteSelected(), _ => CanModifySelected);
        ActivateCommand = new RelayCommand(_ => ActivateSelected(), _ => CanModifySelected);
        VerifyCommand = new RelayCommand(_ => VerifySelectedAsync(), _ => !_disposed && _request is null && _selected?.HasCredentials == true);
        CancelCommand = new RelayCommand(_ => CancelVerification(), _ => _request is not null);
        try
        {
            var settings = _store.Load();
            _active = new(settings.ActiveProfiles);
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
            _message = "Не удалось прочитать профили счетов. Сохранение отключено, чтобы не перезаписать настройки.";
        }
    }

    public ObservableCollection<TradingAccountItem> Profiles { get; } = new();
    public ExchangeOption[] Exchanges { get; } = [new("binance", "Binance"), new("bybit", "Bybit")];
    public TradingEnvironmentOption[] Environments { get; } =
        [new(TradingEnvironment.Testnet, "Testnet"), new(TradingEnvironment.Production, "Основная сеть")];
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
            _message = value is null ? "Новый профиль счета." : value.Result?.Message ?? "Профиль выбран. Проверка подключения выполняется вручную.";
            Notify();
        }
    }
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
        ? "Ключи сохранены в ОЗУ. Для замены введите оба значения."
        : "Введите API key и secret. После перезапуска их нужно ввести заново.";
    public string EnvironmentText => Environment == TradingEnvironment.Testnet ? "Testnet — тестовая сеть" : "ОСНОВНАЯ СЕТЬ — реальный счет";
    public string SelectedPermissions => _selected?.PermissionText ?? "Торговля: неизвестно · Вывод: неизвестно";
    public string WithdrawalNotice => _selected?.Result?.Permissions.CanWithdraw == true
        ? "У ключа есть право вывода. Для торговли создайте отдельный ключ без этого разрешения." : "";
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
        _message = "API key и API secret не должны быть пустыми или состоять только из пробелов.";
        Notify();
    }

    public void BeginNew()
    {
        if (_disposed) return;
        CancelVerification();
        _selected = null;
        _name = "";
        _exchange = "binance";
        _environment = TradingEnvironment.Testnet;
        _message = "Новый профиль счета.";
        ResetEditor();
        Notify();
    }

    /// <summary>The view supplies masked input only for this operation; it is never stored in the view model.</summary>
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
            _message = replacement is null ? "Профиль сохранен." : "Профиль сохранен. Ключи доступны только до закрытия приложения.";
        }
        catch (Exception) { _message = "Не удалось сохранить профиль. Проверьте доступ к папке настроек."; }
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
            _message = "Профиль и ключи удалены.";
        }
        catch (Exception) { _message = "Не удалось удалить профиль. Настройки не изменены."; }
        Notify();
    }

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
            _message = "Профиль выбран для биржи. Торговля пока не подключена.";
        }
        catch (Exception) { _message = "Не удалось сохранить выбор счета."; }
        Notify();
    }

    public async Task VerifySelectedAsync()
    {
        if (_disposed || _request is not null || _selected is null) return;
        var item = _selected;
        using var credentials = _credentials.Read(item.Profile.Id);
        if (credentials is null) { _message = "Ключи отсутствуют в ОЗУ. Введите и сохраните их заново."; Notify(); return; }
        using var request = new CancellationTokenSource();
        var version = ++_requestVersion;
        _request = request;
        _checkingItem = item;
        item.SetChecking(true);
        _message = "Проверка доступа к счету…";
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
                var result = new AccountVerificationResult(false, new(), "Не удалось проверить подключение к счету.");
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
        if (request is not null) _message = "Проверка отменена.";
        if (!_disposed) Notify();
    }

    private void ResetEditor()
    {
        _hasCredentialInput = false;
        _hasCompleteCredentials = false;
        EditorResetRequested?.Invoke();
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
