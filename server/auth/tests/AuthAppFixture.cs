using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MyStack.Auth.Data;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace MyStack.Auth.Tests;

public sealed class AuthAppFixture : IAsyncLifetime
{
    // The seeded public + PKCE client every flow test drives, and the seeded global admin —
    // declared in AuthApplicationFactory's seed configuration, the same way every environment's
    // clients and accounts are.
    public const string ClientId = "test-client";
    public const string RedirectUri = "http://localhost/callback";
    public const string PostLogoutRedirectUri = "http://localhost/signed-out";
    public const string AdminEmail = "admin@mystack.test";
    public const string DefaultPassword = "a perfectly adequate passphrase";

    // The seeded machine client — confidential, client credentials only — that the token-surface
    // tests authenticate as.
    public const string MachineClientId = "test-machine";
    public const string MachineClientSecret = "a secret only machines know";

    // The seeded device-flow client the device-dance tests poll as, and the seeded PAR-required
    // client the pushed-authorization tests drive.
    public const string DeviceClientId = "test-device";
    public const string DeviceClientDisplayName = "Test device";
    public const string ParClientId = "test-par";

    // A browser client whose back-channel logout URI points at a port nothing listens on, so
    // every sign-out also proves an unreachable client blocks neither the response nor the
    // deliveries to the reachable ones.
    public const string UnreachableClientId = "test-unreachable";

    // The seeded confidential browser client — the future BFF's exact shape: a secret presented
    // at the token endpoint, and the introspection permission that comes with being confidential.
    public const string ConfidentialClientId = "test-confidential";
    public const string ConfidentialClientSecret = "a secret the test bff keeps server-side";

    // The images compose runs, so the migration and the broker topology are proven against what
    // the stack actually uses rather than whatever `latest` happens to be.
    private readonly PostgreSqlContainer database = new PostgreSqlBuilder(
        "postgres:18-alpine"
    ).Build();

    private readonly RabbitMqContainer broker = new RabbitMqBuilder(
        "rabbitmq:4-management-alpine"
    ).Build();

    private AuthApplicationFactory? application;

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => application!.Services;

    public RecordingLoggerProvider Logs { get; } = new();

    // The fake relying parties the seeded browser clients deliver logout tokens to —
    // test-client's and test-par's, respectively.
    public FakeRelyingParty RelyingParty { get; private set; } = null!;

    public FakeRelyingParty SecondRelyingParty { get; private set; } = null!;

    public string DatabaseConnectionString => database.GetConnectionString();

    public string BrokerConnectionString => broker.GetConnectionString();

    /// <summary>
    /// A client that keeps cookies and surfaces redirects instead of following them — the shape
    /// every OAuth flow test needs, isolated per call so sessions don't bleed between tests.
    /// </summary>
    public HttpClient CreateFlowClient() =>
        application!.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );

    /// <summary>
    /// A fresh database on the shared container, for tests that boot their own hosts — seeding's
    /// boot-level behavior must not mutate the database every other test shares.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string name)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(database.GetConnectionString())
        {
            Database = name,
        }.ConnectionString;
    }

    public async Task<ApplicationUser> CreateUserAsync(
        string email,
        string password = DefaultPassword,
        bool emailConfirmed = true,
        string? role = null
    )
    {
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = emailConfirmed,
        };

        ThrowIfFailed(await users.CreateAsync(user, password));

        if (role is not null)
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (!await roles.RoleExistsAsync(role))
            {
                ThrowIfFailed(await roles.CreateAsync(new ApplicationRole(role)));
            }

            ThrowIfFailed(await users.AddToRoleAsync(user, role));
        }

        return user;
    }

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(database.StartAsync(), broker.StartAsync());

        // Started before the host so their dynamically bound URIs can ride the seed config.
        RelyingParty = await FakeRelyingParty.StartAsync();
        SecondRelyingParty = await FakeRelyingParty.StartAsync();

        application = new AuthApplicationFactory(
            database.GetConnectionString(),
            broker.GetConnectionString(),
            Logs,
            new Dictionary<string, string?>
            {
                ["Seed:Clients:0:BackchannelLogoutUri"] = RelyingParty.LogoutUri,
                ["Seed:Clients:3:BackchannelLogoutUri"] = SecondRelyingParty.LogoutUri,
                // Port 9 is the discard service nothing runs, so the connection is refused
                // immediately rather than hanging out the delivery timeout.
                ["Seed:Clients:4:ClientId"] = UnreachableClientId,
                ["Seed:Clients:4:Type"] = "Public",
                ["Seed:Clients:4:RedirectUris:0"] = RedirectUri,
                ["Seed:Clients:4:BackchannelLogoutUri"] = "http://127.0.0.1:9/backchannel-logout",
                ["Seed:Clients:5:ClientId"] = ConfidentialClientId,
                ["Seed:Clients:5:Type"] = "Confidential",
                ["Seed:Clients:5:Secret"] = ConfidentialClientSecret,
                ["Seed:Clients:5:RedirectUris:0"] = RedirectUri,
                ["Seed:Clients:5:Scopes:0"] = "email",
                ["Seed:Clients:5:Scopes:1"] = "api.read",
            }
        );

        // CreateClient is what builds the host, so the migration and the seed run here.
        Client = application.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();

        if (application is not null)
        {
            await application.DisposeAsync();
        }

        await database.DisposeAsync();
        await broker.DisposeAsync();

        if (RelyingParty is not null)
        {
            await RelyingParty.DisposeAsync();
        }

        if (SecondRelyingParty is not null)
        {
            await SecondRelyingParty.DisposeAsync();
        }
    }

    private static void ThrowIfFailed(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                string.Join("; ", result.Errors.Select(error => error.Description))
            );
        }
    }
}
