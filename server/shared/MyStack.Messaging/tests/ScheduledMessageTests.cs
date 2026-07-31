using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;

namespace MyStack.Messaging.Tests;

public sealed class ScheduledMessageTests
{
    [Fact]
    public void AddScheduledMessage_registers_the_schedule()
    {
        var services = new ServiceCollection();

        services.AddScheduledMessage<TestTick>("0 3 * * *");

        using var provider = services.BuildServiceProvider();
        var schedule = provider.GetServices<ScheduledMessage>().ShouldHaveSingleItem();
        schedule.MessageType.ShouldBe(nameof(TestTick));
        schedule.Cron.ShouldBe("0 3 * * *");
        schedule.Factory().ShouldBeOfType<TestTick>();
    }

    // A typo'd cron must fail at startup registration, not silently at its first tick.
    [Fact]
    public void AddScheduledMessage_rejects_an_invalid_cron()
    {
        Should.Throw<CronFormatException>(() =>
            new ServiceCollection().AddScheduledMessage<TestTick>("not cron")
        );
    }

    [Fact]
    public async Task Scheduler_publishes_when_a_tick_comes_due()
    {
        // A bare Wolverine host — no broker, no persistence: the message has a local handler, so
        // the publish routes in-process, which is all this test needs to prove the clock works.
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<TickSink>();
                // Six fields = seconds resolution, so the test sees a tick in seconds.
                services.AddScheduledMessage<TestTick>("*/1 * * * * *");
                services.AddHostedService<MessageScheduler>();
            })
            .UseWolverine(options =>
                options.ApplicationAssembly = typeof(ScheduledMessageTests).Assembly
            )
            .StartAsync(TestContext.Current.CancellationToken);

        var sink = host.Services.GetRequiredService<TickSink>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (sink.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        sink.Count.ShouldBeGreaterThanOrEqualTo(1);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}

public sealed record TestTick;

public sealed class TickSink
{
    private int count;

    public int Count => count;

    public void Record() => Interlocked.Increment(ref count);
}

public static class TestTickHandler
{
    public static void Handle(TestTick tick, TickSink sink) => sink.Record();
}
