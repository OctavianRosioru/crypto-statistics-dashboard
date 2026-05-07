using NEW_STATISTIC.Core.Domain;
using NEW_STATISTIC.Core.Options;
using NEW_STATISTIC.Core.Services;

namespace NEW_STATISTIC.Core.Tests;

public class CoreLogicTests
{
    [Fact]
    public void CandleSideResolver_first_below_mid_is_sell()
    {
        var side = CandleSideResolver.Resolve(firstTradePrice: 99m, minPrice: 98m, maxPrice: 102m);
        Assert.Equal(CandleSide.Sell, side);
    }

    [Fact]
    public void CandleSideResolver_first_above_mid_is_buy()
    {
        var side = CandleSideResolver.Resolve(firstTradePrice: 101m, minPrice: 98m, maxPrice: 102m);
        Assert.Equal(CandleSide.Buy, side);
    }

    [Fact]
    public void MillisecondBucketAggregator_prune_removes_old_keys()
    {
        var agg = new MillisecondBucketAggregator();
        var t1 = new NormalizedAggregateTrade("X", "BTC", 1000L, 10m, 1m, null);
        var t2 = new NormalizedAggregateTrade("X", "BTC", 2000L, 11m, 1m, null);
        agg.ApplyTrade(t1);
        agg.ApplyTrade(t2);
        agg.PruneOlderThan(1500L);
        Assert.False(agg.TryGet(1000L, out _));
        Assert.True(agg.TryGet(2000L, out var mm));
        Assert.Equal(11m, mm.Max);
    }

    [Fact]
    public void SimulationEngine_produces_rows_for_buy_candle()
    {
        var opt = new TradingOptions
        {
            SimulationInitialOpenPercent = 0.3m,
            SimulationOpenStepPercent = 0.1m,
            SimulationTakeProfitRatio = 0.5m,
            SimulationStopLossRatio = 0.2m,
            FollowUpSeconds = [1]
        };
        var follow = new FollowUpSnapshot(2000L, new List<IntervalExtreme>
        {
            new(1, 99m, 2001L, 101m, 2002L)
        });
        var candle = new CompletedCandle(
            "T", "S", 1000L, 850L, 1150L, 1100L,
            98m, 102m, 4m, CandleSide.Buy, 100m,
            1000m, 3.33m, follow);

        var trades = new List<NormalizedAggregateTrade>
        {
            new("T", "S", 2001L, 101m, 1m, null),
            new("T", "S", 2002L, 99m,  1m, null)
        };
        var rows = SimulationEngine.Run(candle, opt, trades);
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.True(r.OpenPrice <= candle.MaxPrice));
    }

    [Fact]
    public void QuoteVolume24hStore_calculates_positive_change()
    {
        var store = new QuoteVolume24hStore();
        store.Record("Binance", "BTCUSDT", 0, 100m);
        store.Record("Binance", "BTCUSDT", QuoteVolume24hStore.MinuteMs, 150m);

        var snapshot = store.Snapshot("binance", "btcusdt", QuoteVolume24hStore.MinuteMs);

        Assert.Equal(150m, snapshot.QuoteVolume24hUsdt);
        Assert.Equal(50m, snapshot.Change1mPct);
    }

    [Fact]
    public void QuoteVolume24hStore_calculates_negative_and_zero_change()
    {
        var store = new QuoteVolume24hStore();
        store.Record("Bybit", "ETHUSDT", 0, 200m);
        store.Record("Bybit", "ETHUSDT", 5 * QuoteVolume24hStore.MinuteMs, 150m);
        var negative = store.Snapshot("BYBIT", "ETHUSDT", 5 * QuoteVolume24hStore.MinuteMs);

        store.Record("Bybit", "ETHUSDT", 6 * QuoteVolume24hStore.MinuteMs, 150m);

        var snapshot = store.Snapshot("BYBIT", "ETHUSDT", 6 * QuoteVolume24hStore.MinuteMs);

        Assert.Equal(-25m, negative.Change5mPct);
        Assert.Equal(0m, snapshot.Change1mPct);
    }

    [Fact]
    public void QuoteVolume24hStore_returns_unavailable_for_missing_history_or_stale_ticker()
    {
        var store = new QuoteVolume24hStore();
        store.Record("Binance", "SOLUSDT", 5 * QuoteVolume24hStore.MinuteMs, 100m);

        var fresh = store.Snapshot("Binance", "SOLUSDT", 5 * QuoteVolume24hStore.MinuteMs);
        var stale = store.Snapshot("Binance", "SOLUSDT", 5 * QuoteVolume24hStore.MinuteMs + QuoteVolume24hStore.DefaultMaxStaleMs + 1);

        Assert.Null(fresh.Change1mPct);
        Assert.Null(stale.QuoteVolume24hUsdt);
    }

    [Fact]
    public void QuoteVolume24hStore_keeps_24h_ring_value()
    {
        var store = new QuoteVolume24hStore();
        store.Record("Binance", "XRPUSDT", 0, 100m);
        store.Record("Binance", "XRPUSDT", 1440 * QuoteVolume24hStore.MinuteMs, 150m);

        var snapshot = store.Snapshot("Binance", "XRPUSDT", 1440 * QuoteVolume24hStore.MinuteMs);

        Assert.Equal(50m, snapshot.Change24hPct);
    }
}
