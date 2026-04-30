namespace NEW_STATISTIC.Api.Auth;

/// <summary>
/// Credențiale hardcodate pentru pagina /admin. Pot fi suprascrise din appsettings:
/// "Admin": { "User": "...", "Password": "..." }.
/// Hardcoded ca default, ca să meargă out-of-the-box fără config separat.
/// </summary>
public static class AdminCredentials
{
    public const string DefaultUser = "admin";

    /// <summary>Strong password generat aleator. Schimbă-l în acest fișier sau prin appsettings.</summary>
    public const string DefaultPassword = "Z9b#fK7pQ!w2Vt6Lm8Rx4Yc1"; // 24 chars, mixed
}
