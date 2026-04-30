using System.Text;
using Microsoft.Extensions.Logging;

namespace NEW_STATISTIC.Infrastructure.Logging;

/// <summary>
/// Logger per categorie. Doar formatează linia și o trimite la <see cref="FileLoggerProvider.Write"/>.
/// </summary>
internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly FileLoggerProvider _provider;

    public FileLogger(string category, FileLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider.Options.MinLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var msg = formatter(state, exception);
        if (string.IsNullOrEmpty(msg) && exception is null) return;

        var sb = new StringBuilder();
        sb.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.Append(" [").Append(LevelTag(logLevel)).Append("] [");
        sb.Append(_category).Append("] ");
        sb.Append(msg);
        if (exception is not null)
        {
            sb.AppendLine();
            sb.Append("    ").Append(exception.GetType().FullName).Append(": ").AppendLine(exception.Message);
            sb.Append(exception.StackTrace);
        }
        sb.AppendLine();

        _provider.Write(sb.ToString());
    }

    private static string LevelTag(LogLevel l) => l switch
    {
        LogLevel.Trace       => "TRC",
        LogLevel.Debug       => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning     => "WRN",
        LogLevel.Error       => "ERR",
        LogLevel.Critical    => "CRT",
        _ => l.ToString()
    };
}
