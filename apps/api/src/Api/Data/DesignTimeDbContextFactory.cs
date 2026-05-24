using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Api.Data;

/// <summary>
/// EF tooling (e.g. <c>dotnet ef migrations add</c>) instantiates this factory at design
/// time to get a configured <see cref="AppDbContext"/> without running <c>Program.cs</c>.
/// Reads the connection string from <c>appsettings.Development.json</c> (copied to the
/// project's output dir by the SDK) plus environment variables — so design-time tooling
/// uses the same dev credentials as <c>dotnet run</c>, and changing the password in
/// <c>appsettings.Development.json</c> doesn't silently break EF tooling.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.Development.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "Design-time factory could not resolve ConnectionStrings:DefaultConnection. " +
                "Check appsettings.Development.json or set ConnectionStrings__DefaultConnection.");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AppDbContext(options);
    }
}
