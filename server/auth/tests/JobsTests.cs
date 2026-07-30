using System.Diagnostics;
using System.Diagnostics.Metrics;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using MyStack.Auth.Jobs;
using MyStack.Jobs;
using OpenIddict.Abstractions;
using Shouldly;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyStack.Auth.Tests;

public sealed class JobsTests(AuthAppFixture app)
{
    // CLAUDE.md's rule, from both ends: the enqueue is observable (metric) and the execution
    // produced its side effect (the recorder) — never just "the queue library works".
    [Fact]
    public async Task EnqueuedJob_Executes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var value = $"run-{Guid.NewGuid():N}";
        using var enqueued = CreateCollector("jobs.enqueued");
        using var executions = CreateCollector("jobs.executions");

        app.Services.GetRequiredService<IBackgroundJobClient>()
            .Enqueue<RecordingJob>(job => job.RunAsync(value));

        enqueued
            .GetMeasurementSnapshot()
            .Count(measurement => (string?)measurement.Tags["job_type"] == "RecordingJob.RunAsync")
            .ShouldBeGreaterThanOrEqualTo(1);

        var recorder = app.Services.GetRequiredService<JobRecorder>();
        await WaitUntilAsync(
            () => Task.FromResult(recorder.Contains(value)),
            "the job should execute and record its value",
            cancellationToken
        );

        await WaitUntilAsync(
            () =>
                Task.FromResult(
                    CountExecutions(executions, "RecordingJob.RunAsync", "succeeded") >= 1
                ),
            "a succeeded execution should be counted",
            cancellationToken
        );
    }

    [Fact]
    public async Task FailingJob_RetriesThenDeadLettersVisibly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var value = $"fail-{Guid.NewGuid():N}";
        using var executions = CreateCollector("jobs.executions");

        var jobId = app
            .Services.GetRequiredService<IBackgroundJobClient>()
            .Enqueue(() => AlwaysFailingJob.RunAsync(value));

        // One retry is configured, so the honest sequence is exactly one failed (retry
        // scheduled) execution and then one dead_lettered — the outcome an alert watches.
        await WaitUntilAsync(
            () =>
                Task.FromResult(
                    CountExecutions(executions, "AlwaysFailingJob.RunAsync", "failed") >= 1
                        && CountExecutions(executions, "AlwaysFailingJob.RunAsync", "dead_lettered")
                            >= 1
                ),
            "the job should retry once and then dead-letter",
            cancellationToken
        );

        // Dead-letter visibility: the job is parked on the dashboard's Failed page, exception
        // attached, waiting for a human to requeue or delete it.
        var failed = app
            .Services.GetRequiredService<JobStorage>()
            .GetMonitoringApi()
            .FailedJobs(0, 100);
        failed.ShouldContain(entry => entry.Key == jobId);
    }

    [Fact]
    public async Task Execution_LinksBackToTheEnqueuingTrace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var value = $"trace-{Guid.NewGuid():N}";
        var recorder = app.Services.GetRequiredService<JobRecorder>();
        var executed = new List<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "MyStack.Jobs",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                lock (executed)
                {
                    executed.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        string jobId;
        ActivityTraceId originTraceId;
        using (var origin = new Activity("test-enqueue").Start())
        {
            jobId = app
                .Services.GetRequiredService<IBackgroundJobClient>()
                .Enqueue<RecordingJob>(job => job.RunAsync(value));
            originTraceId = origin.TraceId;
        }

        await WaitUntilAsync(
            () => Task.FromResult(recorder.Contains(value)),
            "the job should execute",
            cancellationToken
        );

        Activity? span;
        lock (executed)
        {
            span = executed.FirstOrDefault(activity =>
                activity.Tags.Any(tag => tag.Key == "job.id" && tag.Value == jobId)
            );
        }

        // The execution runs in its own trace (it may happen minutes later, on another
        // instance) but carries a link back to the trace that enqueued it.
        span.ShouldNotBeNull();
        span.OperationName.ShouldBe("job RecordingJob.RunAsync");
        var link = span.Links.ShouldHaveSingleItem();
        link.Context.TraceId.ShouldBe(originTraceId);
    }

    [Fact]
    public async Task PruneJob_IsScheduledAndPrunesExpiredTokens()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var subject = $"prune-{Guid.NewGuid():N}";
        using var executions = CreateCollector("jobs.executions");

        // Registered on the declared schedule — the recurring half of the proof.
        using (var connection = app.Services.GetRequiredService<JobStorage>().GetConnection())
        {
            var prune = connection.GetRecurringJobs().ShouldHaveSingleItem();
            prune.Id.ShouldBe(PruneOidcTokensJob.Id);
            prune.Cron.ShouldBe("0 3 * * *");
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            await tokens.CreateAsync(
                new OpenIddictTokenDescriptor
                {
                    Subject = subject,
                    Status = Statuses.Valid,
                    Type = TokenTypeHints.AccessToken,
                    CreationDate = DateTimeOffset.UtcNow.AddDays(-40),
                    ExpirationDate = DateTimeOffset.UtcNow.AddDays(-39),
                },
                cancellationToken
            );
        }

        app.Services.GetRequiredService<IRecurringJobManager>().Trigger(PruneOidcTokensJob.Id);

        await WaitUntilAsync(
            async () => await CountTokensAsync(subject, cancellationToken) == 0,
            "the triggered prune should remove the long-expired token",
            cancellationToken
        );

        CountExecutions(executions, "PruneOidcTokensJob.RunAsync", "succeeded")
            .ShouldBeGreaterThanOrEqualTo(1);
    }

    private MetricCollector<long> CreateCollector(string instrument) =>
        new(app.Services.GetRequiredService<IMeterFactory>(), JobsMetrics.MeterName, instrument);

    private static int CountExecutions(
        MetricCollector<long> collector,
        string jobType,
        string outcome
    ) =>
        collector
            .GetMeasurementSnapshot()
            .Count(measurement =>
                (string?)measurement.Tags["job_type"] == jobType
                && (string?)measurement.Tags["outcome"] == outcome
            );

    private async Task<int> CountTokensAsync(string subject, CancellationToken cancellationToken)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();

        var count = 0;
        await foreach (var _ in tokens.FindBySubjectAsync(subject, cancellationToken))
        {
            count++;
        }

        return count;
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        string because,
        CancellationToken cancellationToken
    )
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException(because);
    }
}
