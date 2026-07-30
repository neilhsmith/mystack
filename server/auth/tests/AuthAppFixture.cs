using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyStack.Auth.Data;
using OpenIddict.Abstractions;
using Testcontainers.PostgreSql;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyStack.Auth.Tests;

public sealed class AuthAppFixture : IAsyncLifetime
{
    // The manually registered client auth-track step 4 calls for: public + PKCE, implicit
    // consent, no secret — the same shape the web BFF's registration will take.
    public const string ClientId = "test-client";
    public const string RedirectUri = "http://localhost/callback";
    public const string PostLogoutRedirectUri = "http://localhost/signed-out";
    public const string DefaultPassword = "a perfectly adequate passphrase";

    // The image compose runs, so the migration is proven against the Postgres the stack actually
    // uses rather than whatever `latest` happens to be.
    private readonly PostgreSqlContainer database = new PostgreSqlBuilder(
        "postgres:18-alpine"
    ).Build();

    private AuthApplicationFactory? application;

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => application!.Services;

    public RecordingLoggerProvider Logs { get; } = new();

    /// <summary>
    /// A client that keeps cookies and surfaces redirects instead of following them — the shape
    /// every OAuth flow test needs, isolated per call so sessions don't bleed between tests.
    /// </summary>
    public HttpClient CreateFlowClient() =>
        application!.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );

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
        await database.StartAsync();

        application = new AuthApplicationFactory(database.GetConnectionString(), Logs);

        // CreateClient is what builds the host, so the migration runs here.
        Client = application.CreateClient();

        await RegisterTestClientAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();

        if (application is not null)
        {
            await application.DisposeAsync();
        }

        await database.DisposeAsync();
    }

    private async Task RegisterTestClientAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        await manager.CreateAsync(
            new OpenIddictApplicationDescriptor
            {
                ClientId = ClientId,
                ClientType = ClientTypes.Public,
                ConsentType = ConsentTypes.Implicit,
                DisplayName = "Test client",
                RedirectUris = { new Uri(RedirectUri) },
                PostLogoutRedirectUris = { new Uri(PostLogoutRedirectUri) },
                Permissions =
                {
                    Permissions.Endpoints.Authorization,
                    Permissions.Endpoints.Token,
                    Permissions.Endpoints.EndSession,
                    Permissions.Endpoints.Revocation,
                    Permissions.GrantTypes.AuthorizationCode,
                    Permissions.GrantTypes.RefreshToken,
                    Permissions.ResponseTypes.Code,
                    Permissions.Scopes.Email,
                    Permissions.Scopes.Profile,
                    Permissions.Scopes.Roles,
                    Permissions.Prefixes.Scope + "api.read",
                    Permissions.Prefixes.Scope + "api.write",
                },
                Requirements = { Requirements.Features.ProofKeyForCodeExchange },
            },
            TestContext.Current.CancellationToken
        );
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

    private sealed class AuthApplicationFactory(
        string connectionString,
        RecordingLoggerProvider logs
    ) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Not "Development": that would load appsettings.Development.json and hand the host a
            // working connection string to the developer's compose database, so a test whose own
            // configuration never arrived would still pass — against the wrong database.
            builder.UseEnvironment("Testing");

            // UseSetting, not ConfigureAppConfiguration. Under the minimal hosting model the
            // factory runs Program's own Main and can only reach its configuration through the
            // args it passes in — UseSetting becomes `--key=value`, while
            // ConfigureAppConfiguration delegates are never invoked at all.
            builder.UseSetting("ConnectionStrings:AuthDb", connectionString);
            builder.UseSetting("Database:Migrate", "true");

            // One immediate retry and half-second polling: a retry-then-dead-letter sequence is
            // provable in seconds instead of the production backoff's minutes.
            builder.UseSetting("Jobs:RetryAttempts", "1");
            builder.UseSetting("Jobs:RetryDelaysInSeconds:0", "0");
            builder.UseSetting("Jobs:PollInterval", "00:00:00.500");

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ILoggerProvider>(logs);
                services.AddSingleton<JobRecorder>();
            });
        }
    }
}
