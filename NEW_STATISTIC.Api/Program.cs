using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using NEW_STATISTIC.Api;
using NEW_STATISTIC.Api.Admin;
using NEW_STATISTIC.Api.Auth;
using NEW_STATISTIC.Infrastructure.Data;
using NEW_STATISTIC.Infrastructure.Logging;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddFileLogger(builder.Configuration, builder.Environment.ContentRootPath, "api");

var sqlitePath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "statistic.db"));
Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath)!);

builder.Services.AddDbContextFactory<StatisticDbContext>(options =>
    options.UseSqlite($"Data Source={sqlitePath}"));

builder.Services.AddHealthChecks().AddCheck<StatisticDbHealthCheck>("database");

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddSingleton<ChannelsFileService>();
builder.Services.AddSingleton<WorkerNotifier>();

var app = builder.Build();

{
    var factory = app.Services.GetRequiredService<IDbContextFactory<StatisticDbContext>>();
    await using var db = await factory.CreateDbContextAsync().ConfigureAwait(false);
    await db.Database.MigrateAsync().ConfigureAwait(false);
    var apiLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NEW_STATISTIC.Api");
    apiLog.LogInformation("Database migrated (same file as Worker: repo root statistic.db).");
    apiLog.LogInformation(
        "HTTP API only — no WebSocket. Live Binance/Bybit trades run in NEW_STATISTIC.Worker; start it to stream market data.");
}

app.UseCors();

// Basic Auth pe /admin.html și /api/admin/* — pus ÎNAINTE de StaticFiles ca să blocheze servirea HTML-ului.
app.UseMiddleware<BasicAuthMiddleware>();

app.MapHealthChecks("/health");

app.MapTelegramAdmin();

var frontendRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "frontend"));
PhysicalFileProvider? frontendProvider = null;
if (Directory.Exists(frontendRoot))
{
    frontendProvider = new PhysicalFileProvider(frontendRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = frontendProvider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = frontendProvider });
}

// --- Helper: parsează simboluri din query string ---
static string[] ParseSymbols(string? symbols) =>
    symbols?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

// --- Helper: aplică filtru whitelist/blacklist pe query ---
static IQueryable<CandleEntity> ApplySymbolFilter(IQueryable<CandleEntity> q, string[] symList, bool blacklist) =>
    symList.Length == 0 ? q :
    blacklist ? q.Where(c => !symList.Contains(c.Symbol))
              : q.Where(c => symList.Contains(c.Symbol));

static int NormalizeQavLookbackMinutes(int? minutes) => minutes is 1 or 5 or 15 or 30 or 60 or 180 or 360 or 720 or 1440
    ? minutes.Value
    : 0;

static decimal? QavChangeFor(CandleEntity c, int minutes) => minutes switch
{
    1 => c.QuoteVolume24hChange1mPct,
    5 => c.QuoteVolume24hChange5mPct,
    15 => c.QuoteVolume24hChange15mPct,
    30 => c.QuoteVolume24hChange30mPct,
    60 => c.QuoteVolume24hChange1hPct,
    180 => c.QuoteVolume24hChange3hPct,
    360 => c.QuoteVolume24hChange6hPct,
    720 => c.QuoteVolume24hChange12hPct,
    1440 => c.QuoteVolume24hChange24hPct,
    _ => null
};

static bool QavFilterPasses(CandleEntity c, int? lookbackMinutes, decimal? minPercent, decimal? maxPercent)
{
    var lookback = NormalizeQavLookbackMinutes(lookbackMinutes);
    if (lookback <= 0 || (minPercent is null && maxPercent is null))
        return true;

    var change = QavChangeFor(c, lookback);
    if (change is null) return false;
    if (minPercent is not null && change.Value < minPercent.Value) return false;
    if (maxPercent is not null && change.Value > maxPercent.Value) return false;
    return true;
}

