using Hangfire;
using Microsoft.Extensions.Hosting;

namespace MyStack.Jobs;

/// <summary>
/// Registers every declared recurring job at startup. AddOrUpdate is idempotent, so each boot
/// converges the schedule to what the code declares. A registration that disappears from code is
/// not removed automatically — the dashboard's recurring-jobs page is the manual recovery, and
/// reconciliation can arrive when an id actually gets renamed.
/// </summary>
internal sealed class RecurringJobsRegistrar(
    IEnumerable<RecurringJobRegistration> registrations,
    IRecurringJobManager manager
) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var registration in registrations)
        {
            registration.Register(manager);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
