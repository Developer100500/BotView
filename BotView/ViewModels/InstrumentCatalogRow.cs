using BotView.Models;

namespace BotView.ViewModels;

public sealed record InstrumentCatalogRow(Instrument Instrument)
{
    public string Ticker => Instrument.Key.SecurityId;
    public string Name => Instrument.Name;
    public string ClassCode => Instrument.Key.Board;
    public string Type => Instrument.Key.Market switch
    {
        "shares" => "Акции",
        "bonds" => "Облигации",
        "futures" => "Фьючерсы",
        _ => Instrument.Key.Market
    };
    public decimal? LotSize => Instrument.LotSize > 0 ? Instrument.LotSize : null;
    public decimal? PriceStep => Instrument.PriceStep;
}
