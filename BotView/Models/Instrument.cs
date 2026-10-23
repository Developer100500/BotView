namespace BotView.Models;

/// <summary>Identifies a listing, independently of the API used to read it.</summary>
public sealed record InstrumentKey(string Venue, string Market, string Board, string SecurityId);

/// <summary>Symbol is unique within a provider connection; Key identifies the actual listing.</summary>
public sealed record Instrument(
    InstrumentKey Key, string Symbol, string Name, string? Currency = null,
    decimal LotSize = 1m, decimal? PriceStep = null, DateOnly? Expiry = null);
