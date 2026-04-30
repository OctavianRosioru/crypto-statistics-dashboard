using NEW_STATISTIC.Core.Domain;

namespace NEW_STATISTIC.Worker.Telegram;

/// <summary>
/// Sliding window in-memory cu shoturile clasificate. Singleton.
/// Filtrează la inserție prin <see cref="MinDiffPercent"/> ca să nu colectăm shoturi inutile.
/// </summary>
public sealed class RecentShotsBuffer
{
    private readonly Lock _lock = new();
    private readonly LinkedList<ShotOutcomeEvent> _list = new();
    private readonly int _retentionMs;

    public event Action<ShotOutcomeEvent>? ShotClassified;

    /// <summary>
    /// Setat de TelegramChannelStore la fiecare reload. 0 = acceptă toate.
    /// Pipeline-ul gate-uiește deja la sursă, dar mai filtrăm și aici (defense in depth).
    /// </summary>
    public decimal MinDiffPercent { get; set; }

    public RecentShotsBuffer(int retentionMs = 72 * 60 * 60 * 1000)
    {
        _retentionMs = retentionMs;
    }

    public void Add(ShotOutcomeEvent ev)
    {
        if (MinDiffPercent > 0m && ev.Shot.DiffPercent < MinDiffPercent) return;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cutoff = nowMs - _retentionMs;

        lock (_lock)
        {
            _list.AddLast(ev);
            // prune
            while (_list.First is { } first && first.Value.Shot.ReferenceTimeMs < cutoff)
                _list.RemoveFirst();
        }

        ShotClassified?.Invoke(ev);
    }

    /// <summary>
    /// Snapshot al shoturilor pentru un simbol în ultimele <paramref name="windowMs"/> ms.
    /// Returnat în ordine cronologică.
    /// </summary>
    public List<ShotOutcomeEvent> SnapshotForSymbol(string exchange, string symbol, int windowMs)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var since = nowMs - windowMs;
        var result = new List<ShotOutcomeEvent>();
        lock (_lock)
        {
            foreach (var ev in _list)
            {
                if (ev.Shot.ReferenceTimeMs < since) continue;
                if (!string.Equals(ev.Shot.Symbol, symbol, StringComparison.Ordinal)) continue;
                if (!string.Equals(ev.Shot.Exchange, exchange, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(ev);
            }
        }
        return result;
    }
}
