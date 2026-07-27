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

        application = new AuthApplicationFactory(
            database.GetConnectionString(),
            migrate: true,
            AuthApplicationFactory.TestEnvironment
        );

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
}
