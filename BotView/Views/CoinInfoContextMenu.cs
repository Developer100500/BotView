using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BotView.Models;

namespace BotView.Views;

internal static class CoinInfoContextMenu
{
    public static ContextMenu? Create(FrameworkElement target, Action<string> openInfo, Action<string> openOrderBook)
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
        var orderBook = new MenuItem { Header = "Стакан" };
        orderBook.Click += (_, _) => openOrderBook(pair);
        menu.Items.Add(orderBook);
        return menu;
    }
}
