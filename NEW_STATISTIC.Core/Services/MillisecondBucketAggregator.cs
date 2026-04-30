using NEW_STATISTIC.Core.Domain;

namespace NEW_STATISTIC.Core.Services;

/// <summary>Min/max pe același TradeTimeMs (cheie milisecundă).</summary>
public sealed class MillisecondBucketAggregator
{
    private readonly Dictionary<long, MinMax> _buckets = new();

    public void ApplyTrade(in NormalizedAggregateTrade trade)
    {
        if (_buckets.TryGetValue(trade.TradeTimeMs, out var mm))
            _buckets[trade.TradeTimeMs] = new MinMax(Math.Min(mm.Min, trade.Price), Math.Max(mm.Max, trade.Price));
        else
            _buckets[trade.TradeTimeMs] = new MinMax(trade.Price, trade.Price);
    }

    public bool TryGet(long tradeTimeMs, out MinMax minMax) =>
        _buckets.TryGetValue(tradeTimeMs, out minMax);

    public void PruneOlderThan(long cutoffMs)
    {
        foreach (var key in _buckets.Keys.ToArray())
        {
            if (key < cutoffMs)
                _buckets.Remove(key);
        }
    }

    public readonly record struct MinMax(decimal Min, decimal Max);
}
