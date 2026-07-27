using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MyStack.Auth.Tests;

internal sealed class AuthApplicationFactory(
    string connectionString,
    bool migrate,
    string environment
) : WebApplicationFactory<Program>
{
    // Not "Development": that would load appsettings.Development.json and hand the host a working
    // connection string to the developer's compose database, so a test whose own configuration
    // never arrived would still pass — against the wrong database.
    public const string TestEnvironment = "Testing";

    // Refused immediately rather than left to time out, for the tests that need a database the
    // host cannot reach.
    public const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=nothing;Username=nobody;Password=irrelevant;Timeout=1";

    // UseSetting, not ConfigureAppConfiguration. Under the minimal hosting model the factory runs
    // Program's own Main and can only reach its configuration through the args it passes in —
    // UseSetting becomes `--key=value`, and ConfigureAppConfiguration delegates are never invoked
    // at all, so they fail silently rather than loudly.
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("ConnectionStrings:AuthDb", connectionString);
        builder.UseSetting("Database:Migrate", migrate ? "true" : "false");
    }
}
