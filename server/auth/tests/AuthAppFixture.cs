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

        application = new AuthApplicationFactory(
            database.GetConnectionString(),
            broker.GetConnectionString(),
            Logs
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
