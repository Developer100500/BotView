namespace BotView.Models
{
    public class TradingPairModel
    {
        public string Symbol { get; set; }
        public bool IsSelected { get; set; }

        public TradingPairModel(string symbol, bool isSelected = false)
        {
            Symbol = symbol;
            IsSelected = isSelected;
        }
    }
}
