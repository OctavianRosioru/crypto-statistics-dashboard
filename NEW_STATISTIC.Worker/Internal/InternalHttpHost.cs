using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NEW_STATISTIC.Worker.Telegram;

namespace NEW_STATISTIC.Worker.Internal;

/// <summary>
/// Mic Kestrel host pe 127.0.0.1:{port} care expune endpoint-uri "interne" pentru
/// comunicarea Api → Worker (nu pentru clienți externi). Bind-ul pe loopback blochează
/// accesul din afara mașinii; nu folosim auth.
/// </summary>
public sealed class InternalHttpHost : BackgroundService
{
    private readonly TelegramChannelStore _store;
    private readonly StatisticModeScheduler _scheduler;
    private readonly TelegramClient _telegram;
    private readonly ILogger<InternalHttpHost> _log;
    private readonly int _port;

    public InternalHttpHost(
        TelegramChannelStore store,
        StatisticModeScheduler scheduler,
        TelegramClient telegram,
        IConfiguration config,
        ILogger<InternalHttpHost> log)
    {
        _store = store;
        _scheduler = scheduler;
        _telegram = telegram;
        _log = log;
        _port = config.GetValue<int?>("Worker:InternalPort") ?? 5099;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        builder.WebHost.ConfigureKestrel(opt =>
        {
            opt.ListenLocalhost(_port);
        });

        var app = builder.Build();

        app.MapPost("/internal/telegram/reload", async () =>
        {
            await _store.ReloadAsync().ConfigureAwait(false);
            return Results.Ok(new { reloaded = true, channels = _store.Snapshot.Count });
        });

        app.MapPost("/internal/telegram/run-now/{id}", async (string id, CancellationToken ct) =>
        {
            await _scheduler.RunOnceAsync(id, ct).ConfigureAwait(false);
            return Results.Ok(new { ranNow = true, id });
        });

        app.MapPost("/internal/telegram/test/{id}", async (string id, CancellationToken ct) =>
        {
            var ch = _store.Snapshot.FirstOrDefault(c => c.Id == id);
            if (ch is null) return Results.NotFound();
            var ok = await _telegram.SendAsync(ch.ChatId,
                $"✅ Test din NEW_STATISTIC pentru canalul \"{ch.Name}\".",
                parseMode: null, ct).ConfigureAwait(false);
            return Results.Ok(new { sent = ok });
        });

        app.MapGet("/internal/health", () => Results.Ok(new { ok = true }));

        _log.LogInformation("Internal HTTP host listening on http://127.0.0.1:{Port}", _port);

        try { await app.RunAsync(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
