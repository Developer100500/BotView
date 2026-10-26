using System.Diagnostics;
using System.Text.Json.Serialization;

namespace BotView.Models;

public enum TradingEnvironment { Testnet, Production }

public sealed record TradingAccountProfile(Guid Id, string Name, string Exchange, TradingEnvironment Environment)
{
    public string DisplayName => $"{Name} · {Exchange} · {(Environment == TradingEnvironment.Testnet ? "Testnet" : "Основная сеть")}";
}

public sealed record TradingAccountsSettings(
    IReadOnlyList<TradingAccountProfile> Profiles, IReadOnlyDictionary<string, Guid> ActiveProfiles);

/// <summary>Session-only key material. Never serialize credentials or use records with generated ToString.</summary>
[DebuggerDisplay("Account credentials (redacted)")]
public sealed class AccountCredentials : IDisposable
{
    private readonly char[] _apiKey;
    private readonly char[] _secret;
    private bool _disposed;

    public AccountCredentials(string apiKey, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        _apiKey = apiKey.ToCharArray();
        _secret = secret.ToCharArray();
    }

    [JsonIgnore] public string ApiKey { get { ObjectDisposedException.ThrowIf(_disposed, this); return new(_apiKey); } }
    [JsonIgnore] public string Secret { get { ObjectDisposedException.ThrowIf(_disposed, this); return new(_secret); } }
    public AccountCredentials Copy() => new(ApiKey, Secret);
    public override string ToString() => "Account credentials (redacted)";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Array.Clear(_apiKey);
        Array.Clear(_secret);
    }
}

public sealed record AccountPermissions(bool? CanTrade = null, bool? CanWithdraw = null);
public sealed record AccountVerificationResult(bool Connected, AccountPermissions Permissions, string Message);
