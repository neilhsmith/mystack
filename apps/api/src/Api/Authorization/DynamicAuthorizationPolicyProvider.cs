using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using OpenIddict.Validation.AspNetCore;

namespace Api.Authorization;

/// <summary>
/// Synthesises authorization policies for scope and permission requirements on the fly,
/// so endpoints can write <c>.RequireAuthorization("scope:mystack.read")</c> or
/// <c>.RequireAuthorization("perm:posts.read")</c> without each policy being declared up
/// front. Policies are cached by name in the underlying
/// <see cref="DefaultAuthorizationPolicyProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// The two namespaces are reserved prefixes — anything else falls through to the default
/// provider (which reads <c>AddAuthorization(opts =&gt; opts.AddPolicy(...))</c> entries).
/// </para>
/// <para>
/// Every synthesised policy is bound to
/// <see cref="OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme"/> so they apply
/// to API endpoints (bearer-token resource-server requests), not the cookie-authenticated
/// login flow.
/// </para>
/// </remarks>
public sealed class DynamicAuthorizationPolicyProvider : DefaultAuthorizationPolicyProvider
{
    /// <summary>Prefix for synthetic scope policies (e.g. <c>scope:mystack.read</c>).</summary>
    public const string ScopePrefix = "scope:";

    /// <summary>Prefix for synthetic permission policies (e.g. <c>perm:posts.read</c>).</summary>
    public const string PermissionPrefix = "perm:";

    public DynamicAuthorizationPolicyProvider(IOptions<AuthorizationOptions> options)
        : base(options)
    {
    }

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(ScopePrefix, StringComparison.Ordinal))
        {
            var scope = policyName[ScopePrefix.Length..];
            return new AuthorizationPolicyBuilder(
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .AddRequirements(new ScopeRequirement(scope))
                .Build();
        }

        if (policyName.StartsWith(PermissionPrefix, StringComparison.Ordinal))
        {
            var permission = policyName[PermissionPrefix.Length..];
            return new AuthorizationPolicyBuilder(
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission))
                .Build();
        }

        return await base.GetPolicyAsync(policyName);
    }
}
