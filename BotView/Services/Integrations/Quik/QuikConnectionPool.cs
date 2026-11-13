using System.Net.Sockets;
using System.Text;
using System.Runtime.ExceptionServices;
using BotView.Configuration;
using QuikSharp;

namespace BotView.Services.Integrations.Quik;

/// <summary>Shares one terminal transport; only the final lease may stop it.</summary>
internal static class QuikConnectionPool
{
    private static readonly object Sync = new();
    private static readonly Dictionary<(string Host, int Port), Entry> Entries = [];
    private static readonly Dictionary<(string Host, int Port), Task> Retirements = [];

    /// <summary>Acquires ownership without connecting or blocking the UI thread.</summary>
    internal static Lease Acquire(QuikConnectionOptions options)
    {
        options.Validate();
        lock (Sync)
        {
            var key = (options.Host, options.Port);
            if (!Entries.TryGetValue(key, out var entry))
            {
                entry = new(options, Retirements.GetValueOrDefault(key, Task.CompletedTask));
                Entries.Add(key, entry);
            }
            entry.References++;
            return new Lease(key, entry);
        }
    }

    internal sealed class Entry(QuikConnectionOptions options, Task predecessor)
    {
        public int References;
        public Task<QuikSharp.Quik>? Client;
        public QuikService? Service;
        public Dictionary<InstrumentKey, int> Books { get; } = [];
        public SemaphoreSlim BookGate { get; } = new(1, 1);

        /// <summary>Sets the shared RPC timeout before QUIKSharp's synchronous constructor RPC.</summary>
        public Task<QuikSharp.Quik> GetAsync()
        {
            lock (Sync)
            {
                var failedService = Client?.IsFaulted == true ? Service : null;
                if (Client?.IsFaulted == true) Client = null;
                return Client ??= Task.Run(async () =>
                {
                    await predecessor.ConfigureAwait(false);
                    if (failedService is not null)
                    {
                        try { failedService.Stop(); }
                        catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(e => e is SocketException or OperationCanceledException)) { }
                    }
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    var classes = new ClassFunctions(options.Port, options.Host);
                    Service = classes.QuikService;
                    Service.DefaultSendTimeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
                    try { return new QuikSharp.Quik(options.Port, null, options.Host); }
                    catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
                    {
                        ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                        throw;
                    }
                });
            }
        }
    }

    internal sealed class Lease((string Host, int Port) key, Entry entry) : IAsyncDisposable
    {
        private int _disposed;
        internal SemaphoreSlim BookGate => entry.BookGate;

        /// <summary>Waits for the common transport while allowing a caller to abandon its wait.</summary>
        internal Task<QuikSharp.Quik> GetAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return entry.GetAsync().WaitAsync(ct);
        }

        /// <summary>Counts book consumers separately from terminal leases.</summary>
        internal void RetainBook(InstrumentKey instrument)
        {
            lock (Sync) entry.Books[instrument] = entry.Books.GetValueOrDefault(instrument) + 1;
        }

        /// <summary>Returns true only to the final consumer allowed to unsubscribe a listing.</summary>
        internal bool ReleaseBook(InstrumentKey instrument)
        {
            lock (Sync)
            {
                if (!entry.Books.TryGetValue(instrument, out var count)) return false;
                if (count > 1) { entry.Books[instrument] = count - 1; return false; }
                entry.Books.Remove(instrument);
                return true;
            }
        }

        /// <summary>Releases the shared transport only after all catalog, book and account users leave.</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Task<QuikSharp.Quik>? startup;
            var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Sync)
            {
                if (--entry.References != 0) return;
                Entries.Remove(key);
                Retirements[key] = retired.Task;
                startup = entry.Client;
            }

            try
            {
                if (startup is not null)
                {
                    try { await startup.ConfigureAwait(false); }
                    catch (Exception) { /* Failed initialization still owns sockets that must be stopped. */ }
                    try { await Task.Run(() => entry.Service?.Stop()).ConfigureAwait(false); }
                    catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(e => e is SocketException or OperationCanceledException)) { }
                }
            }
            finally
            {
                retired.TrySetResult();
                lock (Sync)
                {
                    if (Retirements.GetValueOrDefault(key) == retired.Task) Retirements.Remove(key);
                }
            }
        }
    }
}
