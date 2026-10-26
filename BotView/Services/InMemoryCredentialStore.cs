using BotView.Interfaces;
using BotView.Models;

namespace BotView.Services;

/// <summary>Intentionally has no persistence: keys disappear when the process exits.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
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

    public void Write(Guid profileId, AccountCredentials credentials)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var copy = credentials.Copy();
            if (_credentials.Remove(profileId, out var old)) old.Dispose();
            _credentials.Add(profileId, copy);
        }
    }

    public void Delete(Guid profileId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_credentials.Remove(profileId, out var old)) old.Dispose();
        }
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
