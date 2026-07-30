using Hangfire;

namespace MyStack.Jobs;

/// <summary>
/// The registration delegate is built where the job type is statically known, so the registrar
/// stays free of reflection over open generics.
/// </summary>
internal sealed record RecurringJobRegistration(
    string Id,
    string Cron,
    Action<IRecurringJobManager> Register
);