// --- Helper: simularea cu openOffsetPercent cel mai apropiat de target (binary search) ---
static SimulationEntity? FindClosestSim(List<SimulationEntity> sorted, decimal target)
{
    if (sorted.Count == 0) return null;
    int lo = 0, hi = sorted.Count - 1;
    while (lo < hi)
    {
        var mid = (lo + hi) / 2;
        if (sorted[mid].OpenOffsetPercent < target) lo = mid + 1;
        else hi = mid;
    }
    if (lo > 0 && Math.Abs(sorted[lo - 1].OpenOffsetPercent - target) < Math.Abs(sorted[lo].OpenOffsetPercent - target))
        return sorted[lo - 1];
    return sorted[lo];
}

// -------------------------------------------------------
// GET /api/candles
// -------------------------------------------------------
app.MapGet("/api/candles", async (
    IDbContextFactory<StatisticDbContext> factory,
    int? take,
    string? symbols,
    bool? blacklist,
    CancellationToken ct) =>
{
    var n = Math.Clamp(take ?? 50, 1, 500);
    var symList = ParseSymbols(symbols);

    await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
    var q = ApplySymbolFilter(db.Candles.AsNoTracking(), symList, blacklist ?? false);

    var list = await q
        .OrderByDescending(c => c.Id)
        .Take(n)
        .Select(c => new
        {
            c.Id,
            c.Exchange,
            c.Symbol,
            c.TriggerTimeMs,
            c.WindowStartMs,
            c.WindowEndMs,
            c.LastTradeTimeInWindowMs,
            c.MinPrice,
            c.MaxPrice,
            c.DiffPercent,
            c.Side,
            c.TotalQuoteUsdt,
            c.DensityUsdtPerMs,
            c.QuoteVolume24hUsdt,
            c.QuoteVolume24hUpdatedMs,
            c.QuoteVolume24hChange1mPct,
            c.QuoteVolume24hChange5mPct,
            c.QuoteVolume24hChange15mPct,
            c.QuoteVolume24hChange30mPct,
            c.QuoteVolume24hChange1hPct,
            c.QuoteVolume24hChange3hPct,
            c.QuoteVolume24hChange6hPct,
            c.QuoteVolume24hChange12hPct,
            c.QuoteVolume24hChange24hPct,
            c.CreatedAt
        })
        .ToListAsync(ct)
        .ConfigureAwait(false);

    return Results.Json(list);
});

// -------------------------------------------------------
// GET /api/candles/with-simulations
// Returnează candle-uri cu simulările embedded — pentru bucket view (fără N+1).
// -------------------------------------------------------
app.MapGet("/api/candles/with-simulations", async (
    IDbContextFactory<StatisticDbContext> factory,
    int? take,
    long? since,
    string? symbols,
    bool? blacklist,
    CancellationToken ct) =>
{
    var n = Math.Clamp(take ?? 5_000, 1, 20_000);
    var symList = ParseSymbols(symbols);

    await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
    var q = ApplySymbolFilter(db.Candles.AsNoTracking(), symList, blacklist ?? false);
    if (since.HasValue)
        q = q.Where(c => c.TriggerTimeMs >= since.Value);

    var candles = await q
        .OrderByDescending(c => c.Id)
        .Take(n)
        .Include(c => c.Simulations)
        .ToListAsync(ct)
        .ConfigureAwait(false);

    var result = candles.Select(c => new
    {
        c.Id,
        c.Exchange,
        c.Symbol,
        c.TriggerTimeMs,
        c.WindowStartMs,
        c.WindowEndMs,
        c.DiffPercent,
        c.Side,
        c.MinPrice,
        c.MaxPrice,
        c.TotalQuoteUsdt,
        c.DensityUsdtPerMs,
        c.QuoteVolume24hUsdt,
        c.QuoteVolume24hUpdatedMs,
        c.QuoteVolume24hChange1mPct,
        c.QuoteVolume24hChange5mPct,
        c.QuoteVolume24hChange15mPct,
        c.QuoteVolume24hChange30mPct,
        c.QuoteVolume24hChange1hPct,
        c.QuoteVolume24hChange3hPct,
        c.QuoteVolume24hChange6hPct,
        c.QuoteVolume24hChange12hPct,
        c.QuoteVolume24hChange24hPct,
        c.CreatedAt,
        Simulations = c.Simulations
            .OrderBy(s => s.OpenOffsetPercent)
            .Select(s => new
            {
                s.OpenOffsetPercent,
                s.OpenPrice,
                s.TakeProfitPrice,
                s.StopLossPrice,
                Outcomes = JsonSerializer.Deserialize<JsonElement>(s.OutcomesJson)
            })
            .ToList()
    });

    return Results.Json(result);
});

