using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NEW_STATISTIC.Core.Options;
using NEW_STATISTIC.Infrastructure.Data;

namespace NEW_STATISTIC.Worker.Telegram;

/// <summary>
/// Construiește statistici per simbol pentru un canal statistic. Citește din SQLite,
/// folosește OutcomesJson din simulări (cea mai apropiată de DiffPercent al candle-ului)
/// pentru orizontul cerut, agreghează și returnează top-N după categorie.
/// </summary>
public static class ReportAggregator
{
    public sealed record SymbolStat(
        string Symbol,
        string Exchange,
        string DominantSide,    // "Buy" sau "Sell" (dominant între candle-urile profitabile)
        int Shots,
        int Tp,
        int Sl,
        double GainPct,
        double LossPct)
    {
        public double Net => GainPct - LossPct;
    }

    public static async Task<List<SymbolStat>> BuildAsync(
        StatisticDbContext db,
        TelegramStatisticConfig cfg,
        CancellationToken ct)
    {
        var sinceMs = DateTimeOffset.UtcNow.AddHours(-Math.Max(1, cfg.PeriodHours)).ToUnixTimeMilliseconds();
        var horizonKey = cfg.HorizonSec.ToString();

        // Semantica este IDENTICĂ cu /api/candles/buckets de pe dashboard:
        //   DistanceMin = nivelul bucketului (offsetul de intrare)
        //   Toate candle-urile cu DiffPercent >= DistanceMin contribuie la statisticile bucketului
        //   DistanceMax NU mai filtrează apex-ul (rămâne doar pentru naming)
        var q = db.Candles.AsNoTracking()
            .Where(c => c.TriggerTimeMs >= sinceMs && c.DiffPercent >= cfg.DistanceMin);

        if (cfg.MinQuoteUsdt > 0)
            q = q.Where(c => c.TotalQuoteUsdt >= cfg.MinQuoteUsdt);

        // Filtru Side la nivel SQL — eficient.
        if (cfg.Side == TelegramSideFilter.Buy)  q = q.Where(c => c.Side == "Buy");
        if (cfg.Side == TelegramSideFilter.Sell) q = q.Where(c => c.Side == "Sell");

        // Whitelist simboluri (presupus deja UPPERCASE — normalizat la salvare).
        if (cfg.Symbols is { Length: > 0 })
        {
            var allow = cfg.Symbols;
            q = q.Where(c => allow.Contains(c.Symbol));
        }

        var candles = await q
            .Include(c => c.Simulations)
            .AsSplitQuery()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var map = new Dictionary<string, Aggregator>(StringComparer.Ordinal);

        // Offset-ul țintă pentru simulare = DistanceMin (nivelul de intrare).
        // Ca să facem aceeași matematică ca dashboard-ul: pentru fiecare candle care
        // a atins ≥ DistanceMin, evaluăm simularea de la acel offset (NU de la apex-ul candle-ului).
        var targetOffset = Math.Max(0m, cfg.DistanceMin);

        foreach (var candle in candles)
        {
            var stat = ProcessCandle(candle, horizonKey, targetOffset);
            if (stat is null) continue;

            if (!map.TryGetValue(candle.Symbol, out var a))
            {
                a = new Aggregator { Exchange = candle.Exchange };
                map[candle.Symbol] = a;
            }
            // dacă nu am setat Exchange (entry vechi fără valoare), îl actualizăm
            if (string.IsNullOrEmpty(a.Exchange) && !string.IsNullOrEmpty(candle.Exchange))
                a.Exchange = candle.Exchange;

            a.Shots++;
            if (string.Equals(candle.Side, "Buy", StringComparison.Ordinal))      a.BuyCount++;
            else if (string.Equals(candle.Side, "Sell", StringComparison.Ordinal)) a.SellCount++;

            if      (stat.Value.Kind == 1) { a.Tp++; a.Gain += stat.Value.GainPct; }
            else if (stat.Value.Kind == 2) { a.Sl++; a.Loss += stat.Value.LossPct; }
        }

        return map
            .Select(kv => new SymbolStat(
                Symbol:       kv.Key,
                Exchange:     kv.Value.Exchange ?? "",
                DominantSide: kv.Value.BuyCount >= kv.Value.SellCount ? "Buy" : "Sell",
                Shots:        kv.Value.Shots,
                Tp:           kv.Value.Tp,
                Sl:           kv.Value.Sl,
                GainPct:      kv.Value.Gain,
                LossPct:      kv.Value.Loss))
            .ToList();
    }

    public static IEnumerable<SymbolStat> SelectTop(
        IEnumerable<SymbolStat> stats,
        TelegramStatisticCategory category,
        int topN,
        int skip = 0)
    {
        topN = Math.Max(1, topN);
        skip = Math.Max(0, skip);
        return category switch
        {
            TelegramStatisticCategory.Profitable => stats.Where(s => s.Net > 0).OrderByDescending(s => s.Net).Skip(skip).Take(topN),
            TelegramStatisticCategory.Losing     => stats.Where(s => s.Net < 0).OrderBy(s => s.Net).Skip(skip).Take(topN),
            TelegramStatisticCategory.Active     => stats.OrderByDescending(s => s.Shots).Skip(skip).Take(topN),
            _ => Enumerable.Empty<SymbolStat>()
        };
    }

    private sealed class Aggregator
    {
        public string? Exchange;
        public int Shots, Tp, Sl;
        public int BuyCount, SellCount;
        public double Gain, Loss;
    }

    private static (int Kind, double GainPct, double LossPct)? ProcessCandle(
        CandleEntity candle, string horizonKey, decimal targetOffset)
    {
        if (candle.Simulations.Count == 0) return null;

        // Aceeași logică ca dashboard FindClosestSim: simularea cu OpenOffsetPercent
        // cel mai apropiat de targetOffset (bucketul de intrare), tolerață 0.06%.
        SimulationEntity? best = null;
        decimal bestDiff = decimal.MaxValue;
        foreach (var s in candle.Simulations)
        {
            var d = Math.Abs(s.OpenOffsetPercent - targetOffset);
            if (d < bestDiff) { bestDiff = d; best = s; }
        }
        if (best is null || bestDiff > 0.06m || best.OpenPrice == 0) return null;

        Dictionary<string, JsonElement>? outcomes;
        try { outcomes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(best.OutcomesJson); }
        catch { return null; }
        if (outcomes is null || !outcomes.TryGetValue(horizonKey, out var oel)) return null;

        var kind          = oel.TryGetProperty("k", out var kEl) ? kEl.GetInt32() : 0;
        var gainPct       = (double)(Math.Abs(best.TakeProfitPrice - best.OpenPrice) / best.OpenPrice * 100m);
        var theoreticalL  = (double)(Math.Abs(best.OpenPrice - best.StopLossPrice)   / best.OpenPrice * 100m);
        var lossPct       = theoreticalL;

        if (kind == 2 && oel.TryGetProperty("p", out var pEl) && pEl.ValueKind == JsonValueKind.Number)
            lossPct = (double)(Math.Abs(best.OpenPrice - pEl.GetDecimal()) / best.OpenPrice * 100m);

        return (kind, gainPct, lossPct);
    }
}
