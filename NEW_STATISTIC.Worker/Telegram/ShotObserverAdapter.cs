using NEW_STATISTIC.Core.Abstractions;
using NEW_STATISTIC.Core.Domain;

namespace NEW_STATISTIC.Worker.Telegram;

/// <summary>
/// Implementarea <see cref="IShotObserver"/> care leagă SymbolPipeline (Core) de
/// TelegramChannelStore (praguri) și RecentShotsBuffer (memorie) — fără ca pipeline-ul
/// să cunoască direct Telegram-ul.
/// </summary>
public sealed class ShotObserverAdapter : IShotObserver
{
    private readonly TelegramChannelStore _store;
    private readonly RecentShotsBuffer _buffer;

    public ShotObserverAdapter(TelegramChannelStore store, RecentShotsBuffer buffer)
    {
        _store = store;
        _buffer = buffer;

        // Sincronizează pragul de la inserția în buffer cu cel global.
        _buffer.MinDiffPercent = _store.GlobalMinTriggerDiffPercent;
        _store.ConfigChanged += () => _buffer.MinDiffPercent = _store.GlobalMinTriggerDiffPercent;
    }

    public decimal MinDiffPercent => _store.GlobalMinTriggerDiffPercent;
    public int FastFollowUpMs    => _store.GlobalMaxTpAgeMs;
    public IReadOnlyList<OpenOffsetRange> OpenOffsetRanges => _store.GlobalTriggerOpenOffsetRanges;

    public void OnShotDetected(ShotEvent shot)
    {
        // Nimic acum — logica de pattern lucrează după ce avem outcome-ul (OnShotResolved).
        // Hook lăsat pentru extensii viitoare (ex. preview message).
    }

    public void OnShotResolved(IReadOnlyList<ShotOutcomeEvent> outcomes)
    {
        _buffer.AddRange(outcomes);
    }
}