// -------------------------------------------------------
// GET /api/candles/{id:long}
// -------------------------------------------------------
app.MapGet("/api/candles/{id:long}", async (
    long id,
    IDbContextFactory<StatisticDbContext> factory,
    CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
    var c = await db.Candles.AsNoTracking()
        .Where(x => x.Id == id)
        .Select(x => new
        {
            x.Id,
            x.Exchange,
            x.Symbol,
            x.TriggerTimeMs,
            x.WindowStartMs,
            x.WindowEndMs,
            x.LastTradeTimeInWindowMs,
            x.MinPrice,
            x.MaxPrice,
            x.DiffPercent,
            x.Side,
            x.FirstTradePrice,
            x.TotalQuoteUsdt,
            x.DensityUsdtPerMs,
            x.QuoteVolume24hUsdt,
            x.QuoteVolume24hUpdatedMs,
            x.QuoteVolume24hChange1mPct,
            x.QuoteVolume24hChange5mPct,
            x.QuoteVolume24hChange15mPct,
            x.QuoteVolume24hChange30mPct,
            x.QuoteVolume24hChange1hPct,
            x.QuoteVolume24hChange3hPct,
            x.QuoteVolume24hChange6hPct,
            x.QuoteVolume24hChange12hPct,
            x.QuoteVolume24hChange24hPct,
            x.FollowUpJson,
            x.CreatedAt
        })
        .FirstOrDefaultAsync(ct)
        .ConfigureAwait(false);
    return c is null ? Results.NotFound() : Results.Json(c);
});

// -------------------------------------------------------
// GET /api/candles/{id:long}/simulations
// -------------------------------------------------------
app.MapGet("/api/candles/{id:long}/simulations", async (
    long id,
    IDbContextFactory<StatisticDbContext> factory,
    CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
    var rows = await db.Simulations.AsNoTracking()
        .Where(s => s.CandleId == id)
        .OrderBy(s => s.OpenOffsetPercent)
        .Select(s => new
        {
            s.Id,
            s.OpenOffsetPercent,
            s.OpenPrice,
            s.TakeProfitPrice,
            s.StopLossPrice,
            s.OutcomesJson
        })
        .ToListAsync(ct)
        .ConfigureAwait(false);
    return Results.Json(rows);
});

