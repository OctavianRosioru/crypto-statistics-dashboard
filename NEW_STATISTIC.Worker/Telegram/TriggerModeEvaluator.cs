using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NEW_STATISTIC.Core.Domain;
using NEW_STATISTIC.Core.Options;
using NEW_STATISTIC.Infrastructure.Data;

namespace NEW_STATISTIC.Worker.Telegram;

/// <summary>
/// Evaluator pentru canalele Telegram în mod trigger.
/// Se abonează la <see cref="RecentShotsBuffer.ShotClassified"/> și, la fiecare shot nou,
/// evaluează pattern-ul fiecărui canal: ≥ MinTpCount shoturi cu TP în fereastră AND (opțional) P&L net pozitiv.
/// SL-urile și None-urile NU invalidează pattern-ul — ele intră doar în calculul net-ului.
/// </summary>
public sealed class TriggerModeEvaluator : IHostedService
{
    private const int MinTpLeadPercent = 25;
    private const int SmallSampleMaxResolvedShots = 10;
    private const int SmallSampleMinTpLead = 2;
    private const int SmallSampleMinTpToSlRatio = 2;

    private readonly RecentShotsBuffer _buffer;
    private readonly TelegramChannelStore _store;
    private readonly TelegramClient _telegram;
    private readonly IDbContextFactory<StatisticDbContext> _dbFactory;
    private readonly ILogger<TriggerModeEvaluator> _log;

    /// <summary>(channelId, symbol) -&gt; ultimul ms al alertei trimise.</summary>
    private readonly ConcurrentDictionary<(string ChannelId, string Symbol), long> _cooldown = new();

    public TriggerModeEvaluator(
        RecentShotsBuffer buffer,
        TelegramChannelStore store,
        TelegramClient telegram,
        IDbContextFactory<StatisticDbContext> dbFactory,
        ILogger<TriggerModeEvaluator> log)
    {
        _buffer = buffer;
        _store = store;
        _telegram = telegram;
        _dbFactory = dbFactory;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _buffer.ShotClassified += OnShotClassified;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _buffer.ShotClassified -= OnShotClassified;
        return Task.CompletedTask;
    }

    private void OnShotClassified(ShotOutcomeEvent ev)
    {
        _ = EvaluateAsync(ev);
    }

    private async Task EvaluateAsync(ShotOutcomeEvent latest)
    {
        try { await EvaluateCoreAsync(latest, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "TriggerModeEvaluator: eroare la evaluare."); }
    }

