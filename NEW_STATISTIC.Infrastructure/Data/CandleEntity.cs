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

    public decimal? QuoteVolume24hUsdt { get; set; }

    public long? QuoteVolume24hUpdatedMs { get; set; }

    public decimal? QuoteVolume24hChange1mPct { get; set; }

    public decimal? QuoteVolume24hChange5mPct { get; set; }

    public decimal? QuoteVolume24hChange15mPct { get; set; }

    public decimal? QuoteVolume24hChange30mPct { get; set; }

    public decimal? QuoteVolume24hChange1hPct { get; set; }

    public decimal? QuoteVolume24hChange3hPct { get; set; }

    public decimal? QuoteVolume24hChange6hPct { get; set; }

    public decimal? QuoteVolume24hChange12hPct { get; set; }

    public decimal? QuoteVolume24hChange24hPct { get; set; }

    /// <summary>JSON: FollowUpSnapshot.</summary>
    public string FollowUpJson { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<SimulationEntity> Simulations { get; set; } = new List<SimulationEntity>();
}
