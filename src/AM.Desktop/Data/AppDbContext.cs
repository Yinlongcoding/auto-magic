using Microsoft.EntityFrameworkCore;

namespace AM.Desktop.Data;

/// <summary>
/// 应用数据库上下文
/// </summary>
public class AppDbContext : DbContext
{
    public DbSet<BatchEntity> Batches { get; set; }
    public DbSet<HistoryEntity> History { get; set; }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BatchEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.BatchId).IsUnique();
            entity.Property(e => e.BatchId).IsRequired();
            entity.Property(e => e.Keyword).IsRequired();
            entity.Property(e => e.DataJson).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
        });

        modelBuilder.Entity<HistoryEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Time).IsRequired();
            entity.Property(e => e.Batch).IsRequired();
            entity.Property(e => e.Action).IsRequired();
            entity.Property(e => e.Result).IsRequired();
        });
    }
}
