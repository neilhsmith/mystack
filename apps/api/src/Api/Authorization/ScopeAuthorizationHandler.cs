using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;

namespace Api.Authorization;

/// <summary>
/// Decides whether the current principal carries the required OAuth scope.
/// <para>
/// JWT issuance puts scopes into a SINGLE <c>scope</c> claim with space-separated values
/// (RFC 8693). OpenIddict's validation handler ALSO emits one <c>oi_scp</c> claim per
/// scope on the principal for convenience. The test auth handler uses <c>oi_scp</c>
/// directly. Check both shapes so all three paths (real JWT, validation-built principal,
/// test handler) succeed.
/// </para>
/// </summary>
public sealed class ScopeAuthorizationHandler : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ScopeRequirement requirement)
    {
        if (HasScope(context.User, requirement.Scope))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    private static bool HasScope(System.Security.Claims.ClaimsPrincipal user, string required)
    {
        // Per-value claim shape (OpenIddict validation, test handler).
        foreach (var claim in user.FindAll(OpenIddictConstants.Claims.Scope))
        {
            if (string.Equals(claim.Value, required, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // Single space-separated claim shape (RFC 8693, raw JWT).
        foreach (var claim in user.FindAll("scope"))
        {
            foreach (var value in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(value, required, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
