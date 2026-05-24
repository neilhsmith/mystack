using System.Linq.Expressions;
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
            entity.Property(p => p.Title).IsRequired().HasMaxLength(Post.MaxTitleLength);
            entity.Property(p => p.Content).IsRequired();
        });

        // Convention: every ITimestamped entity gets `now()` column defaults so non-EF
        // writers can't leave CreatedAt/UpdatedAt unset. The per-table UPDATE trigger
        // (created in InitialCreate) handles the same concern for UpdatedAt on UPDATE
        // statements that omit it. AuditInterceptor still wins for EF writes — it sends
        // explicit values that the DB stores as-is, so app-time stays authoritative.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(e => typeof(ITimestamped).IsAssignableFrom(e.ClrType)))
        {
            modelBuilder.Entity(entityType.ClrType)
                .Property(nameof(ITimestamped.CreatedAt))
                .HasDefaultValueSql("now()");

            modelBuilder.Entity(entityType.ClrType)
                .Property(nameof(ITimestamped.UpdatedAt))
                .HasDefaultValueSql("now()");
        }

        // Convention: every ISoftDeletable entity gets a global query filter so
        // `db.Posts.ToList()` etc. transparently exclude soft-deleted rows. Use
        // `.IgnoreQueryFilters()` to opt out for admin/audit views.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(e => typeof(ISoftDeletable).IsAssignableFrom(e.ClrType)))
        {
            var parameter = Expression.Parameter(entityType.ClrType, "e");
            var deletedAt = Expression.Property(parameter, nameof(ISoftDeletable.DeletedAt));
            var nullConstant = Expression.Constant(null, typeof(DateTimeOffset?));
            var compare = Expression.Equal(deletedAt, nullConstant);
            var lambda = Expression.Lambda(compare, parameter);

            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(lambda);
        }
    }
}
