using NEW_STATISTIC.Core.Abstractions;
using NEW_STATISTIC.Core.Domain;
using NEW_STATISTIC.Core.Options;

namespace NEW_STATISTIC.Core.Services;

public sealed class SymbolPipeline
{
    private readonly string _exchange;
    private readonly string _symbol;
    private readonly TradingOptions _opt;
    private readonly SlidingWindowTradeBuffer _buffer;
    private readonly IPersistenceSink _sink;
    private readonly IShotObserver? _shotObserver;
    private readonly MillisecondBucketAggregator _msBuckets = new();
    private readonly HashSet<long> _triggeredMs = new();
    private readonly List<PendingCandle> _pending = new();
    private long _maxTradeTimeMs;

    /// <summary>
    /// Perioada de follow-up (ms) = max(FollowUpSeconds) * 1000.
    /// Candle-ul stă în _pending până când market time depășește RefMs + _maxFollowMs,
    /// moment în care buffer-ul conține toate trade-urile necesare calculului TP/SL.
    /// </summary>
    private readonly long _maxFollowMs;

    public SymbolPipeline(
        string exchange,
        string symbol,
        TradingOptions opt,
        IPersistenceSink sink,
        IShotObserver? shotObserver = null)
    {
        _exchange = exchange;
        _symbol = symbol;
        _opt = opt;
        _buffer = new SlidingWindowTradeBuffer(opt.MemoryWindowMinutes);
        _sink = sink;
        _shotObserver = shotObserver;
        _maxFollowMs = opt.FollowUpSeconds is { Length: > 0 }
            ? (long)opt.FollowUpSeconds.Max() * 1_000L
            : 300_000L;
    }

    public async ValueTask ProcessTradeAsync(NormalizedAggregateTrade trade, CancellationToken cancellationToken)
    {
        if (!string.Equals(trade.Symbol, _symbol, StringComparison.Ordinal))
            return;

        _buffer.Add(trade);
        _maxTradeTimeMs = Math.Max(_maxTradeTimeMs, trade.TradeTimeMs);

        var cutoff = trade.TradeTimeMs - _opt.MemoryWindowMinutes * 60_000L;
        _msBuckets.PruneOlderThan(cutoff);
        _triggeredMs.RemoveWhere(k => k < cutoff);

        _msBuckets.ApplyTrade(trade);
        TryFireTrigger(trade.TradeTimeMs);

        await ProcessPendingAsync(trade, cancellationToken).ConfigureAwait(false);
    }

    private void TryFireTrigger(long tradeTimeMs)
    {
        if (!_msBuckets.TryGet(tradeTimeMs, out var mm))
            return;

        if (mm.Min <= 0)
            return;

        var diff = (mm.Max - mm.Min) / mm.Min * 100m;
        if (diff < _opt.TriggerDiffPercent)
            return;

        if (!_triggeredMs.Add(tradeTimeMs))
            return;

        TryEnqueuePending(tradeTimeMs, mm, diff);
    }

    private void TryEnqueuePending(long triggerMs, MillisecondBucketAggregator.MinMax mm, decimal diffPercent)
    {
        var half = _opt.CandleHalfWindowMs;
        var start = triggerMs - half;
        var end = triggerMs + half;

        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (!IntervalsOverlap(start, end, p.WindowStartMs, p.WindowEndMs))
                continue;
            if (p.DiffPercent >= diffPercent)
                return;
            _pending.RemoveAt(i);
        }

