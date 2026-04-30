using NEW_STATISTIC.Core.Domain;

namespace NEW_STATISTIC.Core.Services;

public sealed class SlidingWindowTradeBuffer
{
    private readonly int _windowMs;
    private readonly List<NormalizedAggregateTrade> _trades = new();

    public SlidingWindowTradeBuffer(int memoryWindowMinutes)
    {
        _windowMs = memoryWindowMinutes * 60 * 1000;
    }

    public void Add(in NormalizedAggregateTrade trade)
    {
        _trades.Add(trade);
        var cutoff = trade.TradeTimeMs - _windowMs;
        if (_trades.Count == 0)
            return;
        var i = 0;
        while (i < _trades.Count && _trades[i].TradeTimeMs < cutoff)
            i++;
        if (i > 0)
            _trades.RemoveRange(0, i);
    }

    public IReadOnlyList<NormalizedAggregateTrade> TradesInClosedInterval(long startMsInclusive, long endMsInclusive)
    {
        var list = new List<NormalizedAggregateTrade>();
        foreach (var t in _trades)
        {
            if (t.TradeTimeMs >= startMsInclusive && t.TradeTimeMs <= endMsInclusive)
                list.Add(t);
        }

        list.Sort(static (a, b) => a.TradeTimeMs.CompareTo(b.TradeTimeMs));
        return list;
    }

    public IReadOnlyList<NormalizedAggregateTrade> TradesAfterBefore(long afterMsExclusive, long beforeMsInclusive)
    {
        var list = new List<NormalizedAggregateTrade>();
        foreach (var t in _trades)
        {
            if (t.TradeTimeMs > afterMsExclusive && t.TradeTimeMs <= beforeMsInclusive)
                list.Add(t);
        }

        list.Sort(static (a, b) => a.TradeTimeMs.CompareTo(b.TradeTimeMs));
        return list;
    }
}
