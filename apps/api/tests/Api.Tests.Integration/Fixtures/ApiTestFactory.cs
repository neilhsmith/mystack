using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Api.Tests.Integration.Fixtures;

/// <summary>
/// Boots <c>Program</c> via <see cref="WebApplicationFactory{TEntryPoint}"/> against a
/// throwaway Postgres container. One instance is shared by every integration test class
/// (see <see cref="IntegrationTestCollection"/>) so we pay the container-start cost once
/// per assembly run.
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

        // Set as a process env var so Program reads it regardless of how
        // ConfigureAppConfiguration ordering plays out with WebApplicationFactory.
        // Wins over appsettings.Development.json's localhost:5432 default.
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
