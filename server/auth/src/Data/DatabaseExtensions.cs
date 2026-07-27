using Microsoft.EntityFrameworkCore;

namespace MyStack.Auth.Data;

internal static class DatabaseExtensions
{
    public const string ConnectionStringName = "AuthDb";

    public static IServiceCollection AddAuthDatabase(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var connectionString =
            configuration.GetConnectionString(ConnectionStringName)
            // Failing to boot beats booting against the wrong database, and a fallback value here
            // would be a credential shipped in the binary (docs/architecture.md §3.4).
            ?? throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringName} is not configured."
            );

        services
            .AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName));

        services.AddDbContext<AuthDbContext>(options =>
            options
                .UseNpgsql(
                    connectionString,
                    npgsql =>
                        npgsql.MigrationsHistoryTable(
                            "__ef_migrations_history",
                            AuthDbContext.Schema
                        )
                )
                .UseSnakeCaseNamingConvention()
        );

        services.AddHostedService<DatabaseMigrator>();

        return services;
    }
}