// -------------------------------------------------------
// GET /api/symbols/activity
// Simboluri grupate cu statistici, sortate după max DiffPercent.
// -------------------------------------------------------
app.MapGet("/api/symbols/activity", async (
    IDbContextFactory<StatisticDbContext> factory,
    string? symbols,
    bool? blacklist,
    long? since,
    int? qavChangeLookbackMinutes,
    decimal? qavChangeMinPercent,
    decimal? qavChangeMaxPercent,
    CancellationToken ct) =>
{
    var symList = ParseSymbols(symbols);

    await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
    var q = ApplySymbolFilter(db.Candles.AsNoTracking(), symList, blacklist ?? false);
    if (since.HasValue)
        q = q.Where(c => c.TriggerTimeMs >= since.Value);

    // Agregare in-memory: evitam probleme de traducere SQL pentru decimal pe SQLite.
    var rows = await q
        .Select(c => new
        {
            c.Symbol,
            c.DiffPercent,
            c.TotalQuoteUsdt,
            c.TriggerTimeMs,
            c.QuoteVolume24hUsdt,
            c.QuoteVolume24hUpdatedMs,
            c.QuoteVolume24hChange1mPct,
            c.QuoteVolume24hChange5mPct,
            c.QuoteVolume24hChange15mPct,
            c.QuoteVolume24hChange30mPct,
            c.QuoteVolume24hChange1hPct,
            c.QuoteVolume24hChange3hPct,
            c.QuoteVolume24hChange6hPct,
            c.QuoteVolume24hChange12hPct,
            c.QuoteVolume24hChange24hPct
        })
        .ToListAsync(ct)
        .ConfigureAwait(false);

    var result = rows
        .Where(c => QavFilterPasses(new CandleEntity
        {
            QuoteVolume24hChange1mPct = c.QuoteVolume24hChange1mPct,
            QuoteVolume24hChange5mPct = c.QuoteVolume24hChange5mPct,
            QuoteVolume24hChange15mPct = c.QuoteVolume24hChange15mPct,
            QuoteVolume24hChange30mPct = c.QuoteVolume24hChange30mPct,
            QuoteVolume24hChange1hPct = c.QuoteVolume24hChange1hPct,
            QuoteVolume24hChange3hPct = c.QuoteVolume24hChange3hPct,
            QuoteVolume24hChange6hPct = c.QuoteVolume24hChange6hPct,
            QuoteVolume24hChange12hPct = c.QuoteVolume24hChange12hPct,
            QuoteVolume24hChange24hPct = c.QuoteVolume24hChange24hPct
        }, qavChangeLookbackMinutes, qavChangeMinPercent, qavChangeMaxPercent))
        .GroupBy(c => c.Symbol)
        .Select(g =>
        {
            var latest = g.OrderByDescending(c => c.TriggerTimeMs).First();
            return new
            {
                Symbol = g.Key,
                CandleCount = g.Count(),
                MaxDiff = g.Max(c => c.DiffPercent),
                AvgDiff = g.Average(c => (double)c.DiffPercent),
                TotalQuoteUsdt = g.Sum(c => c.TotalQuoteUsdt),
                LastTriggerMs = latest.TriggerTimeMs,
                latest.QuoteVolume24hUsdt,
                latest.QuoteVolume24hUpdatedMs,
                latest.QuoteVolume24hChange1mPct,
                latest.QuoteVolume24hChange5mPct,
                latest.QuoteVolume24hChange15mPct,
                latest.QuoteVolume24hChange30mPct,
                latest.QuoteVolume24hChange1hPct,
                latest.QuoteVolume24hChange3hPct,
                latest.QuoteVolume24hChange6hPct,
                latest.QuoteVolume24hChange12hPct,
                latest.QuoteVolume24hChange24hPct
            };
        })
        .OrderByDescending(x => x.MaxDiff)
        .ToList();

    return Results.Json(result);
});

