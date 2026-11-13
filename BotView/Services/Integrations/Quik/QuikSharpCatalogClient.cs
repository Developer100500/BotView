using System.Text;
using System.Net.Sockets;
using BotView.Configuration;
using QuikSharp;

namespace BotView.Services.Integrations.Quik;

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
    private readonly QuikConnectionPool.Lease _lease;
    private readonly TimeSpan _timeout;
    private int _disposed;

    internal QuikSharpCatalogClient(QuikConnectionOptions options)
    {
        options.Validate();
        _timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        _lease = QuikConnectionPool.Acquire(options);
    }

    public async Task<bool> IsConnectedAsync(CancellationToken ct) =>
        await (await _lease.GetAsync(ct)).Service.IsConnected((int)_timeout.TotalMilliseconds).WaitAsync(_timeout, ct);

    public async Task<string[]> GetClassesAsync(CancellationToken ct) =>
        await (await _lease.GetAsync(ct)).Class.GetClassesList().WaitAsync(_timeout, ct);

    public async Task<string[]> GetSecuritiesAsync(string classCode, CancellationToken ct) =>
        await (await _lease.GetAsync(ct)).Class.GetClassSecurities(classCode).WaitAsync(_timeout, ct);

    public async Task<QuikSecurityDescription> GetSecurityAsync(string classCode, string code, CancellationToken ct)
    {
        var info = await (await _lease.GetAsync(ct)).Class.GetSecurityInfo(classCode, code).WaitAsync(_timeout, ct).ConfigureAwait(false);
        if (info is null)
            throw new InvalidOperationException(
                $"QUIK did not return a description for {classCode}:{code}.Refresh the catalog again.");
        
        return new QuikSecurityDescription(info.ShortName, info.Name, info.LotSize, info.MinPriceStep);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lease.DisposeAsync().ConfigureAwait(false);
    }
}
