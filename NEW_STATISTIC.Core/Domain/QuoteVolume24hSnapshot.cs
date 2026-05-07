namespace NEW_STATISTIC.Core.Domain;

public sealed record QuoteVolume24hSnapshot(
    decimal? QuoteVolume24hUsdt,
    long? QuoteVolume24hUpdatedMs,
    decimal? Change1mPct,
    decimal? Change5mPct,
    decimal? Change15mPct,
    decimal? Change30mPct,
    decimal? Change1hPct,
    decimal? Change3hPct,
    decimal? Change6hPct,
    decimal? Change12hPct,
    decimal? Change24hPct)
{
    public static QuoteVolume24hSnapshot Empty { get; } = new(
        null, null, null, null, null, null, null, null, null, null, null);

    public decimal? ChangeForMinutes(int minutes) => minutes switch
    {
        1 => Change1mPct,
        5 => Change5mPct,
        15 => Change15mPct,
        30 => Change30mPct,
        60 => Change1hPct,
        180 => Change3hPct,
        360 => Change6hPct,
        720 => Change12hPct,
        1440 => Change24hPct,
        _ => null
    };
}
