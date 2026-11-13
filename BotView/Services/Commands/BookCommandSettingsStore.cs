using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using System.Windows.Media;
using CommandBinding = BotView.Models.CommandBinding;

namespace BotView.Services.Commands;

/// <summary>Persists validated, detached settings; invalid imports never replace the current document.</summary>
public sealed class BookCommandSettingsStore
{
    private readonly string _path;
    private readonly object _sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public event Action? Changed;

    /// <summary>Uses an injectable file so tests never write user preferences.</summary>
    public BookCommandSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BotView", "book-commands.json");
    }

    /// <summary>Returns defaults only when no document exists; damaged files are never silently overwritten.</summary>
    public BookCommandDocument Load()
    {
        lock (_sync)
        {
            return File.Exists(_path) ? Parse(File.ReadAllText(_path)) : new(1, BookCommandCatalog.Defaults(), []);
        }
    }

    /// <summary>Validates the complete document before replacing a file on the same volume.</summary>
    public void Save(BookCommandDocument document)
    {
        Validate(document);

        lock (_sync)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".book-commands-{Guid.NewGuid():N}.tmp");

            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
                File.Move(temporary, _path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Loads one instrument's preferences independently of the selected account.</summary>
    public InstrumentCommandSettings Get(string connection, InstrumentKey instrument) =>
        Load().Instruments.SingleOrDefault(i => i.Connection == connection && i.Instrument == instrument)?.Settings.Copy() ?? new();

    /// <summary>Preserves other instruments and global bindings when updating one book.</summary>
    public void Put(string connection, InstrumentKey instrument, InstrumentCommandSettings settings)
    {
        var document = Load();
        var entries = document.Instruments.Where(i => i.Connection != connection || i.Instrument != instrument)
            .Append(new InstrumentSettingsEntry(connection, instrument, settings.Copy())).ToArray();
        Save(document with { Instruments = entries });
    }

    /// <summary>Imports only after parsing and validating all bindings and instruments.</summary>
    public void Import(string path) => Save(Parse(File.ReadAllText(path)));

    /// <summary>Exports only command preferences, never profiles or credentials.</summary>
    public void Export(string path) => File.WriteAllText(path, JsonSerializer.Serialize(Load(), JsonOptions));

    /// <summary>Rejects unknown formats, missing rows and invalid numeric or enum settings.</summary>
    private static BookCommandDocument Parse(string json)
    {
        var document = JsonSerializer.Deserialize<BookCommandDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("Пустой документ настроек.");
        Validate(document);
        return document;
    }

    /// <summary>Applies the same validation to imported files and settings edited by the UI.</summary>
    public static void Validate(BookCommandDocument document)
    {
        if (document.Version != 1 || document.Bindings is null || document.Instruments is null ||
            document.Bindings.Length != BookCommandCatalog.All.Count || document.Bindings.Any(b => b is null) ||
            document.Bindings.Select(b => b.Command).Distinct().Count() != BookCommandCatalog.All.Count)
            throw new InvalidDataException("Неподдерживаемый или неполный документ настроек.");

        foreach (var binding in document.Bindings)
        {
            var definition = BookCommandCatalog.All.SingleOrDefault(d => d.Id == binding.Command);
            if (definition is null || !Enum.IsDefined(binding.Key) || binding.Key is Key.LeftCtrl or Key.RightCtrl or
                Key.LWin or Key.RWin or Key.LeftShift or Key.RightShift or Key.RightAlt or Key.System or Key.ImeProcessed or Key.DeadCharProcessed ||
                binding.Enabled && binding.Key == Key.None ||
                definition.Fixed && (binding.Key != definition.Key || !binding.Enabled))
                throw new InvalidDataException("Некорректное назначение клавиши.");
        }

        if (document.Bindings.Where(b => b.Key != Key.None).GroupBy(b => b.Key).Any(g => g.Count() > 1))
            throw new InvalidDataException("Клавиша уже назначена другой команде.");

        if (document.Instruments.Any(i => i is null || string.IsNullOrWhiteSpace(i.Connection) || i.Instrument is null ||
            string.IsNullOrWhiteSpace(i.Instrument.Venue) || string.IsNullOrWhiteSpace(i.Instrument.Market) ||
            string.IsNullOrWhiteSpace(i.Instrument.SecurityId)) ||
            document.Instruments.GroupBy(i => (i.Connection, i.Instrument)).Any(g => g.Count() > 1))
            throw new InvalidDataException("Некорректный или повторяющийся инструмент.");

        foreach (var entry in document.Instruments) Validate(entry.Settings);
    }

    /// <summary>Accepts unfinished optional fields but requires valid distances for enabled protection.</summary>
    public static void Validate(InstrumentCommandSettings settings)
    {
        if (settings is null || settings.Quantities is not { Length: 5 } || settings.Budgets is not { Length: 5 } ||
            settings.Quantities.Concat(settings.Budgets).Any(v => v is <= 0) || settings.SelectedVolume is < 0 or > 4 ||
            settings.StopLoss is null || settings.TakeProfit is null || settings.Trailing is null || settings.Stop is null ||
            settings.FastScrollSteps <= 0 || settings.StepIncrement <= 0 ||
            !Enum.IsDefined(settings.ClosingSide) || !Enum.IsDefined(settings.ClosingVolume) ||
            !Enum.IsDefined(settings.RulerUnits) || !Enum.IsDefined(settings.MarketDataScope) ||
            settings.LevelLabel is null || settings.MarketDataSource is null)
            throw new InvalidDataException("Проверьте объёмы и параметры инструмента.");

        foreach (var protection in new[] { settings.StopLoss, settings.TakeProfit })
        {
            if (!Enum.IsDefined(protection.Placement) || !Enum.IsDefined(protection.Unit) ||
                !Enum.IsDefined(protection.Location) || !Enum.IsDefined(protection.Volume) ||
                protection.Distance is <= 0 || protection.Enabled && protection.Distance is null)
                throw new InvalidDataException("Для включённых SL/TP задайте положительное расстояние.");
        }

        var trailing = settings.Trailing;
        var stop = settings.Stop;
        if (!Enum.IsDefined(trailing.Unit) || trailing.Distance is <= 0 || trailing.Step is <= 0 || trailing.Activation < 0 ||
            trailing.Enabled && (trailing.Distance is null || trailing.Step is null) ||
            !Enum.IsDefined(stop.Side) || !Enum.IsDefined(stop.Execution) || !Enum.IsDefined(stop.Volume) ||
            !Enum.IsDefined(stop.Location) || stop.TriggerPrice is <= 0 || stop.LimitPrice is <= 0 || stop.Offset is < 0 ||
            stop.LimitPrice is not null && stop.Offset is not null)
            throw new InvalidDataException("Проверьте трейлинг и параметры стоп-заявки.");

        try
        {
            if (string.IsNullOrWhiteSpace(settings.LevelColor) || ColorConverter.ConvertFromString(settings.LevelColor) is not Color)
                throw new FormatException("Введите цвет уровня.");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("Некорректный цвет уровня.", ex);
        }
    }
}
