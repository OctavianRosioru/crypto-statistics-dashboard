using System.Text.Json;
using NEW_STATISTIC.Core.Options;

namespace NEW_STATISTIC.Api.Admin;

/// <summary>
/// Citește/scrie fișierul telegram-channels.json (același pe care Worker-ul îl citește).
/// Path-ul vine din appsettings: <c>Telegram:ChannelsFilePath</c>, relativ la ContentRoot al Api-ului.
/// După scriere, apelantul (endpoint-ul) notifică Worker-ul prin /internal/telegram/reload.
/// </summary>
public sealed class ChannelsFileService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly object _lock = new();

    public ChannelsFileService(IConfiguration config, IHostEnvironment env)
    {
        var rel = config["Telegram:ChannelsFilePath"] ?? "../telegram-channels.json";
        _filePath = Path.IsPathRooted(rel)
            ? rel
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, rel));
        EnsureExists();
    }

    public string FilePath => _filePath;

    public TelegramChannelsFile Read()
    {
        lock (_lock)
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<TelegramChannelsFile>(json, JsonOpts) ?? new TelegramChannelsFile();
        }
    }

    public void Write(TelegramChannelsFile file)
    {
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(file, JsonOpts);
            File.WriteAllText(_filePath, json);
        }
    }

    private void EnsureExists()
    {
        if (File.Exists(_filePath)) return;
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(new TelegramChannelsFile(), JsonOpts));
    }
}
