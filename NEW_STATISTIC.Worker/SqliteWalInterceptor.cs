using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NEW_STATISTIC.Worker;

/// <summary>
/// Setează wal_autocheckpoint la 200 pagini (față de 1000 default) la fiecare conexiune deschisă.
/// Previne creșterea necontrolată a fișierului .db-wal.
/// </summary>
internal sealed class SqliteWalInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA wal_autocheckpoint=200;";
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA wal_autocheckpoint=200;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
