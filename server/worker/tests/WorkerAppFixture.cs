using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
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

    private readonly IContainer mailpit = new ContainerBuilder("axllent/mailpit:v1.30")
        .WithPortBinding(1025, true)
        .WithPortBinding(8025, true)
        .WithWaitStrategy(
            Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPort(8025).ForPath("/readyz"))
        )
        .Build();

    private WorkerApplicationFactory? application;

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => application!.Services;

    public string BrokerConnectionString => broker.GetConnectionString();

    public Uri MailpitApiBaseAddress =>
        new($"http://{mailpit.Hostname}:{mailpit.GetMappedPublicPort(8025)}");

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(database.StartAsync(), broker.StartAsync(), mailpit.StartAsync());

        application = new WorkerApplicationFactory(
            database.GetConnectionString(),
            broker.GetConnectionString(),
            mailpit.Hostname,
            mailpit.GetMappedPublicPort(1025)
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
        await mailpit.DisposeAsync();
    }

    private sealed class WorkerApplicationFactory(
        string connectionString,
        string brokerConnectionString,
        string smtpHost,
        int smtpPort
    ) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // "Testing" also gates the worker's discovery of this assembly's message handlers —
            // the pipeline-mechanics stand-ins beside the real SendEmail handler.
            builder.UseEnvironment("Testing");

            builder.UseSetting("ConnectionStrings:WorkerDb", connectionString);
            builder.UseSetting("ConnectionStrings:MessageBroker", brokerConnectionString);

            builder.UseSetting("Email:Host", smtpHost);
            builder.UseSetting("Email:Port", smtpPort.ToString());
            builder.UseSetting("Email:From", "no-reply@mystack.test");

            // One immediate retry: a retry-then-dead-letter sequence is provable in seconds
            // instead of the production cooldowns' minutes.
            builder.UseSetting("Messaging:RetryCooldownsInSeconds:0", "0");

            builder.ConfigureServices(services => services.AddSingleton<MessageSink>());
        }
    }
}
