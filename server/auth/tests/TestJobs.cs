using System.Collections.Concurrent;

namespace MyStack.Auth.Tests;

/// <summary>
/// The observable side effect for job tests — proof an execution actually ran, not just that
/// Hangfire changed a state row.
/// </summary>
public sealed class JobRecorder
{
    private readonly ConcurrentQueue<string> entries = new();

    public void Record(string value) => entries.Enqueue(value);

    public bool Contains(string value) => entries.Contains(value);
}

/// <summary>
/// Activated by Hangfire through the host's service provider, which is what proves jobs get
/// constructor injection.
/// </summary>
public sealed class RecordingJob(JobRecorder recorder)
{
    public Task RunAsync(string value)
    {
        recorder.Record(value);
        return Task.CompletedTask;
    }
}

public static class AlwaysFailingJob
{
    public static Task RunAsync(string value) =>
        throw new InvalidOperationException($"Deliberate job failure for {value}.");
}
