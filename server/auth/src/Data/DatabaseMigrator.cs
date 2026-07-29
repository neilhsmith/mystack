using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MyStack.Auth.Data;

// StartingAsync runs before every hosted service's StartAsync, and so before Kestrel binds: the
// schema is current before the first request rather than during it (docs/architecture.md §3.4).
internal sealed partial class DatabaseMigrator(
    IServiceScopeFactory scopeFactory,
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseMigrator> logger
) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Migrate)
        {
            LogMigrationSkipped(logger);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        LogMigrating(logger);
        await context.Database.MigrateAsync(cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Database:Migrate is off — leaving the schema as it is. /health/ready reports whether it is current."
    )]
    private static partial void LogMigrationSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying database migrations.")]
    private static partial void LogMigrating(ILogger logger);
}
