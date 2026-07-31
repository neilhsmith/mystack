using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyStack.Auth.Contracts;
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

        var machine = await applications.FindByClientIdAsync(
            AuthAppFixture.MachineClientId,
            cancellationToken
        );
        machine.ShouldNotBeNull();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await users.FindByEmailAsync(AuthAppFixture.AdminEmail);
        admin.ShouldNotBeNull();
        admin.EmailConfirmed.ShouldBeTrue();
        (await users.IsInRoleAsync(admin, AuthRoles.GlobalAdmin)).ShouldBeTrue();

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
        var before = await SnapshotAsync(first.Services);

        await using var second = Factory(connectionString);
        second.CreateClient().Dispose();
        var after = await SnapshotAsync(second.Services);

        // Concurrency stamps only move on an update, so equality is "no write" — not merely
        // "same values".
        after.ShouldBe(before);
    }

    [Fact]
    public async Task ChangedClientConfig_ReconcilesTheStoredClient()
    {
        var connectionString = await app.CreateDatabaseAsync("seed_reconcile");

        await using var first = Factory(connectionString);
        first.CreateClient().Dispose();
        var before = await SnapshotAsync(first.Services);

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

        var after = await SnapshotAsync(second.Services);
        after.ClientConcurrencyToken.ShouldNotBe(before.ClientConcurrencyToken);
    }

    // Config is the source of truth for a declared account: roles sync exactly, a declared
    // password applies, and an absent password is no opinion rather than "remove it".
    [Fact]
    public async Task ChangedUserConfig_ReconcilesRolesAndDeclaredPassword()
    {
        var connectionString = await app.CreateDatabaseAsync("seed_user_reconcile");

        await using var first = Factory(connectionString);
        first.CreateClient().Dispose();

        await using var second = Factory(
            connectionString,
            new Dictionary<string, string?>
            {
                ["Seed:Users:0:Password"] = AuthAppFixture.DefaultPassword,
                ["Seed:Users:0:Roles:1"] = AuthRoles.Admin,
            }
        );
        second.CreateClient().Dispose();

        await using (var scope = second.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var admin = await users.FindByEmailAsync(AuthAppFixture.AdminEmail);
            admin.ShouldNotBeNull();
            (await users.CheckPasswordAsync(admin, AuthAppFixture.DefaultPassword)).ShouldBeTrue(
                "the declared password should now be usable"
            );
            (await users.IsInRoleAsync(admin, AuthRoles.Admin)).ShouldBeTrue();
        }

        // Back to the original config: the extra role goes, but the password stays — config
        // no longer declares one, and "no opinion" must never strip a usable credential.
        await using var third = Factory(connectionString);
        third.CreateClient().Dispose();

        await using (var scope = third.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var admin = await users.FindByEmailAsync(AuthAppFixture.AdminEmail);
            admin.ShouldNotBeNull();
            (await users.IsInRoleAsync(admin, AuthRoles.Admin)).ShouldBeFalse();
            (await users.IsInRoleAsync(admin, AuthRoles.GlobalAdmin)).ShouldBeTrue();
            (await users.CheckPasswordAsync(admin, AuthAppFixture.DefaultPassword)).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task DeclaredUsers_SeedWithPasswordsAndRoles()
    {
        var connectionString = await app.CreateDatabaseAsync("seed_users");

        await using var factory = Factory(
            connectionString,
            new Dictionary<string, string?>
            {
                ["Seed:Users:1:Email"] = "someone@mystack.test",
                ["Seed:Users:1:Password"] = AuthAppFixture.DefaultPassword,
                ["Seed:Users:1:Roles:0"] = AuthRoles.User,
            }
        );
        factory.CreateClient().Dispose();

        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var someone = await users.FindByEmailAsync("someone@mystack.test");
        someone.ShouldNotBeNull();
        someone.EmailConfirmed.ShouldBeTrue();
        (await users.HasPasswordAsync(someone)).ShouldBeTrue();
        (await users.IsInRoleAsync(someone, AuthRoles.User)).ShouldBeTrue();
    }

    // Seeding must guarantee somebody can administrate; a config without a global admin is a
    // mistake, and the deliberate opt-out is Database:Seed off.
    [Fact]
    public void ConfigWithoutAGlobalAdmin_FailsStartup()
    {
        using var factory = Factory(
            app.DatabaseConnectionString,
            new Dictionary<string, string?> { ["Seed:Users:0:Roles:0"] = AuthRoles.User }
        );

        MessagesOf(Should.Throw<Exception>(() => factory.CreateClient()))
            .ShouldContain(message => message.Contains(AuthRoles.GlobalAdmin));
    }

    [Fact]
    public void ConfigWithAnUnknownRole_FailsStartup()
    {
        using var factory = Factory(
            app.DatabaseConnectionString,
            new Dictionary<string, string?>
            {
                ["Seed:Users:1:Email"] = "typo@mystack.test",
                ["Seed:Users:1:Roles:0"] = "superuser",
            }
        );

        MessagesOf(Should.Throw<Exception>(() => factory.CreateClient()))
            .ShouldContain(message => message.Contains("superuser"));
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
        (await applications.CountAsync(cancellationToken)).ShouldBe(2);
    }

    private AuthApplicationFactory Factory(
        string connectionString,
        IDictionary<string, string?>? settings = null
    ) => new(connectionString, app.BrokerConnectionString, settings: settings);

    private static List<string> MessagesOf(Exception thrown)
    {
        var messages = new List<string>();
        for (
            var current = (Exception?)thrown;
            current is not null;
            current = current.InnerException
        )
        {
            messages.Add(current.Message);
        }

        return messages;
    }

    private static async Task<(
        string ClientConcurrencyToken,
        Guid AdminId,
        string AdminConcurrencyStamp,
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

        var admin = await context
            .Set<ApplicationUser>()
            .Where(user => user.Email == AuthAppFixture.AdminEmail)
            .Select(user => new { user.Id, user.ConcurrencyStamp })
            .SingleAsync();

        return (
            token!,
            admin.Id,
            admin.ConcurrencyStamp!,
            await context.Set<ApplicationRole>().CountAsync()
        );
    }
}
