using BotView.Services.Integrations.Quik;
using Moq;
using QuikSharp.DataStructures;
using QuikSharp.DataStructures.Transaction;

namespace BotView.Tests;

public sealed class QuikTradingTests
{
    private static readonly InstrumentKey Share = new("MOEX", "shares", "TQBR", "SBER");
    private static readonly InstrumentKey Future = new("MOEX", "futures", "SPBFUT", "SiZ6");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Supplies only detached account data; every native command is captured in memory.</summary>
    private static QuikTradingService Service(bool futures, Mock<IQuikTransactionClient> transactions, out Mock<IQuikReadClient> reader)
    {
        var profile = new TradingAccountProfile(Guid.NewGuid(), "Test", "quik", TradingEnvironment.Production,
            futures ? TradingMarket.MoexFutures : TradingMarket.MoexShares, new QuikAccountOptions("localhost", 34130, "firm", "client", "account", 0));
        reader = new Mock<IQuikReadClient>();
        reader.Setup(r => r.ConnectedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        reader.Setup(r => r.InstrumentAsync(It.IsAny<InstrumentKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InstrumentKey key, CancellationToken _) => new Instrument(key, key.SecurityId, "Test", PriceStep: .05m));
        reader.Setup(r => r.BalanceAsync(profile, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountSnapshot(QuikReadClient.Context(profile), [], [], [], DateTimeOffset.UtcNow));
        reader.Setup(r => r.DisposeAsync()).Returns(ValueTask.CompletedTask);
        transactions.Setup(t => t.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return new QuikTradingService(profile, reader.Object, transactions.Object);
    }

    /// <summary>Accepts a single transaction without manufacturing exchange fills or final order state.</summary>
    private static Mock<IQuikTransactionClient> Transactions(int status = 3, long? orderNumber = 99)
    {
        var client = new Mock<IQuikTransactionClient>(MockBehavior.Strict);
        client.Setup(t => t.SendAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionReply { TransID = 7, Status = status, OrderNum = orderNumber });
        return client;
    }

    /// <summary>Stock and FORTS limits preserve native routing, side, lot quantity and formatted price.</summary>
    [Theory]
    [InlineData(false, OrderSide.Buy)]
    [InlineData(true, OrderSide.Sell)]
    public async Task LimitOrderUsesNativeRoutingAndPendingAcknowledgement(bool futures, OrderSide side)
    {
        var transactions = Transactions();
        await using var service = Service(futures, transactions, out _);
        var key = futures ? Future : Share;

        var result = await service.CreateLimitOrderAsync(new LimitOrderRequest(key, side, 2, 100.129m), Ct);

        Assert.True(service.CanExecuteTrades);
        Assert.Equal(new OrderReference(key, "99"), result.Reference);
        Assert.Equal(OrderState.Pending, result.State);
        Assert.Null(result.FilledQuantity);
        transactions.Verify(t => t.SendAsync(It.Is<Transaction>(v => v.ACTION == TransactionAction.NEW_ORDER &&
            v.CLASSCODE == key.Board && v.SECCODE == key.SecurityId && v.FIRM_ID == "firm" && v.ACCOUNT == "account" &&
            v.CLIENT_CODE == "client" && v.TYPE == TransactionType.L && v.QUANTITY == 2 && v.PRICE == 100.10m &&
            v.OPERATION == (side == OrderSide.Buy ? TransactionOperation.B : TransactionOperation.S)), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Stock market requests use zero; FORTS requests use the direction-specific terminal bound.</summary>
    [Theory]
    [InlineData(false, OrderSide.Buy, 0)]
    [InlineData(true, OrderSide.Buy, 120)]
    [InlineData(true, OrderSide.Sell, 80)]
    public async Task MarketOrderUsesCorrectNativePrice(bool futures, OrderSide side, decimal price)
    {
        var transactions = Transactions();
        if (futures) transactions.Setup(t => t.MarketPriceAsync(Future, side, It.IsAny<CancellationToken>())).ReturnsAsync(price);
        await using var service = Service(futures, transactions, out _);

        var result = await service.CreateMarketOrderAsync(new MarketOrderRequest(futures ? Future : Share, side, 1), Ct);

        Assert.Null(result.Price);
        transactions.Verify(t => t.SendAsync(It.Is<Transaction>(v => v.TYPE == TransactionType.M && v.PRICE == price), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Cancellation verifies account-owned identity and keeps the last known state after acknowledgement.</summary>
    [Theory]
    [InlineData(OrderCategory.Regular)]
    [InlineData(OrderCategory.Conditional)]
    public async Task CancellationRoutesCategoryWithoutInventingCanceledState(OrderCategory category)
    {
        var transactions = Transactions();
        await using var service = Service(false, transactions, out var reader);
        var reference = new OrderReference(Share, "42", category);
        var order = new OrderSnapshot(reference, null, OrderSide.Buy, OrderType.Limit, OrderState.Open,
            2, 1, 1, 100, null, null, DateTimeOffset.UtcNow);
        reader.Setup(r => r.OrdersAsync(It.IsAny<TradingAccountProfile>(), Share, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { order });

        var result = await service.CancelOrderAsync(reference, Ct);

        Assert.Equal(OrderState.Open, result.State);
        Assert.Equal(1, result.FilledQuantity);
        transactions.Verify(t => t.SendAsync(It.Is<Transaction>(v => category == OrderCategory.Regular
            ? v.ACTION == TransactionAction.KILL_ORDER && v.ORDER_KEY == "42"
            : v.ACTION == TransactionAction.KILL_STOP_ORDER && v.STOP_ORDER_KEY == "42"), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Native rejections and missing acknowledgements are never retried or exposed as successful orders.</summary>
    [Theory]
    [InlineData(4, 99, "TransactionRejected")]
    [InlineData(6, 99, "TransactionRejected")]
    [InlineData(12, 99, "OutcomeUnknown")]
    [InlineData(3, 0, "OutcomeUnknown")]
    public async Task FailedAcknowledgementsAreNotRetried(int status, long number, string code)
    {
        var transactions = Transactions(status, number);
        await using var service = Service(false, transactions, out _);

        var error = await Assert.ThrowsAsync<TradingServiceException>(() => service.CreateMarketOrderAsync(new MarketOrderRequest(Share, OrderSide.Buy, 1), Ct));

        Assert.Equal(code, error.Error.Code);
        transactions.Verify(t => t.SendAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Unsupported semantics, invalid units, wrong markets and canceled calls never dispatch a transaction.</summary>
    [Fact]
    public async Task InvalidRequestsNeverReachTransactionTransport()
    {
        var transactions = Transactions();
        await using var service = Service(false, transactions, out _);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateMarketOrderAsync(new MarketOrderRequest(Share, OrderSide.Buy, .5m), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateMarketOrderAsync(new MarketOrderRequest(Future, OrderSide.Buy, 1), Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.CreateLimitOrderAsync(new LimitOrderRequest(Share, OrderSide.Buy, 1, 100, PostOnly: true), Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.CreateMarketOrderAsync(new MarketOrderRequest(Share, OrderSide.Sell, 1, ReduceOnly: true), Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.SetLeverageAsync(Share, 5, Ct));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateMarketOrderAsync(new MarketOrderRequest(Share, OrderSide.Buy, 1), stop.Token));
        transactions.Verify(t => t.SendAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Transport failures have unknown outcomes and do not cause a second command.</summary>
    [Fact]
    public async Task TimeoutDoesNotResubmit()
    {
        var transactions = Transactions();
        transactions.Setup(t => t.SendAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException());
        await using var service = Service(false, transactions, out _);

        await Assert.ThrowsAsync<TimeoutException>(() => service.CreateMarketOrderAsync(new MarketOrderRequest(Share, OrderSide.Buy, 1), Ct));
        transactions.Verify(t => t.SendAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Foreign orders cannot be canceled even when their numeric ID is known.</summary>
    [Fact]
    public async Task CancellationRequiresAccountOwnedOrder()
    {
        var transactions = Transactions();
        await using var service = Service(false, transactions, out var reader);
        reader.Setup(r => r.OrdersAsync(It.IsAny<TradingAccountProfile>(), Share, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<OrderSnapshot>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelOrderAsync(new OrderReference(Share, "42"), Ct));
        transactions.Verify(t => t.SendAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Replies arriving inside dispatch are retained; unrelated IDs and intermediate statuses are ignored.</summary>
    [Fact]
    public async Task FastReplyIsCorrelatedBeforeRpcReturns()
    {
        QuikSharp.TransReplyHandler? callback = null;
        var sent = 0;
        var transaction = new Transaction();

        // Simulate QUIKSharp assigning an ID and emitting callbacks before completing its RPC task.
        Task<long> Send(Transaction value)
        {
            sent++;
            value.TRANS_ID = 7;
            callback!(new TransactionReply { TransID = 8, Status = 3, OrderNum = 100 });
            callback!(new TransactionReply { TransID = 7, Status = 1 });
            callback!(new TransactionReply { TransID = 7, Status = 3, OrderNum = 99 });
            return Task.FromResult(7L);
        }

        var reply = await QuikTransactionClient.DispatchAsync(transaction, Send,
            handler => callback += handler, handler => callback -= handler, TimeSpan.FromSeconds(1), Ct);

        Assert.Equal(99, reply.OrderNum);
        Assert.Equal(1, sent);
        Assert.Null(callback);
    }

    /// <summary>A locally rejected transaction or missing callback removes its handler and never resubmits.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DispatchFailureCleansUpWithoutRetry(bool rejected)
    {
        QuikSharp.TransReplyHandler? callback = null;
        var sent = 0;

        // Complete the RPC but deliberately omit any exchange acknowledgement.
        Task<long> Send(Transaction value)
        {
            sent++;
            value.TRANS_ID = 7;
            return Task.FromResult(rejected ? -7L : 7L);
        }

        var error = await Assert.ThrowsAsync<TradingServiceException>(() => QuikTransactionClient.DispatchAsync(
            new Transaction(), Send, handler => callback += handler, handler => callback -= handler,
            TimeSpan.FromMilliseconds(30), Ct));

        Assert.Equal(rejected ? "TransactionRejected" : "OutcomeUnknown", error.Error.Code);
        Assert.Equal(1, sent);
        Assert.Null(callback);
    }

    /// <summary>A disconnected broker or unavailable futures bound prevents command dispatch.</summary>
    [Fact]
    public async Task MissingPreconditionsPreventDispatch()
    {
        var transactions = Transactions();
        transactions.Setup(t => t.MarketPriceAsync(Future, OrderSide.Buy, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TradingServiceException(new TradingError("PriceBoundUnavailable", "No bound")));
        await using var service = Service(true, transactions, out var reader);
        await Assert.ThrowsAsync<TradingServiceException>(() => service.CreateMarketOrderAsync(new MarketOrderRequest(Future, OrderSide.Buy, 1), Ct));

        reader.Setup(r => r.ConnectedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateLimitOrderAsync(new LimitOrderRequest(Future, OrderSide.Buy, 1, 100), Ct));
        transactions.Verify(t => t.SendAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Disposal prevents later commands and releases execution and read boundaries exactly once.</summary>
    [Fact]
    public async Task DisposedServiceCannotSend()
    {
        var transactions = Transactions();
        var service = Service(false, transactions, out var reader);
        await service.DisposeAsync();
        await service.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.CreateMarketOrderAsync(new MarketOrderRequest(Share, OrderSide.Buy, 1), Ct));
        transactions.Verify(t => t.SendAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()), Times.Never);
        transactions.Verify(t => t.DisposeAsync(), Times.Once);
        reader.Verify(t => t.DisposeAsync(), Times.Once);
    }
}
