using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BotView.Services.Commands;
using Microsoft.Win32;
using CommandBinding = BotView.Models.CommandBinding;

namespace BotView.Windows;

/// <summary>Edits one instrument's draft parameters without creating any protection or orders.</summary>
public sealed class InstrumentCommandSettingsWindow : Window
{
    private readonly BookCommandSettingsStore _store;
    private readonly string _connection;
    private readonly InstrumentKey _instrument;
    private readonly InstrumentCommandSettings _draft;
    private readonly List<Action> _readEditors = [];
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(8) };

    /// <summary>Builds explicit parameter pages; editors update only the detached draft on Save.</summary>
    public InstrumentCommandSettingsWindow(BookCommandSettingsStore store, string connection, Instrument instrument)
    {
        _store = store;
        _connection = connection;
        _instrument = instrument.Key;
        var saved = store.Get(connection, instrument.Key);
        _draft = saved.Copy() with
        {
            StopLoss = saved.StopLoss with { }, TakeProfit = saved.TakeProfit with { },
            Trailing = saved.Trailing with { }, Stop = saved.Stop with { }
        };

        Title = $"Настройки инструмента · {instrument.Symbol}";
        Width = 680;
        Height = 720;
        MinWidth = 540;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.WhiteSmoke;
        Foreground = Brushes.Black;
        FontFamily = new FontFamily("Segoe UI");
        var root = new DockPanel { Margin = new(12), Background = Brushes.WhiteSmoke };
        Content = root;
        var notice = new TextBlock
        {
            Text = $"{connection} · {instrument.Key.Board} · {instrument.Key.SecurityId}\n" +
                "Настройки SL/TP и трейлинга. Исполнение не реализовано. Возможности сервера неизвестны.",
            TextWrapping = TextWrapping.Wrap, Margin = new(8), Foreground = Brushes.DarkGoldenrod
        };
        DockPanel.SetDock(notice, Dock.Top);
        root.Children.Add(notice);
        var bottom = new StackPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        bottom.Children.Add(_status);
        var buttons = new WrapPanel();
        bottom.Children.Add(buttons);
        AddButton(buttons, "Сохранить", Save);
        AddButton(buttons, "Отмена", Close);
        AddButton(buttons, "Горячие клавиши", () => new HotkeysSettingsWindow(store) { Owner = this }.ShowDialog());
        var tabs = new TabControl();
        root.Children.Add(tabs);

        var volumes = Page(tabs, "Объёмы");
        volumes.Children.Add(new TextBlock
        {
            Text = $"Количество: {instrument.TradingRules?.QuantityUnit.ToString() ?? "по метаданным"}; " +
                $"валюта суммы: {instrument.TradingRules?.SettlementCurrency ?? instrument.Currency ?? "неизвестна"}.",
            TextWrapping = TextWrapping.Wrap, Margin = new(6)
        });
        for (var index = 0; index < 5; index++)
        {
            Preset(volumes, $"Количество {index + 1}", _draft.Quantities, index);
            Preset(volumes, $"Денежная сумма {index + 1}", _draft.Budgets, index);
        }
        Fields(volumes, _draft, ("Monetary", "Ввод денежной суммы"));

        var protection = Page(tabs, "SL / TP");
        Protection(protection, "Stop Loss", _draft.StopLoss);
        Protection(protection, "Take Profit", _draft.TakeProfit);
        var trailing = Page(tabs, "Трейлинг");
        Fields(trailing, _draft.Trailing, ("Enabled", "Включён в настройках"), ("Distance", "Расстояние"),
            ("Step", "Шаг перемещения"), ("Activation", "Порог активации"), ("Unit", "Единицы расстояния"));
        var stop = Page(tabs, "Стоп-заявки");
        Fields(stop, _draft.Stop, ("Side", "Направление"), ("Execution", "Тип исполнения"),
            ("TriggerPrice", "Цена активации"), ("LimitPrice", "Лимитная цена"), ("Offset", "Смещение вместо цены"),
            ("Volume", "Источник объёма"), ("Location", "Предполагаемое место исполнения"));
        var other = Page(tabs, "Другие режимы");
        Fields(other, _draft, ("ClosingSide", "Сторона закрываемой позиции"), ("ClosingVolume", "Объём закрытия"),
            ("FirstVolumeWorkingLimit", "Ограничивать первым рабочим объёмом"),
            ("FirstVolumePositionLimit", "Ограничивать размером позиции"), ("LevelLabel", "Подпись уровня"),
            ("LevelColor", "Цвет уровня"), ("SignalNotification", "Уведомление сигнального уровня"),
            ("FastScrollSteps", "Шагов ускоренной прокрутки"), ("StepIncrement", "Изменение множителя шага"),
            ("RulerUnits", "Единицы линейки"), ("MarketDataSource", "Источник маркет-даты"),
            ("MarketDataScope", "Область маркет-даты"));
    }

    /// <summary>Adds the same typed controls to independent SL and TP records.</summary>
    private void Protection(Panel panel, string title, ProtectionSettings settings)
    {
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, Margin = new(6, 16, 6, 6) });
        Fields(panel, settings, ("Enabled", "Включён в настройках"), ("Placement", "Способ установки"),
            ("Distance", "Расстояние"), ("Unit", "Единицы"), ("Location", "Предполагаемое место исполнения"),
            ("Volume", "Источник объёма"));
    }

    /// <summary>Creates a scrollable page so all fields remain usable in a resized dialog.</summary>
    private static StackPanel Page(TabControl tabs, string title)
    {
        var panel = new StackPanel { Margin = new(8) };
        tabs.Items.Add(new TabItem { Header = title, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        return panel;
    }

    /// <summary>Registers explicitly selected properties rather than exposing arbitrary model members.</summary>
    private void Fields(Panel panel, object source, params (string Property, string Label)[] fields)
    {
        foreach (var field in fields)
        {
            var property = source.GetType().GetProperty(field.Property)!;
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            FrameworkElement editor;
            if (type == typeof(bool))
            {
                var check = new CheckBox { IsChecked = (bool)property.GetValue(source)!, VerticalAlignment = VerticalAlignment.Center };
                _readEditors.Add(() => property.SetValue(source, check.IsChecked == true));
                editor = check;
            }
            else if (type.IsEnum)
            {
                var combo = new ComboBox
                {
                    ItemsSource = Enum.GetValues(type).Cast<object>().Select(v => new EnumOption(v, EnumLabel(v))).ToArray(),
                    DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedValue = property.GetValue(source)
                };
                _readEditors.Add(() => property.SetValue(source, combo.SelectedValue));
                editor = combo;
            }
            else
            {
                var text = new TextBox { Text = Convert.ToString(property.GetValue(source), CultureInfo.InvariantCulture) ?? "" };
                _readEditors.Add(() => property.SetValue(source, ReadValue(text.Text, property)));
                editor = text;
            }
            Row(panel, field.Label, editor);
        }
    }

    private sealed record EnumOption(object Value, string Label);

    /// <summary>Uses user-facing terms while storing stable typed enum values.</summary>
    private static string EnumLabel(object value) => value switch
    {
        DistanceUnit.Ticks => "Шаги цены", DistanceUnit.Percent => "Проценты",
        ExecutionLocation.Application => "Приложение", ExecutionLocation.Server => "Сервер",
        VolumeSource.Position => "Полная позиция", VolumeSource.WorkingVolume => "Рабочий объём",
        ProtectionPlacement.Manual => "Вручную", ProtectionPlacement.Automatic => "Автоматически",
        StopExecution.Market => "Stop Market", StopExecution.Limit => "Stop Limit",
        OrderSide.Buy => "Покупка", OrderSide.Sell => "Продажа",
        PositionSide.Long => "Long", PositionSide.Short => "Short",
        RulerUnit.Price => "Цена", RulerUnit.Ticks => "Шаги цены", RulerUnit.Percent => "Проценты", RulerUnit.Volume => "Объём",
        MarketDataScope.Connection => "Подключение", MarketDataScope.AllConnections => "Все подключения",
        _ => value.ToString() ?? ""
    };

    /// <summary>Parses numbers consistently while allowing optional prices and distances to stay unknown.</summary>
    private static object? ReadValue(string text, PropertyInfo property)
    {
        if (property.PropertyType == typeof(string)) return text.Trim();
        if (string.IsNullOrWhiteSpace(text) && Nullable.GetUnderlyingType(property.PropertyType) is not null) return null;
        return Convert.ChangeType(text.Trim().Replace(',', '.'), Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType,
            CultureInfo.InvariantCulture);
    }

    /// <summary>Keeps all five slots editable without conflating blank values with zero.</summary>
    private void Preset(Panel panel, string label, decimal?[] values, int index)
    {
        var editor = new TextBox { Text = values[index]?.ToString(CultureInfo.InvariantCulture) ?? "" };
        Row(panel, label, editor);
        _readEditors.Add(() => values[index] = string.IsNullOrWhiteSpace(editor.Text) ? null :
            decimal.Parse(editor.Text.Replace(',', '.'), CultureInfo.InvariantCulture));
    }

    /// <summary>Aligns labels and controls without placing implementation details in the form.</summary>
    private static void Row(Panel panel, string label, FrameworkElement editor)
    {
        var row = new Grid { Margin = new(4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new(210) });
        row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) });
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        panel.Children.Add(row);
    }

    /// <summary>Validates every editor before updating the instrument's persisted settings.</summary>
    private void Save()
    {
        if (TrySave()) DialogResult = true;
    }

    /// <summary>Validates the detached editor before persistence, independently of modal window lifetime.</summary>
    internal bool TrySave()
    {
        try
        {
            foreach (var read in _readEditors) read();
            BookCommandSettingsStore.Validate(_draft);
            _store.Put(_connection, _instrument, _draft);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _status.Text = ex.Message;
            return false;
        }
    }

    /// <summary>Adds a consistently spaced action to settings dialogs.</summary>
    internal static void AddButton(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Padding = new(10, 6, 10, 6), Margin = new(4) };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }
}

