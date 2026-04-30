namespace NEW_STATISTIC.Core.Options;

public sealed class TradingOptions
{
    public const string SectionName = "Trading";

    /// <summary>Binance sau Bybit.</summary>
    public string Exchange { get; set; } = "Binance";

    public decimal TriggerDiffPercent { get; set; } = 0.3m;

    public int CandleHalfWindowMs { get; set; } = 150;

    public int MemoryWindowMinutes { get; set; } = 5;

    /// <summary>Așteptare (ms) dacă lipsește trade cu T &gt; windowEnd.</summary>
    public int CandleCloseWaitMs { get; set; } = 1000;

    public int[] FollowUpSeconds { get; set; } = [1, 5, 15, 30, 60, 300];

    public decimal SimulationInitialOpenPercent { get; set; } = 0.3m;

    public decimal SimulationOpenStepPercent { get; set; } = 0.1m;

    /// <summary>TP = openOffset * acest raport (ex. 0.5 = 50% din offset).</summary>
    public decimal SimulationTakeProfitRatio { get; set; } = 0.5m;

    /// <summary>SL = openOffset * acest raport (ex. 0.2 = 20% din offset).</summary>
    public decimal SimulationStopLossRatio { get; set; } = 0.2m;

    /// <summary>Limitează simbolurile la primul N (performanță / test).</summary>
    public int MaxSymbols { get; set; } = 200;

    /// <summary>
    /// Binance USD futures: mesajul de subscribe are limită (~4000 B pe server).
    /// Simbolurile sunt împărțite în loturi de această dimensiune (implicit 80).
    /// </summary>
    public int BinanceSubscribeSymbolsBatchSize { get; set; } = 80;

    /// <summary>Întârziere inițială reconectare WebSocket (ms).</summary>
    public int WebSocketReconnectInitialMs { get; set; } = 2000;

    /// <summary>Plafon backoff reconectare WebSocket (ms).</summary>
    public int WebSocketReconnectMaxMs { get; set; } = 120_000;

    /// <summary>Număr maxim de lumânări per batch la scriere DB.</summary>
    public int PersistenceBatchSize { get; set; } = 25;

    /// <summary>Interval flush batch persistență (ms), dacă nu s-a atins dimensiunea.</summary>
    public int PersistenceFlushIntervalMs { get; set; } = 250;
}
