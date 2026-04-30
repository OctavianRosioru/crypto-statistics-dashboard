namespace NEW_STATISTIC.Infrastructure.Data;

public sealed class SimulationEntity
{
    public long Id { get; set; }

    public long CandleId { get; set; }

    public CandleEntity? Candle { get; set; }

    public decimal OpenOffsetPercent { get; set; }

    public decimal OpenPrice { get; set; }

    public decimal TakeProfitPrice { get; set; }

    public decimal StopLossPrice { get; set; }

    /// <summary>JSON: dictionary horizon sec -&gt; outcome.</summary>
    public string OutcomesJson { get; set; } = "";
}
