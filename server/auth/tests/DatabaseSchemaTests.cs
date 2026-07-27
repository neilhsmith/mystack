using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyStack.Auth.Data;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class DatabaseSchemaTests(AuthAppFixture app)
{
    [Fact]
    public async Task Migrations_bring_an_empty_database_up_to_the_current_model()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var pending = await context.Database.GetPendingMigrationsAsync(
            TestContext.Current.CancellationToken
        );

        pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_model_change_has_a_migration_behind_it()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        // Fails the moment the entity model moves without `dotnet ef migrations add`, which is the
        // version of this mistake that otherwise surfaces as a runtime error in an environment
        // where Database:Migrate is off.
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Fact]
    public async Task Identity_lives_in_the_auth_schema_under_its_own_table_names()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var tables = await context
            .Database.SqlQuery<string>(
                $"select table_name from information_schema.tables where table_schema = 'auth'"
            )
            .ToListAsync(TestContext.Current.CancellationToken);

        tables.ShouldBe(
            [
                "__ef_migrations_history",
                "role_claims",
                "roles",
                "user_claims",
                "user_logins",
                "user_roles",
                "user_tokens",
                "users",
            ],
            ignoreOrder: true
        );
    }

    [Fact]
    public async Task Columns_are_snake_case()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var columns = await context
            .Database.SqlQuery<string>(
                $"select column_name from information_schema.columns where table_schema = 'auth' and table_name = 'users'"
            )
            .ToListAsync(TestContext.Current.CancellationToken);

        columns.ShouldContain("normalized_email");
        columns.ShouldContain("email_confirmed");
        columns.ShouldContain("access_failed_count");
    }
}
