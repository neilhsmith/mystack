using Wolverine;

namespace MyStack.Auth.Messaging;

/// <summary>
/// Publishes <see cref="PruneOidcTokens"/> daily at 03:00 UTC. A plain timer, on purpose: cron
/// was the one thing the broker doesn't replace, and pruning is idempotent, so a restart missing
/// a window (the next day catches up) or a second instance publishing a duplicate both cost
/// nothing.
/// </summary>
internal sealed class PruneScheduler(IServiceProvider services) : BackgroundService
{
    private static readonly TimeSpan RunAt = TimeSpan.FromHours(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(DelayUntilNextRun(DateTimeOffset.UtcNow), stoppingToken);

            await using var scope = services.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.PublishAsync(new PruneOidcTokens());
        }
    }

    internal static TimeSpan DelayUntilNextRun(DateTimeOffset now)
    {
        // Stays in DateTimeOffset arithmetic: mixing in a bare DateTime would compare through
        // the machine's local offset.
        var utc = now.ToUniversalTime();
        var today =
            new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero) + RunAt;
        var next = utc < today ? today : today.AddDays(1);

        return next - utc;
    }
}
