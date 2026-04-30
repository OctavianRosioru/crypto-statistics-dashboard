using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NEW_STATISTIC.Core.Options;
using NEW_STATISTIC.Infrastructure.Data;

namespace NEW_STATISTIC.Worker.Telegram;

/// <summary>
/// BackgroundService care rulează rapoarte statistice per canal, la frecvențe individuale.
/// Pe fiecare reload de config, recalculează lista de timer-e per canal.
/// </summary>
public sealed class StatisticModeScheduler : BackgroundService
{
    private readonly TelegramChannelStore _store;
    private readonly TelegramClient _telegram;
    private readonly IDbContextFactory<StatisticDbContext> _dbFactory;
    private readonly ILogger<StatisticModeScheduler> _log;

    private readonly ConcurrentDictionary<string, ChannelRunner> _runners = new();
    private CancellationToken _stopToken;

    public StatisticModeScheduler(
        TelegramChannelStore store,
        TelegramClient telegram,
        IDbContextFactory<StatisticDbContext> dbFactory,
        ILogger<StatisticModeScheduler> log)
    {
        _store = store;
        _telegram = telegram;
        _dbFactory = dbFactory;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopToken = stoppingToken;
        Reconcile();
        _store.ConfigChanged += Reconcile;

        // Rămâne activ până la stop; munca e în task-urile per canal.
        return Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _store.ConfigChanged -= Reconcile;
        foreach (var r in _runners.Values) r.Stop();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Reconcile()
    {
        var snapshot = _store.Snapshot
            .Where(c => c.Enabled && c.Mode == TelegramChannelMode.Statistic && c.Statistic is not null)
            .ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);

        _log.LogInformation("Statistic scheduler reconcile: active statistic channels={Count}", snapshot.Count);

        // Stop pentru canalele care nu mai sunt
        foreach (var id in _runners.Keys.ToArray())
        {
            if (!snapshot.ContainsKey(id) && _runners.TryRemove(id, out var r))
            {
                _log.LogInformation("Statistic scheduler stopping runner: channelId={ChannelId}", id);
                r.Stop();
            }
        }

        // (Re)start pentru canalele active — dacă config-ul s-a schimbat, recreăm runner-ul.
        foreach (var (id, ch) in snapshot)
        {
            if (_runners.TryGetValue(id, out var existing))
            {
                if (existing.MatchesConfig(ch)) continue;
                _log.LogInformation("Statistic scheduler restarting runner after config change: channel={Channel} id={ChannelId}",
                    ch.Name, id);
                existing.Stop();
                _runners.TryRemove(id, out _);
            }
            var runner = new ChannelRunner(ch, this, _stopToken);
            _runners[id] = runner;
            _log.LogInformation("Statistic scheduler starting runner: channel={Channel} id={ChannelId} frequency={Frequency}",
                ch.Name, id, DescribeFrequency(ch.Statistic!.FrequencyHours));
            runner.Start();
        }
    }

