namespace NEW_STATISTIC.Core.Domain;

public sealed record CandlePersistencePayload(
    CompletedCandle Candle,
    IReadOnlyList<SimulationResultRow> Simulations);
