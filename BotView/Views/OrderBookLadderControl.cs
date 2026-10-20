using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BotView.Models;
using BotView.ViewModels;

namespace BotView.Views;

/// <summary>Draws only visible price steps, keeping both memory and rendering bounded.</summary>
public sealed class OrderBookLadderControl : FrameworkElement
{
    public const double RowHeight = 22;
    private static readonly Brush AskBackground = Frozen("#FCE4E4");
    private static readonly Brush BidBackground = Frozen("#E1F4E6");
    private static readonly Brush NeutralBackground = Frozen("#111827");
    private static readonly Brush AskVolume = Frozen("#F2ABAB");
    private static readonly Brush BidVolume = Frozen("#92D7A4");
    private static readonly Brush Ink = Frozen("#172033");
    private static readonly Brush MutedInk = Frozen("#64748B");
    private static readonly Pen BestPen = CreateBestPen();
    private IReadOnlyList<OrderBookRow> _rows = [];
    private OrderBookViewModel? _viewModel;

    public OrderBookLadderControl()
    {
        ClipToBounds = true;
        Focusable = true;
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnChanged;
            _viewModel = DataContext as OrderBookViewModel;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnChanged;
            InvalidateVisual();
        };
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(NeutralBackground, null, new Rect(RenderSize));
        _rows = _viewModel?.GetRows((int)(ActualHeight / RowHeight)) ?? [];
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var y = i * RowHeight;
            var rect = new Rect(0, y, ActualWidth, RowHeight);
            var spread = row.Side == OrderBookSide.Spread;
            dc.DrawRectangle(row.Side switch
            {
                OrderBookSide.Ask => AskBackground,
                OrderBookSide.Bid => BidBackground,
                _ => NeutralBackground
            }, null, rect);
            if (row.VolumeFraction > 0)
                dc.DrawRectangle(row.Side == OrderBookSide.Ask ? AskVolume : BidVolume, null,
                    new Rect(0, y + 1, ActualWidth * row.VolumeFraction, RowHeight - 2));
            if (row.IsBest && ActualWidth > 1)
                dc.DrawRectangle(null, BestPen, new Rect(.5, y + .5, ActualWidth - 1, RowHeight - 1));
            var brush = spread ? Brushes.LightGray : row.HasDepth ? Ink : MutedInk;
            Text(dc, OrderBookViewModel.Format(row.Price), ActualWidth * .34, y, ActualWidth * .34, brush, row.IsBest, dpi);
            if (!spread)
            {
                Text(dc, row.HasDepth ? OrderBookViewModel.Format(row.Amount) : "—", 0, y, ActualWidth * .34, brush, false, dpi);
                Text(dc, row.HasDepth ? OrderBookViewModel.Format(row.Cost) : "—", ActualWidth * .68, y, ActualWidth * .32, brush, false, dpi);
            }
        }
    }

    private static void Text(DrawingContext dc, string text, double x, double y, double width, Brush brush, bool bold, double dpi)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            12, brush, dpi)
        {
            MaxTextWidth = Math.Max(1, width - 10),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Right
        };
        dc.DrawText(formatted, new Point(x + 4, y + 3));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        _viewModel?.Scroll(Math.Sign(e.Delta) * 3);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var index = (int)(e.GetPosition(this).Y / RowHeight);
        if (index >= 0 && index < _rows.Count) _viewModel?.SelectPrice(_rows[index].Price);
        Focus();
        e.Handled = true;
    }

    private static Brush Frozen(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    private static Pen CreateBestPen()
    {
        var pen = new Pen(Frozen("#475569"), 1);
        pen.Freeze();
        return pen;
    }
}
