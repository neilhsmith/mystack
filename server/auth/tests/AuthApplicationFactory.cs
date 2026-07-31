using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MyStack.Auth.Tests;

internal sealed class AuthApplicationFactory(
    string databaseConnectionString,
    string brokerConnectionString,
    RecordingLoggerProvider? logs = null,
    IDictionary<string, string?>? settings = null,
    string environment = "Testing"
) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not "Development": that would load appsettings.Development.json and hand the host a
        // working connection string to the developer's compose database, so a test whose own
        // configuration never arrived would still pass — against the wrong database.
        builder.UseEnvironment(environment);

        // UseSetting, not ConfigureAppConfiguration. Under the minimal hosting model the
        // factory runs Program's own Main and can only reach its configuration through the
        // args it passes in — UseSetting becomes `--key=value`, while
        // ConfigureAppConfiguration delegates are never invoked at all.
        builder.UseSetting("ConnectionStrings:AuthDb", databaseConnectionString);
        builder.UseSetting("ConnectionStrings:MessageBroker", brokerConnectionString);
        builder.UseSetting("Database:Migrate", "true");

        // The test client's own origin, so a link parsed out of a composed email can be driven
        // straight back against the host. UseSetting matters here: AddAccountFlows reads it
        // eagerly during Program's registration, before ConfigureAppConfiguration layers in.
        builder.UseSetting("Account:PublicBaseUrl", "http://localhost");

        // One immediate retry: a retry-then-dead-letter sequence is provable in seconds
        // instead of the production cooldowns' minutes.
        builder.UseSetting("Messaging:RetryCooldownsInSeconds:0", "0");

        // The step-4 hand-registered client, now arriving the way every environment's clients
        // do — as seed configuration — so every flow test runs against a seeded client.
        builder.UseSetting("Seed:Clients:0:ClientId", AuthAppFixture.ClientId);
        builder.UseSetting("Seed:Clients:0:Type", "Public");
        builder.UseSetting("Seed:Clients:0:RedirectUris:0", AuthAppFixture.RedirectUri);
        builder.UseSetting(
            "Seed:Clients:0:PostLogoutRedirectUris:0",
            AuthAppFixture.PostLogoutRedirectUri
        );
        builder.UseSetting("Seed:Clients:0:Scopes:0", "email");
        builder.UseSetting("Seed:Clients:0:Scopes:1", "profile");
        builder.UseSetting("Seed:Clients:0:Scopes:2", "roles");
        builder.UseSetting("Seed:Clients:0:Scopes:3", "api.read");
        builder.UseSetting("Seed:Clients:0:Scopes:4", "api.write");
        builder.UseSetting("Seed:Users:0:Email", AuthAppFixture.AdminEmail);
        builder.UseSetting("Seed:Users:0:Roles:0", "globaladmin");

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value ?? "");
            }
        }

        if (logs is not null)
        {
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs));
        }
    }
}
