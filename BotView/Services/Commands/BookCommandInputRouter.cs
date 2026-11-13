using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BotView.Views;
using BotView.Windows;

namespace BotView.Services.Commands;

/// <summary>Routes only foreground WPF input; has no global keyboard hook or trading dependency.</summary>
public sealed class BookCommandInputRouter : IDisposable
{
    private readonly Dictionary<OrderBookLadderControl, BookCommandController> _books = [];
    private readonly InputManager _input = InputManager.Current;
    private readonly HashSet<Key> _handledKeys = [];

    /// <summary>Installs one application-wide preprocessing handler.</summary>
    public BookCommandInputRouter() => _input.PreProcessInput += OnInput;

    /// <summary>Registers a ladder whose owner removes it on closing.</summary>
    public void Register(OrderBookLadderControl ladder, BookCommandController controller) => _books.Add(ladder, controller);

    /// <summary>Releases gesture state and window references when a book closes.</summary>
    public void Unregister(OrderBookLadderControl ladder)
    {
        if (_books.Remove(ladder, out var controller)) controller.Reset();
    }

    /// <summary>Gates text/menu/dialog input before consuming keyboard or pointer gestures.</summary>
    private void OnInput(object sender, PreProcessInputEventArgs e)
    {
        var input = e.StagingItem.Input;
        if (input is not KeyEventArgs && input is not MouseButtonEventArgs && input is not MouseWheelEventArgs) return;
        if (input is KeyEventArgs release && release.RoutedEvent == Keyboard.PreviewKeyUpEvent)
        {
            var physical = release.Key == Key.System ? release.SystemKey : release.Key;
            foreach (var controller in _books.Values) controller.KeyUp(physical);
            release.Handled |= _handledKeys.Remove(physical);
            return;
        }

        var activeWindow = Application.Current?.Windows.OfType<Window>().SingleOrDefault(w => w.IsActive);
        if (activeWindow is not MainWindow && activeWindow is not OrderBookWindow ||
            activeWindow.OwnedWindows.OfType<Window>().Any(w => w.IsVisible && w.IsActive) ||
            IsEditing(Keyboard.FocusedElement as DependencyObject))
        {
            ResetModes();
            return;
        }

        var ladder = Ancestor<OrderBookLadderControl>(Mouse.DirectlyOver as DependencyObject);
        if (ladder is null || !_books.TryGetValue(ladder, out var target) ||
            Window.GetWindow(ladder) is not BotView.Windows.OrderBookWindow ||
            Ancestor<MenuBase>(Mouse.DirectlyOver as DependencyObject) is not null ||
            Ancestor<MenuBase>(Keyboard.FocusedElement as DependencyObject) is not null)
        {
            if (input is MouseButtonEventArgs) ResetModes();
            return;
        }

        try
        {
            if (input is KeyEventArgs key && key.RoutedEvent == Keyboard.PreviewKeyDownEvent &&
                (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Windows)) == 0)
            {
                var physical = key.Key == Key.System ? key.SystemKey : key.Key;
                if (target.KeyDown(physical, key.IsRepeat))
                {
                    key.Handled = true;
                    _handledKeys.Add(physical);
                }
            }
            else if (input is MouseButtonEventArgs click && click.RoutedEvent == Mouse.PreviewMouseDownEvent)
                click.Handled |= target.Click(ladder.PriceAt(click.GetPosition(ladder)), click.ChangedButton, click.ClickCount);
            else if (input is MouseWheelEventArgs wheel && wheel.RoutedEvent == Mouse.PreviewMouseWheelEvent)
                wheel.Handled |= target.Wheel(wheel.Delta);
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            target.Reset();
        }
    }

    /// <summary>Prevents a mode from surviving focus changes into editors or dialogs.</summary>
    private void ResetModes()
    {
        foreach (var controller in _books.Values.Where(c => c.ActiveMode is not null)) controller.Reset();
    }

    /// <summary>Recognizes text and editable selection controls through their visual ancestry.</summary>
    private static bool IsEditing(DependencyObject? element) => Ancestor<TextBoxBase>(element) is not null ||
        Ancestor<PasswordBox>(element) is not null || Ancestor<ComboBox>(element) is not null;

    /// <summary>Walks both visual and logical parents, including popup content.</summary>
    private static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    /// <summary>Detaches the application handler and releases every registered window.</summary>
    public void Dispose()
    {
        _input.PreProcessInput -= OnInput;
        foreach (var controller in _books.Values) controller.Reset();
        _books.Clear();
        _handledKeys.Clear();
    }
}
