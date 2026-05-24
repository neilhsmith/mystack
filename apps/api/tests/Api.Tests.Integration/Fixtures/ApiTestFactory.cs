using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace Api.Tests.Integration.Fixtures;

/// <summary>
/// Boots <c>Program</c> via <see cref="WebApplicationFactory{TEntryPoint}"/> against a
/// throwaway Postgres container. One instance is shared by every integration test class
/// in <see cref="IntegrationTestCollection"/> so the container-start cost is paid once
/// per assembly run.
/// <para>
/// Subclassable: a child factory (e.g. <see cref="RateLimitedTestFactory"/>) can override
/// <see cref="ConfigureWebHost"/> to layer additional config on top — the per-factory
/// in-memory config below replaced an older process-global env-var hack so two factories
/// can coexist in the same test run without trampling each other's connection strings.
/// </para>
/// </summary>
public class ApiTestFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("mystack")
        .WithUsername("mystack")
        .WithPassword("mystack")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

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

        // Inject the container's connection string scoped to this factory. Lambda is
        // evaluated lazily when the host is built (after InitializeAsync has started
        // the container), so `_postgres.GetConnectionString()` returns a valid value.
        // Per-factory rather than process-global — see class doc.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _postgres.GetConnectionString(),
            });
        });
    }
}