// -------------------------------------------------------
// GET /api/candles/buckets
// Agregare server-side pe bucket-uri de offset% — fără date raw la client.
// -------------------------------------------------------
app.MapGet("/api/candles/buckets", async (
    IDbContextFactory<StatisticDbContext> factory,
    string?  symbols,
    bool?    blacklist,
    string?  side,
    long?    since,
    decimal? distanceMin,
    decimal? distanceMax,
    decimal? step,
    int?     horizon,
    int?     topN,
    decimal? minDensity,
    int?     qavChangeLookbackMinutes,
    decimal? qavChangeMinPercent,
    decimal? qavChangeMaxPercent,
    CancellationToken ct) =>
{
    var symList    = ParseSymbols(symbols);
    var stepVal    = Math.Max(0.05m, step ?? 0.5m);
    var dMin       = distanceMin ?? 0m;
    var dMax       = distanceMax ?? 0m;
    var horizonKey = (horizon ?? 300).ToString();
    var topNVal    = Math.Max(1, topN ?? 50);

    await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
    var q = ApplySymbolFilter(db.Candles.AsNoTracking(), symList, blacklist ?? false);

    var effectiveDMin = dMin > 0 ? dMin : stepVal;
    q = q.Where(c => c.DiffPercent >= effectiveDMin);
    if (since.HasValue)    q = q.Where(c => c.TriggerTimeMs  >= since.Value);
    if (dMax > 0)          q = q.Where(c => c.DiffPercent    <  dMax);
    if (minDensity is > 0) q = q.Where(c => c.TotalQuoteUsdt >= minDensity.Value);
    if (!string.IsNullOrEmpty(side) && side != "all")
        q = q.Where(c => c.Side == side);

    var candles = await q
        .AsSplitQuery()
        .Include(c => c.Simulations)
        .ToListAsync(ct)
        .ConfigureAwait(false);

    candles = candles
        .Where(c => QavFilterPasses(c, qavChangeLookbackMinutes, qavChangeMinPercent, qavChangeMaxPercent))
        .ToList();

    if (candles.Count == 0)
        return Results.Json(Array.Empty<object>());

    var maxDiffAll  = candles.Max(c => c.DiffPercent);
    var bucketStart = dMin > 0 ? dMin : stepVal;
    var bucketEnd   = dMax > 0 ? dMax - 0.001m : maxDiffAll;

    var bucketValues = new List<decimal>();
    for (var bv = bucketStart; bv <= bucketEnd + 0.0001m; bv += stepVal)
        bucketValues.Add(Math.Round(bv, 2));

    if (bucketValues.Count == 0)
        return Results.Json(Array.Empty<object>());

    var bucketMap = new Dictionary<decimal, BucketAgg>();
    foreach (var bv in bucketValues)
        bucketMap[bv] = new BucketAgg(bv);

    foreach (var candle in candles)
    {
        if (candle.Simulations.Count == 0) continue;
        var sims = candle.Simulations.OrderBy(s => s.OpenOffsetPercent).ToList();

        foreach (var bv in bucketValues)
        {
            if (candle.DiffPercent < bv) break;

            var best = FindClosestSim(sims, bv);
            if (best is null || Math.Abs(best.OpenOffsetPercent - bv) > 0.06m) continue;

            Dictionary<string, JsonElement>? outcomes;
            try   { outcomes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(best.OutcomesJson); }
            catch { continue; }
            if (outcomes is null || !outcomes.TryGetValue(horizonKey, out var oel)) continue;

            var kind    = oel.TryGetProperty("k", out var kEl) ? kEl.GetInt32() : 0;
            var gainPct = best.OpenPrice > 0 ? (double)(Math.Abs(best.TakeProfitPrice - best.OpenPrice) / best.OpenPrice * 100m) : 0;
            var lossPct = best.OpenPrice > 0 ? (double)(Math.Abs(best.OpenPrice - best.StopLossPrice)   / best.OpenPrice * 100m) : 0;
            if (kind == 2 && oel.TryGetProperty("p", out var pEl) && pEl.ValueKind == JsonValueKind.Number && best.OpenPrice > 0)
                lossPct = (double)(Math.Abs(best.OpenPrice - pEl.GetDecimal()) / best.OpenPrice * 100m);

            var bucket = bucketMap[bv];
            if (!bucket.Symbols.TryGetValue(candle.Symbol, out var symAgg))
            {
                symAgg = new SymbolAgg();
                bucket.Symbols[candle.Symbol] = symAgg;
            }
            if (candle.TriggerTimeMs >= symAgg.LatestTriggerTimeMs)
            {
                symAgg.LatestTriggerTimeMs = candle.TriggerTimeMs;
                symAgg.QuoteVolume24hUsdt = candle.QuoteVolume24hUsdt;
                symAgg.QuoteVolume24hUpdatedMs = candle.QuoteVolume24hUpdatedMs;
                symAgg.QuoteVolume24hChange1mPct = candle.QuoteVolume24hChange1mPct;
                symAgg.QuoteVolume24hChange5mPct = candle.QuoteVolume24hChange5mPct;
                symAgg.QuoteVolume24hChange15mPct = candle.QuoteVolume24hChange15mPct;
                symAgg.QuoteVolume24hChange30mPct = candle.QuoteVolume24hChange30mPct;
                symAgg.QuoteVolume24hChange1hPct = candle.QuoteVolume24hChange1hPct;
                symAgg.QuoteVolume24hChange3hPct = candle.QuoteVolume24hChange3hPct;
                symAgg.QuoteVolume24hChange6hPct = candle.QuoteVolume24hChange6hPct;
                symAgg.QuoteVolume24hChange12hPct = candle.QuoteVolume24hChange12hPct;
                symAgg.QuoteVolume24hChange24hPct = candle.QuoteVolume24hChange24hPct;
            }

            symAgg.Shots++;
            if      (kind == 1) { bucket.Tp++;   symAgg.Tp++;   bucket.TpPnl += gainPct; symAgg.TpPnl += gainPct; }
            else if (kind == 2) { bucket.Sl++;   symAgg.Sl++;   bucket.SlPnl += lossPct; symAgg.SlPnl += lossPct; }
            else                { bucket.None++; symAgg.None++;                                                     }
        }
    }

    var result = bucketMap.Values
        .Where(b => b.Tp + b.Sl + b.None > 0)
        .OrderBy(b => b.OffsetPct)
        .Select(b => new
        {
            OffsetPct = b.OffsetPct,
            Total     = b.Tp + b.Sl + b.None,
            b.Tp,
            b.Sl,
            b.None,
            TpPnl  = Math.Round(b.TpPnl, 2),
            SlPnl  = Math.Round(b.SlPnl, 2),
            NetPnl = Math.Round(b.TpPnl - b.SlPnl, 2),
            Symbols = b.Symbols
                .Select(kv => new
                {
                    Symbol = kv.Key,
                    kv.Value.Shots,
                    kv.Value.Tp,
                    kv.Value.Sl,
                    kv.Value.None,
                    TpPnl  = Math.Round(kv.Value.TpPnl, 2),
                    SlPnl  = Math.Round(kv.Value.SlPnl, 2),
                    NetPnl = Math.Round(kv.Value.TpPnl - kv.Value.SlPnl, 2),
                    kv.Value.QuoteVolume24hUsdt,
                    kv.Value.QuoteVolume24hUpdatedMs,
                    kv.Value.QuoteVolume24hChange1mPct,
                    kv.Value.QuoteVolume24hChange5mPct,
                    kv.Value.QuoteVolume24hChange15mPct,
                    kv.Value.QuoteVolume24hChange30mPct,
                    kv.Value.QuoteVolume24hChange1hPct,
                    kv.Value.QuoteVolume24hChange3hPct,
                    kv.Value.QuoteVolume24hChange6hPct,
                    kv.Value.QuoteVolume24hChange12hPct,
                    kv.Value.QuoteVolume24hChange24hPct
                })
                .OrderByDescending(x => x.Shots)
                .Take(topNVal)
                .ToList()
        })
        .ToList();

    return Results.Json(result);
});

