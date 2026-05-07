using NEW_STATISTIC.Core.Domain;

namespace NEW_STATISTIC.Core.Abstractions;

public interface IQuoteVolume24hProvider
{
    QuoteVolume24hSnapshot Snapshot(string exchange, string symbol, long nowMs);
}
