namespace NEW_STATISTIC.Api.Admin;

/// <summary>Apelează endpoint-urile interne ale Worker-ului (loopback). Best-effort.</summary>
public sealed class WorkerNotifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly HttpClient RunNowHttp = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly string _baseUrl;
    private readonly ILogger<WorkerNotifier> _log;

    public WorkerNotifier(IConfiguration config, ILogger<WorkerNotifier> log)
    {
        _baseUrl = (config["Worker:InternalUrl"] ?? "http://127.0.0.1:5099").TrimEnd('/');
        _log = log;
    }

    public async Task<bool> ReloadAsync(CancellationToken ct)
    {
        return await PostAsync($"{_baseUrl}/internal/telegram/reload", ct).ConfigureAwait(false);
    }

    public async Task<bool> RunNowAsync(string channelId, CancellationToken ct)
    {
        return await PostAsync(
                RunNowHttp,
                $"{_baseUrl}/internal/telegram/run-now/{Uri.EscapeDataString(channelId)}",
                ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> TestAsync(string channelId, CancellationToken ct)
    {
        return await PostAsync($"{_baseUrl}/internal/telegram/test/{Uri.EscapeDataString(channelId)}", ct)
            .ConfigureAwait(false);
    }

    private async Task<bool> PostAsync(string url, CancellationToken ct)
    {
        return await PostAsync(Http, url, ct).ConfigureAwait(false);
    }

    private async Task<bool> PostAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var resp = await http.PostAsync(url, new StringContent(""), ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WorkerNotifier: apelul {Url} a eșuat (Worker offline?).", url);
            return false;
        }
    }
}
