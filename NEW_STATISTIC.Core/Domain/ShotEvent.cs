namespace NEW_STATISTIC.Core.Domain;

/// <summary>
/// Eveniment emis de SymbolPipeline imediat ce datele ferestrei (faza 1) sunt calculate,
/// adică cu mult înainte de finalul follow-up-ului lung. Folosit de modul Telegram trigger
/// pentru a aplica filtre live (distance, side, symbol whitelist) și a porni evaluarea
/// rapidă a outcome-ului.
/// </summary>
public sealed record ShotEvent(
    string Exchange,
    string Symbol,
    long TriggerTimeMs,
    long ReferenceTimeMs,
    decimal MinPrice,
    decimal MaxPrice,
    decimal DiffPercent,
    CandleSide Side,
    decimal FirstTradePrice,
    decimal TotalQuoteUsdt)
{
    public QuoteVolume24hSnapshot QuoteVolume24h { get; init; } = QuoteVolume24hSnapshot.Empty;
}

/// <summary>
/// Rezultatul evaluării rapide TP/SL a unui shot pe orizontul scurt configurat de canalul trigger.
/// Outcome-ul este determinat din primele trade-uri sosite în [Ref, Ref + maxAgeMs].
///
/// <see cref="PnlPercent"/> reflectă P&amp;L-ul REAL (nu teoretic): pentru TP e
/// |outcomePrice − openPrice| / openPrice × 100 (pozitiv); pentru SL e aceeași magnitudine,
/// returnată ca valoare NEGATIVĂ (slippage real, poate depăși SimulationStopLossRatio × diff).
/// Null pentru None.
/// </summary>
public sealed record ShotOutcomeEvent(
    ShotEvent Shot,
    decimal OpenOffsetPercent,
    OutcomeKind Outcome,
    long? OutcomeAgeMs,
    decimal? OutcomePrice,
    double? PnlPercent);