    public async Task RunOnceAsync(string channelId, CancellationToken ct)
    {
        var ch = _store.Snapshot.FirstOrDefault(c => c.Id == channelId);
        if (ch is null || ch.Statistic is null)
        {
            _log.LogWarning("RunOnce: canal {Id} inexistent / fără config statistic.", channelId);
            return;
        }
        await ExecuteAsync(ch, ct).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(TelegramChannel ch, CancellationToken ct)
    {
        if (ch.Statistic is null) return;
        var cfg = ch.Statistic;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            _log.LogInformation(
                "Statistic report start: channel={Ch} period={Period}h frequency={Frequency} category={Cat} side={Side} distanceMin={DMin} distanceMax={DMax} horizon={Horizon}s skip={Skip} topN={TopN} symbolsFilter={Symbols}",
                ch.Name,
                cfg.PeriodHours,
                DescribeFrequency(cfg.FrequencyHours),
                cfg.Category,
                cfg.Side,
                cfg.DistanceMin,
                cfg.DistanceMax,
                cfg.HorizonSec,
                cfg.Skip,
                cfg.TopN,
                cfg.Symbols.Length == 0 ? "all" : cfg.Symbols.Length.ToString());

            var stats = await ReportAggregator.BuildAsync(db, cfg, ct).ConfigureAwait(false);
            var top   = ReportAggregator.SelectTop(stats, cfg.Category, cfg.TopN, cfg.Skip).ToList();

            _log.LogInformation("Statistic report selected: channel={Ch} stats={StatsCount} selected={TopCount}",
                ch.Name, stats.Count, top.Count);

            if (top.Count == 0)
            {
                _log.LogInformation("Statistic report: channel={Ch} category={Cat} period={H}h — niciun simbol în top, nu trimit nimic.",
                    ch.Name, cfg.Category, cfg.PeriodHours);
                return;
            }

            // Un mesaj per simbol, cu pauză între ele — așa cum cere aplicația de trading.
            var delayMs = Math.Max(0, cfg.DelayBetweenMessagesMs);
            var template = string.IsNullOrWhiteSpace(cfg.MessageFormat) ? "#{symbol} {exchange} {side}" : cfg.MessageFormat;
            int sent = 0;

            for (int i = 0; i < top.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var s = top[i];

                // Side-ul mesajului: filtru explicit pe canal dacă e setat, altfel dominantul per simbol.
                var side = cfg.Side switch
                {
                    TelegramSideFilter.Buy  => "BUY",
                    TelegramSideFilter.Sell => "SELL",
                    _ => s.DominantSide.Equals("Sell", StringComparison.OrdinalIgnoreCase) ? "SELL" : "BUY",
                };

                var msg = FormatMessage(template, s, side);
                _log.LogInformation(
                    "Statistic report send attempt: channel={Ch} index={Index}/{Total} symbol={Symbol} exchange={Exchange} side={Side} net={Net:F2} tp={Tp} sl={Sl} shots={Shots}",
                    ch.Name, i + 1, top.Count, s.Symbol, s.Exchange, side, s.Net, s.Tp, s.Sl, s.Shots);

                var ok = await _telegram.SendAsync(ch.ChatId, msg, parseMode: null, ct).ConfigureAwait(false);
                if (ok) sent++;
                _log.LogInformation("Statistic report send result: channel={Ch} index={Index}/{Total} symbol={Symbol} ok={Ok}",
                    ch.Name, i + 1, top.Count, s.Symbol, ok);

                // Pauză între mesaje (nu și după ultimul)
                if (i < top.Count - 1 && delayMs > 0)
                {
                    try { await Task.Delay(delayMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }

            _log.LogInformation("Statistic report sent: channel={Ch} category={Cat} period={H}h items={N} ok={Ok}",
                ch.Name, cfg.Category, cfg.PeriodHours, top.Count, sent);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogWarning("Statistic report cancelled: channel={Ch}", ch.Name);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Statistic report failed for channel {Ch}", ch.Name);
        }
    }

    private static string FormatMessage(string template, ReportAggregator.SymbolStat s, string side)
    {
        var sign = s.Net >= 0 ? "+" : "";
        return template
            .Replace("{symbol}",   s.Symbol)
            .Replace("{exchange}", (s.Exchange ?? "").ToUpperInvariant())
            .Replace("{side}",     side)
            .Replace("{tp}",       s.Tp.ToString())
            .Replace("{sl}",       s.Sl.ToString())
            .Replace("{shots}",    s.Shots.ToString())
            .Replace("{net}",      sign + s.Net.ToString("F2"));
    }

    private static TimeSpan GetFrequencyInterval(int frequencyHours)
    {
        return frequencyHours <= 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromHours(frequencyHours);
    }

    private static string DescribeFrequency(int frequencyHours)
    {
        return frequencyHours <= 0 ? "30s test" : $"{frequencyHours}h";
    }

    // ──────────────────────────────────────────────────────────────────
    private sealed class ChannelRunner
    {
        private readonly TelegramChannel _ch;
        private readonly StatisticModeScheduler _owner;
        private readonly CancellationTokenSource _cts;
        private Task? _task;

        public ChannelRunner(TelegramChannel ch, StatisticModeScheduler owner, CancellationToken outer)
        {
            _ch = ch;
            _owner = owner;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        }

        public bool MatchesConfig(TelegramChannel other)
        {
            if (_ch.Statistic is null || other.Statistic is null) return false;
            return _ch.ChatId == other.ChatId
                && _ch.Statistic.PeriodHours      == other.Statistic.PeriodHours
                && _ch.Statistic.FrequencyHours   == other.Statistic.FrequencyHours
                && _ch.Statistic.Side             == other.Statistic.Side
                && _ch.Statistic.Skip             == other.Statistic.Skip
                && _ch.Statistic.TopN             == other.Statistic.TopN
                && _ch.Statistic.Category         == other.Statistic.Category
                && _ch.Statistic.DistanceMin      == other.Statistic.DistanceMin
                && _ch.Statistic.DistanceMax      == other.Statistic.DistanceMax
                && _ch.Statistic.MinQuoteUsdt     == other.Statistic.MinQuoteUsdt
                && _ch.Statistic.HorizonSec       == other.Statistic.HorizonSec
                && _ch.Statistic.MessageFormat    == other.Statistic.MessageFormat
                && _ch.Statistic.DelayBetweenMessagesMs == other.Statistic.DelayBetweenMessagesMs
                && SymbolsEqual(_ch.Statistic.Symbols, other.Statistic.Symbols);
        }

        private static bool SymbolsEqual(string[] a, string[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        public void Start()
        {
            _task = Task.Run(RunAsync, _cts.Token);
        }

        public void Stop()
        {
            try { _cts.Cancel(); } catch { }
        }

        private async Task RunAsync()
        {
            var cfg = _ch.Statistic!;
            var interval = GetFrequencyInterval(cfg.FrequencyHours);

            // Frecvența de test pornește rapid, fără aliniere la ora fixă.
            var now     = DateTime.UtcNow;
            var nextRun = cfg.FrequencyHours <= 0
                ? now.Add(interval)
                : now.Date.AddHours(now.Hour + 1);

            _owner._log.LogInformation(
                "Statistic scheduler next run: channel={Channel} id={ChannelId} nextRunUtc={NextRunUtc:O} delay={Delay} frequency={Frequency}",
                _ch.Name,
                _ch.Id,
                nextRun,
                nextRun - now,
                DescribeFrequency(cfg.FrequencyHours));

            try
            {
                var initialDelay = nextRun - now;
                if (initialDelay > TimeSpan.Zero)
                    await Task.Delay(initialDelay, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _owner._log.LogInformation("Statistic scheduler initial wait cancelled: channel={Channel} id={ChannelId}",
                    _ch.Name, _ch.Id);
                return;
            }

            using var timer = new PeriodicTimer(interval);
            _owner._log.LogInformation("Statistic scheduler due: channel={Channel} id={ChannelId} dueUtc={DueUtc:O}",
                _ch.Name, _ch.Id, DateTime.UtcNow);
            await _owner.ExecuteAsync(_ch, _cts.Token).ConfigureAwait(false);
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
                {
                    _owner._log.LogInformation("Statistic scheduler due: channel={Channel} id={ChannelId} dueUtc={DueUtc:O}",
                        _ch.Name, _ch.Id, DateTime.UtcNow);
                    await _owner.ExecuteAsync(_ch, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                _owner._log.LogInformation("Statistic scheduler runner cancelled: channel={Channel} id={ChannelId}",
                    _ch.Name, _ch.Id);
            }
        }
    }
}
