using Microsoft.EntityFrameworkCore;
using MyStack.Auth.Seeding;

namespace MyStack.Auth.Data;

internal static class DatabaseExtensions
{
    public const string ConnectionStringName = "AuthDb";

    public static WebApplicationBuilder AddAuthDatabase(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;

        var connectionString =
            configuration.GetConnectionString(ConnectionStringName)
            // Failing to boot beats booting against the wrong database, and a fallback value here
            // would be a credential shipped in the binary (docs/architecture.md §3.4).
            ?? throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringName} is not configured."
            );

        builder
            .Services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName));
        builder
            .Services.AddOptions<SeedOptions>()
            .Bind(configuration.GetSection(SeedOptions.SectionName));

        builder.Services.AddDbContext<AuthDbContext>(options =>
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

        builder.Services.AddScoped<AuthSeeder>();
        builder.Services.AddHostedService<DatabaseInitializer>();

        return builder;
    }
}
