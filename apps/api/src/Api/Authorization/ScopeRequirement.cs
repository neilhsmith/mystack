using Microsoft.AspNetCore.Authorization;

namespace Api.Authorization;

/// <summary>
/// Requires the bearer-token principal to carry a specific OAuth scope. Backed by
/// <see cref="ScopeAuthorizationHandler"/>; produced dynamically by
/// <see cref="DynamicAuthorizationPolicyProvider"/> from policy names of the form
/// <c>scope:&lt;name&gt;</c> so endpoints can use <c>.RequireAuthorization("scope:mystack.read")</c>
/// without each scope being pre-registered.
/// </summary>
public sealed class ScopeRequirement(string scope) : IAuthorizationRequirement
{
    /// <summary>The scope identifier (e.g. <c>mystack.read</c>).</summary>
    public string Scope { get; } = scope;
}
