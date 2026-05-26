using System.Security.Claims;
using Api.Rbac;
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;

namespace Api.Authorization;

/// <summary>
/// Decides whether the current principal's role(s) include the required permission. The
/// token carries one or more <c>role</c> claims (no <c>permissions</c> claim — that lookup
/// happens here so admin role↔permission edits propagate without re-issuing tokens). The
/// catalog is loaded from Postgres at startup and refreshed periodically by
/// <see cref="RolePermissionCatalogRefresher"/>, so this check is a frozen-set hash lookup.
/// </summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly RolePermissionCatalog _catalog;
    private readonly ILogger<PermissionAuthorizationHandler> _logger;

    public PermissionAuthorizationHandler(
        RolePermissionCatalog catalog,
        ILogger<PermissionAuthorizationHandler> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (!_catalog.IsLoaded)
        {
            // Catalog hasn't been primed yet (first refresh hasn't succeeded). Refuse
            // rather than risk a false positive, but tell the operator why — this is the
            // signal that the API came up but can't authorize anything.
            _logger.LogWarning(
                "Permission check '{Permission}' denied: role-permission catalog not yet loaded.",
                requirement.Permission);
            return Task.CompletedTask;
        }

        var roles = ExtractRoles(context.User);
        if (_catalog.HasPermission(roles, requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Read role claims from the principal. OpenIddict emits each role as its own claim
    /// using <see cref="OpenIddictConstants.Claims.Role"/> (which is <c>"role"</c>);
    /// <see cref="ClaimTypes.Role"/> is the legacy long URI ASP.NET sometimes uses. Check
    /// both so this handler keeps working regardless of which constant the claims builder
    /// reaches for.
    /// </summary>
    private static IEnumerable<string> ExtractRoles(ClaimsPrincipal user)
    {
        foreach (var claim in user.FindAll(OpenIddictConstants.Claims.Role))
        {
            yield return claim.Value;
        }

        foreach (var claim in user.FindAll(ClaimTypes.Role))
        {
            yield return claim.Value;
        }
    }
}
