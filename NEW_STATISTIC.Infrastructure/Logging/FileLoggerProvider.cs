using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NEW_STATISTIC.Infrastructure.Logging;

/// <summary>
/// Provider thread-safe care scrie loguri pe disc cu rolling zilnic.
/// O singură instanță reține StreamWriter-ul curent + ziua activă; toți logger-ii copii
/// trimit linii prin <see cref="Write"/> (cu lock scurt). Flush la fiecare scriere.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly string _resolvedDirectory;

    private StreamWriter? _writer;
    private DateOnly _activeDay;

    public FileLoggerOptions Options { get; }

    public FileLoggerProvider(FileLoggerOptions options, string contentRoot)
    {
        Options = options;
        _resolvedDirectory = Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.GetFullPath(Path.Combine(contentRoot, options.Directory));
        Directory.CreateDirectory(_resolvedDirectory);
        EnsureWriter();
        TryPurgeOldFiles();
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    internal void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                EnsureWriter();
                _writer!.Write(line);
                _writer.Flush();
            }
            catch
            {
                // Logging-ul nu trebuie niciodată să arunce excepții către cod normal.
            }
        }
    }

    private void EnsureWriter()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_writer is not null && today == _activeDay) return;

        _writer?.Flush();
        _writer?.Dispose();

        var fileName = $"{Options.FilePrefix}-{today:yyyyMMdd}.log";
        var fullPath = Path.Combine(_resolvedDirectory, fileName);
        var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, System.Text.Encoding.UTF8) { AutoFlush = false };
        _activeDay = today;

        TryPurgeOldFiles();
    }

    private void TryPurgeOldFiles()
    {
        if (Options.RetainDays <= 0) return;
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-Options.RetainDays);
            foreach (var path in Directory.EnumerateFiles(_resolvedDirectory, $"{Options.FilePrefix}-*.log"))
            {
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc < cutoff)
                {
                    try { info.Delete(); } catch { }
                }
            }
        }
        catch { /* best-effort cleanup */ }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}