// -------------------------------------------------------
// GET /api/candles/shots
// Shots individuale per simbol + offset — lazy load din frontend.
// -------------------------------------------------------
app.MapGet("/api/candles/shots", async (
    IDbContextFactory<StatisticDbContext> factory,
    string?  symbol,
    decimal? offsetPct,
    string?  side,
    long?    since,
    decimal? distanceMax,
    int?     horizon,
    decimal? minDensity,
    int?     qavChangeLookbackMinutes,
    decimal? qavChangeMinPercent,
    decimal? qavChangeMaxPercent,
    int?     take,
    CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(symbol) || offsetPct is null)
        return Results.BadRequest(new { error = "symbol and offsetPct are required" });

    var horizonKey = (horizon ?? 300).ToString();
    var n          = Math.Clamp(take ?? 500, 1, 2000);
    var target     = offsetPct.Value;

    await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
    var q = db.Candles.AsNoTracking()
        .Where(c => c.Symbol == symbol && c.DiffPercent >= target);

    if (since.HasValue)    q = q.Where(c => c.TriggerTimeMs  >= since.Value);
    if (distanceMax is > 0) q = q.Where(c => c.DiffPercent   <  distanceMax.Value);
    if (minDensity  is > 0) q = q.Where(c => c.TotalQuoteUsdt >= minDensity.Value);
    if (!string.IsNullOrEmpty(side) && side != "all")
        q = q.Where(c => c.Side == side);

    var candles = await q
        .OrderByDescending(c => c.TriggerTimeMs)
        .Take(n)
        .AsSplitQuery()
        .Include(c => c.Simulations)
        .ToListAsync(ct)
        .ConfigureAwait(false);

    var shots = new List<object>();
    foreach (var candle in candles)
    {
        if (!QavFilterPasses(candle, qavChangeLookbackMinutes, qavChangeMinPercent, qavChangeMaxPercent))
            continue;

        var sims = candle.Simulations.OrderBy(s => s.OpenOffsetPercent).ToList();
        var best = FindClosestSim(sims, target);
        if (best is null || Math.Abs(best.OpenOffsetPercent - target) > 0.06m) continue;

        Dictionary<string, JsonElement>? outcomes;
        try   { outcomes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(best.OutcomesJson); }
        catch { continue; }
        if (outcomes is null || !outcomes.TryGetValue(horizonKey, out var oel)) continue;

        var kind    = oel.TryGetProperty("k", out var kEl) ? kEl.GetInt32() : 0;
        var gainPct = best.OpenPrice > 0 ? (double)(Math.Abs(best.TakeProfitPrice - best.OpenPrice) / best.OpenPrice * 100m) : 0;
        var lossPct = best.OpenPrice > 0 ? (double)(Math.Abs(best.OpenPrice - best.StopLossPrice)   / best.OpenPrice * 100m) : 0;
        var theoPct = lossPct;
        if (kind == 2 && oel.TryGetProperty("p", out var pEl) && pEl.ValueKind == JsonValueKind.Number && best.OpenPrice > 0)
            lossPct = (double)(Math.Abs(best.OpenPrice - pEl.GetDecimal()) / best.OpenPrice * 100m);

        shots.Add(new
        {
            Symbol         = candle.Symbol,
            Side           = candle.Side,
            DiffPct        = candle.DiffPercent,
            TotalQuoteUsdt = candle.TotalQuoteUsdt,
            TriggerTimeMs  = candle.TriggerTimeMs,
            Outcome        = kind,
            PnlPct         = Math.Round(kind == 1 ? gainPct : kind == 2 ? lossPct : 0, 4),
            SlipPct        = Math.Round(kind == 2 ? Math.Max(0, lossPct - theoPct) : 0, 4),
            candle.QuoteVolume24hUsdt,
            candle.QuoteVolume24hUpdatedMs,
            candle.QuoteVolume24hChange1mPct,
            candle.QuoteVolume24hChange5mPct,
            candle.QuoteVolume24hChange15mPct,
            candle.QuoteVolume24hChange30mPct,
            candle.QuoteVolume24hChange1hPct,
            candle.QuoteVolume24hChange3hPct,
            candle.QuoteVolume24hChange6hPct,
            candle.QuoteVolume24hChange12hPct,
            candle.QuoteVolume24hChange24hPct,
        });
    }

    return Results.Json(shots);
});

