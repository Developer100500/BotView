using BotView.Models;

namespace BotView.Interfaces;

public interface ITradingAccountStore
{
    TradingAccountsSettings Load();
    void Save(TradingAccountsSettings settings);
}

public interface ICredentialStore : IDisposable
{
    bool Contains(Guid profileId);
    /// <summary>Returns a disposable copy; ownership of the original stays with the store.</summary>
    AccountCredentials? Read(Guid profileId);
    void Write(Guid profileId, AccountCredentials credentials);
    void Delete(Guid profileId);
}

public interface IAccountConnectionVerifier
{
    Task<AccountVerificationResult> VerifyAsync(TradingAccountProfile profile, AccountCredentials credentials,
        CancellationToken ct = default);
}
