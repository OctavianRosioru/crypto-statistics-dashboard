using System.Text.Json.Serialization;

namespace NEW_STATISTIC.Core.Options;

public enum TelegramChannelMode
{
    Trigger = 0,
    Statistic = 1
}

public enum TelegramSideFilter
{
    Any = 0,
    Buy = 1,
    Sell = 2
}

public enum TelegramStatisticCategory
{
    Profitable = 0,
    Losing = 1,
    Active = 2
}

/// <summary>
/// Un canal Telegram configurat din admin UI. Persistat în telegram-channels.json
/// și încărcat live de Worker (TelegramChannelStore).
/// </summary>
public sealed class TelegramChannel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Chat ID Telegram (poate fi negativ pentru grupuri/canale).</summary>
    public string ChatId { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TelegramChannelMode Mode { get; set; } = TelegramChannelMode.Trigger;

    public TelegramTriggerConfig? Trigger { get; set; }
    public TelegramStatisticConfig? Statistic { get; set; }
}

public sealed class TelegramTriggerConfig
{
    /// <summary>Filtru exchange ("BINANCE", "BYBIT", sau "*" pentru orice).</summary>
    public string Exchange { get; set; } = "*";

    /// <summary>Whitelist simboluri. Goală = toate.</summary>
    public string[] Symbols { get; set; } = Array.Empty<string>();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TelegramSideFilter Side { get; set; } = TelegramSideFilter.Any;

    public decimal DistanceMin { get; set; } = 0.5m;
    public decimal DistanceMax { get; set; } = 0m;

    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Numărul minim de TP-uri în fereastră ca să se considere pattern valid.
    /// SL-urile și None-urile sunt acceptate (nu invalidează pattern-ul), dar nu se numără aici.
    /// </summary>
    public int MinTpCount { get; set; } = 2;

    /// <summary>
    /// Dacă true, pe lângă MinTpCount mai cere și ca P&amp;L net (Σ TP gains − Σ SL losses) să fie &gt; 0.
    /// Câștigul TP = diff × SimulationTakeProfitRatio; pierderea SL = diff × SimulationStopLossRatio.
    /// </summary>
    public bool RequirePositiveNet { get; set; } = true;

    /// <summary>Cât așteptăm după shot pentru TP (ms). Determină și fereastra fast-followup.</summary>
    public int MaxTpAgeMs { get; set; } = 1500;

    /// <summary>Perioada din DB folosită pentru verificarea statisticii înainte de trigger (1, 3, 7, 14 sau 30 zile).</summary>
    public int StatsLookbackDays { get; set; } = 1;

    /// <summary>Anti-spam per (canal, simbol).</summary>
    public int CooldownSeconds { get; set; } = 30;

    /// <summary>
    /// Template mesaj. Tokeni: {exchange} {symbol} {side} {distance} {shots} {age}.
    /// Default: "#{exchange} #{symbol} {side} #{distance}".
    /// </summary>
    public string MessageFormat { get; set; } = "#{exchange} #{symbol} {side} #{distance}";
}

public sealed class TelegramStatisticConfig
{
    public int PeriodHours { get; set; } = 24;
    public int FrequencyHours { get; set; } = 1;

    /// <summary>
    /// Filtru side. Buy/Sell = doar candle-uri cu side-ul respectiv intră în top.
    /// Any = ambele direcții; mesajul folosește side-ul dominant per simbol.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TelegramSideFilter Side { get; set; } = TelegramSideFilter.Any;

    /// <summary>
    /// Format mesaj per simbol. Tokeni: {symbol} {exchange} {side} {tp} {sl} {shots} {net}.
    /// Default e formatul cerut de aplicația trading (un simbol per mesaj).
    /// </summary>
    public string MessageFormat { get; set; } = "#{symbol} {exchange} {side}";

    /// <summary>Pauză între mesaje (ms) ca să nu spam-uiești canalul.</summary>
    public int DelayBetweenMessagesMs { get; set; } = 1000;

    /// <summary>Câte simboluri sărim înainte să luăm TopN (paginare). 0 = de la primul.</summary>
    public int Skip { get; set; } = 0;

    public int TopN { get; set; } = 20;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TelegramStatisticCategory Category { get; set; } = TelegramStatisticCategory.Profitable;

    public decimal DistanceMin { get; set; } = 0m;
    public decimal DistanceMax { get; set; } = 0m;
    public decimal MinQuoteUsdt { get; set; } = 0m;
    public int HorizonSec { get; set; } = 300;

    /// <summary>
    /// Whitelist de simboluri (deja normalizate UPPERCASE). Goală = toate.
    /// Util pentru a limita la simboluri tranzacționabile (de ex. cu max leverage ≤ X).
    /// </summary>
    public string[] Symbols { get; set; } = Array.Empty<string>();
}

/// <summary>Container pentru fișierul telegram-channels.json.</summary>
public sealed class TelegramChannelsFile
{
    public List<TelegramChannel> Channels { get; set; } = new();
}
