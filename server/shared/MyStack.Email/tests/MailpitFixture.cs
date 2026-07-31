using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace MyStack.Email.Tests;

public sealed class MailpitFixture : IAsyncLifetime
{
    // The image compose runs, so the adapter is proven against what the stack actually uses.
    private readonly IContainer container = new ContainerBuilder("axllent/mailpit:v1.30")
        .WithPortBinding(1025, true)
        .WithPortBinding(8025, true)
        .WithWaitStrategy(
            Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPort(8025).ForPath("/readyz"))
        )
        .Build();

    public string Host => container.Hostname;

    public int SmtpPort => container.GetMappedPublicPort(1025);

    public Uri ApiBaseAddress =>
        new($"http://{container.Hostname}:{container.GetMappedPublicPort(8025)}");

    public async ValueTask InitializeAsync() => await container.StartAsync();

    public async ValueTask DisposeAsync() => await container.DisposeAsync();
}
