using System.Globalization;
using BotView.Configuration;
using QuikSharp.DataStructures.Transaction;

namespace BotView.Services.Integrations.Quik;

/// <summary>Isolates transaction dispatch and futures market-price bounds from account reads.</summary>
internal interface IQuikTransactionClient : IAsyncDisposable
{
    Task<TransactionReply> SendAsync(Transaction transaction, CancellationToken ct);
    Task<decimal> MarketPriceAsync(InstrumentKey instrument, OrderSide side, CancellationToken ct);
}

/// <summary>Dispatches once and correlates terminal acknowledgements independently of order callbacks.</summary>
internal sealed class QuikTransactionClient : IQuikTransactionClient
{
    private readonly QuikConnectionPool.Lease _lease;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Acquires a lazy lease without connecting to or sending commands to the terminal.</summary>
    internal QuikTransactionClient(QuikConnectionOptions options)
    {
        _lease = QuikConnectionPool.Acquire(options);
        _timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
    }

    /// <summary>Uses native direction-specific FORTS bounds; absent bounds never become a guessed price.</summary>
    public async Task<decimal> MarketPriceAsync(InstrumentKey instrument, OrderSide side, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var client = await _lease.GetAsync(linked.Token).ConfigureAwait(false);
        var parameter = side == OrderSide.Buy ? "PRICEMAX" : "PRICEMIN";
        var result = await client.Trading.GetParamEx2(instrument.Board, instrument.SecurityId, parameter)
            .WaitAsync(_timeout, linked.Token).ConfigureAwait(false);

        if (!decimal.TryParse(result?.ParamValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var price) || price <= 0)
        {
            throw new TradingServiceException(new TradingError("PriceBoundUnavailable", "QUIK did not report a valid futures market-price bound."));
        }

        return price;
    }

    /// <summary>Registers before dispatch so even an acknowledgement preceding the RPC response is retained.</summary>
    public async Task<TransactionReply> SendAsync(Transaction transaction, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;
        var client = await _lease.GetAsync(token).ConfigureAwait(false);
        return await DispatchAsync(transaction, client.Trading.SendTransaction,
            handler => client.Events.OnTransReply += handler,
            handler => client.Events.OnTransReply -= handler, _timeout, token).ConfigureAwait(false);
    }

    /// <summary>Correlates replies around one dispatch; injectable callbacks allow protocol-race tests without a terminal.</summary>
    internal static async Task<TransactionReply> DispatchAsync(Transaction transaction, Func<Transaction, Task<long>> send,
        Action<QuikSharp.TransReplyHandler> subscribe, Action<QuikSharp.TransReplyHandler> unsubscribe,
        TimeSpan timeout, CancellationToken token)
    {
        var completion = new TaskCompletionSource<TransactionReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        // QUIKSharp assigns TRANS_ID synchronously before dispatching the native request.
        void Receive(TransactionReply reply)
        {
            if (reply.TransID == transaction.TRANS_ID && reply.Status is not (0 or 1))
                completion.TrySetResult(reply);
        }

        subscribe(Receive);
        try
        {
            token.ThrowIfCancellationRequested();
            var id = await send(transaction).WaitAsync(timeout, token).ConfigureAwait(false);
            if (id <= 0)
                throw new TradingServiceException(new TradingError("TransactionRejected", "QUIK rejected the transaction before exchange acknowledgement."));

            return await completion.Task.WaitAsync(timeout, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TradingServiceException(new TradingError("OutcomeUnknown", "QUIK transaction outcome is unknown. Check terminal orders before submitting again."));
        }
        finally
        {
            unsubscribe(Receive);
        }
    }

    /// <summary>Stops pending waits and releases only this transaction consumer's lease.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _lease.DisposeAsync().ConfigureAwait(false);
    }
}
