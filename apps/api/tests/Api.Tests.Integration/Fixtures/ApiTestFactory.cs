using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using Testcontainers.PostgreSql;

namespace Api.Tests.Integration.Fixtures;

/// <summary>
/// Boots <c>Program</c> via <see cref="WebApplicationFactory{TEntryPoint}"/> against a
/// throwaway Postgres container. One instance is shared by every integration test class
/// in <see cref="IntegrationTestCollection"/> so the container-start cost is paid once
/// per assembly run.
/// <para>
/// <strong>Auth in tests.</strong> The factory swaps the OpenIddict validation scheme
/// handler with a <see cref="TestAuthHandler"/> for the same scheme name. Tests express
/// their identity by setting headers (<see cref="TestAuthHandler.RoleHeader"/> +
/// <see cref="TestAuthHandler.ScopeHeader"/>) — no token issuance, no PKCE machinery to
/// drive. End-to-end tests of the real OAuth flow live in <c>AuthFlowTests</c> and use
/// the seeded <c>mystack-service</c> client to acquire actual JWTs.
/// </para>
/// <para>
/// The connection string is published as a process env var (<c>ConnectionStrings__DefaultConnection</c>)
/// in <see cref="InitializeAsync"/>. This deliberately uses a process-global side effect:
/// <c>WebApplicationFactory.ConfigureWebHost</c>'s <c>ConfigureAppConfiguration</c> callback
/// doesn't propagate to a minimal-API <see cref="WebApplicationBuilder"/>'s frozen config
/// chain (see <see href="https://github.com/dotnet/aspnetcore/issues/45563"/>), so the
/// in-memory provider approach silently fails — the JSON default <c>localhost:5432</c>
/// wins and tests connect to whatever DB happens to be listening there. The env var is
/// read by ASP.NET's default <c>EnvironmentVariablesConfigurationProvider</c> when the
/// host builds, so it wins.
/// </para>
/// <para>
/// Implication: only one <see cref="ApiTestFactory"/> may be alive in the test process
/// at a time — keep it sealed.
/// </para>
/// </summary>
public sealed class ApiTestFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>The dev password seeded for the three test users.</summary>
    public const string SeedPassword = "TestPassword123!";

    /// <summary>The client secret seeded for <c>mystack-service</c> in tests.</summary>
    public const string ServiceClientSecret = "test-service-secret";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("mystack")
        .WithUsername("mystack")
        .WithPassword("mystack")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        // See class doc for why this is an env var rather than an in-memory config provider.
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection",
            _postgres.GetConnectionString());

        // Seed credentials — tests can rely on these to drive the real OAuth flow when
        // needed (e.g. AuthFlowTests). Otherwise they use TestAuthHandler headers.
        Environment.SetEnvironmentVariable("Seed__Users__GlobalAdminPassword", SeedPassword);
        Environment.SetEnvironmentVariable("Seed__Users__AdminPassword", SeedPassword);
        Environment.SetEnvironmentVariable("Seed__Users__UserPassword", SeedPassword);
        Environment.SetEnvironmentVariable("Seed__Clients__ServiceClientSecret", ServiceClientSecret);
    }

    public override async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Force Development so Program.cs applies EF migrations on startup
        // against the test container.
        builder.UseEnvironment("Development");

        builder.ConfigureTestServices(services =>
        {
            // Replace the OpenIddict validation handler with the TestAuthHandler for the
            // same scheme name. Endpoint authorization policies are bound to the OpenIddict
            // validation scheme — swapping the handler means policies "just work" against
            // the synthetic principal the test handler builds from headers.
            //
            // AuthenticationOptions.Schemes is an in-options list of "builders"; we mutate
            // each one whose Name matches by pointing its HandlerType at TestAuthHandler.
            // PostConfigure runs after OpenIddict's own configuration, so this wins.
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                foreach (var scheme in options.Schemes)
                {
                    if (scheme.Name == OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                    {
                        scheme.HandlerType = typeof(TestAuthHandler);
                    }
                }
            });

            // TestAuthHandler is resolved via DI by the auth framework — register it.
            services.AddTransient<TestAuthHandler>();
        });
    }
}
