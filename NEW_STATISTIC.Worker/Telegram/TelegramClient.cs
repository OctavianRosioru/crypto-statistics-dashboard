using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NEW_STATISTIC.Core.Options;

namespace NEW_STATISTIC.Worker.Telegram;

/// <summary>
/// Wrapper minimal peste Telegram Bot HTTP API. Singleton; reutilizează un singur HttpClient.
/// Fără retry — apelantul decide politica.
/// </summary>
public sealed class TelegramClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly IOptionsMonitor<TelegramOptions> _options;
    private readonly ILogger<TelegramClient> _log;

    public TelegramClient(IOptionsMonitor<TelegramOptions> options, ILogger<TelegramClient> log)
    {
        _options = options;
        _log = log;
    }

    /// <summary>
    /// Trimite mesaj. <paramref name="parseMode"/> = "HTML", "MarkdownV2" sau null pentru text plain.
    /// </summary>
    public async Task<bool> SendAsync(string chatId, string text, string? parseMode, CancellationToken ct)
    {
        var opt = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(opt.BotToken))
        {
            _log.LogWarning("Telegram: BotToken neconfigurat — skip mesaj.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(chatId))
            return false;

        var url  = $"https://api.telegram.org/bot{opt.BotToken}/sendMessage";
        object payload = parseMode is null
            ? new { chat_id = chatId, text }
            : new { chat_id = chatId, text, parse_mode = parseMode };

        var body = JsonSerializer.Serialize(payload);
        using var content  = new StringContent(body, Encoding.UTF8, "application/json");

        try
        {
            using var response = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return true;

            var err = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _log.LogWarning("Telegram API {Status} pentru chat {ChatId}: {Err}",
                (int)response.StatusCode, chatId, err);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Telegram: eroare HTTP pentru chat {ChatId}", chatId);
            return false;
        }
    }
}
