using System.Collections.Concurrent;
using NEW_STATISTIC.Core.Abstractions;
using NEW_STATISTIC.Core.Domain;

namespace NEW_STATISTIC.Core.Services;

public sealed class QuoteVolume24hStore : IQuoteVolume24hProvider
{
    public const int MaxLookbackMinutes = 1440;
    public const int RetainedMinuteSlots = MaxLookbackMinutes + 1;
    public const long MinuteMs = 60_000L;
    public const long DefaultMaxStaleMs = 120_000L;

    public static readonly int[] SupportedLookbackMinutes = [1, 5, 15, 30, 60, 180, 360, 720, 1440];

    private readonly ConcurrentDictionary<Key, SymbolState> _states = new();
    private readonly long _maxStaleMs;

    public QuoteVolume24hStore(long maxStaleMs = DefaultMaxStaleMs)
    {
        _maxStaleMs = Math.Max(MinuteMs, maxStaleMs);
    }

    public void Record(string exchange, string symbol, long eventTimeMs, decimal quoteVolume24hUsdt)
    {
        if (string.IsNullOrWhiteSpace(exchange) ||
            string.IsNullOrWhiteSpace(symbol) ||
            quoteVolume24hUsdt < 0m)
        {
            return;
        }

        var key = new Key(Normalize(exchange), Normalize(symbol));
        var state = _states.GetOrAdd(key, static _ => new SymbolState());
        var minuteMs = FloorToMinute(eventTimeMs);

        lock (state.Gate)
        {
            state.LatestValue = quoteVolume24hUsdt;
            state.LatestUpdatedMs = eventTimeMs;

            if (!state.ValuesByMinute.ContainsKey(minuteMs))
                state.MinuteOrder.Enqueue(minuteMs);

            state.ValuesByMinute[minuteMs] = quoteVolume24hUsdt;

            var cutoff = minuteMs - MaxLookbackMinutes * MinuteMs;
            while (state.MinuteOrder.Count > 0 && state.MinuteOrder.Peek() < cutoff)
            {
                var old = state.MinuteOrder.Dequeue();
                state.ValuesByMinute.Remove(old);
            }
        }
    }

    public QuoteVolume24hSnapshot Snapshot(string exchange, string symbol, long nowMs)
    {
        if (!_states.TryGetValue(new Key(Normalize(exchange), Normalize(symbol)), out var state))
            return QuoteVolume24hSnapshot.Empty;

        lock (state.Gate)
        {
            if (state.LatestValue is null ||
                state.LatestUpdatedMs is null ||
                nowMs - state.LatestUpdatedMs.Value > _maxStaleMs)
            {
                return QuoteVolume24hSnapshot.Empty;
            }

            var current = state.LatestValue.Value;
            var currentMinute = FloorToMinute(state.LatestUpdatedMs.Value);

            return new QuoteVolume24hSnapshot(
                current,
                state.LatestUpdatedMs,
                ChangeFrom(state, current, currentMinute, 1),
                ChangeFrom(state, current, currentMinute, 5),
                ChangeFrom(state, current, currentMinute, 15),
                ChangeFrom(state, current, currentMinute, 30),
                ChangeFrom(state, current, currentMinute, 60),
                ChangeFrom(state, current, currentMinute, 180),
                ChangeFrom(state, current, currentMinute, 360),
                ChangeFrom(state, current, currentMinute, 720),
                ChangeFrom(state, current, currentMinute, 1440));
        }
    }

    public static int NormalizeLookbackMinutes(int minutes) =>
        SupportedLookbackMinutes.Contains(minutes) ? minutes : 0;

    private static decimal? ChangeFrom(SymbolState state, decimal current, long currentMinuteMs, int lookbackMinutes)
    {
        var oldMinute = currentMinuteMs - lookbackMinutes * MinuteMs;
        if (!state.ValuesByMinute.TryGetValue(oldMinute, out var old) || old <= 0m)
            return null;

        return (current - old) / old * 100m;
    }

    private static long FloorToMinute(long ms) => ms - ms % MinuteMs;

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();

    private readonly record struct Key(string Exchange, string Symbol);

    private sealed class SymbolState
    {
        public object Gate { get; } = new();
        public decimal? LatestValue { get; set; }
        public long? LatestUpdatedMs { get; set; }
        public Queue<long> MinuteOrder { get; } = new();
        public Dictionary<long, decimal> ValuesByMinute { get; } = new();
    }
}
