namespace NEW_STATISTIC.Core.Domain;

public sealed record FollowUpSnapshot(long ReferenceTimeMs, IReadOnlyList<IntervalExtreme> Intervals);
