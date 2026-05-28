using Microsoft.AspNetCore.Authorization;

namespace Api.Authorization;

/// <summary>
/// Requires the bearer-token principal to hold a role that grants
/// <see cref="Permission"/>. Backed by <see cref="PermissionAuthorizationHandler"/>;
/// produced dynamically by <see cref="DynamicAuthorizationPolicyProvider"/> from policy
/// names of the form <c>perm:&lt;name&gt;</c> so endpoints can use
/// <c>.RequireAuthorization("perm:posts.read")</c> without each permission being
/// pre-registered.
/// </summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    /// <summary>The permission identifier (e.g. <c>posts.read</c>).</summary>
    public string Permission { get; } = permission;
}
