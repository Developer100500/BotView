namespace BotView.ViewModels
{
    public sealed class ExchangeOption
    {
        public ExchangeOption(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }

        public string Id { get; }
        public string DisplayName { get; }
    }
}
