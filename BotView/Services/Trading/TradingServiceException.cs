using BotView.Models;

namespace BotView.Services.Trading;

/// <summary>A provider-independent failure for read/metadata operations. Raw provider exception text,
/// request headers and signed URLs must be normalized by the adapter before crossing this boundary.</summary>
public sealed class TradingServiceException : Exception
{
    public TradingError Error { get; }

    /// <summary>Creates an exception from an already sanitized, stable application error.</summary>
    public TradingServiceException(TradingError error) : base(error.Message) => Error = error;
}
