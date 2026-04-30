using Microsoft.Extensions.Logging;

namespace NEW_STATISTIC.Infrastructure.Logging;

/// <summary>
/// Config pentru <see cref="FileLoggerProvider"/>. Mapată din "Logging:File:*" în appsettings.
/// </summary>
public sealed class FileLoggerOptions
{
    /// <summary>Folder unde se scriu fișierele. Relativ la ContentRoot dacă nu e absolut.</summary>
    public string Directory { get; set; } = "logs";

    /// <summary>Prefix la numele fișierului. Ex: "worker" → worker-20260427.log.</summary>
    public string FilePrefix { get; set; } = "app";

    /// <summary>Pragul minim de severitate. Implicit Warning. Trace/Debug/Information/Warning/Error/Critical.</summary>
    public LogLevel MinLevel { get; set; } = LogLevel.Warning;

    /// <summary>După câte zile ștergem fișiere vechi. 0 = fără retention.</summary>
    public int RetainDays { get; set; } = 14;
}
