namespace NEW_STATISTIC.Infrastructure.Data;

public sealed class CandleEntity
{
    public long Id { get; set; }

    public string Exchange { get; set; } = "";

    public string Symbol { get; set; } = "";

    public long TriggerTimeMs { get; set; }

    public long WindowStartMs { get; set; }

    public long WindowEndMs { get; set; }

    public long LastTradeTimeInWindowMs { get; set; }

    public decimal MinPrice { get; set; }

    public decimal MaxPrice { get; set; }

    public decimal DiffPercent { get; set; }

    public string Side { get; set; } = "";

    public decimal FirstTradePrice { get; set; }

    public decimal TotalQuoteUsdt { get; set; }

    public decimal DensityUsdtPerMs { get; set; }

    /// <summary>JSON: FollowUpSnapshot.</summary>
    public string FollowUpJson { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<SimulationEntity> Simulations { get; set; } = new List<SimulationEntity>();
}
