using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BotView.Models;

namespace BotView.Views;

internal static class CoinInfoContextMenu
{
    public static ContextMenu? Create(FrameworkElement target, Action<string> openInfo,
            Action<string, TradingMarket> openOrderBook)
    {
        var pair = target.DataContext switch
        {
            TradingPairModel favorite => favorite.Symbol,
            MarketMover mover => mover.Symbol,
            string symbol => symbol,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(pair)) return null;

        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = PlacementMode.MousePoint,
            Background = new SolidColorBrush(Color.FromRgb(24, 34, 53)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(38, 52, 73))
        };

        var info = new MenuItem { Header = "Info" };
        info.Click += (_, _) => openInfo(pair);
        menu.Items.Add(info);

        var spotOrderBook = new MenuItem { Header = "Spot order book" };
        spotOrderBook.Click += (_, _) => openOrderBook(pair, TradingMarket.Spot);
        menu.Items.Add(spotOrderBook);

        var futuresOrderBook = new MenuItem { Header = "Futures order book" };
        futuresOrderBook.Click += (_, _) => openOrderBook(pair, TradingMarket.UsdtFutures);
        menu.Items.Add(futuresOrderBook);

        return menu;
    }
}
