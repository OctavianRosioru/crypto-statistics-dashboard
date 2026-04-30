using Microsoft.EntityFrameworkCore;
using NEW_STATISTIC.Infrastructure.Data;

namespace NEW_STATISTIC.Worker;

public sealed class DatabaseMigrationHostedService : IHostedService
{
    private readonly IDbContextFactory<StatisticDbContext> _factory;
    private readonly ILogger<DatabaseMigrationHostedService> _log;

    public DatabaseMigrationHostedService(
        IDbContextFactory<StatisticDbContext> factory,
        ILogger<DatabaseMigrationHostedService> log)
    {
        _factory = factory;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        _log.LogInformation("Database migrated.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
