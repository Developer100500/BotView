using System.Globalization;

namespace BotView.Models;

/// <summary>A spot pair's rolling 24-hour price change.</summary>
public sealed record MarketMover(string Symbol, double ChangePercent)
{
    public string ChangeText => ChangePercent.ToString("+0.00;-0.00;0.00", CultureInfo.GetCultureInfo("ru-RU")) + "%";
    public string ChangeColor => ChangePercent < 0 ? "#EF4444" : "#22C55E";
}
