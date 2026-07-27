using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using MyStack.Auth.Data;

namespace MyStack.Auth.Health;

internal static class HealthCheckExtensions
{
    private const string ReadyTag = "ready";

    public static IServiceCollection AddAuthHealthChecks(this IServiceCollection services)
    {
        services
            .AddHealthChecks()
            // CanConnectAsync over the pooled DbContext, so it exercises the connection the
            // application actually uses rather than opening one of its own.
            .AddDbContextCheck<AuthDbContext>("database", tags: [ReadyTag])
            .AddCheck<PendingMigrationsHealthCheck>("database-schema", tags: [ReadyTag]);

        return services;
    }

    public static WebApplication MapAuthHealthChecks(this WebApplication app)
    {
        // Liveness answers one question: can this process still respond? Checking a dependency
        // here would have the orchestrator restart every instance during a database blip, turning
        // a recoverable outage into a restart loop. Dependencies belong to readiness.
        app.MapHealthChecks(
            "/health/live",
            new HealthCheckOptions
            {
                Predicate = _ => false,
                ResponseWriter = HealthResponseWriter.WriteAsync,
            }
        );

        app.MapHealthChecks(
            "/health/ready",
            new HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains(ReadyTag),
                ResponseWriter = HealthResponseWriter.WriteAsync,
            }
        );

        return app;
    }
}
