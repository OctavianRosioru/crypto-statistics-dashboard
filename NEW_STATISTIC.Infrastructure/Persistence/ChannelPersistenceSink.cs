using System.Threading.Channels;
using NEW_STATISTIC.Core.Abstractions;
using NEW_STATISTIC.Core.Domain;

namespace NEW_STATISTIC.Infrastructure.Persistence;

public sealed class ChannelPersistenceSink : IPersistenceSink
{
    private readonly Channel<CandlePersistencePayload> _channel =
        Channel.CreateUnbounded<CandlePersistencePayload>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ChannelReader<CandlePersistencePayload> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(CandlePersistencePayload payload, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(payload, cancellationToken);
    }
}
