using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace MyStack.Messaging;

/// <summary>
/// Runs every declared <see cref="ScheduledMessage"/>: sleep until the next cron occurrence,
/// publish, repeat. Deliberately just a clock over the message pipeline — retries, dead-lettering
/// and telemetry belong to the handler's queue, so the scheduler owns nothing but timing. An
/// instance that is down when a tick passes skips it, and two instances both publish; schedules
/// carry maintenance-style work whose handlers are idempotent, and the §4 trigger for a
/// coordinated scheduler is a schedule where a missed or duplicated run actually costs something.
/// </summary>
internal sealed class MessageScheduler(
    IEnumerable<ScheduledMessage> schedules,
    IServiceProvider services,
    ILogger<MessageScheduler> logger
) : BackgroundService
{
    // Task.Delay overflows past ~24.8 days, and shorter chunks re-read the clock, which also
    // self-corrects after a machine sleeps.
    private static readonly TimeSpan MaxChunk = TimeSpan.FromHours(6);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(schedules.Select(schedule => RunAsync(schedule, stoppingToken)));

    private async Task RunAsync(ScheduledMessage schedule, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = schedule.Expression.GetNextOccurrence(
                DateTimeOffset.UtcNow,
                TimeZoneInfo.Utc
            );
            if (next is null)
            {
                // A cron with no future occurrence has nothing left to do.
                return;
            }

            var remaining = next.Value - DateTimeOffset.UtcNow;
            while (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining > MaxChunk ? MaxChunk : remaining, stoppingToken);
                remaining = next.Value - DateTimeOffset.UtcNow;
            }

            try
            {
                await using var scope = services.CreateAsyncScope();
                var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
                await bus.PublishAsync(schedule.Factory());
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed publish (the broker having a bad moment) must not kill the schedule;
                // the next occurrence tries again.
                logger.LogWarning(
                    exception,
                    "Failed to publish scheduled message {MessageType}",
                    schedule.MessageType
                );
            }
        }
    }
}
