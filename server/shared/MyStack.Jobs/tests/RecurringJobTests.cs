using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace MyStack.Jobs.Tests;

public sealed class RecurringJobTests
{
    [Fact]
    public void AddRecurringJob_registers_the_job_type_and_schedule()
    {
        var services = new ServiceCollection();

        services.AddRecurringJob<FakeJob>("fake-job", "0 3 * * *");

        using var provider = services.BuildServiceProvider();
        var registration = provider.GetServices<RecurringJobRegistration>().ShouldHaveSingleItem();
        registration.Id.ShouldBe("fake-job");
        registration.Cron.ShouldBe("0 3 * * *");

        var manager = new RecordingRecurringJobManager();
        registration.Register(manager);

        var (id, job, cron) = manager.Registered.ShouldHaveSingleItem();
        id.ShouldBe("fake-job");
        cron.ShouldBe("0 3 * * *");
        job.Type.ShouldBe(typeof(FakeJob));
        job.Method.Name.ShouldBe(nameof(IRecurringJob.RunAsync));
    }

    [Fact]
    public async Task Registrar_registers_every_declared_job_at_startup()
    {
        var manager = new RecordingRecurringJobManager();
        var registrations = new[]
        {
            new RecurringJobRegistration(
                "one",
                "* * * * *",
                m =>
                    m.AddOrUpdate<FakeJob>(
                        "one",
                        j => j.RunAsync(CancellationToken.None),
                        "* * * * *"
                    )
            ),
            new RecurringJobRegistration(
                "two",
                "0 * * * *",
                m =>
                    m.AddOrUpdate<FakeJob>(
                        "two",
                        j => j.RunAsync(CancellationToken.None),
                        "0 * * * *"
                    )
            ),
        };
        var registrar = new RecurringJobsRegistrar(registrations, manager);

        await registrar.StartAsync(TestContext.Current.CancellationToken);

        manager.Registered.Select(r => r.Id).ShouldBe(["one", "two"]);
    }

    private sealed class FakeJob : IRecurringJob
    {
        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingRecurringJobManager : IRecurringJobManager
    {
        public List<(string Id, Job Job, string Cron)> Registered { get; } = [];

        public void AddOrUpdate(
            string recurringJobId,
            Job job,
            string cronExpression,
            RecurringJobOptions options
        ) => Registered.Add((recurringJobId, job, cronExpression));

        public void Trigger(string recurringJobId) { }

        public void RemoveIfExists(string recurringJobId) { }
    }
}