        _pending.Add(new PendingCandle(triggerMs, start, end, mm.Min, mm.Max, diffPercent));
    }

    private static bool IntervalsOverlap(long a0, long a1, long b0, long b1)
        => !(a1 < b0 || b1 < a0);

    private async ValueTask ProcessPendingAsync(NormalizedAggregateTrade trade, CancellationToken cancellationToken)
    {
        if (_pending.Count == 0)
            return;

        var toRemove = new List<PendingCandle>();

        foreach (var p in _pending.ToArray())
        {
            // --- FAZA 1: ferestra s-a închis — calculează datele din fereastră ---
            if (!p.WindowDataComputed)
            {
                if (_maxTradeTimeMs >= p.WindowEndMs && p.StreamReachedWindowEndUtc is null)
                    p.StreamReachedWindowEndUtc = DateTime.UtcNow;

                var windowClosed = trade.TradeTimeMs > p.WindowEndMs;
                var waitedAfterStreamEnd = p.StreamReachedWindowEndUtc is DateTime u
                    && (DateTime.UtcNow - u).TotalMilliseconds >= _opt.CandleCloseWaitMs;

                if (windowClosed || waitedAfterStreamEnd)
                {
                    if (!TryComputeWindowData(p))
                    {
                        // Niciun trade în fereastră — ignorăm candle-ul.
                        toRemove.Add(p);
                        continue;
                    }

                    // Notifică observer-ul (Telegram trigger fast-path) imediat după faza 1.
                    NotifyShotDetected(p);
                }
            }

            // --- FAZA 1.5: fast follow-up pentru Telegram trigger (orizont scurt) ---
            if (p.ShotEmitted && !p.ShotResolved && _shotObserver is not null)
            {
                var fastMs = _shotObserver.FastFollowUpMs;
                if (fastMs <= 0)
                {
                    // observer dezactivat între timp — marchează rezolvat ca să nu mai încercăm
                    p.ShotResolved = true;
                }
                else if ((DateTime.UtcNow - p.WindowDataComputedUtc).TotalMilliseconds >= fastMs)
                {
                    ResolveShotFast(p, fastMs);
                }
            }

            // --- FAZA 2: perioada de follow-up a expirat — completează și salvează ---
            // Folosim ceas real (wall clock) — nu depindem de lichiditatea simbolului.
            if (p.WindowDataComputed &&
                (DateTime.UtcNow - p.WindowDataComputedUtc).TotalMilliseconds >= _maxFollowMs)
            {
                await CompleteWithFollowUpAsync(p, cancellationToken).ConfigureAwait(false);
                toRemove.Add(p);
            }
        }

        foreach (var p in toRemove)
            _pending.Remove(p);
    }

    /// <summary>
    /// Calculează datele din fereastra de ±CandleHalfWindowMs și le stochează în PendingCandle.
    /// Returnează false dacă nu există trade-uri în fereastră (candle-ul va fi ignorat).
    /// </summary>
    private bool TryComputeWindowData(PendingCandle p)
    {
        var inWin = _buffer.TradesInClosedInterval(p.WindowStartMs, p.WindowEndMs);
        if (inWin.Count == 0)
            return false;

        var min = inWin[0].Price;
        var max = inWin[0].Price;
        foreach (var t in inWin)
        {
            min = Math.Min(min, t.Price);
            max = Math.Max(max, t.Price);
        }

        var first = inWin[0];
        var last  = inWin[^1];
        var totalQuote = inWin.Sum(t => t.Price * t.Quantity);
        var duration   = Math.Max(1, p.WindowEndMs - p.WindowStartMs);
        var density    = totalQuote / duration;
        var diffPct    = min <= 0 ? 0 : (max - min) / min * 100m;
        var side       = CandleSideResolver.Resolve(first.Price, min, max);

        p.SetWindowData(
            refMs:       last.TradeTimeMs,
            min:         min,
            max:         max,
            diffPct:     diffPct,
            side:        side,
            firstPrice:  first.Price,
            totalQuote:  totalQuote,
            density:     density);

        return true;
    }

    /// <summary>
    /// Emite ShotEvent dacă observer-ul cere asta (prag global îndeplinit, fast-path activ).
    /// Marchează pending-ul ca observat ca să facem follow-up rapid mai târziu.
    /// </summary>
    private void NotifyShotDetected(PendingCandle p)
    {
        if (_shotObserver is null) return;
        if (_shotObserver.FastFollowUpMs <= 0) return;

        var minDiff = _shotObserver.MinDiffPercent;
        if (minDiff > 0m && p.WinDiffPct < minDiff) return;

        var shot = new ShotEvent(
            Exchange:        _exchange,
            Symbol:          _symbol,
            TriggerTimeMs:   p.TriggerMs,
            ReferenceTimeMs: p.RefMs,
            MinPrice:        p.WinMin,
            MaxPrice:        p.WinMax,
            DiffPercent:     p.WinDiffPct,
            Side:            p.WinSide,
            FirstTradePrice: p.WinFirstPrice,
            TotalQuoteUsdt:  p.WinTotalQuote);

        try { _shotObserver.OnShotDetected(shot); }
        catch { /* observer-ul e best-effort, nu poate să rupă pipeline-ul */ }

        p.Shot = shot;
        p.ShotEmitted = true;
    }

    /// <summary>
    /// După ce a expirat fereastra rapidă (FastFollowUpMs), determinăm TP/SL/None
    /// folosind aceeași logică de prețuri ca SimulationEngine, pentru offset-urile
    /// de intrare configurate de canalele Telegram trigger.
    /// </summary>
    private void ResolveShotFast(PendingCandle p, int fastMs)
    {
        if (p.Shot is null) return;
        var shot = p.Shot;

        var horizonEnd = shot.ReferenceTimeMs + fastMs;
        var trades     = _buffer.TradesAfterBefore(shot.ReferenceTimeMs, horizonEnd);

        var offsets = _shotObserver?.OpenOffsetPercents ?? Array.Empty<decimal>();
        foreach (var offset in offsets)
        {
            if (offset <= 0m || shot.DiffPercent < offset)
            {
                continue;
            }

            var ev = ResolveShotAtOffset(shot, trades, offset);
            try { _shotObserver?.OnShotResolved(ev); }
            catch { /* best-effort */ }
        }

        p.ShotResolved = true;
    }

    private ShotOutcomeEvent ResolveShotAtOffset(
        ShotEvent shot,
        IReadOnlyList<NormalizedAggregateTrade> trades,
        decimal openOffsetPercent)
    {
        var (open, tp, sl) = shot.Side == CandleSide.Buy
            ? PricesForBuy(shot.MaxPrice, openOffsetPercent, _opt.SimulationTakeProfitRatio, _opt.SimulationStopLossRatio)
            : PricesForSell(shot.MinPrice, openOffsetPercent, _opt.SimulationTakeProfitRatio, _opt.SimulationStopLossRatio);

        long? tpMs = null; decimal? tpPrice = null;
        long? slMs = null; decimal? slPrice = null;

        foreach (var t in trades)
        {
            if (tpMs is null)
            {
                bool crossed = shot.Side == CandleSide.Buy ? t.Price >= tp : t.Price <= tp;
                if (crossed) { tpMs = t.TradeTimeMs; tpPrice = t.Price; }
            }
            if (slMs is null)
            {
                bool crossed = shot.Side == CandleSide.Buy ? t.Price <= sl : t.Price >= sl;
                if (crossed) { slMs = t.TradeTimeMs; slPrice = t.Price; }
            }
            if (tpMs is not null && slMs is not null) break;
        }

        if (tpMs is not null && (slMs is null || tpMs <= slMs))
        {
            double pnl = (double)(openOffsetPercent * _opt.SimulationTakeProfitRatio);
            return new ShotOutcomeEvent(shot, openOffsetPercent, OutcomeKind.TakeProfit, tpMs - shot.ReferenceTimeMs, tpPrice, pnl);
        }

        if (slMs is not null)
        {
            double? pnl = open > 0 && slPrice is not null
                ? -(double)(Math.Abs(open - slPrice.Value) / open * 100m)
                : null;
            return new ShotOutcomeEvent(shot, openOffsetPercent, OutcomeKind.StopLoss, slMs - shot.ReferenceTimeMs, slPrice, pnl);
        }

        return new ShotOutcomeEvent(shot, openOffsetPercent, OutcomeKind.None, null, null, null);
    }

    private static (decimal open, decimal tp, decimal sl) PricesForBuy(
        decimal candleMax, decimal openOffsetPercent, decimal tpRatio, decimal slRatio)
    {
        var open = candleMax * (1m - openOffsetPercent / 100m);
        var tp   = open * (1m + (openOffsetPercent * tpRatio) / 100m);
        var sl   = open * (1m - (openOffsetPercent * slRatio) / 100m);
        return (open, tp, sl);
    }

    private static (decimal open, decimal tp, decimal sl) PricesForSell(
        decimal candleMin, decimal openOffsetPercent, decimal tpRatio, decimal slRatio)
    {
        var open = candleMin * (1m + openOffsetPercent / 100m);
        var tp   = open * (1m - (openOffsetPercent * tpRatio) / 100m);
        var sl   = open * (1m + (openOffsetPercent * slRatio) / 100m);
        return (open, tp, sl);
    }

    /// <summary>
    /// Se apelează după ce perioada de follow-up a expirat.
    /// La acest moment buffer-ul conține trade-urile din (RefMs, RefMs + _maxFollowMs].
    /// </summary>
    private async ValueTask CompleteWithFollowUpAsync(PendingCandle p, CancellationToken cancellationToken)
    {
        var after  = _buffer.TradesAfterBefore(p.RefMs, p.RefMs + _maxFollowMs);
        var follow = FollowUpCalculator.Compute(p.RefMs, after, (int)_maxFollowMs, _opt.FollowUpSeconds);

        var candle = new CompletedCandle(
            _exchange,
            _symbol,
            p.TriggerMs,
            p.WindowStartMs,
            p.WindowEndMs,
            p.RefMs,
            p.WinMin,
            p.WinMax,
            p.WinDiffPct,
            p.WinSide,
            p.WinFirstPrice,
            p.WinTotalQuote,
            p.WinDensity,
            follow);

        var sims = SimulationEngine.Run(candle, _opt, after);
        await _sink.EnqueueAsync(new CandlePersistencePayload(candle, sims), cancellationToken).ConfigureAwait(false);
    }

    private sealed class PendingCandle
    {
        public PendingCandle(long triggerMs, long windowStartMs, long windowEndMs,
                             decimal triggerMin, decimal triggerMax, decimal diffPercent)
        {
            TriggerMs    = triggerMs;
            WindowStartMs = windowStartMs;
            WindowEndMs  = windowEndMs;
            TriggerMin   = triggerMin;
            TriggerMax   = triggerMax;
            DiffPercent  = diffPercent;
        }

        // --- Date de identificare (faza 1) ---
        public long    TriggerMs    { get; }
        public long    WindowStartMs { get; }
        public long    WindowEndMs  { get; }
        public decimal TriggerMin   { get; }
        public decimal TriggerMax   { get; }
        public decimal DiffPercent  { get; }

        /// <summary>Momentul când stream-ul a atins WindowEnd; pentru timeout CandleCloseWaitMs.</summary>
        public DateTime? StreamReachedWindowEndUtc { get; set; }

        // --- Date din fereastră (faza 2, după ce fereastra s-a închis) ---
        public bool     WindowDataComputed { get; private set; }
        /// <summary>Momentul real (UTC) când datele ferestrei au fost calculate — folosit pentru timeout Faza 2.</summary>
        public DateTime WindowDataComputedUtc { get; private set; }
        public long     RefMs       { get; private set; }
        public decimal  WinMin      { get; private set; }
        public decimal  WinMax      { get; private set; }
        public decimal  WinDiffPct  { get; private set; }
        public CandleSide WinSide   { get; private set; }
        public decimal  WinFirstPrice { get; private set; }
        public decimal  WinTotalQuote { get; private set; }
        public decimal  WinDensity  { get; private set; }

        // --- Telegram trigger fast-path ---
        public ShotEvent? Shot { get; set; }
        public bool ShotEmitted { get; set; }
        public bool ShotResolved { get; set; }

        public void SetWindowData(long refMs, decimal min, decimal max, decimal diffPct,
                                  CandleSide side, decimal firstPrice,
                                  decimal totalQuote, decimal density)
        {
            RefMs          = refMs;
            WinMin         = min;
            WinMax         = max;
            WinDiffPct     = diffPct;
            WinSide        = side;
            WinFirstPrice  = firstPrice;
            WinTotalQuote  = totalQuote;
            WinDensity     = density;
            WindowDataComputedUtc = DateTime.UtcNow;
            WindowDataComputed = true;
        }
    }
}
