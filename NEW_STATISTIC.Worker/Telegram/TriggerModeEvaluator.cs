using System.Collections.Concurrent;
using NEW_STATISTIC.Core.Domain;
using NEW_STATISTIC.Core.Options;

namespace NEW_STATISTIC.Worker.Telegram;

/// <summary>
/// Evaluator pentru canalele Telegram în mod trigger.
/// Se abonează la <see cref="RecentShotsBuffer.ShotClassified"/> și, la fiecare shot nou,
/// evaluează pattern-ul fiecărui canal: ≥ MinTpCount shoturi cu TP în fereastră AND (opțional) P&L net pozitiv.
/// SL-urile și None-urile NU invalidează pattern-ul — ele intră doar în calculul net-ului.
/// </summary>
public sealed class TriggerModeEvaluator : IHostedService
{
    private readonly RecentShotsBuffer _buffer;
    private readonly TelegramChannelStore _store;
    private readonly TelegramClient _telegram;
    private readonly ILogger<TriggerModeEvaluator> _log;

    /// <summary>(channelId, symbol) -&gt; ultimul ms al alertei trimise.</summary>
    private readonly ConcurrentDictionary<(string ChannelId, string Symbol), long> _cooldown = new();

    public TriggerModeEvaluator(
        RecentShotsBuffer buffer,
        TelegramChannelStore store,
        TelegramClient telegram,
        ILogger<TriggerModeEvaluator> log)
    {
        _buffer = buffer;
        _store = store;
        _telegram = telegram;
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
        try { Evaluate(ev); }
        catch (Exception ex) { _log.LogWarning(ex, "TriggerModeEvaluator: eroare la evaluare."); }
    }

    private void Evaluate(ShotOutcomeEvent latest)
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

            // Folosim PnlPercent precalculat pe outcome-ul reprezentativ:
            //    TP = +diff × tpRatio (limit order, execuție exactă la țintă);
            //    SL = pierdere REALĂ pe baza prețului care a traversat SL (slippage inclus, semn negativ).
            int tpCount = 0, slCount = 0, noneCount = 0;
            double net = 0;
            foreach (var result in shotResults)
            {
                switch (result.Outcome.Outcome)
                {
                    case OutcomeKind.TakeProfit:
                        tpCount++;
                        if (result.Outcome.PnlPercent.HasValue) net += result.Outcome.PnlPercent.Value;
                        break;
                    case OutcomeKind.StopLoss:
                        slCount++;
                        if (result.Outcome.PnlPercent.HasValue) net += result.Outcome.PnlPercent.Value; // deja semnat negativ
                        break;
                    default:
                        noneCount++;
                        break;
                }
            }

            // 4. Verifică pragurile.
            if (tpCount < Math.Max(1, t.MinTpCount)) continue;
            if (t.RequirePositiveNet && net <= 0) continue;

            // 5. Cooldown per (canal, simbol).
            var key = (ch.Id, latest.Shot.Symbol);
            if (_cooldown.TryGetValue(key, out var lastMs))
            {
                var elapsed = nowMs - lastMs;
                if (elapsed < t.CooldownSeconds * 1000L) continue;
            }
            _cooldown[key] = nowMs;

            var messageOutcome = shotResults
                .Where(r => MakeShotKey(r.Outcome) == MakeShotKey(latest))
                .Select(r => r.Outcome)
                .FirstOrDefault() ?? latest;
            var msg = FormatMessage(t.MessageFormat, messageOutcome, tpCount, slCount, noneCount, net);
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
}
