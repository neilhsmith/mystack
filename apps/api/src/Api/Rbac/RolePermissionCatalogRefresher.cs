using Api.Data;
using Api.Identity;
using Microsoft.EntityFrameworkCore;

namespace Api.Rbac;

/// <summary>
/// Background service that keeps <see cref="RolePermissionCatalog"/> in sync with the
/// <c>role_permissions</c> table. Runs an initial load on app start (also explicitly
/// awaited by <c>Program.cs</c> after migrations + seed so the first request can already
/// resolve permissions), then refreshes every <see cref="RefreshIntervalSeconds"/>.
/// <para>
/// Failure modes:
/// </para>
/// <list type="bullet">
///   <item>DB unreachable at first load → catalog stays empty, all permission checks deny.
///   Logged at <c>Critical</c>. The hosted service keeps retrying on its tick.</item>
///   <item>DB unreachable mid-life → previous snapshot is kept (no replace), error logged
///   at <c>Warning</c>. Permission checks continue against the last good snapshot.</item>
/// </list>
/// </summary>
public sealed class RolePermissionCatalogRefresher : BackgroundService
{
    /// <summary>How often the catalog is rebuilt from Postgres.</summary>
    public const int RefreshIntervalSeconds = 300;

    private readonly IServiceScopeFactory _scopes;
    private readonly RolePermissionCatalog _catalog;
    private readonly ILogger<RolePermissionCatalogRefresher> _logger;

    public RolePermissionCatalogRefresher(
        IServiceScopeFactory scopes,
        RolePermissionCatalog catalog,
        ILogger<RolePermissionCatalogRefresher> logger)
    {
        _scopes = scopes;
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>
    /// Load the catalog once, synchronously. Called from <c>Program.cs</c> after migrations
    /// and seed so the first inbound request never races the background loop.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        await RefreshOnceAsync(ct, throwOnFailure: true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First tick gets a chance to log success/failure even if Program.cs already did
        // a manual load — idempotent, and a fresh refresh is what we want anyway.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(RefreshIntervalSeconds));

        // Initial pass on entry to ExecuteAsync — covers the path where the explicit
        // LoadAsync was skipped (e.g. some integration-test factory wiring).
        await RefreshOnceAsync(stoppingToken, throwOnFailure: false);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RefreshOnceAsync(stoppingToken, throwOnFailure: false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    private async Task RefreshOnceAsync(CancellationToken ct, bool throwOnFailure)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Group the join in-memory after a flat fetch — the RBAC tables are tiny
            // (hundreds of rows at most) and a single SELECT with no joins keeps the
            // refresh cheap. Adjust to a server-side aggregate if/when this stops being
            // true (probably never for this scale).
            var pairs = await db.Set<RolePermission>()
                .AsNoTracking()
                .Join(
                    db.Set<ApplicationRole>().AsNoTracking(),
                    rp => rp.RoleId,
                    r => r.Id,
                    (rp, r) => new { RoleName = r.Name!, rp.PermissionName })
                .ToListAsync(ct);

            var byRole = pairs
                .GroupBy(p => p.RoleName, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyCollection<string>)g.Select(p => p.PermissionName).ToArray(),
                    StringComparer.Ordinal);

            _catalog.Replace(byRole);

            _logger.LogInformation(
                "Role-permission catalog refreshed: {RoleCount} roles, {AssignmentCount} assignments.",
                byRole.Count,
                pairs.Count);
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            _logger.LogWarning(
                ex,
                "Failed to refresh role-permission catalog; previous snapshot retained (loaded={Loaded}).",
                _catalog.IsLoaded);
        }
    }
}
