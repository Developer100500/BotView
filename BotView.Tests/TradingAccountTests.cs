using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BotView.Interfaces;
using BotView.Models;
using BotView.Services;
using BotView.ViewModels;
using BotView.Views;

namespace BotView.Tests;

public sealed class TradingAccountTests
{
    private static TradingAccountProfile Profile(string exchange = "binance", TradingEnvironment environment = TradingEnvironment.Testnet) =>
        new(Guid.NewGuid(), "Test account", exchange, environment);
    private static AccountCredentials Keys(string suffix = "") => new("test-api-key" + suffix, "test-secret" + suffix);
    private static readonly AccountVerificationResult Success = new(true, new(true, false), "Доступ подтвержден.");

    [Fact]
    public void MemoryCredentialsAreCopiedReplacedRemovedAndNotSerialized()
    {
        var profile = Profile();
        using var store = new InMemoryCredentialStore();
        using (var original = Keys()) store.Write(profile.Id, original);
        using var firstCopy = store.Read(profile.Id)!;
        Assert.Equal("test-api-key", firstCopy.ApiKey);
        Assert.DoesNotContain("test-api-key", JsonSerializer.Serialize(firstCopy));
        Assert.DoesNotContain("test-secret", firstCopy.ToString());
        using (var replacement = Keys("-new")) store.Write(profile.Id, replacement);
        using (var secondCopy = store.Read(profile.Id)!) Assert.Equal("test-api-key-new", secondCopy.ApiKey);
        Assert.Equal("test-api-key", firstCopy.ApiKey);
        store.Delete(profile.Id);
        Assert.Null(store.Read(profile.Id));
        Assert.False(store.Contains(profile.Id));
        firstCopy.Dispose();
        Assert.Throws<ObjectDisposedException>(() => firstCopy.Secret);
        store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.Contains(profile.Id));
    }

    [Fact]
    public void JsonStoresOnlyMetadataAndRestoresSelectionWithoutCredentials()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "accounts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "profiles.json");
            var store = new TradingAccountStore(path);
            using var memory = new InMemoryCredentialStore();
            var profile = Profile();
            using (var keys = Keys()) memory.Write(profile.Id, keys);
            store.Save(new([profile], new Dictionary<string, Guid> { [profile.Exchange] = profile.Id }));
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("test-api-key", json);
            Assert.DoesNotContain("test-secret", json);
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(new[] { "Id", "Name", "Exchange", "Environment" },
                parsed.RootElement.GetProperty("Profiles")[0].EnumerateObject().Select(p => p.Name));
            var settings = new TradingAccountStore(path).Load();
            Assert.Equal(profile, Assert.Single(settings.Profiles));
            Assert.Equal(profile.Id, settings.ActiveProfiles["binance"]);
            using var nextProcessMemory = new InMemoryCredentialStore();
            using var vm = new TradingAccountsViewModel(new TradingAccountStore(path), nextProcessMemory, new FakeVerifier());
            Assert.True(Assert.Single(vm.Profiles).IsActive);
            Assert.Equal("Нужны ключи", vm.SelectedAccount!.Status);
            Assert.False(vm.VerifyCommand.CanExecute(null));
            File.WriteAllText(path, "{invalid json");
            using var invalidVm = new TradingAccountsViewModel(store, memory, new FakeVerifier());
            invalidVm.Name = "Do not overwrite";
            invalidVm.SetCredentialInputState(true, true);
            Assert.False(invalidVm.CanSave);
            Assert.Contains("Сохранение отключено", invalidVm.Message);
            Assert.Equal("{invalid json", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ProfileCrudKeepsExchangeAndEnvironmentImmutableAndRetainsKeysAcrossWindowReopens()
    {
        var store = new FakeStore();
        using var memory = new InMemoryCredentialStore();
        using var vm = new TradingAccountsViewModel(store, memory, new FakeVerifier());
        Assert.Equal(TradingEnvironment.Testnet, vm.Environment);
        vm.Name = "First";
        vm.SetCredentialInputState(true, true);
        using (var keys = Keys()) vm.Save(keys);
        var first = vm.SelectedAccount!;
        Assert.True(first.HasCredentials);
        vm.ActivateSelected();
        vm.Name = "Renamed";
        vm.Exchange = "bybit";
        vm.Environment = TradingEnvironment.Production;
        vm.Save(null);
        Assert.Equal("Renamed", first.Profile.Name);
        Assert.Equal("binance", first.Profile.Exchange);
        Assert.Equal(TradingEnvironment.Testnet, first.Profile.Environment);
        using (var reopened = new TradingAccountsViewModel(store, memory, new FakeVerifier()))
        {
            Assert.True(reopened.SelectedAccount!.HasCredentials);
            Assert.True(reopened.SelectedAccount.IsActive);
            Assert.Null(reopened.SelectedAccount.Result);
        }
        vm.BeginNew();
        vm.Name = "Other";
        vm.Exchange = "bybit";
        vm.Environment = TradingEnvironment.Production;
        vm.SetCredentialInputState(true, true);
        using (var keys = Keys("-other")) vm.Save(keys);
        vm.ActivateSelected();
        Assert.True(first.IsActive);
        Assert.True(vm.SelectedAccount!.IsActive);
        var secondId = vm.SelectedAccount.Profile.Id;
        vm.DeleteSelected();
        Assert.False(memory.Contains(secondId));
        Assert.Single(vm.Profiles);
        Assert.False(store.Settings.ActiveProfiles.ContainsKey("bybit"));
    }

    [Fact]
    public void FailedMetadataSaveLeavesOriginalKeysAndProfileIntact()
    {
        var profile = Profile();
        var store = new FakeStore { Settings = new([profile], new Dictionary<string, Guid>()) };
        using var memory = new InMemoryCredentialStore();
        using (var keys = Keys()) memory.Write(profile.Id, keys);
        using var vm = new TradingAccountsViewModel(store, memory, new FakeVerifier());
        store.FailSave = true;
        vm.Name = "Changed";
        vm.SetCredentialInputState(true, true);
        using (var keys = Keys("-replacement")) vm.Save(keys);
        Assert.Equal(profile.Name, vm.SelectedAccount!.Profile.Name);
        using var stillOriginal = memory.Read(profile.Id)!;
        Assert.Equal("test-api-key", stillOriginal.ApiKey);
        Assert.Contains("Не удалось сохранить", vm.Message);
    }

    [Fact]
    public async Task SwitchingProfileOrClosingCancelsCheckAndRejectsLateResult()
    {
        var profile = Profile();
        var store = new FakeStore { Settings = new([profile], new Dictionary<string, Guid>()) };
        using var memory = new InMemoryCredentialStore();
        using (var keys = Keys()) memory.Write(profile.Id, keys);
        var pending = new TaskCompletionSource<AccountVerificationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var verifier = new FakeVerifier { Verify = (_, _, ct) => { observed = ct; return pending.Task; } };
        var vm = new TradingAccountsViewModel(store, memory, verifier);
        var original = vm.SelectedAccount!;
        var check = vm.VerifySelectedAsync();
        Assert.True(vm.IsBusy);
        vm.BeginNew();
        Assert.True(observed.IsCancellationRequested);
        pending.SetResult(Success);
        await check;
        Assert.Null(original.Result);
        Assert.False(vm.IsBusy);
        vm.SelectedAccount = original;
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        check = vm.VerifySelectedAsync();
        vm.Dispose();
        Assert.True(observed.IsCancellationRequested);
        pending.SetResult(Success);
        await check;
        Assert.Null(original.Result);
        Assert.True(memory.Contains(profile.Id));
    }

    [Fact]
    public async Task SuccessfulReadonlyCheckDoesNotClaimTradingPermission()
    {
        var profile = Profile();
        var store = new FakeStore { Settings = new([profile], new Dictionary<string, Guid>()) };
        using var memory = new InMemoryCredentialStore();
        using (var keys = Keys()) memory.Write(profile.Id, keys);
        var verifier = new FakeVerifier { Verify = (_, _, _) => Task.FromResult(new AccountVerificationResult(true, new(false, false), "Read access")) };
        using var vm = new TradingAccountsViewModel(store, memory, verifier);
        await vm.VerifySelectedAsync();
        Assert.Equal("Подключено", vm.SelectedAccount!.Status);
        Assert.Contains("Торговля: запрещено", vm.SelectedPermissions);
        vm.SetCredentialInputState(true, true);
        using (var keys = Keys("-replacement")) vm.Save(keys);
        Assert.Null(vm.SelectedAccount.Result);
        Assert.Contains("Торговля: неизвестно", vm.SelectedPermissions);
    }

    [Fact]
    public async Task UnexpectedVerifierExceptionIsSanitizedByTheViewModel()
    {
        var profile = Profile();
        var store = new FakeStore { Settings = new([profile], new Dictionary<string, Guid>()) };
        using var memory = new InMemoryCredentialStore();
        using (var keys = Keys()) memory.Write(profile.Id, keys);
        var verifier = new FakeVerifier { Verify = (_, _, _) => throw new InvalidOperationException("test-secret test-api-key") };
        using var vm = new TradingAccountsViewModel(store, memory, verifier);
        await vm.VerifySelectedAsync();
        Assert.Equal("Ошибка", vm.SelectedAccount!.Status);
        Assert.DoesNotContain("test-secret", vm.Message);
        Assert.DoesNotContain("test-api-key", vm.Message);
    }

    [Fact]
    public async Task ConnectionVerifierUsesBackgroundThreadAndOnlyReportsVerifiedPermissions()
    {
        var client = new FakeClient();
        var verifier = new AccountConnectionVerifier((_, _) => client);
        using var keys = Keys();
        var result = await verifier.VerifyAsync(Profile(), keys, TestContext.Current.CancellationToken);
        Assert.True(result.Connected);
        Assert.True(result.Permissions.CanTrade);
        Assert.False(result.Permissions.CanWithdraw);
        Assert.True(client.RanInBackground);
        Assert.True(client.Disposed);
        client = new FakeClient { PermissionError = new ccxt.PermissionDenied("test-api-key test-secret signed-url") };
        result = await verifier.VerifyAsync(Profile(), keys, TestContext.Current.CancellationToken);
        Assert.True(result.Connected);
        Assert.Null(result.Permissions.CanTrade);
        Assert.Contains("не проверены", result.Message);
        Assert.DoesNotContain("test-secret", result.Message);
    }

    [Fact]
    public async Task TimeoutAndCancellationDisposeClientWithoutExposingExchangeResponses()
    {
        var client = new FakeClient { Access = ct => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => true, CancellationToken.None) };
        var verifier = new AccountConnectionVerifier((_, _) => client, TimeSpan.FromMilliseconds(40));
        using var keys = Keys();
        var result = await verifier.VerifyAsync(Profile(), keys, TestContext.Current.CancellationToken);
        Assert.False(result.Connected);
        Assert.True(client.Disposed);
        Assert.Contains("время", result.Message);
        client = new FakeClient { Access = async ct => { await Task.Delay(Timeout.Infinite, ct); return true; } };
        using var cancel = new CancellationTokenSource();
        var task = verifier.VerifyAsync(Profile(), keys, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        var message = AccountConnectionVerifier.SafeError(new AggregateException(new ccxt.AuthenticationError("test-api-key test-secret")));
        Assert.DoesNotContain("test-api-key", message);
        Assert.DoesNotContain("test-secret", message);
        Assert.Contains("авторизоваться", message);
    }

    [Theory]
    [InlineData("binance", "testnet.binance.vision", "api.binance.com")]
    [InlineData("bybit", "api-testnet.bybit.com", "api.bybit.com")]
    public async Task TestnetClientRoutesPrivateRequestsToTestnetBeforeAnyNetworkCall(string exchange, string testHost, string productionHost)
    {
        using var keys = Keys();
        var test = CcxtAccountVerificationClient.CreateClient(Profile(exchange), keys);
        var production = CcxtAccountVerificationClient.CreateClient(Profile(exchange, TradingEnvironment.Production), keys);
        try
        {
            var testApi = (Dictionary<string, object>)((Dictionary<string, object>)test.urls)["api"];
            var productionApi = (Dictionary<string, object>)((Dictionary<string, object>)production.urls)["api"];
            var testUrl = test.implodeHostname(testApi["private"]);
            var productionUrl = production.implodeHostname(productionApi["private"]);
            Assert.Contains(testHost, testUrl);
            Assert.DoesNotContain(productionHost, testUrl);
            Assert.Contains(productionHost, productionUrl);
            Assert.False(test.verbose);
        }
        finally { await test.Close(); await production.Close(); }
    }

    [Fact]
    public void PermissionsDistinguishReadonlyMissingAndWithdrawCapabilities()
    {
        var bybit = new Dictionary<string, object>
        {
            ["readOnly"] = 0,
            ["permissions"] = new Dictionary<string, object>
            {
                ["Spot"] = new List<object> { "SpotTrade" }, ["Wallet"] = new List<object> { "Withdraw" }
            }
        };
        Assert.Equal(new AccountPermissions(true, true), CcxtAccountVerificationClient.ParseBybitPermissions(bybit));
        bybit["readOnly"] = 1;
        Assert.Equal(new AccountPermissions(false, false), CcxtAccountVerificationClient.ParseBybitPermissions(bybit));
        Assert.Equal(new AccountPermissions(), CcxtAccountVerificationClient.ParseBybitPermissions(new Dictionary<string, object>()));
        Assert.Equal(new AccountPermissions(false, false), CcxtAccountVerificationClient.ParseBinancePermissions(
            new Dictionary<string, object> { ["enableSpotAndMarginTrading"] = false, ["enableWithdrawals"] = false }));
        Assert.Equal(new AccountPermissions(), CcxtAccountVerificationClient.ParseBinancePermissions(new Dictionary<string, object>()));
    }

    [Fact]
    public void AccountWindowMasksAndClearsKeysAndRendersAtMinimumSize()
    {
        OnSta(() =>
        {
            var store = new FakeStore();
            using var credentials = new InMemoryCredentialStore();
            var window = new TradingAccountsWindow(store, credentials, new FakeVerifier());
            var vm = (TradingAccountsViewModel)window.DataContext;
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(720, 500));
            content.Arrange(new Rect(0, 0, 720, 500));
            content.UpdateLayout();
            vm.Name = "Testnet account";
            var key = (PasswordBox)window.FindName("ApiKeyInput");
            var secret = (PasswordBox)window.FindName("SecretInput");
            key.Password = " ";
            secret.Password = " ";
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(vm.Profiles);
            Assert.Contains("пробелов", vm.Message);
            secret.Clear();
            key.Password = "test-api-key";
            Assert.False(vm.CanSave);
            secret.Password = "test-secret";
            Assert.True(vm.CanSave);
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(vm.Profiles);
            Assert.Equal("", key.Password);
            Assert.Equal("", secret.Password);
            key.Password = "unsaved-replacement";
            ((Button)window.FindName("ReplaceKeysButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("", key.Password);
            using (var stored = credentials.Read(vm.Profiles[0].Profile.Id)!) Assert.Equal("test-api-key", stored.ApiKey);
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(720, 500, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            Assert.True(((ListBox)window.FindName("AccountList")).ActualHeight > 250);
            if (Environment.GetEnvironmentVariable("BOTVIEW_ACCOUNTS_PREVIEW") is { Length: > 0 } path)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(path);
                encoder.Save(output);
            }
            window.Close();
            Assert.True(credentials.Contains(vm.Profiles[0].Profile.Id));
        });
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class FakeStore : ITradingAccountStore
    {
        public TradingAccountsSettings Settings = new([], new Dictionary<string, Guid>());
        public bool FailSave;
        public TradingAccountsSettings Load() => Settings;
        public void Save(TradingAccountsSettings settings)
        {
            if (FailSave) throw new IOException("Test failure");
            Settings = new(settings.Profiles.ToArray(), new Dictionary<string, Guid>(settings.ActiveProfiles));
        }
    }

    private sealed class FakeVerifier : IAccountConnectionVerifier
    {
        public Func<TradingAccountProfile, AccountCredentials, CancellationToken, Task<AccountVerificationResult>> Verify = (_, _, _) => Task.FromResult(Success);
        public Task<AccountVerificationResult> VerifyAsync(TradingAccountProfile profile, AccountCredentials credentials, CancellationToken ct = default) => Verify(profile, credentials, ct);
    }

    private sealed class FakeClient : IAccountVerificationClient
    {
        public Func<CancellationToken, Task<bool>> Access = _ => Task.FromResult(true);
        public Exception? PermissionError;
        public bool RanInBackground;
        public bool Disposed;
        public Task<bool> VerifyAccessAsync(CancellationToken ct) { RanInBackground = Thread.CurrentThread.IsThreadPoolThread; return Access(ct); }
        public Task<AccountPermissions> GetPermissionsAsync(CancellationToken ct) => PermissionError is null
            ? Task.FromResult(new AccountPermissions(true, false)) : throw PermissionError;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
