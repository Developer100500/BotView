using System.Collections;
using System.Globalization;
using BotView.Interfaces;
using BotView.Models;

namespace BotView.Services;

public sealed class AccountConnectionVerifier : IAccountConnectionVerifier
{
    private readonly Func<TradingAccountProfile, AccountCredentials, IAccountVerificationClient> _create;
    private readonly TimeSpan _timeout;

    public AccountConnectionVerifier() : this((p, c) => new CcxtAccountVerificationClient(p, c)) { }
    internal AccountConnectionVerifier(Func<TradingAccountProfile, AccountCredentials, IAccountVerificationClient> create,
        TimeSpan? timeout = null) { _create = create; _timeout = timeout ?? TimeSpan.FromSeconds(15); }

    public async Task<AccountVerificationResult> VerifyAsync(TradingAccountProfile profile, AccountCredentials credentials,
        CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        using var copy = credentials.Copy();
        return await Task.Run(async () =>
        {
            IAccountVerificationClient? client = null;
            try
            {
                timeout.Token.ThrowIfCancellationRequested();
                client = _create(profile, copy);
                if (!await WaitAsync(client.VerifyAccessAsync(timeout.Token), timeout.Token).ConfigureAwait(false))
                    return Failure("Не удалось подтвердить доступ к счету.");
                var permissions = new AccountPermissions();
                try { permissions = await WaitAsync(client.GetPermissionsAsync(timeout.Token), timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { /* Authentication succeeded; unavailable permission endpoints are not proof of rights. */ }
                timeout.Token.ThrowIfCancellationRequested();
                return new AccountVerificationResult(true, permissions,
                    permissions.CanTrade is null ? "Доступ подтвержден; права торговли не проверены."
                        : permissions.CanTrade.Value ? "Доступ к счету подтвержден." : "Доступ подтвержден; торговля запрещена для этого ключа.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { return Failure("Истекло время проверки подключения (15 секунд)."); }
            catch (Exception ex) { return Failure(SafeError(ex)); }
            finally
            {
                if (client is not null)
                {
                    try { await client.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception) { /* Never log exchange exceptions: they can contain signed URLs or keys. */ }
                }
            }
        }).ConfigureAwait(false);
    }

    private static AccountVerificationResult Failure(string message) => new(false, new(), message);

    internal static string SafeError(Exception ex)
    {
        ex = ex.GetBaseException();
        return ex switch
        {
            ccxt.PermissionDenied => "Недостаточно разрешений API или доступ ограничен по IP.",
            ccxt.InvalidNonce => "Проверьте дату и время компьютера.",
            ccxt.AuthenticationError => "Не удалось авторизоваться. Проверьте ключи, ограничения IP и выбранную сеть.",
            ccxt.RateLimitExceeded => "Биржа ограничила частоту запросов. Повторите проверку позже.",
            ccxt.RequestTimeout => "Истекло время ожидания ответа биржи.",
            ccxt.NetworkError => "Биржа недоступна. Проверьте сеть и повторите проверку.",
            NotSupportedException => "Биржа или среда не поддерживается для подключения счета.",
            _ => "Не удалось проверить подключение к счету. Проверьте настройки и повторите попытку."
        };
    }

    private static async Task<T> WaitAsync<T>(Task<T> task, CancellationToken ct)
    {
        try { return await task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
}

internal interface IAccountVerificationClient : IAsyncDisposable
{
    Task<bool> VerifyAccessAsync(CancellationToken ct);
    Task<AccountPermissions> GetPermissionsAsync(CancellationToken ct);
}

internal sealed class CcxtAccountVerificationClient : IAccountVerificationClient
{
    private readonly TradingAccountProfile _profile;
    private readonly ccxt.Exchange _client;
    private IDictionary<string, object>? _bybitKeyInfo;

    public CcxtAccountVerificationClient(TradingAccountProfile profile, AccountCredentials credentials)
    {
        _profile = profile;
        _client = CreateClient(profile, credentials);
    }

    internal static ccxt.Exchange CreateClient(TradingAccountProfile profile, AccountCredentials credentials)
    {
        if (!Enum.IsDefined(profile.Environment)) throw new NotSupportedException();
        var settings = new Dictionary<string, object>
        {
            ["apiKey"] = credentials.ApiKey, ["secret"] = credentials.Secret,
            ["enableRateLimit"] = true, ["timeout"] = 15000, ["verbose"] = false,
            ["options"] = new Dictionary<string, object> { ["defaultType"] = "spot" }
        };
        ccxt.Exchange client = profile.Exchange switch
        {
            "binance" => new ccxt.binance(settings),
            "bybit" => new ccxt.bybit(settings),
            _ => throw new NotSupportedException()
        };
        // No market loading or other request is permitted before this switch.
        if (profile.Environment == TradingEnvironment.Testnet) client.setSandboxMode(true);
        return client;
    }

    public async Task<bool> VerifyAccessAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_client is ccxt.binance binance)
        {
            await binance.privateGetAccount().ConfigureAwait(false);
        }
        else if (_client is ccxt.bybit bybit)
        {
            var response = await bybit.privateGetV5UserQueryApi().ConfigureAwait(false);
            _bybitKeyInfo = Nested(response, "result");
            ct.ThrowIfCancellationRequested();
            var accountType = _bybitKeyInfo is not null && Number(Value(_bybitKeyInfo, "uta")) == 0 ? "SPOT" : "UNIFIED";
            await bybit.privateGetV5AccountWalletBalance(new Dictionary<string, object> { ["accountType"] = accountType }).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        return true;
    }

    public async Task<AccountPermissions> GetPermissionsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_client is ccxt.binance binance)
        {
            // Spot Testnet has no /sapi endpoints. Do not query production as a fallback.
            if (_profile.Environment == TradingEnvironment.Testnet) return new();
            return ParseBinancePermissions(await binance.sapiGetAccountApiRestrictions().ConfigureAwait(false));
        }
        return ParseBybitPermissions(_bybitKeyInfo);
    }

    internal static AccountPermissions ParseBinancePermissions(IDictionary<string, object> data) =>
        new(Boolean(Value(data, "enableSpotAndMarginTrading")), Boolean(Value(data, "enableWithdrawals")));

    internal static AccountPermissions ParseBybitPermissions(IDictionary<string, object>? data)
    {
        if (data is null) return new();
        var permissions = Nested(data, "permissions");
        var readOnly = Number(Value(data, "readOnly"));
        var spot = Contains(permissions is null ? null : Value(permissions, "Spot"), "SpotTrade");
        var withdraw = Contains(permissions is null ? null : Value(permissions, "Wallet"), "Withdraw");
        return new(readOnly == 1 ? false : readOnly == 0 ? spot : null, readOnly == 1 ? false : withdraw);
    }

    private static IDictionary<string, object>? Nested(IDictionary<string, object> data, string key) =>
        Value(data, key) as IDictionary<string, object>;
    private static object? Value(IDictionary<string, object> data, string key) => data.TryGetValue(key, out var value) ? value : null;
    private static bool? Boolean(object? value) => value is bool b ? b : bool.TryParse(value?.ToString(), out var parsed) ? parsed : null;
    private static int? Number(object? value) => int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed) ? parsed : null;
    private static bool? Contains(object? value, string permission) => value is IEnumerable items && value is not string
        ? items.Cast<object>().Any(p => p?.ToString() == permission) : null;

    public async ValueTask DisposeAsync()
    {
        try { await _client.Close().ConfigureAwait(false); }
        finally { _client.apiKey = ""; _client.secret = ""; _bybitKeyInfo?.Clear(); }
    }
}
