namespace BotView.Database.Models
{
    /// <summary>Модель торговой пары</summary>
    public class TradingPair
    {
        public int Id { get; set; }
        public string Symbol { get; set; } = string.Empty;
    }
}

