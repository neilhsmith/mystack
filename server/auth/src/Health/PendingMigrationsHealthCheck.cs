using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MyStack.Auth.Data;

namespace MyStack.Auth.Health;

// A schema behind the code doesn't fail on boot — it fails on the first query that needs the new
// column. Reporting it as unready keeps traffic off the instance and makes the missed migration
// visible where a deployment is already looking.
internal sealed class PendingMigrationsHealthCheck(AuthDbContext database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        var pending = await database.Database.GetPendingMigrationsAsync(cancellationToken);
        var count = pending.Count();

        return count == 0
            ? HealthCheckResult.Healthy("The schema matches the migrations in this build.")
            : new HealthCheckResult(
                context.Registration.FailureStatus,
                $"{count} migration(s) have not been applied."
            );
    }
}
