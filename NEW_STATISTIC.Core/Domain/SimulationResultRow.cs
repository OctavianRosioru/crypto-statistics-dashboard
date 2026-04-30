namespace NEW_STATISTIC.Core.Domain;

public enum OutcomeKind
{
    None = 0,
    TakeProfit = 1,
    StopLoss = 2
}

/// <summary>
/// ActualPrice = prețul real al primului trade care a traversat TP sau SL.
/// Pentru SL acesta poate fi mai rău decât StopLossPrice (slippage/întârziere).
/// </summary>
public sealed record HorizonOutcome(OutcomeKind Kind, long? EventTimeMs, decimal? ActualPrice = null);

public sealed record SimulationResultRow(
    decimal OpenOffsetPercent,
    decimal OpenPrice,
    decimal TakeProfitPrice,
    decimal StopLossPrice,
    IReadOnlyDictionary<int, HorizonOutcome> OutcomesByHorizonSec);
