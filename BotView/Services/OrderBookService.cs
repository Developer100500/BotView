using System.Collections;
using System.Globalization;
using BotView.Configuration;
using BotView.Interfaces;
using BotView.Models;

namespace BotView.Services;

public sealed class OrderBookService : IOrderBookService
{
    private readonly Func<string, IOrderBookClient> _create;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public OrderBookService() : this(id => new ProOrderBookClient(id), Task.Delay) { }

    internal OrderBookService(Func<string, IOrderBookClient> create,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _create = create;
        _delay = delay ?? Task.Delay;
    }

    public Task<IOrderBookSubscription> SubscribeAsync(string exchange, string symbol, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var id = MarketCatalog.GetExchange(exchange).Id;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IOrderBookSubscription>(new Subscription(id, symbol, _create, _delay, ct));
    }

    private sealed class Subscription : IOrderBookSubscription
    {
        private readonly CancellationTokenSource _stop;
        private readonly Task _worker;
        private readonly object _disposeSync = new();
        private Task? _disposeTask;
        private OrderBookUpdate _current = new(null, OrderBookConnection.Connecting, "Подключение…");
        public OrderBookUpdate Current => Volatile.Read(ref _current);

        public Subscription(string exchange, string symbol, Func<string, IOrderBookClient> create,
            Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _worker = Task.Run(() => RunAsync(exchange, symbol, create, delay, _stop.Token));
        }

        private void Publish(OrderBookConnection connection, string message, OrderBookSnapshot? snapshot = null) =>
            Volatile.Write(ref _current, new(snapshot ?? Current.Snapshot, connection, message));

        private async Task RunAsync(string exchange, string symbol, Func<string, IOrderBookClient> create,
            Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
        {
            var retry = 1;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    IOrderBookClient? client = null;
                    var fatal = false;
                    try
                    {
                        client = create(exchange);
                        var market = await client.LoadMarketAsync(symbol, ct).ConfigureAwait(false);
                        while (!ct.IsCancellationRequested)
                        {
                            var snapshot = await client.WatchAsync(market, ct).ConfigureAwait(false);
                            ct.ThrowIfCancellationRequested();
                            Publish(OrderBookConnection.Live, "Подключено", snapshot);
                            retry = 1;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex) when (ex is NotSupportedException or ccxt.BadRequest or ccxt.NotSupported)
                    {
                        Publish(OrderBookConnection.Error, ex.Message);
                        fatal = true;
                    }
                    catch (Exception ex)
                    {
                        Publish(OrderBookConnection.Reconnecting, $"Данные устарели. Переподключение: {ex.Message}");
                    }
                    finally
                    {
                        if (client is not null)
                        {
                            try { await client.DisposeAsync().ConfigureAwait(false); }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Order book close: {ex.Message}"); }
                        }
                    }
                    if (fatal) return;
                    await delay(TimeSpan.FromSeconds(retry), ct).ConfigureAwait(false);
                    retry = Math.Min(30, retry * 2);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally
            {
                if (ct.IsCancellationRequested) Publish(OrderBookConnection.Closed, "Отключено");
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_disposeSync) return new(_disposeTask ??= StopAsync());
        }

        private async Task StopAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await _worker.ConfigureAwait(false);
            _stop.Dispose();
        }
    }
}

internal interface IOrderBookClient : IAsyncDisposable
{
    Task<OrderBookMarket> LoadMarketAsync(string symbol, CancellationToken ct);
    Task<OrderBookSnapshot> WatchAsync(OrderBookMarket market, CancellationToken ct);
}

internal sealed class ProOrderBookClient : IOrderBookClient
{
    private readonly ccxt.Exchange _client;
    private readonly int _subscriptionDepth;

    public ProOrderBookClient(string id)
    {
        var settings = CreateSettings();
        // Bybit spot streams accept 1/50/200/1000, so receive 200 and retain the nearest 100.
        _subscriptionDepth = id == "bybit" ? 200 : 100;
        _client = id switch
        {
            "binance" => new ccxt.pro.binance(settings),
            "bybit" => new ccxt.pro.bybit(settings),
            "okx" => new ccxt.pro.okx(settings),
            "kraken" => new ccxt.pro.kraken(settings),
            _ => throw new NotSupportedException($"Стакан биржи {id} не поддерживается.")
        };
    }

