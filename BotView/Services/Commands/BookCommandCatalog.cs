using System.Windows.Input;
using CommandBinding = BotView.Models.CommandBinding;

namespace BotView.Services.Commands;

/// <summary>Declares every book command independently of any trading provider.</summary>
public static class BookCommandCatalog
{
    public static IReadOnlyList<BookCommandDefinition> All { get; } = Array.AsReadOnly<BookCommandDefinition>(
    [
        new(BookCommand.InstrumentSettings, "Настройки инструмента", Key.F4),
        new(BookCommand.Volume1, "Рабочий объём 1", Key.D1, Fixed: true),
        new(BookCommand.Volume2, "Рабочий объём 2", Key.D2, Fixed: true),
        new(BookCommand.Volume3, "Рабочий объём 3", Key.D3, Fixed: true),
        new(BookCommand.Volume4, "Рабочий объём 4", Key.D4, Fixed: true),
        new(BookCommand.Volume5, "Рабочий объём 5", Key.D5, Fixed: true),
        new(BookCommand.QuantityUnits, "Единицы рабочего объёма", Key.M),
        new(BookCommand.ClosePosition, "Закрыть позицию", Key.D, FuturesOnly: true),
        new(BookCommand.CancelLimits, "Отменить лимитные заявки", Key.F),
        new(BookCommand.CancelStops, "Отменить стоп-заявки", Key.B),
        new(BookCommand.BestBuy, "Лимитная покупка", Key.A),
        new(BookCommand.BestSell, "Лимитная продажа", Key.S),
        new(BookCommand.MarketBuy, "Купить по рынку", Key.T),
        new(BookCommand.MarketSell, "Продать по рынку", Key.Y),
        new(BookCommand.ReversePosition, "Перевернуть позицию", Key.R, FuturesOnly: true),
        new(BookCommand.Interrupt, "Прервать команду", Key.Q),
        new(BookCommand.Protection, "Установка SL/TP", Key.C, CommandGesture.HoldClick),
        new(BookCommand.Trailing, "Трейлинг SL", Key.N),
        new(BookCommand.StopOrder, "Установка стоп-заявки", Key.V, CommandGesture.ClickOnce),
        new(BookCommand.CloseHeld, "Заявка на закрытие: удержание", Key.LeftAlt, CommandGesture.HoldClick),
        new(BookCommand.CloseOnce, "Заявка на закрытие: один клик", Key.Z, CommandGesture.ClickOnce),
        new(BookCommand.FirstVolume, "Первый объём по рынку", Key.X, CommandGesture.HoldClick),
        new(BookCommand.Level, "Уровень", Key.L, CommandGesture.HoldClick),
        new(BookCommand.SignalLevel, "Сигнальный уровень", Key.K, CommandGesture.HoldClick),
        new(BookCommand.DeleteLevels, "Удалить уровни", Key.O),
        new(BookCommand.FastScroll, "Ускоренная прокрутка", Key.Oem3, CommandGesture.HoldWheel),
        new(BookCommand.PriceStepWheel, "Изменение шага колесом", Key.H, CommandGesture.HoldWheel),
        new(BookCommand.IncreaseStep, "Увеличить шаг", Key.OemPlus),
        new(BookCommand.DecreaseStep, "Уменьшить шаг", Key.OemMinus),
        new(BookCommand.DisplayUnits, "Единицы объёмов стакана", Key.E),
        new(BookCommand.Ruler, "Ручная линейка", Key.G, CommandGesture.TwoPrices),
        new(BookCommand.MarketData, "Маркет-дата подключения", Key.P),
        new(BookCommand.AllMarketData, "Маркет-дата всех подключений", Key.None),
        new(BookCommand.ExpandBook, "Развернуть стакан/график", Key.None),
        new(BookCommand.NextFuture, "Следующий фьючерс", Key.None, FuturesOnly: true),
        new(BookCommand.PreviousFuture, "Предыдущий фьючерс", Key.None, FuturesOnly: true)
    ]);

    /// <summary>Builds independent defaults, leaving reversal and unassigned commands disabled.</summary>
    public static CommandBinding[] Defaults() => All.Select(d => new CommandBinding(d.Id, d.Key,
        d.Key != Key.None && d.Id != BookCommand.ReversePosition)).ToArray();
}
