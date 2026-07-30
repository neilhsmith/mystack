using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Wolverine;

namespace MyStack.Worker.Tests;

public sealed class MessagePipelineTests(WorkerAppFixture app)
{
    // Both ends, through the real broker: published into the worker's queue, handled with the
    // host's own DI (the sink arrives by injection).
    [Fact]
    public async Task PublishedMessage_IsHandledByTheWorker()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var value = $"ping-{Guid.NewGuid():N}";

        await Publish(new TestPing(value));

        var sink = app.Services.GetRequiredService<MessageSink>();
        await WaitUntilAsync(
            () => Task.FromResult(sink.Contains(value)),
            "the worker should consume the message and record it",
            cancellationToken
        );
    }

    [Fact]
    public async Task FailingMessage_RetriesThenDeadLetters()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var value = $"explosion-{Guid.NewGuid():N}";
        var sink = app.Services.GetRequiredService<MessageSink>();

        await Publish(new TestExplosion(value));

        // One retry is configured, so the honest count is exactly two executions: the original
        // and the retry.
        await WaitUntilAsync(
            () => Task.FromResult(sink.Count(value) == 2),
            "the handler should run twice — the original attempt and one retry",
            cancellationToken
        );

        // Dead-letter visibility: past its retries the message parks in the broker's native
        // dead-letter queue — the one the management UI shows and can shovel back — instead of
        // being dropped.
        await WaitUntilAsync(
            async () => await CountDeadLettersAsync(cancellationToken) >= 1,
            "the exhausted message should land in the dead-letter queue",
            cancellationToken
        );
    }

    // Explicitly to the worker's Rabbit queue — the same address a producing app will use — so
    // the test can't quietly pass over Wolverine's in-process local queue instead of the broker.
    private async ValueTask Publish<T>(T message)
        where T : notnull
    {
        await using var scope = app.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        await bus.EndpointFor(new Uri("rabbitmq://queue/worker")).SendAsync(message);
    }

    private async Task<long> CountDeadLettersAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory { Uri = new Uri(app.BrokerConnectionString) };
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(
            cancellationToken: cancellationToken
        );

        try
        {
            var queue = await channel.QueueDeclarePassiveAsync(
                "wolverine-dead-letter-queue",
                cancellationToken
            );

            return queue.MessageCount;
        }
        catch (OperationInterruptedException)
        {
            // The queue is only declared once something dead-letters.
            return 0;
        }
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        string because,
        CancellationToken cancellationToken
    )
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException(because);
    }
}