    internal static Dictionary<string, object> CreateSettings() =>
        new()
        {
            ["enableRateLimit"] = true,
            ["timeout"] = 15000,
            ["options"] = new Dictionary<string, object>
            {
                ["defaultType"] = "spot",
                ["fetchMarkets"] = new Dictionary<string, object> { ["types"] = new List<object> { "spot" } }
            }
        };

    public async Task<OrderBookMarket> LoadMarketAsync(string symbol, CancellationToken ct)
    {
        if (!_client.has.TryGetValue("watchOrderBook", out var supported) || supported is not true)
            throw new NotSupportedException("Биржа не поддерживает потоковый стакан.");
        var markets = await AwaitAsync(_client.LoadMarkets(), ct).ConfigureAwait(false);
        if (!markets.TryGetValue(symbol, out var market) || market.spot != true || market.active == false)
            throw new NotSupportedException($"Спотовая пара {symbol} недоступна на этой бирже.");
        var precision = market.precision?.price;
        var tick = GetTickSize(precision, Convert.ToInt32(_client.precisionMode, CultureInfo.InvariantCulture));
        return new(symbol, market.baseCurrency ?? symbol.Split('/')[0], market.quote ?? symbol.Split('/').Last(), tick);
    }

    internal static decimal GetTickSize(double? precision, int precisionMode)
    {
        if (precision is null || !double.IsFinite(precision.Value))
            throw new NotSupportedException("Биржа не предоставила шаг цены.");
        // CCXT: DECIMAL_PLACES = 2, TICK_SIZE = 4. Significant digits do not define a fixed ladder tick.
        decimal tick = precisionMode switch
        {
            4 => Convert.ToDecimal(precision.Value, CultureInfo.InvariantCulture),
            2 when precision.Value == Math.Truncate(precision.Value) && precision.Value is >= -28 and <= 28 =>
                DecimalPower(-(int)precision.Value),
            _ => throw new NotSupportedException("Режим точности рынка не задает постоянный шаг цены.")
        };
        return tick > 0 ? tick : throw new NotSupportedException("Некорректный шаг цены биржи.");
    }

    private static decimal DecimalPower(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < Math.Abs(exponent); i++) result = exponent < 0 ? result / 10m : result * 10m;
        return result;
    }

    public async Task<OrderBookSnapshot> WatchAsync(OrderBookMarket market, CancellationToken ct)
    {
        var book = await AwaitAsync(_client.WatchOrderBook(market.Symbol, _subscriptionDepth), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // Copy both sides before awaiting again: CCXT owns and updates the live cache.
        var bids = CopyLevels(book.bids.Copy(), descending: true);
        var asks = CopyLevels(book.asks.Copy(), descending: false);
        return new(market, bids, asks, DateTimeOffset.UtcNow);
    }

    internal static IReadOnlyList<OrderBookLevel> CopyLevels(IEnumerable levels, bool descending)
    {
        var copy = new List<OrderBookLevel>();
        foreach (var entry in levels)
        {
            if (entry is not IList row || row.Count < 2) continue;
            var price = Convert.ToDouble(row[0], CultureInfo.InvariantCulture);
            var amount = Convert.ToDouble(row[1], CultureInfo.InvariantCulture);
            if (!double.IsFinite(price) || !double.IsFinite(amount) || price <= 0 || amount <= 0) continue;
            copy.Add(new(Convert.ToDecimal(price, CultureInfo.InvariantCulture), Convert.ToDecimal(amount, CultureInfo.InvariantCulture)));
        }
        var ordered = descending ? copy.OrderByDescending(l => l.Price) : copy.OrderBy(l => l.Price);
        return Array.AsReadOnly(ordered.Take(100).ToArray());
    }

    private static async Task<T> AwaitAsync<T>(Task<T> task, CancellationToken ct)
    {
        try { return await task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    public async ValueTask DisposeAsync() => await _client.Close().ConfigureAwait(false);
}
