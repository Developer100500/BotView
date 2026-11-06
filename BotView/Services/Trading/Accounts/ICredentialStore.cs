using BotView.Models;

namespace BotView.Services.Trading.Accounts;

/// <summary>Notifies application-owned sessions when a provider replaces or removes profile keys.</summary>
public interface ICredentialNotifications
{
    event Action<Guid>? CredentialsChanged;
}

/// <summary>Stores exchange keys separately from profile metadata, independently of the storage provider.</summary>
public interface ICredentialStore : IDisposable
{
    /// <summary>Checks whether keys exist for the profile.</summary>
    bool Contains(Guid profileId);

    /// <summary>Returns an owned, disposable copy, or null when no keys exist.</summary>
    AccountCredentials? Read(Guid profileId);

    /// <summary>Replaces both keys without taking ownership of the supplied credentials.</summary>
    void Write(Guid profileId, AccountCredentials credentials);

    /// <summary>Removes keys; a missing entry is already considered deleted.</summary>
    void Delete(Guid profileId);
}
