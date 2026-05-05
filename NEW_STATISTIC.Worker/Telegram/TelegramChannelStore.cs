using System.Text.Json;
using Microsoft.Extensions.Options;
using NEW_STATISTIC.Core.Abstractions;
using NEW_STATISTIC.Core.Options;

namespace NEW_STATISTIC.Worker.Telegram;

/// <summary>
/// Sursa de adevăr pentru canalele Telegram dinamice.
/// Citește/scrie un fișier JSON și expune un snapshot imutabil + eveniment de reload.
///
/// Reload-ul se declanșează automat (FileSystemWatcher) sau manual via ReloadAsync()
/// (de ex. când Api scrie fișierul și apelează endpoint-ul intern de reload).
/// </summary>
public sealed class TelegramChannelStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly ILogger<TelegramChannelStore> _log;
    private readonly FileSystemWatcher? _watcher;
    private readonly Lock _lock = new();
    private DateTime _lastReloadUtc = DateTime.MinValue;

    private List<TelegramChannel> _channels = new();
    private decimal _globalMinTriggerDiffPercent;
    private int     _globalMaxTpAgeMs;
    private OpenOffsetRange[] _globalTriggerOpenOffsetRanges = Array.Empty<OpenOffsetRange>();

    public event Action? ConfigChanged;

    public TelegramChannelStore(
        IOptions<TelegramOptions> options,
        IHostEnvironment env,
        ILogger<TelegramChannelStore> log)
    {
        _log = log;
        var opt = options.Value;
        _filePath = Path.IsPathRooted(opt.ChannelsFilePath)
            ? opt.ChannelsFilePath
            : Path.Combine(env.ContentRootPath, opt.ChannelsFilePath);

        EnsureFileExists();
        ReloadInternal(silent: true);

        try
        {
            var dir = Path.GetDirectoryName(_filePath)!;
            var name = Path.GetFileName(_filePath);
            _watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (_, _) => DebouncedReload();
            _watcher.Created += (_, _) => DebouncedReload();
            _watcher.Renamed += (_, _) => DebouncedReload();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TelegramChannelStore: nu pot porni FileSystemWatcher. Reload doar manual.");
        }
    }

    public IReadOnlyList<TelegramChannel> Snapshot
    {
        get { lock (_lock) return _channels.ToArray(); }
    }

    public decimal GlobalMinTriggerDiffPercent { get { lock (_lock) return _globalMinTriggerDiffPercent; } }
    public int     GlobalMaxTpAgeMs            { get { lock (_lock) return _globalMaxTpAgeMs; } }
    public IReadOnlyList<OpenOffsetRange> GlobalTriggerOpenOffsetRanges { get { lock (_lock) return _globalTriggerOpenOffsetRanges.ToArray(); } }

    public string FilePath => _filePath;

    public Task ReloadAsync()
    {
        ReloadInternal(silent: false);
        return Task.CompletedTask;
    }

    public Task SaveAsync(IEnumerable<TelegramChannel> channels, CancellationToken ct)
    {
        var file = new TelegramChannelsFile { Channels = channels.ToList() };
        var json = JsonSerializer.Serialize(file, JsonOpts);
        File.WriteAllText(_filePath, json);
        ReloadInternal(silent: false);
        return Task.CompletedTask;
    }

    private void DebouncedReload()
    {
        // FileSystemWatcher poate emite mai multe evenimente pentru o singură scriere — debounce 200ms.
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if ((now - _lastReloadUtc).TotalMilliseconds < 200) return;
            _lastReloadUtc = now;
        }
        try { Thread.Sleep(50); ReloadInternal(silent: false); }
        catch (Exception ex) { _log.LogWarning(ex, "TelegramChannelStore: reload eșuat."); }
    }

    private void EnsureFileExists()
    {
        if (File.Exists(_filePath)) return;
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(new TelegramChannelsFile(), JsonOpts));
        _log.LogInformation("TelegramChannelStore: fișier inițial creat la {Path}", _filePath);
    }

    private void ReloadInternal(bool silent)
    {
        try
        {
            var json = File.ReadAllText(_filePath);
            var file = JsonSerializer.Deserialize<TelegramChannelsFile>(json, JsonOpts) ?? new TelegramChannelsFile();
            var channels = file.Channels ?? new List<TelegramChannel>();

            // Calculează praguri globale pentru pipeline (doar canalele trigger active).
            decimal minDiff = 0m;
            int     maxAge  = 0;
            bool    anyTrig = false;
            var ranges = new List<OpenOffsetRange>();
            foreach (var c in channels)
            {
                if (!c.Enabled) continue;
                if (c.Mode != TelegramChannelMode.Trigger || c.Trigger is null) continue;
                anyTrig = true;
                var offset = Math.Max(0m, c.Trigger.DistanceMin);
                if (offset > 0m)
                {
                    var max = c.Trigger.DistanceMax > 0m
                        ? Math.Max(offset, c.Trigger.DistanceMax)
                        : 0m;
                    var range = new OpenOffsetRange(offset, max);
                    if (!ranges.Contains(range))
                        ranges.Add(range);
                    if (minDiff == 0m || offset < minDiff)
                        minDiff = offset;
                }
                if (c.Trigger.MaxTpAgeMs > maxAge) maxAge = c.Trigger.MaxTpAgeMs;
            }
            if (!anyTrig) { minDiff = 0m; maxAge = 0; }

            lock (_lock)
            {
                _channels = channels;
                _globalMinTriggerDiffPercent = minDiff;
                _globalMaxTpAgeMs = maxAge;
                _globalTriggerOpenOffsetRanges = ranges
                    .OrderBy(r => r.MinPercent)
                    .ThenBy(r => r.MaxPercent)
                    .ToArray();
            }

            if (!silent)
                _log.LogInformation("TelegramChannelStore: reload OK — {N} canale, minDiff={Min}, maxTpAgeMs={Age}",
                    channels.Count, minDiff, maxAge);
            ConfigChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "TelegramChannelStore: parse/load eșuat pentru {Path}", _filePath);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
