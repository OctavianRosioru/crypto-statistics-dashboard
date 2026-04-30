using NEW_STATISTIC.Core.Domain;

namespace NEW_STATISTIC.Core.Services;

public static class FollowUpCalculator
{
    public static FollowUpSnapshot Compute(
        long referenceTimeMs,
        IReadOnlyList<NormalizedAggregateTrade> orderedTradesAfterRef,
        int maxFollowUpMs,
        IReadOnlyList<int> horizonSeconds)
    {
        var endCap = referenceTimeMs + maxFollowUpMs;
        var intervals = new List<IntervalExtreme>();

        foreach (var h in horizonSeconds)
        {
            var horizonEnd = referenceTimeMs + h * 1000L;
            if (horizonEnd > endCap)
                horizonEnd = endCap;

            decimal? minP = null;
            long? minT = null;
            decimal? maxP = null;
            long? maxT = null;

            foreach (var t in orderedTradesAfterRef)
            {
                if (t.TradeTimeMs <= referenceTimeMs || t.TradeTimeMs > horizonEnd)
                    continue;

                if (minP is null || t.Price < minP)
                {
                    minP = t.Price;
                    minT = t.TradeTimeMs;
                }

                if (maxP is null || t.Price > maxP)
                {
                    maxP = t.Price;
                    maxT = t.TradeTimeMs;
                }
            }

            intervals.Add(new IntervalExtreme(h, minP, minT, maxP, maxT));
        }

        return new FollowUpSnapshot(referenceTimeMs, intervals);
    }
}
