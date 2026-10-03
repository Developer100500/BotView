using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using BotView.Interfaces;
using BotView.Models;
using BotView.Services;

namespace BotView.ViewModels;

public sealed class FuturesQuoteViewModel : INotifyPropertyChanged, IDisposable
{
    public const string Symbol = "ES=F";

    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("ru-RU");
    private readonly IQuoteProvider _provider;
    private readonly CancellationTokenSource _lifetime = new();
    private int _refreshing;
    private bool _disposed;
    private string _priceText = "—";
    private string _directionText = "Загрузка…";
    private string _changeText = "—";
    private string _statusText = "Загрузка котировки…";
    private string _changeColor = "#94A3B8";

    public FuturesQuoteViewModel(IQuoteProvider provider) =>
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public string PriceText { get => _priceText; private set => Set(ref _priceText, value); }
    //public string DirectionText { get => _directionText; private set => Set(ref _directionText, value); }
    public string ChangeText { get => _changeText; private set => Set(ref _changeText, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string ChangeColor { get => _changeColor; private set => Set(ref _changeColor, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task RefreshAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshing, 1) != 0)
            return;

        try
        {
            var quote = await _provider.GetQuoteAsync(Symbol, _lifetime.Token);
            if (_disposed) return;

            PriceText = quote.LastPrice.ToString("N2", DisplayCulture);
            var morningMovement = MorningMovementCalculator.Calculate(quote);
            var movement = morningMovement ?? (quote.PreviousClose is > 0
                ? new PriceMovement(quote.PreviousClose.Value, quote.LastPrice, quote.AsOf)
                : null);
            if (movement is null)
            {
                StatusText = $"{quote.Source} (Нет цены сравнения)";
                ChangeText = "—";
                ChangeColor = "#94A3B8";
            }
            else
            {
                ChangeColor = movement.ChangePoints > 0 ? "#22C55E" :
                    movement.ChangePoints < 0 ? "#EF4444" : "#94A3B8";
                ChangeText = $"{movement.ChangePoints.ToString("+0.00;-0.00;0.00", DisplayCulture)} п. " +
                    $"({movement.ChangePercent.ToString("+0.00;-0.00;0.00", DisplayCulture)}%)";
            }

            //var localTime = quote.AsOf.ToLocalTime().ToString("dd.MM HH:mm", DisplayCulture);
            var source = quote.IsDelayed ? $"{quote.Source} (с задержкой)" : quote.Source;
            //var reference = morningMovement is not null
            //    ? $"От 09:30 NY {movement!.ReferencePrice.ToString("N2", DisplayCulture)}"
            //    : movement is not null
            //        ? $"От предыдущего закрытия {movement.ReferencePrice.ToString("N2", DisplayCulture)}"
            //        : "Нет цены для сравнения";
            StatusText = $"{source}";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Window closed while a request was in progress.
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            PriceText = "—";
            //DirectionText = "Котировка недоступна";
            ChangeText = "—";
            ChangeColor = "#94A3B8";
            StatusText = $"Ошибка получения котировки: {ex.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