    private async Task EvaluateCoreAsync(ShotOutcomeEvent latest, CancellationToken ct)
    {
        var channels = _store.Snapshot;
        if (channels.Count == 0) return;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var ch in channels)
        {
            if (!ch.Enabled || ch.Mode != TelegramChannelMode.Trigger || ch.Trigger is null) continue;
            var t = ch.Trigger;

            // 1. Filtru de bază pe ULTIMUL shot — fără el nu are rost să mai numărăm.
            if (!ShotMatchesFilters(latest, t)) continue;
            if (!OffsetMatchesChannelRange(latest, t)) continue;

            // 2. Adun toate shoturile din fereastră care îndeplinesc filtrele
            //    (NU ne uităm la outcome aici — TPs, SLs și Nones intră toate la calcul).
            var windowMs = Math.Max(1, t.WindowSeconds) * 1000;
            var matches  = _buffer
                .SnapshotForSymbol(latest.Shot.Exchange, latest.Shot.Symbol, windowMs)
                .Where(x => ShotMatchesFilters(x, t))
                .Where(x => OffsetMatchesChannelRange(x, t))
                .ToList();

            // 3. Numără TP / calculează net P&L o singură dată per shot fizic.
            //    Un shot cu DiffPercent mare poate avea multe offseturi în range, dar pentru Pattern
            //    contează ca un singur shot.
            var shotResults = matches
                .GroupBy(MakeShotKey)
                .Select(g => AggregateRangeShot(g))
                .ToList();

            var latestShotResult = shotResults
                .Where(r => MakeShotKey(r.Outcome) == MakeShotKey(latest))
                .Select(r => r.Outcome)
                .FirstOrDefault();
            if (latestShotResult is null || latestShotResult.Outcome != OutcomeKind.TakeProfit)
                continue;

            // Folosim PnlPercent precalculat pe outcome-ul reprezentativ:
            //    TP = +diff × tpRatio (limit order, execuție exactă la țintă);
            //    SL = pierdere REALĂ pe baza prețului care a traversat SL (slippage inclus, semn negativ).
            var liveStats = BuildStats(shotResults.Select(r => r.Outcome));
            var minTpCount = Math.Max(1, t.MinTpCount);
            if (!StatsPasses(liveStats, minTpCount, t.RequirePositiveNet))
                continue;

            if (t.RequirePositiveNet)
            {
                var dbStats = await LoadHistoricalStatsAsync(latestShotResult, t, ct)
                    .ConfigureAwait(false);
                if (!StatsPasses(dbStats, minTpCount, requirePositiveNet: true))
                    continue;
            }

            int tpCount = liveStats.Tp, slCount = liveStats.Sl, noneCount = liveStats.None;
            double net = liveStats.Net;

            // 5. Cooldown per (canal, simbol).
            var key = (ch.Id, latest.Shot.Symbol);
            if (_cooldown.TryGetValue(key, out var lastMs))
            {
                var elapsed = nowMs - lastMs;
                if (elapsed < t.CooldownSeconds * 1000L) continue;
            }
            _cooldown[key] = nowMs;

            var msg = FormatMessage(t.MessageFormat, latestShotResult, tpCount, slCount, noneCount, net);
            _ = SendAsync(ch, msg);
        }
    }

    /// <summary>Filtre care țin de shotul în sine (exchange/symbol/side), NU de outcome.</summary>
    private static bool ShotMatchesFilters(ShotOutcomeEvent ev, TelegramTriggerConfig t)
    {
        // Exchange filter
        if (!string.Equals(t.Exchange, "*", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(t.Exchange, ev.Shot.Exchange, StringComparison.OrdinalIgnoreCase))
            return false;

        // Symbol whitelist (gol = toate)
        if (t.Symbols is { Length: > 0 } &&
            !t.Symbols.Any(s => string.Equals(s, ev.Shot.Symbol, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Side filter
        if (t.Side == TelegramSideFilter.Buy  && ev.Shot.Side != CandleSide.Buy)  return false;
        if (t.Side == TelegramSideFilter.Sell && ev.Shot.Side != CandleSide.Sell) return false;

        return true;
    }

    private static bool OffsetMatchesChannelRange(ShotOutcomeEvent ev, TelegramTriggerConfig t)
    {
        var min = Math.Max(0m, t.DistanceMin);
        var max = t.DistanceMax > 0m ? Math.Max(min, t.DistanceMax) : 0m;
        return ev.OpenOffsetPercent >= min - 0.000001m &&
            (max <= 0m || ev.OpenOffsetPercent <= max + 0.000001m);
    }

    private static int NormalizeStatsLookbackDays(int days) =>
        days is 3 or 7 or 14 or 30 ? days : 1;

    private async Task<StatsSummary> LoadHistoricalStatsAsync(
        ShotOutcomeEvent latest,
        TelegramTriggerConfig t,
        CancellationToken ct)
    {
        var lookbackDays = NormalizeStatsLookbackDays(t.StatsLookbackDays);
        var sinceMs = DateTimeOffset.UtcNow.AddDays(-lookbackDays).ToUnixTimeMilliseconds();
        var min = Math.Max(0m, t.DistanceMin);
        var max = t.DistanceMax > 0m ? Math.Max(min, t.DistanceMax) : 0m;
        var side = latest.Shot.Side.ToString();
        var targetHorizonSec = Math.Max(1, (int)Math.Ceiling(t.MaxTpAgeMs / 1000d));

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var candles = await db.Candles
            .AsNoTracking()
            .AsSplitQuery()
            .Include(c => c.Simulations)
            .Where(c =>
                c.Exchange == latest.Shot.Exchange &&
                c.Symbol == latest.Shot.Symbol &&
                c.Side == side &&
                c.TriggerTimeMs >= sinceMs &&
                c.DiffPercent >= min)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var outcomes = new List<StatsOutcome>();
        foreach (var candle in candles)
        {
            var candleOutcomes = candle.Simulations
                .Where(s => s.OpenOffsetPercent >= min - 0.000001m)
                .Where(s => max <= 0m || s.OpenOffsetPercent <= max + 0.000001m)
                .Select(s => TryReadSimulationOutcome(s, targetHorizonSec))
                .Where(o => o is not null)
                .Select(o => o!)
                .ToList();

            if (candleOutcomes.Count == 0)
                continue;

            outcomes.Add(AggregateRangeStats(candleOutcomes));
        }

        return BuildStats(outcomes);
    }

    private static StatsOutcome? TryReadSimulationOutcome(SimulationEntity sim, int targetHorizonSec)
    {
        try
        {
            using var doc = JsonDocument.Parse(sim.OutcomesJson);
            if (!TryGetHorizonOutcome(doc.RootElement, targetHorizonSec, out var outcome))
                return null;

            var kind = outcome.TryGetProperty("k", out var kEl) ? kEl.GetInt32() : 0;
            var gainPct = sim.OpenPrice > 0
                ? (double)(Math.Abs(sim.TakeProfitPrice - sim.OpenPrice) / sim.OpenPrice * 100m)
                : 0d;
            var lossPct = sim.OpenPrice > 0
                ? (double)(Math.Abs(sim.OpenPrice - sim.StopLossPrice) / sim.OpenPrice * 100m)
                : 0d;

            if (kind == (int)OutcomeKind.StopLoss &&
                outcome.TryGetProperty("p", out var pEl) &&
                pEl.ValueKind == JsonValueKind.Number &&
                sim.OpenPrice > 0)
            {
                lossPct = (double)(Math.Abs(sim.OpenPrice - pEl.GetDecimal()) / sim.OpenPrice * 100m);
            }

            return kind switch
            {
                (int)OutcomeKind.TakeProfit => new StatsOutcome(OutcomeKind.TakeProfit, gainPct),
                (int)OutcomeKind.StopLoss => new StatsOutcome(OutcomeKind.StopLoss, -lossPct),
                _ => new StatsOutcome(OutcomeKind.None, null)
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetHorizonOutcome(JsonElement root, int targetHorizonSec, out JsonElement outcome)
    {
        if (root.TryGetProperty(targetHorizonSec.ToString(), out outcome))
            return true;

        JsonProperty? best = null;
        var bestHorizon = int.MaxValue;
        JsonProperty? fallback = null;
        var fallbackHorizon = 0;

        foreach (var prop in root.EnumerateObject())
        {
            if (!int.TryParse(prop.Name, out var horizon))
                continue;

            if (horizon >= targetHorizonSec && horizon < bestHorizon)
            {
                best = prop;
                bestHorizon = horizon;
            }

            if (horizon > fallbackHorizon)
            {
                fallback = prop;
                fallbackHorizon = horizon;
            }
        }

        if (best is not null)
        {
            outcome = best.Value.Value;
            return true;
        }

        if (fallback is not null)
        {
            outcome = fallback.Value.Value;
            return true;
        }

        outcome = default;
        return false;
    }

    private static StatsSummary BuildStats(IEnumerable<ShotOutcomeEvent> outcomes) =>
        BuildStats(outcomes.Select(o => new StatsOutcome(o.Outcome, o.PnlPercent)));

    private static StatsSummary BuildStats(IEnumerable<StatsOutcome> outcomes)
    {
        int tp = 0, sl = 0, none = 0;
        double net = 0;
        foreach (var outcome in outcomes)
        {
            switch (outcome.Kind)
            {
                case OutcomeKind.TakeProfit:
                    tp++;
                    if (outcome.PnlPercent.HasValue) net += outcome.PnlPercent.Value;
                    break;
                case OutcomeKind.StopLoss:
                    sl++;
                    if (outcome.PnlPercent.HasValue) net += outcome.PnlPercent.Value;
                    break;
                default:
                    none++;
                    break;
            }
        }

        return new StatsSummary(tp, sl, none, net);
    }

    private static StatsOutcome AggregateRangeStats(IReadOnlyList<StatsOutcome> outcomes)
    {
        var tp = outcomes
            .Where(x => x.Kind == OutcomeKind.TakeProfit)
            .OrderByDescending(x => x.PnlPercent ?? double.MinValue)
            .FirstOrDefault();
        if (tp is not null) return tp;

        var sl = outcomes
            .Where(x => x.Kind == OutcomeKind.StopLoss)
            .OrderBy(x => x.PnlPercent ?? double.MaxValue)
            .FirstOrDefault();
        if (sl is not null) return sl;

        return outcomes[0];
    }

    private static bool StatsPasses(StatsSummary stats, int minTpCount, bool requirePositiveNet)
    {
        if (stats.Tp < minTpCount) return false;
        if (requirePositiveNet && stats.Net <= 0) return false;
        return HasReliableTpLead(stats.Tp, stats.Sl);
    }

    private static bool HasReliableTpLead(int tpCount, int slCount)
    {
        if (tpCount <= 0) return false;
        if (slCount <= 0) return true;

        var total = tpCount + slCount;
        var lead = tpCount - slCount;
        if (total <= SmallSampleMaxResolvedShots)
        {
            return lead >= SmallSampleMinTpLead &&
                tpCount >= slCount * SmallSampleMinTpToSlRatio;
        }

        return tpCount * 100 > slCount * (100 + MinTpLeadPercent) &&
            lead >= Math.Sqrt(total);
    }

    private static ShotKey MakeShotKey(ShotOutcomeEvent ev) =>
        new(
            ev.Shot.Exchange,
            ev.Shot.Symbol,
            ev.Shot.TriggerTimeMs,
            ev.Shot.ReferenceTimeMs,
            ev.Shot.Side);

    private static RangeShotResult AggregateRangeShot(IEnumerable<ShotOutcomeEvent> outcomes)
    {
        var ordered = outcomes
            .OrderBy(x => x.OpenOffsetPercent)
            .ToList();

        var tp = ordered
            .Where(x => x.Outcome == OutcomeKind.TakeProfit)
            .OrderByDescending(x => x.PnlPercent ?? double.MinValue)
            .ThenBy(x => x.OpenOffsetPercent)
            .FirstOrDefault();
        if (tp is not null) return new RangeShotResult(tp);

        var sl = ordered
            .Where(x => x.Outcome == OutcomeKind.StopLoss)
            .OrderBy(x => x.PnlPercent ?? double.MaxValue)
            .ThenBy(x => x.OpenOffsetPercent)
            .FirstOrDefault();
        if (sl is not null) return new RangeShotResult(sl);

        return new RangeShotResult(ordered[0]);
    }

    private static string FormatMessage(
        string template, ShotOutcomeEvent ev,
        int tpCount, int slCount, int noneCount, double net)
    {
        var side = ev.Shot.Side == CandleSide.Buy ? "BUY" : "SELL";
        var sign = net >= 0 ? "+" : "";
        return template
            .Replace("{exchange}", ev.Shot.Exchange.ToUpperInvariant())
            .Replace("{symbol}",   ev.Shot.Symbol)
            .Replace("{side}",     side)
            .Replace("{distance}", ev.OpenOffsetPercent.ToString("F2"))
            .Replace("{tp}",       tpCount.ToString())
            .Replace("{sl}",       slCount.ToString())
            .Replace("{none}",     noneCount.ToString())
            .Replace("{shots}",    (tpCount + slCount + noneCount).ToString())
            .Replace("{net}",      sign + net.ToString("F2"))
            .Replace("{age}",      (ev.OutcomeAgeMs ?? 0).ToString());
    }

    private async Task SendAsync(TelegramChannel ch, string text)
    {
        try
        {
            var ok = await _telegram.SendAsync(ch.ChatId, text, parseMode: null, CancellationToken.None)
                .ConfigureAwait(false);
            if (ok) _log.LogInformation("Telegram trigger fired: channel={Ch} text={Text}", ch.Name, text);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Telegram trigger send failed for channel {Ch}", ch.Name);
        }
    }

    private readonly record struct ShotKey(
        string Exchange,
        string Symbol,
        long TriggerTimeMs,
        long ReferenceTimeMs,
        CandleSide Side);

    private sealed record RangeShotResult(ShotOutcomeEvent Outcome);
    private sealed record StatsOutcome(OutcomeKind Kind, double? PnlPercent);
    private sealed record StatsSummary(int Tp, int Sl, int None, double Net);
}
