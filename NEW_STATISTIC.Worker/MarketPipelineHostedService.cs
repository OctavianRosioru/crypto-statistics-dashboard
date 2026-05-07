using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using NEW_STATISTIC.Core.Abstractions;
using NEW_STATISTIC.Core.Domain;
using NEW_STATISTIC.Core.Options;
using NEW_STATISTIC.Core.Services;

namespace NEW_STATISTIC.Worker;

public sealed class MarketPipelineHostedService : BackgroundService
{
    private readonly IExchangeAggregateTradeSource _source;
    private readonly IOptions<TradingOptions> _options;
    private readonly IPersistenceSink _sink;
    private readonly IShotObserver? _shotObserver;
    private readonly IQuoteVolume24hProvider _quoteVolume24h;
    private readonly TradeIngestMetrics _metrics;
    private readonly ILogger<MarketPipelineHostedService> _log;
    private readonly ConcurrentDictionary<string, SymbolPipeline> _pipelines = new();
    private int _firstTradeLogged;

    public MarketPipelineHostedService(
        IExchangeAggregateTradeSource source,
        IOptions<TradingOptions> options,
        IPersistenceSink sink,
        TradeIngestMetrics metrics,
        IQuoteVolume24hProvider quoteVolume24h,
        ILogger<MarketPipelineHostedService> log,
        IShotObserver? shotObserver = null)
    {
        _source = source;
        _options = options;
        _sink = sink;
        _shotObserver = shotObserver;
        _quoteVolume24h = quoteVolume24h;
        _metrics = metrics;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = Channel.CreateUnbounded<NormalizedAggregateTrade>(new UnboundedChannelOptions
        {
            SingleReader = true,
            // Several WebSocket subscribe batches can deliver trades on different threads at once.
            SingleWriter = false
        });

        _log.LogInformation(
            "Market pipeline: WebSocket producer starting for {Exchange} (REST + subscribe). Waiting for trades…",
            _source.ExchangeName);

        var producer = Task.Run(() => _source.RunAsync(channel.Writer, stoppingToken), stoppingToken);

        try
        {
            await foreach (var trade in channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                if (Interlocked.CompareExchange(ref _firstTradeLogged, 1, 0) == 0)
                {
                    _log.LogInformation(
                        "Market pipeline: first trade received from WebSocket — {Exchange} {Symbol} tradeTimeMs={TradeTimeMs} price={Price}",
                        trade.Exchange, trade.Symbol, trade.TradeTimeMs, trade.Price);
                }

                var pl = _pipelines.GetOrAdd(
                    trade.Symbol,
                    sym => new SymbolPipeline(_source.ExchangeName, sym, _options.Value, _sink, _shotObserver, _quoteVolume24h));

                try
                {
                    await pl.ProcessTradeAsync(trade, stoppingToken).ConfigureAwait(false);
                    _metrics.RecordTrade();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "ProcessTradeAsync failed for {Exchange} {Symbol}", trade.Exchange, trade.Symbol);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Market pipeline reader failed");
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        try
        {
            await producer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
    }
}
