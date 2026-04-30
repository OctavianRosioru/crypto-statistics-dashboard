namespace NEW_STATISTIC.Infrastructure.Exchange;

internal static class ReconnectBackoff
{
    public static int GetDelayMs(int attempt, int initialMs, int maxMs)
    {
        if (attempt < 0)
            attempt = 0;
        var exp = (long)initialMs << attempt;
        if (exp > maxMs || exp < 0)
            return maxMs;
        return (int)exp;
    }
}
