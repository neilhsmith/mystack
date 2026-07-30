using System.Diagnostics;
using Hangfire.Client;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;

namespace MyStack.Jobs;

/// <summary>
/// One filter carries the whole telemetry story, so every job gets it without opting in:
/// the enqueuing request's trace context is stamped into the job's parameters (client side),
/// the execution runs inside a span that links back to it (server side), and the counters are
/// recorded from the state transitions Hangfire actually persisted (state side) — which is the
/// only place a retry and a dead-letter are distinguishable.
/// </summary>
internal sealed class JobTelemetryFilter(JobsMetrics metrics)
    : IClientFilter,
        IServerFilter,
        IApplyStateFilter
{
    public const string ActivitySourceName = "MyStack.Jobs";

    private const string TraceParentParameter = "TraceParent";
    private const string TraceStateParameter = "TraceState";
    private const string ActivityItem = "MyStack.Jobs.Activity";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    public void OnCreating(CreatingContext context)
    {
        // The job payload carries the W3C context, so the link survives a restart — the job is
        // re-read from storage, not from memory.
        if (Activity.Current is { } origin)
        {
            context.SetJobParameter(TraceParentParameter, origin.Id);
            if (origin.TraceStateString is not null)
            {
                context.SetJobParameter(TraceStateParameter, origin.TraceStateString);
            }
        }
    }

    public void OnCreated(CreatedContext context)
    {
        if (context.Exception is null)
        {
            metrics.Enqueued(JobType(context.Job));
        }
    }

    public void OnPerforming(PerformingContext context)
    {
        // A link rather than a parent: the execution may run minutes later on another instance,
        // so it gets its own trace, connected back to the request that asked for it.
        IEnumerable<ActivityLink>? links = null;
        if (
            ActivityContext.TryParse(
                context.GetJobParameter<string>(TraceParentParameter),
                context.GetJobParameter<string>(TraceStateParameter),
                out var origin
            )
        )
        {
            links = [new ActivityLink(origin)];
        }

        var activity = Source.StartActivity(
            $"job {JobType(context.BackgroundJob.Job)}",
            ActivityKind.Consumer,
            parentContext: default,
            tags: [new KeyValuePair<string, object?>("job.id", context.BackgroundJob.Id)],
            links: links
        );

        if (activity is not null)
        {
            context.Items[ActivityItem] = activity;
        }
    }

    public void OnPerformed(PerformedContext context)
    {
        if (context.Items.TryGetValue(ActivityItem, out var item) && item is Activity activity)
        {
            if (context.Exception is not null)
            {
                activity.SetStatus(ActivityStatusCode.Error, context.Exception.Message);
            }

            activity.Dispose();
        }
    }

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        var jobType = JobType(context.BackgroundJob.Job);
        switch (context.NewState)
        {
            case SucceededState:
                metrics.Executed(jobType, JobOutcomes.Succeeded);
                break;
            // With AttemptsExceededAction.Fail, a Failed state is only ever persisted once the
            // retry filter has given up — an execution that will be retried lands in Scheduled
            // (or straight back in Enqueued when the retry delay is zero), and arriving in
            // either from Processing is what marks it a retry rather than a delayed or fresh
            // job.
            case FailedState:
                metrics.Executed(jobType, JobOutcomes.DeadLettered);
                break;
            case ScheduledState
            or EnqueuedState when context.OldStateName == ProcessingState.StateName:
                metrics.Executed(jobType, JobOutcomes.Failed);
                break;
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction) { }

    private static string JobType(Job? job) =>
        job is null ? "unknown" : $"{job.Type.Name}.{job.Method.Name}";
}
