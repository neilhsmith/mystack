using System.Diagnostics.Metrics;

namespace MyStack.Jobs;

public sealed class JobsMetrics
{
    // Under the MyStack.* naming convention MyStack.Observability subscribes by wildcard.
    public const string MeterName = "MyStack.Jobs";

    private const string JobTypeTag = "job_type";
    private const string OutcomeTag = "outcome";

    private readonly Counter<long> enqueued;
    private readonly Counter<long> executions;

    public JobsMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        enqueued = meter.CreateCounter<long>(
            "jobs.enqueued",
            unit: "{job}",
            description: "Jobs persisted to the queue."
        );
        executions = meter.CreateCounter<long>(
            "jobs.executions",
            unit: "{execution}",
            description: "Job executions by outcome — dead_lettered is the alert signal."
        );
    }

    internal void Enqueued(string jobType) =>
        enqueued.Add(1, new KeyValuePair<string, object?>(JobTypeTag, jobType));

    internal void Executed(string jobType, string outcome) =>
        executions.Add(
            1,
            new KeyValuePair<string, object?>(JobTypeTag, jobType),
            new KeyValuePair<string, object?>(OutcomeTag, outcome)
        );
}
