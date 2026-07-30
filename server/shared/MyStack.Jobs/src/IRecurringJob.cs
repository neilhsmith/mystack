namespace MyStack.Jobs;

/// <summary>
/// A job registered on a cron schedule via <c>AddRecurringJob&lt;TJob&gt;</c>. Resolved from a DI
/// scope per execution; the token observes server shutdown.
/// </summary>
public interface IRecurringJob
{
    Task RunAsync(CancellationToken cancellationToken);
}
