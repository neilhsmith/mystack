using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace MyStack.Auth.Data;

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options),
        IDataProtectionKeyContext
{
    // auth and api share one Postgres database and take a schema each — the same split that gives
    // MyStack.Messaging its wolverine_<app> envelope schemas (docs/architecture.md §3.3).
    public const string Schema = "auth";

    // The data-protection key ring (IdentityExtensions) — what keeps emailed account tokens
    // valid across restarts and replicas.
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    // Per-user permission overrides (architecture §3.1), minted into tokens by TokenPrincipals.
    public DbSet<PermissionOverride> PermissionOverrides => Set<PermissionOverride>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema(Schema);

        // Identity's AspNet* names describe the framework rather than this schema. Renaming them
        // costs nothing now and a table-rename migration at any later point.
        builder.Entity<ApplicationUser>().ToTable("users");
        builder.Entity<ApplicationRole>().ToTable("roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");

        // Same rename rationale: the defaults would snake_case into open_iddict_* here.
        builder.UseOpenIddict();
        builder.Entity<OpenIddictEntityFrameworkCoreApplication>().ToTable("oidc_applications");
        builder.Entity<OpenIddictEntityFrameworkCoreAuthorization>().ToTable("oidc_authorizations");
        builder.Entity<OpenIddictEntityFrameworkCoreScope>().ToTable("oidc_scopes");
        builder.Entity<OpenIddictEntityFrameworkCoreToken>().ToTable("oidc_tokens");

        // The application owns key generation (see ApplicationUser), so EF must not substitute a
        // value generator or read one back from the database.
        builder.Entity<ApplicationUser>().Property(user => user.Id).ValueGeneratedNever();
        builder.Entity<ApplicationRole>().Property(role => role.Id).ValueGeneratedNever();

        builder.Entity<PermissionOverride>(entity =>
        {
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.Permission).HasMaxLength(128);
            // Stored as text so a row reads as what it is without decoding an integer.
            entity.Property(row => row.Kind).HasConversion<string>().HasMaxLength(16);
            // One row per (user, permission): a simultaneous grant and deny is a contradiction
            // the schema refuses rather than an arithmetic the API resolves. The index doubles
            // as the minting lookup's path.
            entity.HasIndex(row => new { row.UserId, row.Permission }).IsUnique();
            entity
                .HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(row => row.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
