namespace NEW_STATISTIC.Infrastructure.Exchange;

internal static class TickerReflection
{
    public static bool TryReadString(object? value, string propertyName, out string result)
    {
        result = "";
        if (value is null) return false;

        var prop = value.GetType().GetProperty(propertyName);
        var raw = prop?.GetValue(value);
        if (raw is null) return false;

        result = raw.ToString() ?? "";
        return !string.IsNullOrWhiteSpace(result);
    }

    public static bool TryReadDecimal(object? value, string propertyName, out decimal result)
    {
        result = 0m;
        if (value is null) return false;

        var prop = value.GetType().GetProperty(propertyName);
        var raw = prop?.GetValue(value);
        if (raw is null) return false;

        return raw switch
        {
            decimal d => Set(d, out result),
            double d => Set((decimal)d, out result),
            float d => Set((decimal)d, out result),
            int i => Set(i, out result),
            long l => Set(l, out result),
            string s => decimal.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result),
            _ => decimal.TryParse(raw.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result)
        };
    }

    private static bool Set(decimal value, out decimal result)
    {
        result = value;
        return true;
    }
}
