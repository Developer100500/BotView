using System.Windows;
using System.Windows.Controls;

namespace BotView.Windows;

/// <summary>Requires an explicit contract selection before opening a derivative order book.</summary>
public sealed class ContractSelectionWindow : Window
{
    private readonly ComboBox _contracts;
    public string? SelectedContract => _contracts.SelectedItem as string;

    /// <summary>Shows exchange-supplied symbols, including settlement currency and expiry when applicable.</summary>
    public ContractSelectionWindow(IReadOnlyList<string> contracts)
    {
        Title = "Select USDT futures contract";
        Width = 430;
        Height = 220;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _contracts = new ComboBox { ItemsSource = contracts, SelectedIndex = 0, Margin = new Thickness(0, 12, 0, 12) };
        var open = new Button { Content = "Open order book", Padding = new Thickness(10), IsDefault = true };
        open.Click += (_, _) => DialogResult = SelectedContract is not null;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Choose the contract. A symbol with a date is a delivery future.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_contracts);
        panel.Children.Add(open);
        Content = panel;
    }
}
