namespace BotView.Database.Models
{
    /// <summary>Связь пользователя с избранными торговыми парами</summary>
    public class UserFavoritePair
    {
        public int UserId { get; set; }
        public int TradingPairId { get; set; }
    }
}

