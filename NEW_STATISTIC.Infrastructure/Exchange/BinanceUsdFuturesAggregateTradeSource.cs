using System.Threading;
using System.Threading.Channels;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEW_STATISTIC.Core.Abstractions;
using NEW_STATISTIC.Core.Domain;
using NEW_STATISTIC.Core.Options;
using NEW_STATISTIC.Core.Services;

namespace NEW_STATISTIC.Infrastructure.Exchange;

public sealed class BinanceUsdFuturesAggregateTradeSource : IExchangeAggregateTradeSource
{
    private const int TradeSummaryEvery = 5000;

    private readonly IBinanceRestClient _rest;
    private readonly IBinanceSocketClient _socket;
    private readonly TradingOptions _opt;
    private readonly QuoteVolume24hStore _quoteVolumes;
    private readonly ILogger<BinanceUsdFuturesAggregateTradeSource> _log;
    private long _sessionTradeCount;
    private int _firstTradeLogged;
    private int _firstTickerLogged;

    public BinanceUsdFuturesAggregateTradeSource(
        IBinanceRestClient rest,
        IBinanceSocketClient socket,
        IOptions<TradingOptions> opt,
        QuoteVolume24hStore quoteVolumes,
        ILogger<BinanceUsdFuturesAggregateTradeSource> log)
    {
        _rest = rest;
        _socket = socket;
        _opt = opt.Value;
        _quoteVolumes = quoteVolumes;
        _log = log;
    }

    public string ExchangeName => "Binance";

    public async Task RunAsync(ChannelWriter<NormalizedAggregateTrade> writer, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(writer, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var delay = ReconnectBackoff.GetDelayMs(
                    attempt++,
                    _opt.WebSocketReconnectInitialMs,
                    _opt.WebSocketReconnectMaxMs);
                _log.LogWarning(ex, "Binance session ended; reconnect in {DelayMs} ms (attempt {Attempt})", delay, attempt);
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task RunSessionAsync(ChannelWriter<NormalizedAggregateTrade> writer, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _sessionTradeCount, 0);
        Interlocked.Exchange(ref _firstTradeLogged, 0);
        Interlocked.Exchange(ref _firstTickerLogged, 0);

        var info = await _rest.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(cancellationToken).ConfigureAwait(false);
        if (!info.Success || info.Data?.Symbols is null)
        {
            throw new InvalidOperationException($"Binance GetExchangeInfo failed: {info.Error?.Message}");
        }

        var symbols = info.Data.Symbols
            .Where(s => s.ContractType == ContractType.Perpetual
                        && s.QuoteAsset == "USDT"
                        && s.Status == SymbolStatus.Trading)
            .Select(s => s.Name)
            .Take(_opt.MaxSymbols)
            .ToArray();

        if (symbols.Length == 0)
        {
            _log.LogWarning("No Binance USDT perpetual symbols.");
            await Task.Delay(_opt.WebSocketReconnectMaxMs, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Binance: zero USDT perpetual symbols.");
        }

        var batchSize = Math.Clamp(
            _opt.BinanceSubscribeSymbolsBatchSize > 0 ? _opt.BinanceSubscribeSymbolsBatchSize : 80,
            1,
            500);
        var batches = (int)Math.Ceiling(symbols.Length / (double)batchSize);
        _log.LogInformation(
            "Binance: subscribing to WebSocket (aggregate trades) for {Count} symbols in {Batches} batch(es) of up to {BatchSize} (server max message ~4000 B)…",
            symbols.Length,
            batches,
            batchSize);

        var batchNo = 0;
        foreach (var batch in symbols.Chunk(batchSize))
        {
            batchNo++;
            var batchSymbols = batch.ToArray();
            var sub = await _socket.UsdFuturesApi.ExchangeData
                .SubscribeToAggregatedTradeUpdatesAsync(batchSymbols, ev =>
                {
                    try
                    {
                        var d = ev.Data;
                        var ms = new DateTimeOffset(d.TradeTime).ToUnixTimeMilliseconds();
                        var t = new NormalizedAggregateTrade(ExchangeName, d.Symbol, ms, d.Price, d.Quantity, d.BuyerIsMaker);
                        writer.TryWrite(t);

                        var n = Interlocked.Increment(ref _sessionTradeCount);
                        if (Interlocked.CompareExchange(ref _firstTradeLogged, 1, 0) == 0)
                        {
                            _log.LogInformation(
                                "Binance WebSocket: receiving trades. First: {Symbol} timeMs={TimeMs} price={Price} qty={Quantity}",
                                d.Symbol, ms, d.Price, d.Quantity);
                        }

                        _log.LogDebug(
                            "Binance trade: {Symbol} timeMs={TimeMs} price={Price} qty={Quantity} buyerIsMaker={BuyerIsMaker}",
                            d.Symbol, ms, d.Price, d.Quantity, d.BuyerIsMaker);

                        if (n > 0 && n % TradeSummaryEvery == 0)
                        {
                            _log.LogInformation("Binance WebSocket: received {Count} aggregate trades in this session (sample).", n);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Binance aggregate trade callback failed");
                    }
                }, cancellationToken).ConfigureAwait(false);

            if (!sub.Success)
            {
                throw new InvalidOperationException(
                    $"Binance socket subscribe failed (batch {batchNo}/{batches}, {batchSymbols.Length} symbols): {sub.Error?.Message}");
            }

            var tickerSub = await _socket.UsdFuturesApi.ExchangeData
                .SubscribeToTickerUpdatesAsync(batchSymbols, ev =>
                {
                    try
                    {
                        var d = ev.Data;
                        if (!TickerReflection.TryReadString(d, "Symbol", out var symbol) &&
                            !TickerReflection.TryReadString(ev, "Symbol", out symbol))
                        {
                            return;
                        }

                        if (!TickerReflection.TryReadDecimal(d, "QuoteVolume", out var quoteVolume))
                            return;

                        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        _quoteVolumes.Record(ExchangeName, symbol, nowMs, quoteVolume);

                        if (Interlocked.CompareExchange(ref _firstTickerLogged, 1, 0) == 0)
                        {
                            _log.LogInformation(
                                "Binance ticker WebSocket: receiving 24h QAV. First: {Symbol} quoteVolume24h={QuoteVolume}",
                                symbol, quoteVolume);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Binance ticker callback failed");
                    }
                }, cancellationToken).ConfigureAwait(false);

            if (!tickerSub.Success)
            {
                throw new InvalidOperationException(
                    $"Binance ticker socket subscribe failed (batch {batchNo}/{batches}, {batchSymbols.Length} symbols): {tickerSub.Error?.Message}");
            }

            _log.LogInformation(
                "Binance WebSocket: batch {BatchNo}/{Batches} subscribed ({InBatch} symbols), trade subscription id {TradeSubscriptionId}, ticker subscription id {TickerSubscriptionId}.",
                batchNo,
                batches,
                batchSymbols.Length,
                sub.Data?.Id ?? 0,
                tickerSub.Data?.Id ?? 0);
        }

        _log.LogInformation(
            "Binance WebSocket: all {BatchCount} batch(es) active ({SymbolCount} symbols total).",
            batches,
            symbols.Length);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // oprire normală
        }
    }
}
