using NEW_STATISTIC.Core.Options;

namespace NEW_STATISTIC.Api.Admin;

public static class TelegramAdminEndpoints
{
    /// <summary>
    /// Normalizează câmpurile sensibile la case (în special listele de simboluri) la UPPERCASE,
    /// ca să se potrivească cu Symbol-ul stocat în DB indiferent cum a tastat utilizatorul.
    /// Aplicată ÎNAINTE de scriere — datele de pe disc sunt mereu canonice.
    /// </summary>
    private static void NormalizeChannel(TelegramChannel ch)
    {
        if (ch.Trigger is { } t)
        {
            t.Exchange = string.IsNullOrWhiteSpace(t.Exchange) ? "*" : t.Exchange.Trim().ToUpperInvariant();
            t.Symbols  = (t.Symbols ?? Array.Empty<string>())
                .Select(s => s?.Trim().ToUpperInvariant() ?? "")
                .Where(s => s.Length > 0)
                .Distinct()
                .ToArray();
        }
        if (ch.Statistic is { } s)
        {
            s.Symbols = (s.Symbols ?? Array.Empty<string>())
                .Select(x => x?.Trim().ToUpperInvariant() ?? "")
                .Where(x => x.Length > 0)
                .Distinct()
                .ToArray();
        }
    }

    public static IEndpointRouteBuilder MapTelegramAdmin(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/telegram/channels", (ChannelsFileService svc) =>
        {
            var file = svc.Read();
            return Results.Json(file.Channels);
        });

        app.MapPost("/api/admin/telegram/channels", async (
            TelegramChannel input,
            ChannelsFileService svc,
            WorkerNotifier worker,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.ChatId))
                return Results.BadRequest(new { error = "name and chatId required" });

            NormalizeChannel(input);

            var file = svc.Read();
            input.Id = string.IsNullOrEmpty(input.Id) ? Guid.NewGuid().ToString("N") : input.Id;
            file.Channels.Add(input);
            svc.Write(file);
            await worker.ReloadAsync(ct).ConfigureAwait(false);
            return Results.Json(input);
        });

        app.MapPut("/api/admin/telegram/channels/{id}", async (
            string id,
            TelegramChannel input,
            ChannelsFileService svc,
            WorkerNotifier worker,
            CancellationToken ct) =>
        {
            NormalizeChannel(input);

            var file = svc.Read();
            var idx = file.Channels.FindIndex(c => c.Id == id);
            if (idx < 0) return Results.NotFound();
            input.Id = id;
            file.Channels[idx] = input;
            svc.Write(file);
            await worker.ReloadAsync(ct).ConfigureAwait(false);
            return Results.Json(input);
        });

        app.MapDelete("/api/admin/telegram/channels/{id}", async (
            string id,
            ChannelsFileService svc,
            WorkerNotifier worker,
            CancellationToken ct) =>
        {
            var file = svc.Read();
            var idx = file.Channels.FindIndex(c => c.Id == id);
            if (idx < 0) return Results.NotFound();
            file.Channels.RemoveAt(idx);
            svc.Write(file);
            await worker.ReloadAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { deleted = id });
        });

        app.MapPost("/api/admin/telegram/channels/{id}/test", async (
            string id,
            WorkerNotifier worker,
            CancellationToken ct) =>
        {
            var ok = await worker.TestAsync(id, ct).ConfigureAwait(false);
            return Results.Json(new { sent = ok });
        });

        app.MapPost("/api/admin/telegram/channels/{id}/run-now", async (
            string id,
            WorkerNotifier worker,
            CancellationToken ct) =>
        {
            var ok = await worker.RunNowAsync(id, ct).ConfigureAwait(false);
            return Results.Json(new { ranNow = ok });
        });

        // Endpoint pentru ca UI-ul să afișeze user-ul curent (după login).
        app.MapGet("/api/admin/whoami", (HttpContext ctx) =>
            Results.Json(new { user = "admin", path = ctx.Request.Path.Value }));

        return app;
    }
}