if (frontendProvider is not null)
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = frontendProvider });

app.Run();

// ── Clase helper ─────────────────────────────────────────────────────────────
file sealed class BucketAgg(decimal offsetPct)
{
    public decimal OffsetPct { get; } = offsetPct;
    public int     Tp        { get; set; }
    public int     Sl        { get; set; }
    public int     None      { get; set; }
    public double  TpPnl     { get; set; }
    public double  SlPnl     { get; set; }
    public Dictionary<string, SymbolAgg> Symbols { get; } = new(StringComparer.Ordinal);
}

file sealed class SymbolAgg
{
    public int    Shots { get; set; }
    public int    Tp    { get; set; }
    public int    Sl    { get; set; }
    public int    None  { get; set; }
    public double TpPnl { get; set; }
    public double SlPnl { get; set; }
    public long LatestTriggerTimeMs { get; set; }
    public decimal? QuoteVolume24hUsdt { get; set; }
    public long? QuoteVolume24hUpdatedMs { get; set; }
    public decimal? QuoteVolume24hChange1mPct { get; set; }
    public decimal? QuoteVolume24hChange5mPct { get; set; }
    public decimal? QuoteVolume24hChange15mPct { get; set; }
    public decimal? QuoteVolume24hChange30mPct { get; set; }
    public decimal? QuoteVolume24hChange1hPct { get; set; }
    public decimal? QuoteVolume24hChange3hPct { get; set; }
    public decimal? QuoteVolume24hChange6hPct { get; set; }
    public decimal? QuoteVolume24hChange12hPct { get; set; }
    public decimal? QuoteVolume24hChange24hPct { get; set; }
}
