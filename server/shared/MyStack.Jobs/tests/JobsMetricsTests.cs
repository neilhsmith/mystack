using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Shouldly;

namespace MyStack.Jobs.Tests;

public sealed class JobsMetricsTests : IDisposable
{
    private readonly ServiceProvider provider;
    private readonly JobsMetrics metrics;
    private readonly IMeterFactory meterFactory;

    public JobsMetricsTests()
    {
        provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        meterFactory = provider.GetRequiredService<IMeterFactory>();
        metrics = new JobsMetrics(meterFactory);
    }

    public void Dispose() => provider.Dispose();

    [Fact]
    public void Enqueued_counts_by_job_type()
    {
        using var collector = new MetricCollector<long>(
            meterFactory,
            JobsMetrics.MeterName,
            "jobs.enqueued"
        );

        metrics.Enqueued("MailJob.SendAsync");

        var measurement = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Value.ShouldBe(1);
        measurement.Tags["job_type"].ShouldBe("MailJob.SendAsync");
    }

    [Fact]
    public void Executed_counts_by_job_type_and_outcome()
    {
        using var collector = new MetricCollector<long>(
            meterFactory,
            JobsMetrics.MeterName,
            "jobs.executions"
        );

        metrics.Executed("MailJob.SendAsync", JobOutcomes.DeadLettered);

        var measurement = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Value.ShouldBe(1);
        measurement.Tags["job_type"].ShouldBe("MailJob.SendAsync");
        measurement.Tags["outcome"].ShouldBe("dead_lettered");
    }
}
