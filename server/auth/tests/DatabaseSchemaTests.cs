using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyStack.Auth.Data;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class DatabaseSchemaTests(AuthAppFixture app)
{
    [Fact]
    public async Task Migrations_CoverEveryModelChange()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        // Fails the moment the entity model moves without `dotnet ef migrations add` — the
        // mistake that otherwise surfaces as a runtime error in an environment where
        // Database:Migrate is off.
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Fact]
    public async Task IdentityTables_LiveInTheAuthSchema()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        // The schema boundary is what lets auth and api share one database (architecture §3.3),
        // and nothing else fails loudly if a table quietly lands in public instead.
        var tables = await context
            .Database.SqlQuery<string>(
                $"select table_name from information_schema.tables where table_schema = 'auth'"
            )
            .ToListAsync(TestContext.Current.CancellationToken);

        tables.ShouldContain("users");
        tables.ShouldContain("roles");
        tables.ShouldContain("oidc_applications");
        tables.ShouldContain("oidc_authorizations");
        tables.ShouldContain("oidc_scopes");
        tables.ShouldContain("oidc_tokens");
    }

    [Fact]
    public async Task HangfireTables_LiveInTheirOwnSchema()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        // Hangfire manages its own tables outside EF, in the per-app schema that architecture
        // §3.3 leans on for queue isolation.
        var tables = await context
            .Database.SqlQuery<string>(
                $"select table_name from information_schema.tables where table_schema = 'hangfire_auth'"
            )
            .ToListAsync(TestContext.Current.CancellationToken);

        tables.ShouldContain("job");
        tables.ShouldContain("jobqueue");
    }
}
