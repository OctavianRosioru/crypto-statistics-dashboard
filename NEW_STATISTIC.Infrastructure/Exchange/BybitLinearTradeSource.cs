using System.Threading.Channels;
using Bybit.Net.Enums;
using Bybit.Net.Interfaces.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEW_STATISTIC.Core.Abstractions;
using NEW_STATISTIC.Core.Domain;
using NEW_STATISTIC.Core.Options;

namespace NEW_STATISTIC.Infrastructure.Exchange;

/// <summary>Public linear trades (nu sunt agregate ca Binance, dar același model intern).</summary>
public sealed class BybitLinearTradeSource : IExchangeAggregateTradeSource
{
    private const int TradeSummaryEvery = 5000;
    private const int SymbolsPageSize = 200;

    private readonly IBybitRestClient _rest;
    private readonly IBybitSocketClient _socket;
    private readonly TradingOptions _opt;
    private readonly ILogger<BybitLinearTradeSource> _log;
    private long _sessionTradeCount;
    private int _firstTradeLogged;

    public BybitLinearTradeSource(
        IBybitRestClient rest,
        IBybitSocketClient socket,
        IOptions<TradingOptions> opt,
        ILogger<BybitLinearTradeSource> log)
    {
        _rest = rest;
        _socket = socket;
        _opt = opt.Value;
        _log = log;
    }

    public string ExchangeName => "Bybit";

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
                _log.LogWarning(ex, "Bybit session ended; reconnect in {DelayMs} ms (attempt {Attempt})", delay, attempt);
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

        var symbols = await GetTradingUsdtSymbolsAsync(cancellationToken).ConfigureAwait(false);

        if (symbols.Length == 0)
        {
            _log.LogWarning("No Bybit linear USDT symbols.");
            await Task.Delay(_opt.WebSocketReconnectMaxMs, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Bybit: zero linear USDT symbols.");
        }

        _log.LogInformation("Bybit: subscribing to WebSocket (public linear trades) for {Count} symbols…", symbols.Length);

        var sub = await _socket.V5LinearApi.SubscribeToTradeUpdatesAsync(symbols, ev =>
        {
            foreach (var d in ev.Data)
            {
                var ms = new DateTimeOffset(d.Timestamp).ToUnixTimeMilliseconds();
                bool? buyerMaker = d.Side == OrderSide.Buy ? false : true;
                var t = new NormalizedAggregateTrade(ExchangeName, d.Symbol, ms, d.Price, d.Quantity, buyerMaker);
                writer.TryWrite(t);

                var n = Interlocked.Increment(ref _sessionTradeCount);
                if (Interlocked.CompareExchange(ref _firstTradeLogged, 1, 0) == 0)
                {
                    _log.LogInformation(
                        "Bybit WebSocket: receiving trades. First: {Symbol} timeMs={TimeMs} price={Price} qty={Quantity} side={Side}",
                        d.Symbol, ms, d.Price, d.Quantity, d.Side);
                }

                _log.LogDebug(
                    "Bybit trade: {Symbol} timeMs={TimeMs} price={Price} qty={Quantity} side={Side}",
                    d.Symbol, ms, d.Price, d.Quantity, d.Side);

                if (n > 0 && n % TradeSummaryEvery == 0)
                {
                    _log.LogInformation("Bybit WebSocket: received {Count} trades in this session (sample).", n);
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        if (!sub.Success)
        {
            throw new InvalidOperationException($"Bybit socket subscribe failed: {sub.Error?.Message}");
        }

        _log.LogInformation(
            "Bybit WebSocket connected and subscribed successfully ({SymbolCount} symbols). Subscription id: {SubscriptionId}",
            symbols.Length,
            sub.Data?.Id ?? 0);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // oprire normală
        }
    }

    private async Task<string[]> GetTradingUsdtSymbolsAsync(CancellationToken cancellationToken)
    {
        var symbols = new List<string>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        do
        {
            var info = await _rest.V5Api.ExchangeData.GetLinearInverseSymbolsAsync(
                Category.Linear,
                symbol: null,
                baseAsset: null,
                SymbolStatus.Trading,
                symbolType: null,
                limit: SymbolsPageSize,
                cursor: cursor,
                cancellationToken).ConfigureAwait(false);

            if (!info.Success || info.Data?.List is null)
            {
                throw new InvalidOperationException($"Bybit GetLinearInverseSymbols failed: {info.Error?.Message}");
            }

            foreach (var symbol in info.Data.List)
            {
                if (!string.Equals(symbol.QuoteAsset, "USDT", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                symbols.Add(symbol.Name);
                if (symbols.Count >= _opt.MaxSymbols)
                {
                    return symbols.ToArray();
                }
            }

            cursor = info.Data.NextPageCursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor) && seenCursors.Add(cursor));

        return symbols.ToArray();
    }
}
