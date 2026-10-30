using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using BotView.Services;
using BotView.ViewModels;

namespace BotView.Windows;

public partial class CoinInfoWindow : Window
{
    private readonly CoinInfoViewModel _viewModel;

    public CoinInfoWindow(string pair, ICoinInfoProvider provider)
    {
        InitializeComponent();
        _viewModel = new CoinInfoViewModel(pair, provider);
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (!CoinGeckoInfoProvider.IsWebUrl(e.Uri?.AbsoluteUri)) return;
        try { Process.Start(new ProcessStartInfo(e.Uri!.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the link. {ex.Message}", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
