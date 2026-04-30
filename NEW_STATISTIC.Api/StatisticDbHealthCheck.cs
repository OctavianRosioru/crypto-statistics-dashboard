using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NEW_STATISTIC.Infrastructure.Data;

namespace NEW_STATISTIC.Api;

public sealed class StatisticDbHealthCheck : IHealthCheck
{
    private readonly IDbContextFactory<StatisticDbContext> _factory;

    public StatisticDbHealthCheck(IDbContextFactory<StatisticDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            if (!await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
                return HealthCheckResult.Unhealthy("SQLite nu răspunde.");
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Eroare health DB.", ex);
        }
    }
}
