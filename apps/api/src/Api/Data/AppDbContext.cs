using System.Linq.Expressions;
using Api.Features.Posts;
using Api.Identity;
using Api.Rbac;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Api.Data;

/// <summary>
/// Single DbContext for the whole process. Owns:
/// <list type="bullet">
///   <item>Business entities (e.g. <see cref="Posts.Post"/>) — declared as direct
///   <see cref="DbSet{T}"/> properties.</item>
///   <item>ASP.NET Identity tables (users, roles, role-claims, user-roles, user-tokens, ...)
///   — picked up by inheriting <see cref="IdentityDbContext{TUser,TRole,TKey}"/>.</item>
///   <item>RBAC join (<see cref="Permission"/>, <see cref="RolePermission"/>) — declared
///   below; one Postgres row per role↔permission assignment, the on-disk projection of
///   <see cref="Roles.DefaultPermissions"/> after seeding.</item>
///   <item>OpenIddict server stores (applications, authorizations, scopes, tokens) —
///   wired by <c>options.UseOpenIddict()</c> in <c>Program.cs</c> against this same
///   context.</item>
/// </list>
/// One DbContext = one migrations history = one Postgres database. The auth concerns sit
/// inside the API process by deliberate choice (see CLAUDE.md / PR description); if you
/// ever extract auth into its own service, this is the file where the split begins.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public DbSet<Post> Posts => Set<Post>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Run IdentityDbContext's mapping first (configures aspnet_users, aspnet_roles, etc.).
        base.OnModelCreating(modelBuilder);

        // Per-entity config lives in each feature folder as `IEntityTypeConfiguration<T>`
        // (e.g. PostConfiguration). Discovered here so the data layer doesn't need to
        // import every feature's namespace.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

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

        // Convention: every business entity gets Postgres's `xmin` system column as a
        // concurrency token. EF tracks it as a shadow property (no entity property
        // required), throws DbUpdateConcurrencyException on stale writes, and the column
        // is provider-managed so there's no migration cost when a new entity is added.
        // ETags are derived from this same value (see ETag.From(DbContext, object)) so
        // the HTTP precondition check and EF's DB-level concurrency check agree on what
        // "current" means.
        //
        // Skip:
        //  - Identity tables (mapped by IdentityDbContext, fixed schema we don't own).
        //  - OpenIddict tables (managed by OpenIddict's EF stores).
        //  - RBAC tables (Permission, RolePermission — admin-managed reference data; xmin
        //    would conflict with the seeder's upsert-then-insert pattern).
        // Inlined from Npgsql's typed UseXminAsConcurrencyToken() extension — that one
        // hangs off EntityTypeBuilder<T> only, and the convention loop scans by Type.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(e => e.BaseType is null
                         && !e.IsOwned()
                         && !IsExternallyManagedTable(e.ClrType)))
        {
            modelBuilder.Entity(entityType.ClrType)
                .Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        }
    }

    /// <summary>
    /// Returns true for entity types whose schema is owned by an external concern
    /// (Identity, OpenIddict, RBAC reference data) — these are excluded from the xmin
    /// concurrency-token convention.
    /// </summary>
    private static bool IsExternallyManagedTable(Type clrType)
    {
        if (clrType.Namespace is { } ns)
        {
            // Identity tables — Microsoft.AspNetCore.Identity.* + our ApplicationUser/Role
            // both sit logically in the Identity bucket.
            if (ns.StartsWith("Microsoft.AspNetCore.Identity", StringComparison.Ordinal)
                || ns.StartsWith("Api.Identity", StringComparison.Ordinal))
            {
                return true;
            }

            // OpenIddict EF Core entities live in the OpenIddict.EntityFrameworkCore.Models
            // namespace; the stores manage their own concurrency tokens.
            if (ns.StartsWith("OpenIddict", StringComparison.Ordinal))
            {
                return true;
            }

            // RBAC catalog tables — admin-managed, low-write, no contention worth
            // protecting against; xmin would also confuse the upsert seeder.
            if (ns.StartsWith("Api.Rbac", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