/// <summary>Edits global bindings and captures physical keys without dispatching book commands.</summary>
public sealed class HotkeysSettingsWindow : Window
{
    private readonly BookCommandSettingsStore _store;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(6) };
    private readonly Dictionary<BookCommand, (CheckBox Enabled, Button Key)> _editors = [];
    private CommandBinding[] _bindings;
    private BookCommand? _capture;

    /// <summary>Loads a detached binding draft; only successful saves or imports affect running books.</summary>
    public HotkeysSettingsWindow(BookCommandSettingsStore store)
    {
        _store = store;
        _bindings = store.Load().Bindings;
        Title = "Настройки → Горячие клавиши";
        Width = 800;
        Height = 740;
        MinWidth = 650;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.WhiteSmoke;
        Foreground = Brushes.Black;
        FontFamily = new FontFamily("Segoe UI");
        var root = new DockPanel { Margin = new(12), Background = Brushes.WhiteSmoke };
        Content = root;
        var footer = new StackPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        footer.Children.Add(_status);
        var buttons = new WrapPanel();
        footer.Children.Add(buttons);
        InstrumentCommandSettingsWindow.AddButton(buttons, "Сохранить", () => Run(() => { Persist(); DialogResult = true; }));
        InstrumentCommandSettingsWindow.AddButton(buttons, "Сброс", () => { _bindings = BookCommandCatalog.Defaults(); BuildRows(); });
        InstrumentCommandSettingsWindow.AddButton(buttons, "Импорт", Import);
        InstrumentCommandSettingsWindow.AddButton(buttons, "Экспорт", Export);
        InstrumentCommandSettingsWindow.AddButton(buttons, "Отмена", Close);
        root.Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        PreviewKeyDown += CaptureKey;
        BuildRows();
    }

    /// <summary>Rebuilds controls after a reset or validated import.</summary>
    private void BuildRows()
    {
        _capture = null;
        _rows.Children.Clear();
        _editors.Clear();
        foreach (var definition in BookCommandCatalog.All)
        {
            var binding = _bindings.Single(b => b.Command == definition.Id);
            var row = new DockPanel { Margin = new(4) };
            var enabled = new CheckBox { IsChecked = binding.Enabled, IsEnabled = !definition.Fixed, Width = 28, VerticalAlignment = VerticalAlignment.Center };
            var key = new Button { Content = binding.Key.ToString(), Width = 100, IsEnabled = !definition.Fixed, Margin = new(6, 0, 6, 0) };
            var clear = new Button { Content = "Очистить", Width = 85, IsEnabled = !definition.Fixed };
            DockPanel.SetDock(clear, Dock.Right);
            DockPanel.SetDock(key, Dock.Right);
            row.Children.Add(enabled);
            row.Children.Add(clear);
            row.Children.Add(key);
            row.Children.Add(new TextBlock { Text = $"{definition.Label} · {GestureLabel(definition.Gesture)}", VerticalAlignment = VerticalAlignment.Center });
            _rows.Children.Add(row);
            _editors.Add(definition.Id, (enabled, key));
            key.Click += (_, _) => { _capture = definition.Id; _status.Text = "Нажмите клавишу. Esc — отмена назначения."; };
            clear.Click += (_, _) => SetKey(definition.Id, Key.None);
        }
    }

    /// <summary>Explains how a binding is invoked without exposing framework terminology.</summary>
    private static string GestureLabel(CommandGesture gesture) => gesture switch
    {
        CommandGesture.Press => "нажатие", CommandGesture.HoldClick => "удержание + клик",
        CommandGesture.ClickOnce => "нажатие + клик", CommandGesture.HoldWheel => "удержание + колесо",
        CommandGesture.TwoPrices => "нажатие + две цены", _ => ""
    };

    /// <summary>Captures physical WPF keys, including Alt, without executing a command.</summary>
    private void CaptureKey(object sender, KeyEventArgs e)
    {
        if (_capture is not { } command) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { _capture = null; _status.Text = "Назначение отменено."; return; }
        if (e.IsRepeat) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Windows)) != 0 ||
            key is Key.LeftCtrl or Key.RightCtrl or Key.LWin or Key.RWin or Key.LeftShift or Key.RightShift or
            Key.RightAlt or Key.System or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            _status.Text = "Выберите обычную клавишу или LeftAlt. Сочетания с Ctrl/Win не назначаются.";
            return;
        }
        if (_bindings.Any(b => b.Command != command && b.Key == key))
        {
            _status.Text = "Клавиша занята. Прежнее назначение сохранено.";
            _capture = null;
            return;
        }
        SetKey(command, key);
        _capture = null;
        _status.Text = "Назначение изменено в черновике.";
    }

    /// <summary>Updates the draft while making cleared bindings explicitly inactive.</summary>
    private void SetKey(BookCommand command, Key key)
    {
        var index = Array.FindIndex(_bindings, b => b.Command == command);
        _bindings[index] = _bindings[index] with { Key = key, Enabled = key != Key.None };
        _editors[command].Key.Content = key.ToString();
        _editors[command].Enabled.IsChecked = key != Key.None;
    }

    /// <summary>Merges bindings with the latest instrument preferences before atomic persistence.</summary>
    private void Persist()
    {
        var bindings = _bindings.Select(b => b with { Enabled = _editors[b.Command].Enabled.IsChecked == true }).ToArray();
        _store.Save(_store.Load() with { Bindings = bindings });
    }

    /// <summary>Imports the entire validated document and refreshes the editor.</summary>
    private void Import()
    {
        var dialog = new OpenFileDialog { Filter = "Настройки JSON|*.json" };
        if (dialog.ShowDialog(this) == true) Run(() => { _store.Import(dialog.FileName); _bindings = _store.Load().Bindings; BuildRows(); _status.Text = "Импорт выполнен."; });
    }

    /// <summary>Exports after saving the displayed assignments so the file matches the editor.</summary>
    private void Export()
    {
        var dialog = new SaveFileDialog { Filter = "Настройки JSON|*.json", FileName = "book-commands.json" };
        if (dialog.ShowDialog(this) == true) Run(() => { Persist(); _store.Export(dialog.FileName); _status.Text = "Экспорт выполнен."; });
    }

    /// <summary>Reports file and validation errors while leaving the dialog available for correction.</summary>
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _status.Text = ex.Message;
        }
    }
}
