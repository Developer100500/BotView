using System.Windows;
using System.Windows.Controls;

namespace BotView.Views;

/// <summary>Measures compact controls before the ladder so expanding settings cannot push actions out of view.</summary>
public sealed class OrderBookLayoutGrid : Grid
{
    private FrameworkElement[] _fixedSections = [];
    private Expander? _depthSettings;
    private ScrollViewer? _tradeScroll;

    /// <summary>Identifies the content-sized sections and the two independently scrollable panels.</summary>
    internal void Configure(FrameworkElement header, Expander depthSettings, FrameworkElement status,
        FrameworkElement actions, ScrollViewer tradeScroll)
    {
        _fixedSections = [header, depthSettings, status, actions];
        _depthSettings = depthSettings;
        _tradeScroll = tradeScroll;
    }

    /// <summary>Bounds the trade panel using the available height, preserving four rows and pinned Buy/Sell.</summary>
    protected override Size MeasureOverride(Size constraint)
    {
        if (_tradeScroll is not null && _depthSettings is not null && double.IsFinite(constraint.Height))
        {
            _depthSettings.MaxHeight = Math.Max(24, Math.Min(150, constraint.Height * .24));

            foreach (var section in _fixedSections)
            {
                section.Measure(new Size(constraint.Width, double.PositiveInfinity));
            }

            var available = constraint.Height - _fixedSections.Sum(s => s.DesiredSize.Height) - 24 - 4 * OrderBookLadderControl.RowHeight;
            _tradeScroll.MaxHeight = Math.Max(60, Math.Min(constraint.Height * .52, available));
        }

        return base.MeasureOverride(constraint);
    }
}
