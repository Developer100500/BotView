using System.Text;
using System.Net.Sockets;
using BotView.Configuration;
using QuikSharp;

namespace BotView.Services.Providers;

// These transport details and QUIKSharp types never escape the adapter.
internal sealed record QuikSecurityDescription(string? ShortName, string? Name, int LotSize, double PriceStep);

internal interface IQuikCatalogClient : IAsyncDisposable
{
    Task<bool> IsConnectedAsync(CancellationToken ct);
    Task<string[]> GetClassesAsync(CancellationToken ct);
    Task<string[]> GetSecuritiesAsync(string classCode, CancellationToken ct);
    Task<QuikSecurityDescription> GetSecurityAsync(string classCode, string code, CancellationToken ct);
}

internal sealed class QuikSharpCatalogClient : IQuikCatalogClient
{
    private readonly ClassFunctions _classes;
    private readonly ServiceFunctions _service;
    private readonly TimeSpan _timeout;
    private int _disposed;

    internal QuikSharpCatalogClient(QuikConnectionOptions options)
    {
        options.Validate();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        // Quik's constructor calls GetWorkingFolder().Result with an infinite timeout.
        // The individual facades start the same transport without that synchronous RPC.
        _classes = new ClassFunctions(options.Port, options.Host);
        _service = new ServiceFunctions(options.Port, options.Host);
        _classes.QuikService.DefaultSendTimeout = _timeout;
    }

    public Task<bool> IsConnectedAsync(CancellationToken ct) =>
        _service.IsConnected((int)_timeout.TotalMilliseconds).WaitAsync(_timeout, ct);

    public Task<string[]> GetClassesAsync(CancellationToken ct) =>
        _classes.GetClassesList().WaitAsync(_timeout, ct);

    public Task<string[]> GetSecuritiesAsync(string classCode, CancellationToken ct) =>
        _classes.GetClassSecurities(classCode).WaitAsync(_timeout, ct);

    public async Task<QuikSecurityDescription> GetSecurityAsync(string classCode, string code, CancellationToken ct)
    {
        var info = await _classes.GetSecurityInfo(classCode, code).WaitAsync(_timeout, ct).ConfigureAwait(false);
        if (info is null)
            throw new InvalidOperationException(
                $"QUIK did not return a description for {classCode}:{code}.Refresh the catalog again.");
        
        return new QuikSecurityDescription(info.ShortName, info.Name, info.LotSize, info.MinPriceStep);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await Task.Run(() => _classes.QuikService.Stop()).ConfigureAwait(false); }
        catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(
                   e => e is SocketException or OperationCanceledException))
        {
            // QUIKSharp 2.0.0 calls Shutdown even on sockets that never connected.
            // Stop has already signalled cancellation; preserve the original request error.
            System.Diagnostics.Debug.WriteLine($"QUIKSharp stopped with disconnected sockets: {ex.Message}");
        }
    }
}
