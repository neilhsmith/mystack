using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyStack.Auth.Seeding;
using Npgsql;

namespace MyStack.Auth.Data;

// StartingAsync runs before every hosted service's StartAsync, and so before Kestrel binds: the
// schema is current and the seed data present before the first request rather than during it
// (docs/architecture.md §3.4).
internal sealed partial class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseInitializer> logger
) : IHostedLifecycleService
{
    // Concurrent instances race on both migrate and seed, so both run under one Postgres advisory
    // lock. Session-scoped on a dedicated connection: pg_advisory_xact_lock cannot span
    // MigrateAsync, which runs its own transactions. The key is per app — api's initializer
    // derives its own from its name.
    private static readonly long LockKey = BitConverter.ToInt64(
        SHA256.HashData("mystack:auth"u8.ToArray()),
        0
    );

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var database = options.Value;
        if (!database.Migrate && !database.Seed.Reference && !database.Seed.Sample)
        {
            LogNothingToDo(logger);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        await using var lockConnection = new NpgsqlConnection(
            context.Database.GetConnectionString()
        );
        await lockConnection.OpenAsync(cancellationToken);
        await ExecuteAsync(lockConnection, "SELECT pg_advisory_lock($1)", cancellationToken);
        try
        {
            if (database.Migrate)
            {
                LogMigrating(logger);
                await context.Database.MigrateAsync(cancellationToken);
            }
            else
            {
                LogMigrationSkipped(logger);
            }

            if (database.Seed.Reference || database.Seed.Sample)
            {
                // One transaction around the whole seed: a mid-seed failure leaves nothing
                // half-written. (The migration keeps its own transaction handling.)
                await using var transaction = await context.Database.BeginTransactionAsync(
                    cancellationToken
                );
                var seeder = scope.ServiceProvider.GetRequiredService<AuthSeeder>();

                if (database.Seed.Reference)
                {
                    await seeder.SeedReferenceAsync(cancellationToken);
                }

                if (database.Seed.Sample)
                {
                    await seeder.SeedSampleAsync();
                }

                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            // Released explicitly rather than by the connection closing, so a failure inside the
            // try can never strand the lock on a pooled session.
            await ExecuteAsync(
                lockConnection,
                "SELECT pg_advisory_unlock($1)",
                CancellationToken.None
            );
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using var command = new NpgsqlCommand(sql, connection)
        {
            Parameters = { new NpgsqlParameter { Value = LockKey } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Database:Migrate and both seed switches are off — leaving the database as it is."
    )]
    private static partial void LogNothingToDo(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Database:Migrate is off — leaving the schema as it is. /health/ready reports whether it is current."
    )]
    private static partial void LogMigrationSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying database migrations.")]
    private static partial void LogMigrating(ILogger logger);
}
