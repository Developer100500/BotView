using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BotView.Models;
using BotView.Services.Integrations.Ccxt;
using BotView.Services.OrderBooks;
using BotView.Windows;
using Moq;

namespace BotView.Tests;

public sealed class OrderPositionDisplayTests
{
    /// <summary>One-way orders must not acquire a hedge leg from their Buy or Sell direction.</summary>
    [Theory]
    [InlineData("positionSide")]
    [InlineData("ps")]
    public void BinanceOneWayPositionColumnRemainsEmpty(string field)
    {
        var key = new InstrumentKey("binance", "futures", "", "BTC/USDT:USDT");

        Assert.Null(MapOrder(key, "buy-order", "buy", field, "BOTH").PositionSide);
        Assert.Null(MapOrder(key, "sell-order", "sell", field, "BOTH").PositionSide);
    }

    /// <summary>Opening and closing directions must retain the exchange's explicit hedge leg.</summary>
    [Theory]
    [InlineData("binance", "positionSide", "LONG", PositionSide.Long)]
    [InlineData("binance", "positionSide", "SHORT", PositionSide.Short)]
    [InlineData("binance", "ps", "LONG", PositionSide.Long)]
    [InlineData("binance", "ps", "SHORT", PositionSide.Short)]
    [InlineData("bybit", "positionIdx", 1, PositionSide.Long)]
    [InlineData("bybit", "positionIdx", 2, PositionSide.Short)]
    [InlineData("bybit", "positionIdx", 1L, PositionSide.Long)]
    [InlineData("bybit", "positionIdx", 2L, PositionSide.Short)]
    [InlineData("bybit", "positionIdx", "1", PositionSide.Long)]
    [InlineData("bybit", "positionIdx", "2", PositionSide.Short)]
    [InlineData("okx", "posSide", "long", PositionSide.Long)]
    [InlineData("okx", "posSide", "short", PositionSide.Short)]
    public void HedgeLegIsIndependentOfBuySellAndReduceOnly(
        string venue, string field, object value, PositionSide expected)
    {
        var key = new InstrumentKey(venue, "futures", "", "BTC/USDT:USDT");

        foreach (var direction in new[] { "buy", "sell" })
        {
            foreach (var reduceOnly in new[] { false, true })
            {
                var native = new ccxt.Order
                {
                    id = "order-1",
                    side = direction,
                    type = "limit",
                    status = "open",
                    reduceOnly = reduceOnly,
                    info = new Dictionary<string, object> { [field] = value }
                };

                var order = CcxtTradingMapper.MapOrder(native, key);

                Assert.Equal(expected, order.PositionSide);
                Assert.Equal(direction == "buy" ? OrderSide.Buy : OrderSide.Sell, order.Side);
                Assert.Equal(reduceOnly, order.ReduceOnly);
            }
        }
    }

    /// <summary>The real order table must render both Long and Short from mapped exchange replies.</summary>
    [Theory]
    [InlineData("binance", "positionSide", "LONG", "SHORT")]
    [InlineData("binance", "ps", "LONG", "SHORT")]
    [InlineData("bybit", "positionIdx", 1, 2)]
    [InlineData("okx", "posSide", "long", "short")]
    public void PositionColumnDisplaysBothHedgeLegs(string venue, string field, object longValue, object shortValue)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            OrderBookWindow? window = null;

            try
            {
                var key = new InstrumentKey(venue, "futures", "", "BTC/USDT:USDT");
                var orders = new[]
                {
                    MapOrder(key, "long-order", "buy", field, longValue),
                    MapOrder(key, "short-order", "sell", field, shortValue)
                };
                window = new OrderBookWindow(venue, key.SecurityId, new Mock<IOrderBookService>().Object,
                    environment: TradingEnvironment.Testnet, market: TradingMarket.UsdtFutures);
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1000, 1800));
                content.Arrange(new Rect(0, 0, 1000, 1800));
                content.UpdateLayout();

                var grid = FindOrderTable(content)!;
                Assert.NotNull(grid);

                grid.ItemsSource = orders;
                grid.Measure(new Size(1000, 200));
                grid.Arrange(new Rect(0, 0, 1000, 200));
                grid.UpdateLayout();

                var column = Assert.Single(grid.Columns.OfType<DataGridTextColumn>(), c => Equals(c.Header, "Position"));
                Assert.Equal("Long", Assert.IsType<TextBlock>(column.GetCellContent(orders[0])).Text);
                Assert.Equal("Short", Assert.IsType<TextBlock>(column.GetCellContent(orders[1])).Text);
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                if (window is not null)
                {
                    window.Close();
                    window.CleanupTask.GetAwaiter().GetResult();
                }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>Builds an exchange response with a distinct identity and explicit position metadata.</summary>
    private static OrderSnapshot MapOrder(InstrumentKey key, string id, string side, string field, object value)
    {
        return CcxtTradingMapper.MapOrder(new ccxt.Order
        {
            id = id,
            side = side,
            type = "limit",
            status = "open",
            info = new Dictionary<string, object> { [field] = value }
        }, key);
    }

    /// <summary>Locates the actual order table by its Position column in the window's visual tree.</summary>
    private static DataGrid? FindOrderTable(DependencyObject parent)
    {
        if (parent is DataGrid grid && grid.Columns.Any(c => Equals(c.Header, "Position")))
        {
            return grid;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var table = FindOrderTable(VisualTreeHelper.GetChild(parent, index));

            if (table is not null)
            {
                return table;
            }
        }

        return null;
    }
}
