using BotView.Configuration;
using BotView.Services;
using BotView.Models;

namespace BotView.Services.Integrations.Quik;

/// <summary>Read-only instrument catalog from one running QUIK terminal.</summary>
public sealed class QuikInstrumentProvider : IInstrumentProvider
{
    private static readonly (string Class, string Market)[] Markets =
        [("TQBR", "shares"), ("TQOB", "bonds"), ("SPBFUT", "futures")];
    private readonly Func<IQuikCatalogClient> _clientFactory;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private IQuikCatalogClient? _client;
    private int _disposed;

    public QuikInstrumentProvider() : this(() => new QuikSharpCatalogClient(QuikConnectionOptions.Load())) { }

    internal QuikInstrumentProvider(Func<IQuikCatalogClient> clientFactory) => _clientFactory = clientFactory;

    public ExchangeDefinition Definition { get; } = new(
        "quik", "QUIK / MOEX", "", true, [], "quiksharp", "MOEX");

    /// <summary>Loads listing keys and native lots/contracts; unavailable financial valuation fields stay unknown.</summary>
    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = linked.Token;
        await _loadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            var client = _client ??= _clientFactory();
            if (!await client.IsConnectedAsync(token).ConfigureAwait(false))
                throw new InvalidOperationException("The QUIK terminal is not connected to the broker. Sign in to QUIK and click Refresh.");
            var classes = (await client.GetClassesAsync(token).ConfigureAwait(false))
                .Select(c => c.Trim()).ToHashSet(StringComparer.Ordinal);
            var instruments = new List<Instrument>();
            foreach (var (classCode, market) in Markets)
            {
                if (!classes.Contains(classCode)) continue;
                var codes = (await client.GetSecuritiesAsync(classCode, token).ConfigureAwait(false))
                    .Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal);
                foreach (var batch in codes.Chunk(8))
                {
                    token.ThrowIfCancellationRequested();
                    var items = await Task.WhenAll(batch.Select(async code =>
                    {
                        var info = await client.GetSecurityAsync(classCode, code, token).ConfigureAwait(false);
                        var name = !string.IsNullOrWhiteSpace(info.ShortName) ? info.ShortName.Trim()
                            : !string.IsNullOrWhiteSpace(info.Name) ? info.Name.Trim() : code;
                        if (info.LotSize < 0 || !double.IsFinite(info.PriceStep) || info.PriceStep < 0)
                            throw new InvalidOperationException($"Invalid instrument description {classCode}:{code}.");
                        return new Instrument(new("MOEX", market, classCode, code), $"{classCode}:{code}", name,
                            LotSize: info.LotSize, PriceStep: info.PriceStep > 0 ? (decimal)info.PriceStep : null,
                            TradingRules: new(market == "futures" ? QuantityUnit.Contracts : QuantityUnit.Lots,
                                market switch
                                {
                                    "bonds" => PriceQuotation.PercentOfFaceValue,
                                    "futures" => PriceQuotation.Points,
                                    _ => PriceQuotation.CurrencyPerUnit
                                }));
                    })).ConfigureAwait(false);
                    instruments.AddRange(items);
                }
            }
            if (!await client.IsConnectedAsync(token).ConfigureAwait(false))
                throw new InvalidOperationException("The QUIK broker connection was lost while loading the catalog.");
            token.ThrowIfCancellationRequested();
            return instruments.OrderBy(i => i.Key.Market, StringComparer.Ordinal)
                .ThenBy(i => i.Key.SecurityId, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex)
        {
            await CloseClientAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (ex is TimeoutException)
                throw new TimeoutException("QUIK timed out. Check that QuikSharp.lua is running and verify the address and ports in quik.json.", ex);
            throw;
        }
        finally { _loadGate.Release(); }
    }

    private async ValueTask CloseClientAsync()
    {
        var client = _client;
        _client = null;
        if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _loadGate.WaitAsync().ConfigureAwait(false);
        try { await CloseClientAsync().ConfigureAwait(false); }
        finally { _loadGate.Release(); }
    }
}
