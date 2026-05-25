using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Api.Tests.Integration.Fixtures;

/// <summary>
/// Boots <c>Program</c> via <see cref="WebApplicationFactory{TEntryPoint}"/> against a
/// throwaway Postgres container. One instance is shared by every integration test class
/// in <see cref="IntegrationTestCollection"/> so the container-start cost is paid once
/// per assembly run.
/// <para>
/// The connection string is published as a process env var (<c>ConnectionStrings__DefaultConnection</c>)
/// in <see cref="InitializeAsync"/>. This deliberately uses a process-global side effect:
/// <c>WebApplicationFactory.ConfigureWebHost</c>'s <c>ConfigureAppConfiguration</c> callback
/// doesn't propagate to a minimal-API <see cref="WebApplicationBuilder"/>'s frozen config
/// chain (see <see href="https://github.com/dotnet/aspnetcore/issues/45563"/>), so the
/// in-memory provider approach silently fails — the JSON default <c>localhost:5432</c>
/// wins and tests connect to whatever DB happens to be listening there (your dev compose
/// in the best case, nothing in CI). The env var is read by ASP.NET's default
/// <c>EnvironmentVariablesConfigurationProvider</c> when the host builds, so it wins.
/// </para>
/// <para>
/// Implication: only one <see cref="ApiTestFactory"/> may be alive in the test process
/// at a time — keep it sealed. If a future test needs different config (e.g. tighter
/// rate-limit policy), apply it per-endpoint via a named policy / diagnostic probe rather
/// than spinning up a sibling factory.
/// </para>
/// </summary>
public sealed class ApiTestFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
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
    }
}
