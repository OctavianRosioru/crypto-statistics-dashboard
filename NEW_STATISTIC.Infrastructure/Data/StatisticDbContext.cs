using Microsoft.EntityFrameworkCore;

namespace NEW_STATISTIC.Infrastructure.Data;

public sealed class StatisticDbContext : DbContext
{
    public StatisticDbContext(DbContextOptions<StatisticDbContext> options)
        : base(options)
    {
    }

    public DbSet<CandleEntity> Candles => Set<CandleEntity>();

    public DbSet<SimulationEntity> Simulations => Set<SimulationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var c = modelBuilder.Entity<CandleEntity>();
        c.ToTable("Candles");
        c.HasKey(x => x.Id);
        c.HasIndex(x => new { x.Symbol, x.TriggerTimeMs });
        c.HasIndex(x => new { x.Exchange, x.CreatedAt });
        c.Property(x => x.Exchange).HasMaxLength(32);
        c.Property(x => x.Symbol).HasMaxLength(64);
        c.Property(x => x.Side).HasMaxLength(8);
        c.HasMany(x => x.Simulations)
            .WithOne(x => x.Candle!)
            .HasForeignKey(x => x.CandleId)
            .OnDelete(DeleteBehavior.Cascade);

        var s = modelBuilder.Entity<SimulationEntity>();
        s.ToTable("Simulations");
        s.HasKey(x => x.Id);
        s.HasIndex(x => x.CandleId);
    }
}
