namespace NEW_STATISTIC.Worker;

public sealed class TradeIngestMetrics
{
    private long _tradeCount;

    public void RecordTrade() => Interlocked.Increment(ref _tradeCount);

    public long ResetCount()
    {
        return Interlocked.Exchange(ref _tradeCount, 0);
    }
}
