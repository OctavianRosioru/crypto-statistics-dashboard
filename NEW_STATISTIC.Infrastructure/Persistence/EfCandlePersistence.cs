using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NEW_STATISTIC.Core.Domain;
using NEW_STATISTIC.Infrastructure.Data;

namespace NEW_STATISTIC.Infrastructure.Persistence;

public sealed class EfCandlePersistence
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly IDbContextFactory<StatisticDbContext> _factory;

    public EfCandlePersistence(IDbContextFactory<StatisticDbContext> factory)
    {
        _factory = factory;
    }

    public async Task SaveAsync(CandlePersistencePayload payload, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await SaveSingleInContextAsync(db, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>O singură tranzacție pentru mai multe lumânări (batch).</summary>
    public async Task SaveBatchAsync(
        IReadOnlyList<CandlePersistencePayload> batch,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
            return;

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var payload in batch)
                await SaveSingleInContextAsync(db, payload, cancellationToken).ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task SaveSingleInContextAsync(
        StatisticDbContext db,
        CandlePersistencePayload payload,
        CancellationToken cancellationToken)
    {
        var c = payload.Candle;
        var entity = new CandleEntity
        {
            Exchange = c.Exchange,
            Symbol = c.Symbol,
            TriggerTimeMs = c.TriggerTimeMs,
            WindowStartMs = c.WindowStartMs,
            WindowEndMs = c.WindowEndMs,
            LastTradeTimeInWindowMs = c.LastTradeTimeInWindowMs,
            MinPrice = c.MinPrice,
            MaxPrice = c.MaxPrice,
            DiffPercent = c.DiffPercent,
            Side = c.Side.ToString(),
            FirstTradePrice = c.FirstTradePrice,
            TotalQuoteUsdt = c.TotalQuoteUsdt,
            DensityUsdtPerMs = c.DensityUsdtPerMs,
            FollowUpJson = JsonSerializer.Serialize(c.FollowUp, Json),
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Candles.Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var row in payload.Simulations)
        {
            var outcomes = row.OutcomesByHorizonSec.ToDictionary(
                static kv => kv.Key.ToString(),
                static kv => new { k = (int)kv.Value.Kind, t = kv.Value.EventTimeMs, p = kv.Value.ActualPrice });
            db.Simulations.Add(new SimulationEntity
            {
                CandleId = entity.Id,
                OpenOffsetPercent = row.OpenOffsetPercent,
                OpenPrice = row.OpenPrice,
                TakeProfitPrice = row.TakeProfitPrice,
                StopLossPrice = row.StopLossPrice,
                OutcomesJson = JsonSerializer.Serialize(outcomes, Json)
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
