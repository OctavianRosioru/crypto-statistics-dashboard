namespace NEW_STATISTIC.Core.Domain;

public sealed record CompletedCandle(
    string Exchange,
    string Symbol,
    long TriggerTimeMs,
    long WindowStartMs,
    long WindowEndMs,
    long LastTradeTimeInWindowMs,
    decimal MinPrice,
    decimal MaxPrice,
    decimal DiffPercent,
    CandleSide Side,
    decimal FirstTradePrice,
    decimal TotalQuoteUsdt,
    decimal DensityUsdtPerMs,
    FollowUpSnapshot FollowUp)
{
    public QuoteVolume24hSnapshot QuoteVolume24h { get; init; } = QuoteVolume24hSnapshot.Empty;
}
