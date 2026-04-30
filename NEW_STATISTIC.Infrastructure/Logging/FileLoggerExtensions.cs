using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NEW_STATISTIC.Infrastructure.Logging;

public static class FileLoggerExtensions
{
    /// <summary>
    /// Adaugă file logging cu rolling zilnic. Opțiunile vin din "Logging:File" în appsettings;
    /// orice câmp lipsă păstrează default-ul. Path-ul e relativ la <paramref name="contentRoot"/>.
    /// </summary>
    public static ILoggingBuilder AddFileLogger(
        this ILoggingBuilder builder,
        IConfiguration config,
        string contentRoot,
        string defaultFilePrefix)
    {
        var options = new FileLoggerOptions { FilePrefix = defaultFilePrefix };
        config.GetSection("Logging:File").Bind(options);
        if (string.IsNullOrWhiteSpace(options.FilePrefix))
            options.FilePrefix = defaultFilePrefix;

        var provider = new FileLoggerProvider(options, contentRoot);
        builder.AddProvider(provider);
        return builder;
    }
}
