using NEW_STATISTIC.Core.Domain;

namespace NEW_STATISTIC.Core.Abstractions;

/// <summary>
/// Observer pentru drumul rapid Telegram trigger: SymbolPipeline anunță aici fiecare shot
/// imediat ce datele ferestrei sunt calculate, apoi (după FastFollowUpMs) raportează outcome-ul
/// rapid TP/SL/None. Implementarea trăiește în Worker (Telegram/TriggerModeEvaluator).
///
/// Ca să nu emitem evenimente inutile când nu există canale trigger active, pipeline-ul
/// consultă <see cref="MinDiffPercent"/>, <see cref="FastFollowUpMs"/> și
/// <see cref="OpenOffsetPercents"/> înainte de orice
/// muncă: dacă pragul e &gt; 0 dar diff-ul shotului e sub el, sau dacă FastFollowUpMs &lt;= 0,
/// evenimentul e ignorat.
/// </summary>
public interface IShotObserver
{
    /// <summary>
    /// Pragul minim global = min(distanceMin) peste toate canalele trigger active.
    /// Returnează valori &gt; 0 doar dacă există cel puțin un canal trigger activ.
    /// </summary>
    decimal MinDiffPercent { get; }

    /// <summary>
    /// Cât timp (ms) păstrăm shotul în pipeline pentru evaluarea rapidă TP/SL.
    /// = max(maxTpAgeMs) peste canalele trigger active. 0 = fast-path dezactivat.
    /// </summary>
    int FastFollowUpMs { get; }

    /// <summary>
    /// Offset-urile de intrare configurate de canalele trigger active.
    /// Outcome-ul rapid este calculat pentru fiecare offset, nu pentru apex-ul shotului.
    /// </summary>
    IReadOnlyList<decimal> OpenOffsetPercents { get; }

    /// <summary>
    /// Notifică un shot proaspăt detectat (fereastra închisă, datele cunoscute, dar fără outcome încă).
    /// Implementarea poate decide să-l ignore.
    /// </summary>
    void OnShotDetected(ShotEvent shot);

    /// <summary>
    /// Notifică outcome-ul rapid (TP/SL/None) după ce a expirat FastFollowUpMs de la ReferenceTimeMs.
    /// </summary>
    void OnShotResolved(ShotOutcomeEvent outcome);
}
