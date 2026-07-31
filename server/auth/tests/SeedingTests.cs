using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyStack.Auth.Data;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using Shouldly;

namespace MyStack.Auth.Tests;

// Boot-level behavior gets its own database per test (AuthAppFixture.CreateDatabaseAsync): these
// tests must not rewrite the client every other test drives.
public sealed class SeedingTests(AuthAppFixture app)
{
    // The shared host is itself a fresh boot against an empty database, so it is the
    // "fresh database + compose up yields a working state" proof.
    [Fact]
    public async Task FreshBoot_SeedsRolesScopesClientAndAdmin()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = app.Services.CreateAsyncScope();

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        foreach (var role in AuthRoles.All)
        {
            (await roles.RoleExistsAsync(role)).ShouldBeTrue($"role '{role}' should be seeded");
        }

        var scopes = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();
        foreach (var name in new[] { "api.read", "api.write" })
        {
            var apiScope = await scopes.FindByNameAsync(name, cancellationToken);
            apiScope.ShouldNotBeNull($"scope '{name}' should be seeded");
            (await scopes.GetResourcesAsync(apiScope, cancellationToken)).ShouldBe(["api"]);
        }

        var applications =
            scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var client = await applications.FindByClientIdAsync(
            AuthAppFixture.ClientId,
            cancellationToken
        );
        client.ShouldNotBeNull();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await users.FindByEmailAsync(AuthAppFixture.AdminEmail);
        admin.ShouldNotBeNull();
        admin.EmailConfirmed.ShouldBeTrue();
        (await users.IsInRoleAsync(admin, AuthRoles.Admin)).ShouldBeTrue();

        // The production posture: no password in config, so none usable — the account is
        // activated through the forgot-password flow, not a credential in a repo.
        (await users.HasPasswordAsync(admin)).ShouldBeFalse();
    }

    [Fact]
    public async Task SecondBoot_WritesNothing()
    {
        var connectionString = await app.CreateDatabaseAsync("seed_second_boot");

        await using var first = Factory(connectionString);
        first.CreateClient().Dispose();
        var (tokenBefore, adminBefore, roleCountBefore) = await SnapshotAsync(first.Services);

        await using var second = Factory(connectionString);
        second.CreateClient().Dispose();
        var (tokenAfter, adminAfter, roleCountAfter) = await SnapshotAsync(second.Services);

        // The client's concurrency token only moves on an update, so equality is "no write" —
        // not merely "same values".
        tokenAfter.ShouldBe(tokenBefore);
        adminAfter.ShouldBe(adminBefore);
        roleCountAfter.ShouldBe(roleCountBefore);
    }

    [Fact]
    public async Task ChangedClientConfig_ReconcilesTheStoredClient()
    {
        var connectionString = await app.CreateDatabaseAsync("seed_reconcile");

        await using var first = Factory(connectionString);
        first.CreateClient().Dispose();
        var (tokenBefore, _, _) = await SnapshotAsync(first.Services);

        await using var second = Factory(
            connectionString,
            new Dictionary<string, string?> { ["Seed:Clients:0:DisplayName"] = "Renamed client" }
        );
        second.CreateClient().Dispose();

        await using var scope = second.Services.CreateAsyncScope();
        var applications =
            scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var client = await applications.FindByClientIdAsync(
            AuthAppFixture.ClientId,
            TestContext.Current.CancellationToken
        );
        client.ShouldNotBeNull();
        (
            await applications.GetDisplayNameAsync(client, TestContext.Current.CancellationToken)
        ).ShouldBe("Renamed client");

        var (tokenAfter, _, _) = await SnapshotAsync(second.Services);
        tokenAfter.ShouldNotBe(tokenBefore);
    }

    [Fact]
    public async Task SampleSwitch_SeedsTheDeclaredAccounts()
    {
        var connectionString = await app.CreateDatabaseAsync("seed_sample");

        await using var factory = Factory(
            connectionString,
            new Dictionary<string, string?>
            {
                ["Database:Seed:Sample"] = "true",
                ["Seed:Sample:Users:0:Email"] = "sample@mystack.test",
                ["Seed:Sample:Users:0:Password"] = AuthAppFixture.DefaultPassword,
                ["Seed:Sample:Users:0:Roles:0"] = AuthRoles.User,
            }
        );
        factory.CreateClient().Dispose();

        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var sample = await users.FindByEmailAsync("sample@mystack.test");
        sample.ShouldNotBeNull();
        sample.EmailConfirmed.ShouldBeTrue();
        (await users.HasPasswordAsync(sample)).ShouldBeTrue();
        (await users.IsInRoleAsync(sample, AuthRoles.User)).ShouldBeTrue();
    }

    [Fact]
    public void SampleSwitch_InProduction_FailsStartup()
    {
        using var factory = new AuthApplicationFactory(
            app.DatabaseConnectionString,
            app.BrokerConnectionString,
            settings: new Dictionary<string, string?> { ["Database:Seed:Sample"] = "true" },
            environment: "Production"
        );

        var thrown = Should.Throw<Exception>(() => factory.CreateClient());

        var messages = new List<string>();
        for (
            var current = (Exception?)thrown;
            current is not null;
            current = current.InnerException
        )
        {
            messages.Add(current.Message);
        }

        messages.ShouldContain(
            message => message.Contains("Database:Seed:Sample"),
            "startup should refuse sample seeding in Production"
        );
    }

    // Two instances booting against one fresh database — the race the advisory lock exists for.
    [Fact]
    public async Task ConcurrentBoots_SeedExactlyOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await app.CreateDatabaseAsync("seed_race");

        await using var first = Factory(connectionString);
        await using var second = Factory(connectionString);
        await Task.WhenAll(
            Task.Run(() => first.CreateClient().Dispose(), cancellationToken),
            Task.Run(() => second.CreateClient().Dispose(), cancellationToken)
        );

        await using var scope = first.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        users.Users.Count(user => user.Email == AuthAppFixture.AdminEmail).ShouldBe(1);

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        roles.Roles.Count().ShouldBe(AuthRoles.All.Count);

        var applications =
            scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        (await applications.CountAsync(cancellationToken)).ShouldBe(1);
    }

    private AuthApplicationFactory Factory(
        string connectionString,
        IDictionary<string, string?>? settings = null
    ) => new(connectionString, app.BrokerConnectionString, settings: settings);

    private static async Task<(
        string ClientConcurrencyToken,
        Guid AdminId,
        int RoleCount
    )> SnapshotAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var token = await context
            .Set<OpenIddictEntityFrameworkCoreApplication>()
            .Where(application => application.ClientId == AuthAppFixture.ClientId)
            .Select(application => application.ConcurrencyToken)
            .SingleAsync();

        var adminId = await context
            .Set<ApplicationUser>()
            .Where(user => user.Email == AuthAppFixture.AdminEmail)
            .Select(user => user.Id)
            .SingleAsync();

        return (token!, adminId, await context.Set<ApplicationRole>().CountAsync());
    }
}
