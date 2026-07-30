namespace MyStack.Jobs;

public sealed class JobsOptions
{
    public const string SectionName = "Jobs";

    /// <summary>
    /// Retries after the first failed execution; past them the job dead-letters into the
    /// dashboard's Failed page and stays there until a human requeues or deletes it.
    /// </summary>
    public int RetryAttempts { get; set; } = 5;

    /// <summary>
    /// Seconds between retries, one entry per attempt. Null uses Hangfire's exponential backoff;
    /// tests set [0] so a retry schedule is provable in seconds rather than minutes.
    /// </summary>
    public int[]? RetryDelaysInSeconds { get; set; }

    /// <summary>
    /// How often the queue and the retry schedule are polled. The floor on job latency, and a
    /// per-worker query against Postgres — two seconds keeps local jobs snappy without turning
    /// the poll into measurable load.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Concurrent workers. Null uses Hangfire's default (processor-count based).</summary>
    public int? WorkerCount { get; set; }
}
