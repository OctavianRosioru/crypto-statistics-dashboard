using System.Security.Cryptography;
using System.Text;

namespace NEW_STATISTIC.Api.Auth;

/// <summary>
/// Middleware Basic Auth aplicat pe endpoint-urile admin (/api/admin/*) și pe pagina admin.html.
/// Acceptă "admin" + parola din appsettings (sau din <see cref="AdminCredentials"/> ca fallback).
/// Compară prin <see cref="CryptographicOperations.FixedTimeEquals"/> ca să evite timing attacks.
/// </summary>
public sealed class BasicAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _user;
    private readonly byte[] _passwordBytes;

    public BasicAuthMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _user = config["Admin:User"] ?? AdminCredentials.DefaultUser;
        var pw = config["Admin:Password"];
        var pwStr = string.IsNullOrEmpty(pw) ? AdminCredentials.DefaultPassword : pw;
        _passwordBytes = Encoding.UTF8.GetBytes(pwStr);
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!IsProtected(ctx.Request.Path))
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        if (!TryParseBasic(ctx.Request.Headers.Authorization.ToString(), out var user, out var pwd))
        {
            Challenge(ctx);
            return;
        }

        var userOk = string.Equals(user, _user, StringComparison.Ordinal);
        var pwdBytes = Encoding.UTF8.GetBytes(pwd);
        var pwdOk = pwdBytes.Length == _passwordBytes.Length
            && CryptographicOperations.FixedTimeEquals(pwdBytes, _passwordBytes);

        if (!userOk || !pwdOk)
        {
            Challenge(ctx);
            return;
        }

        await _next(ctx).ConfigureAwait(false);
    }

    private static bool IsProtected(PathString path) =>
        path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/admin.html", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/admin", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseBasic(string? header, out string user, out string pwd)
    {
        user = pwd = "";
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var idx = raw.IndexOf(':');
            if (idx <= 0) return false;
            user = raw[..idx];
            pwd  = raw[(idx + 1)..];
            return true;
        }
        catch { return false; }
    }

    private static void Challenge(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"NEW_STATISTIC admin\", charset=\"UTF-8\"";
    }
}
