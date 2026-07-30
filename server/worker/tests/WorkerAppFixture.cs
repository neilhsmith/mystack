using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace MyStack.Worker.Tests;

public sealed class WorkerAppFixture : IAsyncLifetime
{
    // The images compose runs, so envelope storage and broker topology are proven against what
    // the stack actually uses rather than whatever `latest` happens to be.
    private readonly PostgreSqlContainer database = new PostgreSqlBuilder(
        "postgres:18-alpine"
    ).Build();

    private readonly RabbitMqContainer broker = new RabbitMqBuilder(
        "rabbitmq:4-management-alpine"
    ).Build();

    private WorkerApplicationFactory? application;

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => application!.Services;

    public string BrokerConnectionString => broker.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(database.StartAsync(), broker.StartAsync());

        application = new WorkerApplicationFactory(
            database.GetConnectionString(),
            broker.GetConnectionString()
        );

        // CreateClient is what builds the host, so Wolverine's storage and queues provision here.
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

    private sealed class WorkerApplicationFactory(
        string connectionString,
        string brokerConnectionString
    ) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // "Testing" also gates the worker's discovery of this assembly's message handlers —
            // the pipeline's stand-ins until MyStack.Email brings the first real ones.
            builder.UseEnvironment("Testing");

            builder.UseSetting("ConnectionStrings:WorkerDb", connectionString);
            builder.UseSetting("ConnectionStrings:MessageBroker", brokerConnectionString);

            // One immediate retry: a retry-then-dead-letter sequence is provable in seconds
            // instead of the production cooldowns' minutes.
            builder.UseSetting("Messaging:RetryCooldownsInSeconds:0", "0");

            builder.ConfigureServices(services => services.AddSingleton<MessageSink>());
        }
    }
}
