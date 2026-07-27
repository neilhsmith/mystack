using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MyStack.Auth.Data;

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    // auth and api share one Postgres database and take a schema each — the same split that gives
    // MyStack.Jobs its hangfire_auth / hangfire_api schemas (docs/architecture.md §3.3).
    public const string Schema = "auth";

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

        // The application owns key generation (see ApplicationUser), so EF must not substitute a
        // value generator or read one back from the database.
        builder.Entity<ApplicationUser>().Property(user => user.Id).ValueGeneratedNever();
        builder.Entity<ApplicationRole>().Property(role => role.Id).ValueGeneratedNever();
    }
}
