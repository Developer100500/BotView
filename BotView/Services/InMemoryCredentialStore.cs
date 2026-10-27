using BotView.Interfaces;
using BotView.Models;

namespace BotView.Services;

/// <summary>Intentionally has no persistence: keys disappear when the process exits.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore, ICredentialNotifications
{
    public event Action<Guid>? CredentialsChanged;
    private readonly Dictionary<Guid, AccountCredentials> _credentials = new();
    private readonly object _sync = new();
    private bool _disposed;

    public bool Contains(Guid profileId)
    {
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); return _credentials.ContainsKey(profileId); }
    }

    public AccountCredentials? Read(Guid profileId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _credentials.TryGetValue(profileId, out var value) ? value.Copy() : null;
        }
    }

    /// <summary>Replaces RAM-only keys and immediately invalidates any session using the old material.</summary>
    public void Write(Guid profileId, AccountCredentials credentials)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var copy = credentials.Copy();
            if (_credentials.Remove(profileId, out var old)) old.Dispose();
            _credentials.Add(profileId, copy);
        }
        CredentialsChanged?.Invoke(profileId);
    }

    /// <summary>Erases the stored arrays and notifies application-owned sessions.</summary>
    public void Delete(Guid profileId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_credentials.Remove(profileId, out var old)) old.Dispose();
        }
        CredentialsChanged?.Invoke(profileId);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            foreach (var credentials in _credentials.Values) credentials.Dispose();
            _credentials.Clear();
            _disposed = true;
        }
    }
}
