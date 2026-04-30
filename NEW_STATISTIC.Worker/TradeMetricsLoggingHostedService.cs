namespace NEW_STATISTIC.Worker;

/// <summary>Loghează periodic debitul de trade-uri (observabilitate).</summary>
public sealed class TradeMetricsLoggingHostedService : BackgroundService
{
    private readonly TradeIngestMetrics _metrics;
    private readonly ILogger<TradeMetricsLoggingHostedService> _log;

    public TradeMetricsLoggingHostedService(
        TradeIngestMetrics metrics,
        ILogger<TradeMetricsLoggingHostedService> log)
    {
        _metrics = metrics;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var n = _metrics.ResetCount();
                _log.LogInformation("Trade ingest: {Count} trades in last interval", n);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
    }
}
