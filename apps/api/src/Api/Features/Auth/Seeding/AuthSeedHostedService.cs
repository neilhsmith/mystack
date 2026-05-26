using Api.Data;
using Api.Rbac;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Auth.Seeding;

/// <summary>
/// Runs the auth-related startup work in order, ONCE, before
/// <see cref="RolePermissionCatalogRefresher"/> serves its first request: migrate,
/// then seed RBAC (permissions, roles, role↔permission), then dev users, then OpenIddict
/// scopes/clients, then prime <see cref="RolePermissionCatalog"/>.
/// <para>
/// Implemented as a hosted service rather than ad-hoc code in <c>Program.cs</c> so the
/// scoped DbContext / UserManager / RoleManager resolve correctly. Only runs migrations
/// in <c>Development</c> — same policy as the existing API: production deployments use
/// EF tooling, not a startup hook.
/// </para>
/// </summary>
public sealed class AuthSeedHostedService : IHostedService
{
    private readonly IServiceProvider _provider;
    private readonly IHostEnvironment _env;
    private readonly ILogger<AuthSeedHostedService> _logger;

    public AuthSeedHostedService(
        IServiceProvider provider,
        IHostEnvironment env,
        ILogger<AuthSeedHostedService> logger)
    {
        _provider = provider;
        _env = env;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        if (_env.IsDevelopment())
        {
            var db = sp.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync(cancellationToken);
        }

        var rbac = sp.GetRequiredService<RbacSeeder>();
        await rbac.SeedAsync(cancellationToken);

        var users = sp.GetRequiredService<UserSeeder>();
        await users.SeedAsync(cancellationToken);

        var openIddict = sp.GetRequiredService<OpenIddictSeeder>();
        await openIddict.SeedAsync(cancellationToken);

        // Prime the catalog so the very first authorized request can resolve permissions
        // without waiting for the refresher's first tick.
        var refresher = sp.GetRequiredService<RolePermissionCatalogRefresher>();
        await refresher.LoadAsync(cancellationToken);

        _logger.LogInformation("Auth startup seed complete.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
