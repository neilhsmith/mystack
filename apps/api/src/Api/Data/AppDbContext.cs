using Api.Features.Posts;
using Microsoft.EntityFrameworkCore;

namespace Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Post> Posts => Set<Post>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Post>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Title).IsRequired().HasMaxLength(200);
            entity.Property(p => p.Content).IsRequired();
        });

        // Convention: every IHasTimestamps entity gets `now()` column defaults so non-EF
        // writers (Hangfire jobs, future serverless workers, raw SQL maintenance) can't
        // leave CreatedAt/UpdatedAt unset. The per-table UPDATE trigger (created in the
        // initial migration) handles the same concern for UpdatedAt on UPDATE statements
        // that omit it. The TimestampsInterceptor still wins for EF writes — it sends
        // explicit values that the DB stores as-is, so app-time stays authoritative in
        // the normal path.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(e => typeof(IHasTimestamps).IsAssignableFrom(e.ClrType)))
        {
            modelBuilder.Entity(entityType.ClrType)
                .Property(nameof(IHasTimestamps.CreatedAt))
                .HasDefaultValueSql("now()");

            modelBuilder.Entity(entityType.ClrType)
                .Property(nameof(IHasTimestamps.UpdatedAt))
                .HasDefaultValueSql("now()");
        }
    }
}
