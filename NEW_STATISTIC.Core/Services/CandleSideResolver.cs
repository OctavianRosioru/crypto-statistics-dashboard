using NEW_STATISTIC.Core.Domain;

namespace NEW_STATISTIC.Core.Services;

public static class CandleSideResolver
{
    public static CandleSide Resolve(decimal firstTradePrice, decimal minPrice, decimal maxPrice)
    {
        var mid = (minPrice + maxPrice) * 0.5m;
        return firstTradePrice < mid ? CandleSide.Sell : CandleSide.Buy;
    }
}
