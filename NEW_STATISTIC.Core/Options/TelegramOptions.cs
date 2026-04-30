namespace NEW_STATISTIC.Core.Options;

/// <summary>
/// Configurație generală Telegram (din appsettings.json). Toate canalele specifice
/// (trigger / statistic) sunt în fișierul separat indicat de <see cref="ChannelsFilePath"/>
/// și sunt editabile live prin pagina admin — nu se atinge appsettings la editare.
/// </summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    /// <summary>Token bot Telegram (@BotFather). Mută în env var pentru securitate în producție.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>
    /// Cale (relativă la Worker ContentRoot sau absolută) către fișierul cu canalele dinamice.
    /// </summary>
    public string ChannelsFilePath { get; set; } = "../telegram-channels.json";

    /// <summary>Retenția în memorie pentru shoturile clasificate folosite de trigger mode.</summary>
    public int RecentShotsRetentionHours { get; set; } = 72;
}
