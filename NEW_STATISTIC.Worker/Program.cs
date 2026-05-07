using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEW_STATISTIC.Core.Abstractions;
using NEW_STATISTIC.Core.Options;
using NEW_STATISTIC.Core.Services;
using NEW_STATISTIC.Infrastructure.Data;
using NEW_STATISTIC.Infrastructure.Exchange;
using NEW_STATISTIC.Infrastructure.Logging;
using NEW_STATISTIC.Infrastructure.Persistence;
using NEW_STATISTIC.Worker;
using NEW_STATISTIC.Worker.Internal;
using NEW_STATISTIC.Worker.Telegram;

var builder = Host.CreateApplicationBuilder(args);

// File logging — Warning+ în logs/worker-YYYYMMDD.log; nu interferă cu console/journalctl.
builder.Logging.AddFileLogger(builder.Configuration, builder.Environment.ContentRootPath, "worker");

var sqlitePath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "statistic.db"));
Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath)!);

builder.Services.Configure<TradingOptions>(builder.Configuration.GetSection(TradingOptions.SectionName));
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));

builder.Services.AddDbContextFactory<StatisticDbContext>(options =>
    options.UseSqlite($"Data Source={sqlitePath}")
           .AddInterceptors(new SqliteWalInterceptor()));

builder.Services.AddBinance();
builder.Services.AddBybit();

builder.Services.AddSingleton<BinanceUsdFuturesAggregateTradeSource>();
builder.Services.AddSingleton<BybitLinearTradeSource>();
builder.Services.AddSingleton<IExchangeAggregateTradeSource>(sp =>
{
    var opt = sp.GetRequiredService<IOptions<TradingOptions>>().Value;
    return opt.Exchange.Equals("Bybit", StringComparison.OrdinalIgnoreCase)
        ? sp.GetRequiredService<BybitLinearTradeSource>()
        : sp.GetRequiredService<BinanceUsdFuturesAggregateTradeSource>();
});

// Persistență
builder.Services.AddSingleton<ChannelPersistenceSink>();
builder.Services.AddSingleton<IPersistenceSink>(sp => sp.GetRequiredService<ChannelPersistenceSink>());
builder.Services.AddSingleton<EfCandlePersistence>();
builder.Services.AddSingleton<TradeIngestMetrics>();
builder.Services.AddSingleton<QuoteVolume24hStore>();
builder.Services.AddSingleton<IQuoteVolume24hProvider>(sp => sp.GetRequiredService<QuoteVolume24hStore>());

// ── Telegram (admin-driven) ──────────────────────────────────────────
builder.Services.AddSingleton<TelegramClient>();
builder.Services.AddSingleton<TelegramChannelStore>();
builder.Services.AddSingleton(sp =>
{
    var telegram = sp.GetRequiredService<IOptions<TelegramOptions>>().Value;
    var retentionHours = Math.Clamp(telegram.RecentShotsRetentionHours, 1, 72);
    return new RecentShotsBuffer(retentionHours * 60 * 60 * 1000);
});
builder.Services.AddSingleton<ShotObserverAdapter>();
builder.Services.AddSingleton<IShotObserver>(sp => sp.GetRequiredService<ShotObserverAdapter>());
builder.Services.AddSingleton<StatisticModeScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StatisticModeScheduler>());
builder.Services.AddHostedService<TriggerModeEvaluator>();

// Hosted services
builder.Services.AddHostedService<DatabaseMigrationHostedService>();
builder.Services.AddHostedService<PersistenceHostedService>();
builder.Services.AddHostedService<TradeMetricsLoggingHostedService>();
builder.Services.AddHostedService<MarketPipelineHostedService>();
builder.Services.AddHostedService<InternalHttpHost>();

var host = builder.Build();

// Initialize TelegramChannelStore eager (creează fișierul dacă lipsește, populează praguri).
host.Services.GetRequiredService<TelegramChannelStore>();
host.Services.GetRequiredService<ShotObserverAdapter>();

{
    var trading = host.Services.GetRequiredService<IOptions<TradingOptions>>().Value;
    host.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("NEW_STATISTIC.Worker")
        .LogInformation(
            ">>> LIVE TRADES: this process opens the WebSocket to {Exchange}. The API (NEW_STATISTIC.Api) does not — start Worker to see trade logs and fill the DB.",
            trading.Exchange);
}

host.Run();
