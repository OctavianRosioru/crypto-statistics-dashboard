using System.Threading.Channels;
using NEW_STATISTIC.Core.Domain;

namespace NEW_STATISTIC.Core.Abstractions;

public interface IExchangeAggregateTradeSource
{
    string ExchangeName { get; }

    Task RunAsync(ChannelWriter<NormalizedAggregateTrade> writer, CancellationToken cancellationToken);
}
