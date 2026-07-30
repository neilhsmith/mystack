using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MyStack.Jobs;

public static class JobsExtensions
{
    /// <summary>
    /// Hangfire on the app's own <c>hangfire_&lt;app&gt;</c> Postgres schema, with retry policy,
    /// trace linking and the jobs.* counters wired in. Each app runs its own server against its
    /// own schema — this library is setup, conventions, telemetry and the testing seam, never a
    /// shared queue (docs/architecture.md §3.3).
    /// </summary>
    public static TBuilder AddJobs<TBuilder>(
        this TBuilder builder,
        string appName,
        string connectionStringName
    )
        where TBuilder : IHostApplicationBuilder
    {
        var connectionString =
            builder.Configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException(
                $"ConnectionStrings:{connectionStringName} is not configured."
            );

        builder
            .Services.AddOptions<JobsOptions>()
            .Bind(builder.Configuration.GetSection(JobsOptions.SectionName));
        builder.Services.AddSingleton<JobsMetrics>();
        builder.Services.AddSingleton<JobTelemetryFilter>();

        builder.Services.AddHangfire(
            (provider, configuration) =>
            {
                var options = provider.GetRequiredService<IOptions<JobsOptions>>().Value;

                configuration
                    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                    .UseSimpleAssemblyNameTypeSerializer()
                    .UseRecommendedSerializerSettings()
                    .UsePostgreSqlStorage(
                        storage => storage.UseNpgsqlConnection(connectionString),
                        new PostgreSqlStorageOptions
                        {
                            SchemaName = SchemaFor(appName),
                            QueuePollInterval = options.PollInterval,
                            // The 1.21 default, pinned because it is load-bearing: an Enqueue
                            // inside a TransactionScope joins the app's write and commits or
                            // rolls back with it — verified, one local Postgres transaction,
                            // provided both use the same connection string (architecture §3.3).
                            EnableTransactionScopeEnlistment = true,
                        }
                    );

                ReplaceGlobalFilters(provider, options);
            }
        );

        builder.Services.AddHangfireServer(
            (provider, server) =>
            {
                var options = provider.GetRequiredService<IOptions<JobsOptions>>().Value;

                // The schedule poller is what turns a due retry back into an enqueued job, so it
                // runs at the same cadence as the queue poll or retries crawl regardless of the
                // configured delays.
                server.SchedulePollingInterval = options.PollInterval;
                if (options.WorkerCount is { } workerCount)
                {
                    server.WorkerCount = workerCount;
                }
            }
        );

        builder.Services.AddHostedService<RecurringJobsRegistrar>();

        return builder;
    }

    /// <summary>
    /// Declares a cron-scheduled job. <typeparamref name="TJob"/> is resolved from a DI scope per
    /// execution, so it can take constructor dependencies like any scoped service.
    /// </summary>
    public static IServiceCollection AddRecurringJob<TJob>(
        this IServiceCollection services,
        string id,
        string cron
    )
        where TJob : class, IRecurringJob
    {
        services.TryAddScoped<TJob>();
        services.AddSingleton(
            new RecurringJobRegistration(
                id,
                cron,
                // CancellationToken.None is a placeholder Hangfire swaps for the server's
                // shutdown token when the job actually runs.
                manager =>
                    manager.AddOrUpdate<TJob>(id, job => job.RunAsync(CancellationToken.None), cron)
            )
        );

        return services;
    }

    /// <summary>
    /// Mounts the Hangfire dashboard behind the given authorization policy. The policy is a
    /// required argument on purpose: the dashboard can requeue and delete jobs, so there is no
    /// overload that mounts it open.
    /// </summary>
    public static IEndpointConventionBuilder MapJobsDashboard(
        this IEndpointRouteBuilder endpoints,
        Action<AuthorizationPolicyBuilder> authorization,
        string path = "/jobs"
    ) =>
        endpoints
            .MapHangfireDashboard(
                path,
                new DashboardOptions
                {
                    // Endpoint authorization below is the gate. Hangfire's own default filter
                    // (local requests only) would wave through everything a reverse proxy
                    // forwards from localhost, so it is removed rather than stacked.
                    Authorization = [],
                    DisplayStorageConnectionString = false,
                }
            )
            .RequireAuthorization(authorization);

    internal static string SchemaFor(string appName) => $"hangfire_{appName}";

    private static void ReplaceGlobalFilters(IServiceProvider provider, JobsOptions options)
    {
        // Hangfire's filter collection is process-global while hosts come and go — every test
        // factory is a new host — so earlier instances (including Hangfire's default 10-attempt
        // retry filter) are removed before this host's are added, never stacked.
        var filters = GlobalJobFilters.Filters;
        foreach (
            var stale in filters
                .Where(filter => filter.Instance is JobTelemetryFilter or AutomaticRetryAttribute)
                .Select(filter => filter.Instance)
                .ToList()
        )
        {
            filters.Remove(stale);
        }

        var retry = new AutomaticRetryAttribute
        {
            Attempts = options.RetryAttempts,
            // Exhausted retries park the job in Failed — visible, alertable, requeueable —
            // instead of silently deleting it.
            OnAttemptsExceeded = AttemptsExceededAction.Fail,
        };
        if (options.RetryDelaysInSeconds is { Length: > 0 } delays)
        {
            retry.DelaysInSeconds = delays;
        }

        filters.Add(retry);
        filters.Add(provider.GetRequiredService<JobTelemetryFilter>());
    }
}
