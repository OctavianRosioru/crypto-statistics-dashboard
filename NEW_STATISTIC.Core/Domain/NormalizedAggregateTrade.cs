namespace NEW_STATISTIC.Core.Domain;

/// <summary>Trade agregat normalizat (indiferent de exchange).</summary>
public readonly record struct NormalizedAggregateTrade(
    string Exchange,
    string Symbol,
    long TradeTimeMs,
    decimal Price,
    decimal Quantity,
    bool? BuyerIsMaker);
