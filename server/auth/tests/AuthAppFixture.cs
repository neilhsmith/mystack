using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace MyStack.Auth.Tests;

public sealed class AuthAppFixture : IAsyncLifetime
{
    // The image compose runs, so the migration is proven against the Postgres the stack actually
    // uses rather than whatever `latest` happens to be.
    private readonly PostgreSqlContainer database = new PostgreSqlBuilder(
        "postgres:18-alpine"
    ).Build();

    private AuthApplicationFactory? application;

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => application!.Services;

    public async ValueTask InitializeAsync()
    {
        await database.StartAsync();

        application = new AuthApplicationFactory(database.GetConnectionString());

        // CreateClient is what builds the host, so the migration runs here.
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
    }

    private sealed class AuthApplicationFactory(string connectionString)
        : WebApplicationFactory<Program>
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
        }
    }
}
