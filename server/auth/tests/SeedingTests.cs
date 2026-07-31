using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyStack.Auth.Data;
using MyStack.Contracts.Auth;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using Shouldly;

namespace MyStack.Auth.Tests;

// Boot-level behavior that *commits* gets its own database per test
// (AuthAppFixture.CreateDatabaseAsync): those boots must not rewrite the clients every other test
// drives. The failing-boot tests run against the shared database on purpose — safe only because
// DatabaseInitializer wraps the whole seed in one transaction, so a boot that throws rolls back
// every reconciliation it performed first. If the seeder ever commits per item, those tests must
// move onto their own databases.
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

        // Raw wire literals on purpose: the assertions must break if the seeder's shapes drift.
        var device = await applications.FindByClientIdAsync(
            AuthAppFixture.DeviceClientId,
            cancellationToken
        );
        device.ShouldNotBeNull();
        var devicePermissions = await applications.GetPermissionsAsync(device, cancellationToken);
        devicePermissions.ShouldContain("ept:device_authorization");
        devicePermissions.ShouldContain("gt:urn:ietf:params:oauth:grant-type:device_code");
        devicePermissions.ShouldNotContain("ept:authorization");

        var par = await applications.FindByClientIdAsync(
            AuthAppFixture.ParClientId,
            cancellationToken
        );
        par.ShouldNotBeNull();
        (await applications.GetRequirementsAsync(par, cancellationToken)).ShouldContain("ft:par");
        (await applications.GetPermissionsAsync(client, cancellationToken)).ShouldContain(
            "ept:pushed_authorization"
        );

        // The confidential browser shape: a secret-holding client gains introspection — the
        // liveness check a BFF runs without holding auth's key material.
        var confidential = await applications.FindByClientIdAsync(
            AuthAppFixture.ConfidentialClientId,
            cancellationToken
        );
        confidential.ShouldNotBeNull();
        (await applications.GetPermissionsAsync(confidential, cancellationToken)).ShouldContain(
            "ept:introspection"
        );

        (await applications.GetSettingsAsync(client, cancellationToken)).ShouldContainKey(
            "mystack:backchannel_logout_uri"
        );

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

    [Fact]
    public async Task ChangedBackchannelLogoutUri_ReconcilesTheStoredClient()
    {
        var connectionString = await app.CreateDatabaseAsync("seed_backchannel_reconcile");

        await using var first = Factory(
            connectionString,
            new Dictionary<string, string?>
            {
                ["Seed:Clients:0:BackchannelLogoutUri"] = "http://localhost/old-logout",
            }
        );
        first.CreateClient().Dispose();

        await using var second = Factory(
            connectionString,
            new Dictionary<string, string?>
            {
                ["Seed:Clients:0:BackchannelLogoutUri"] = "http://localhost/new-logout",
            }
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
        (await applications.GetSettingsAsync(client, TestContext.Current.CancellationToken))[
            "mystack:backchannel_logout_uri"
        ]
            .ShouldBe("http://localhost/new-logout");
    }

    // Back-channel logout is a browser-client concern: a machine client has no user session to
    // end, a device client no server endpoint to notify.
    [Fact]
    public void MachineClientWithABackchannelLogoutUri_FailsStartup()
    {
        using var factory = Factory(
            app.DatabaseConnectionString,
            new Dictionary<string, string?>
            {
                ["Seed:Clients:1:BackchannelLogoutUri"] = "http://localhost/logout",
            }
        );

        MessagesOf(Should.Throw<Exception>(() => factory.CreateClient()))
            .ShouldContain(message => message.Contains("BackchannelLogoutUri"));
    }

    [Fact]
    public void DeviceClientWithABackchannelLogoutUri_FailsStartup()
    {
        using var factory = Factory(
            app.DatabaseConnectionString,
            new Dictionary<string, string?>
            {
                ["Seed:Clients:2:BackchannelLogoutUri"] = "http://localhost/logout",
            }
        );

        MessagesOf(Should.Throw<Exception>(() => factory.CreateClient()))
            .ShouldContain(message => message.Contains("BackchannelLogoutUri"));
    }

    // Every misdeclared client shape the seeder refuses, one theory row each: failing to boot
    // beats booting wrong (§3.4). Shared database — the failed seed rolls back (class comment).
    public static TheoryData<string, string, string, string?> Misdeclarations =>
        new()
        {
            { "browser without redirects", "Public", "", "has no RedirectUris" },
            { "confidential without a secret", "Confidential", "RedirectUris:0", "has no Secret" },
            { "public with a secret", "Public|Secret", "RedirectUris:0", "declares a Secret" },
            { "machine without a secret", "Machine", "", "has no Secret" },
            {
                "machine with redirects",
                "Machine|Secret",
                "RedirectUris:0",
                "declares redirect URIs"
            },
            {
                "machine requiring PAR",
                "Machine|Secret|RequirePar",
                "",
                "never uses the authorization endpoint"
            },
            { "device with a secret", "Device|Secret", "", "declares a Secret" },
            { "device with redirects", "Device", "RedirectUris:0", "declares redirect URIs" },
            {
                "device requiring PAR",
                "Device|RequirePar",
                "",
                "never uses the authorization endpoint"
            },
            { "unknown scope", "Public|BadScope", "RedirectUris:0", "unknown scope" },
            // On Unix a bare "/path" parses as an absolute file:// URI, so this row also pins
            // the seeder's http(s) scheme check.
            {
                "relative redirect URI",
                "Public",
                "RelativeRedirect",
                "not an absolute http(s) URI"
            },
        };

    [Theory]
    [MemberData(nameof(Misdeclarations))]
    public void MisdeclaredClientShape_FailsStartup(
        string label,
        string shape,
        string extras,
        string? messageFragment
    )
    {
        var parts = shape.Split('|');
        var settings = new Dictionary<string, string?>
        {
            ["Seed:Clients:4:ClientId"] = "misdeclared",
            ["Seed:Clients:4:Type"] = parts[0],
        };

        if (parts.Contains("Secret"))
        {
            settings["Seed:Clients:4:Secret"] = "a secret";
        }

        if (parts.Contains("RequirePar"))
        {
            settings["Seed:Clients:4:RequirePushedAuthorizationRequests"] = "true";
        }

        if (parts.Contains("BadScope"))
        {
            settings["Seed:Clients:4:Scopes:0"] = "made.up";
        }

        if (extras == "RedirectUris:0")
        {
            settings["Seed:Clients:4:RedirectUris:0"] = "http://localhost/misdeclared";
        }
        else if (extras == "RelativeRedirect")
        {
            settings["Seed:Clients:4:RedirectUris:0"] = "/relative";
        }

        using var factory = Factory(app.DatabaseConnectionString, settings);

        MessagesOf(Should.Throw<Exception>(() => factory.CreateClient()))
            .ShouldContain(message => message.Contains(messageFragment!), label);
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
        (await applications.CountAsync(cancellationToken)).ShouldBe(4);
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
