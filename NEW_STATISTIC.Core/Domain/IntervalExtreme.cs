namespace NEW_STATISTIC.Core.Domain;

public sealed record IntervalExtreme(
    int HorizonSeconds,
    decimal? MinPrice,
    long? MinTimeMs,
    decimal? MaxPrice,
    long? MaxTimeMs);
