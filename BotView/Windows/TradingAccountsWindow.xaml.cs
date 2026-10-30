using System.Windows;
using BotView.Services;
using BotView.Models;
using BotView.ViewModels;

namespace BotView.Windows;

public partial class TradingAccountsWindow : Window
{
    private readonly TradingAccountsViewModel _viewModel;
    private bool _resetting; // This is used to prevent the editor from being reset while the user is typing.

    public TradingAccountsWindow(
        ITradingAccountStore store,
        ICredentialStore credentials,
        IAccountConnectionVerifier verifier) // This is used to verify the connection to the trading account.
    {
        InitializeComponent();
        _viewModel = new(store, credentials, verifier);
        DataContext = _viewModel;
        _viewModel.EditorResetRequested += ClearEditor;
    }

    private void CredentialsChanged(object sender, RoutedEventArgs e)
    {
        if (_resetting || _viewModel is null || ApiKeyInput is null || SecretInput is null) return;
        // Read lengths without copying passwords into bindable view-model properties.
        using var key = ApiKeyInput.SecurePassword;
        using var secret = SecretInput.SecurePassword;
        _viewModel.SetCredentialInputState(key.Length > 0 || secret.Length > 0, key.Length > 0 && secret.Length > 0);
    }

    private void SaveAccount(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanSave) return;
        using var key = ApiKeyInput.SecurePassword;
        using var secret = SecretInput.SecurePassword;
        try
        {
            using var replacement = key.Length > 0 && secret.Length > 0
                ? new AccountCredentials(ApiKeyInput.Password, SecretInput.Password) : null;
            _viewModel.Save(replacement);
        }
        catch (ArgumentException) { _viewModel.ReportInvalidCredentials(); }
    }

    private void ReplaceKeys(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanReplaceCredentials) return;
        ClearEditor();
        _viewModel.SetCredentialInputState(false, false);
        ApiKeyInput.Focus();
    }

    private void ClearEditor()
    {
        _resetting = true;
        try { ApiKeyInput.Clear(); SecretInput.Clear(); }
        finally { _resetting = false; }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        _viewModel.EditorResetRequested -= ClearEditor;
        base.OnClosed(e);
    }
}
