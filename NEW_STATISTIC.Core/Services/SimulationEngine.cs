using NEW_STATISTIC.Core.Domain;
using NEW_STATISTIC.Core.Options;

namespace NEW_STATISTIC.Core.Services;

public static class SimulationEngine
{
    public static IReadOnlyList<SimulationResultRow> Run(
        CompletedCandle candle,
        TradingOptions opt,
        IReadOnlyList<NormalizedAggregateTrade> followUpTrades)
    {
        var rows = new List<SimulationResultRow>();
        var openPct = opt.SimulationInitialOpenPercent;
        var maxOpenPct = candle.DiffPercent;

        while (openPct <= maxOpenPct)
        {
            var (o, tp, sl) = candle.Side == CandleSide.Buy
                ? PricesForBuy(candle.MaxPrice, openPct, opt.SimulationTakeProfitRatio, opt.SimulationStopLossRatio)
                : PricesForSell(candle.MinPrice, openPct, opt.SimulationTakeProfitRatio, opt.SimulationStopLossRatio);

            var outcomes = new Dictionary<int, HorizonOutcome>();
            foreach (var interval in candle.FollowUp.Intervals)
            {
                var ho = EvaluateHorizon(candle.Side, tp, sl, interval, followUpTrades, candle.FollowUp.ReferenceTimeMs);
                outcomes[interval.HorizonSeconds] = ho;
            }

            rows.Add(new SimulationResultRow(openPct, o, tp, sl, outcomes));
            openPct += opt.SimulationOpenStepPercent;
        }

        return rows;
    }

    /// <summary>
    /// Buy = spike descendent (dip) → intrare long sub prețul normal (MaxPrice = baza).
    /// open = maxPrice * (1 - offset%); TP deasupra open (vindem mai scump), SL sub open.
    /// </summary>
    private static (decimal open, decimal tp, decimal sl) PricesForBuy(
        decimal candleMax,
        decimal openOffsetPercent,
        decimal tpRatio,
        decimal slRatio)
    {
        var open = candleMax * (1m - openOffsetPercent / 100m);
        var tp = open * (1m + (openOffsetPercent * tpRatio) / 100m);
        var sl = open * (1m - (openOffsetPercent * slRatio) / 100m);
        return (open, tp, sl);
    }

    /// <summary>
    /// Sell = spike ascendent → intrare short deasupra prețului normal (MinPrice = baza).
    /// open = minPrice * (1 + offset%); TP sub open (cumpărăm mai ieftin), SL deasupra open.
    /// </summary>
    private static (decimal open, decimal tp, decimal sl) PricesForSell(
        decimal candleMin,
        decimal openOffsetPercent,
        decimal tpRatio,
        decimal slRatio)
    {
        var open = candleMin * (1m + openOffsetPercent / 100m);
        var tp = open * (1m - (openOffsetPercent * tpRatio) / 100m);
        var sl = open * (1m + (openOffsetPercent * slRatio) / 100m);
        return (open, tp, sl);
    }

    private static HorizonOutcome EvaluateHorizon(
        CandleSide side,
        decimal tpPrice,
        decimal slPrice,
        IntervalExtreme interval,
        IReadOnlyList<NormalizedAggregateTrade> followUpTrades,
        long referenceTimeMs)
    {
        if (interval.MinPrice is null || interval.MaxPrice is null)
            return new HorizonOutcome(OutcomeKind.None, null);

        bool tpHit = side == CandleSide.Buy
            ? interval.MaxPrice >= tpPrice
            : interval.MinPrice <= tpPrice;

        bool slHit = side == CandleSide.Buy
            ? interval.MinPrice <= slPrice
            : interval.MaxPrice >= slPrice;

        if (!tpHit && !slHit)
            return new HorizonOutcome(OutcomeKind.None, null);

        // Găsim prima traversare cronologică a TP și/sau SL în trade-urile reale.
        // Intervalul curent acoperă (referenceTimeMs, referenceTimeMs + HorizonSeconds * 1000].
        var horizonEndMs = referenceTimeMs + interval.HorizonSeconds * 1000L;
        long?    firstTpMs    = null;
        decimal? firstTpPrice = null;
        long?    firstSlMs    = null;
        decimal? firstSlPrice = null;

        foreach (var t in followUpTrades)
        {
            if (t.TradeTimeMs <= referenceTimeMs)
                continue;
            if (t.TradeTimeMs > horizonEndMs)
                break; // lista e sortată cronologic

            if (tpHit && firstTpMs is null)
            {
                bool crossed = side == CandleSide.Buy ? t.Price >= tpPrice : t.Price <= tpPrice;
                if (crossed) { firstTpMs = t.TradeTimeMs; firstTpPrice = t.Price; }
            }

            if (slHit && firstSlMs is null)
            {
                bool crossed = side == CandleSide.Buy ? t.Price <= slPrice : t.Price >= slPrice;
                if (crossed) { firstSlMs = t.TradeTimeMs; firstSlPrice = t.Price; }
            }

            if ((!tpHit || firstTpMs is not null) && (!slHit || firstSlMs is not null))
                break;
        }

        // Dacă ambele au fost atinse, câștigă cel care a apărut primul cronologic.
        if (firstTpMs is not null && firstSlMs is not null)
        {
            return firstTpMs <= firstSlMs
                ? new HorizonOutcome(OutcomeKind.TakeProfit, firstTpMs, firstTpPrice)
                : new HorizonOutcome(OutcomeKind.StopLoss,   firstSlMs, firstSlPrice);
        }

        if (firstTpMs is not null)
            return new HorizonOutcome(OutcomeKind.TakeProfit, firstTpMs, firstTpPrice);
        if (firstSlMs is not null)
            return new HorizonOutcome(OutcomeKind.StopLoss, firstSlMs, firstSlPrice);

        return new HorizonOutcome(OutcomeKind.None, null);
    }
}
