using NEW_STATISTIC.Core.Domain;

namespace NEW_STATISTIC.Core.Abstractions;

public interface IPersistenceSink
{
    ValueTask EnqueueAsync(CandlePersistencePayload payload, CancellationToken cancellationToken = default);
}
