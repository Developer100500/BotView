using BotView.Interfaces;
using BotView.Models;

namespace BotView.Services;

/// <summary>Application-owned sessions survive window closure. Only explicit account changes retire them.</summary>
public sealed class TradingSessionManager : IAsyncDisposable
{
    private readonly ITradingAccountStore _profiles;
    private readonly ICredentialStore _credentials;
    private readonly Func<TradingAccountProfile, AccountCredentials, ISpotTradingClient> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, CcxtTradingService> _sessions = new();
    private readonly HashSet<Guid> _invalidated = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lazy<TradingJournal> _journal;
    private int _revision;
    private bool _disposed;
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>Builds the application manager without reading credentials or connecting until requested.</summary>
    public TradingSessionManager(ITradingAccountStore profiles, ICredentialStore credentials)
        : this(profiles, credentials, (p, c) => new CcxtSpotTradingClient(p, c), new(() => new TradingJournal())) { }

    /// <summary>Injects a deterministic exchange boundary and journal for tests.</summary>
    internal TradingSessionManager(ITradingAccountStore profiles, ICredentialStore credentials,
        Func<TradingAccountProfile, AccountCredentials, ISpotTradingClient> factory, Lazy<TradingJournal> journal)
    {
        _profiles = profiles; _credentials = credentials; _factory = factory; _journal = journal;
        if (profiles is ITradingProfileNotifications changes) changes.Changed += ProfilesChanged;
        if (credentials is ICredentialNotifications keys) keys.CredentialsChanged += CredentialsChanged;
    }

    /// <summary>Returns profile metadata only; missing keys remain an explicit UI state.</summary>
    public TradingAccountsSettings Settings() => _profiles.Load();

    /// <summary>Authenticates on a background task, then reuses one session per verified owner/network.</summary>
    public Task<CcxtTradingService> GetAsync(Guid profileId, CancellationToken ct = default) => Task.Run(async () =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await RetireCoreAsync().ConfigureAwait(false);
            lock (_sync) if (_sessions.TryGetValue(profileId, out var existing)) return existing;
            var revision = Revision;
            var profile = _profiles.Load().Profiles.SingleOrDefault(p => p.Id == profileId)
                ?? throw new TradingServiceException(new("ProfileMissing", "The selected account no longer exists."));
            using var credentials = _credentials.Read(profileId)
                ?? throw new TradingServiceException(new("KeysRequired", "Enter and save this account's API keys first."));
            ISpotTradingClient? client = null;
            try
            {
                client = _factory(profile, credentials);
                var connected = await client.ConnectAsync(token).ConfigureAwait(false);
                if (revision != Revision) throw new TradingServiceException(new("AccountChanged", "Account settings changed; select the account again."));
                // A read-only profile must never acquire a session created using another profile's trading key.
                CcxtTradingService? shared;
                lock (_sync) shared = connected.CanTrade == false ? null : _sessions.Values.FirstOrDefault(s =>
                    s.Account.Venue == profile.Exchange && s.Account.Environment == profile.Environment && s.Account.AccountId == connected.Identity && s.KeyCanTrade == connected.CanTrade);
                if (shared is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    client = null;
                    lock (_sync) _sessions[profileId] = shared;
                    return shared;
                }
                var service = new CcxtTradingService(profile, client, _journal.Value, connected.Identity, connected.CanTrade);
                client = null;
                lock (_sync) _sessions[profileId] = service;
                return service;
            }
            catch (OperationCanceledException) { throw; }
            catch (TradingServiceException) { throw; }
            catch (Exception ex) { throw new TradingServiceException(new("ConnectionFailed", AccountConnectionVerifier.SafeError(ex))); }
            finally { if (client is not null) try { await client.DisposeAsync().ConfigureAwait(false); } catch (Exception) { } }
        }
        finally { _gate.Release(); }
    }, ct);

    /// <summary>Invalidates credentials immediately, before a window can send through a stale session.</summary>
    private void CredentialsChanged(Guid id)
    {
        lock (_sync)
        {
            _invalidated.Add(id);
            if (_sessions.TryGetValue(id, out var service)) service.Suspend();
        }
        Interlocked.Increment(ref _revision);
        _ = RetireAsync();
    }

    /// <summary>Profile removal retires its sessions; selecting an active profile leaves other sessions supervised.</summary>
    private void ProfilesChanged()
    {
        try
        {
            var ids = _profiles.Load().Profiles.Select(p => p.Id).ToHashSet();
            lock (_sync) foreach (var pair in _sessions.Where(p => !ids.Contains(p.Key))) { _invalidated.Add(pair.Key); pair.Value.Suspend(); }
        }
        catch (Exception) { }
        Interlocked.Increment(ref _revision);
        _ = RetireAsync();
    }

    /// <summary>Serializes asynchronous retirement after store notifications.</summary>
    private async Task RetireAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await RetireCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Disposes invalidated clients and removes all aliases to a shared retired session.</summary>
    private async Task RetireCoreAsync()
    {
        CcxtTradingService[] retired;
        lock (_sync)
        {
            retired = _sessions.Where(p => _invalidated.Contains(p.Key)).Select(p => p.Value).Distinct().ToArray();
            foreach (var id in _sessions.Where(p => retired.Contains(p.Value)).Select(p => p.Key).ToArray()) _sessions.Remove(id);
            _invalidated.Clear();
        }
        foreach (var session in retired) try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
    }

    /// <summary>Ends local supervision without canceling any exchange order.</summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_profiles is ITradingProfileNotifications changes) changes.Changed -= ProfilesChanged;
        if (_credentials is ICredentialNotifications keys) keys.CredentialsChanged -= CredentialsChanged;
        await _stop.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CcxtTradingService[] sessions;
            lock (_sync) { sessions = _sessions.Values.Distinct().ToArray(); _sessions.Clear(); }
            foreach (var session in sessions) try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
        }
        finally { _gate.Release(); }
    }
}
