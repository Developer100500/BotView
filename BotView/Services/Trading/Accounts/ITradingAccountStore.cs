using BotView.Models;

namespace BotView.Services.Trading.Accounts;

/// <summary>Optional change notification implemented by the application stores.</summary>
public interface ITradingProfileNotifications { event Action? Changed; }

public interface ITradingAccountStore
{
    TradingAccountsSettings Load();
    void Save(TradingAccountsSettings settings);
}

/// <summary>Checks that a trading account's credentials can reach the exchange
/// and reports whether the account has access and trading permissions.</summary>
public interface IAccountConnectionVerifier
{
    Task<AccountVerificationResult> VerifyAsync(TradingAccountProfile profile, AccountCredentials credentials,
        CancellationToken ct = default);
}
