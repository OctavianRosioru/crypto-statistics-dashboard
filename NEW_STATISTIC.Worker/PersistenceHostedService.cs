using Microsoft.Extensions.Options;
using NEW_STATISTIC.Core.Domain;
using NEW_STATISTIC.Core.Options;
using NEW_STATISTIC.Infrastructure.Persistence;

namespace NEW_STATISTIC.Worker;

public sealed class PersistenceHostedService : BackgroundService
{
    private readonly ChannelPersistenceSink _channelSink;
    private readonly EfCandlePersistence _persistence;
    private readonly IOptions<TradingOptions> _options;
    private readonly ILogger<PersistenceHostedService> _log;

    public PersistenceHostedService(
        ChannelPersistenceSink channelSink,
        EfCandlePersistence persistence,
        IOptions<TradingOptions> options,
        ILogger<PersistenceHostedService> log)
    {
        _channelSink = channelSink;
        _persistence = persistence;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batchSize = Math.Max(1, _options.Value.PersistenceBatchSize);
        var flushMs = Math.Max(50, _options.Value.PersistenceFlushIntervalMs);
        var batch = new List<CandlePersistencePayload>(batchSize);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // PeriodicTimer allows only one concurrent WaitForNextTickAsync; racing it with
                // WaitToReadAsync left the previous tick wait running → InvalidOperationException.
                using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var readTask = _channelSink.Reader.WaitToReadAsync(raceCts.Token).AsTask();
                var delayTask = Task.Delay(flushMs, raceCts.Token);
                var finished = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);

                if (ReferenceEquals(finished, delayTask))
                {
                    raceCts.Cancel();
                    try { await readTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    try { await delayTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }

                    if (batch.Count > 0)
                        await FlushBatchAsync(batch, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    raceCts.Cancel();
                    try { await delayTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }

                    bool more;
                    try
                    {
                        more = await readTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        continue;
                    }

                    if (!more)
                        break;

                    while (_channelSink.Reader.TryRead(out var item))
                    {
                        batch.Add(item);
                        if (batch.Count >= batchSize)
                            await FlushBatchAsync(batch, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }

        if (batch.Count > 0)
        {
            try
            {
                await FlushBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Final persistence flush failed");
            }
        }
    }

    private async Task FlushBatchAsync(List<CandlePersistencePayload> batch, CancellationToken cancellationToken)
    {
        try
        {
            if (batch.Count == 1)
                await _persistence.SaveAsync(batch[0], cancellationToken).ConfigureAwait(false);
            else
                await _persistence.SaveBatchAsync(batch, cancellationToken).ConfigureAwait(false);

            _log.LogDebug("Persisted {Count} candle(s)", batch.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Batch persist failed for {Count} items", batch.Count);
        }
        finally
        {
            batch.Clear();
        }
    }
}
